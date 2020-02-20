﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using Mono.Collections.Generic;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Microsoft.DotNet.SourceBuild.Tasks.LeakDetection
{
    public class MarkAndCatalogPackages : Task
    {
        private const string CatalogElementName = "HashCatalog";
        private const string PoisonMarker = "POISONED by DotNetSourceBuild - Should not ship";

        private readonly Type[] AssemblyPropertiesToReplace = new Type[] {
            typeof(AssemblyProductAttribute),
            typeof(AssemblyInformationalVersionAttribute),
            typeof(AssemblyDescriptionAttribute),
            typeof(AssemblyTitleAttribute)
        };

        /// <summary>
        /// The name of the XML file to write the hash catalog out to,
        /// for later checking build output against.  This is optional -
        /// if not used, assemblies will still be poisoned in their attributes.
        /// </summary>
        public string CatalogOutputFilePath { get; set; }

        /// <summary>
        /// The name of the marker file to drop in the nupkgs.  This can vary
        /// with the packages, so you can use ".prebuilt" for one set of packages
        /// and ".source-built" for another if you would like to tell the difference
        /// between two sets of poisoned packages.
        /// </summary>
        public string MarkerFileName { get; set; }

        /// <summary>
        /// The packages to poison and/or catalog:
        /// %(Identity): Path to the nupkg.
        /// </summary>
        [Required]
        public ITaskItem[] PackagesToMark { get; set; }

        public override bool Execute()
        {
            var tempDirName = Path.GetRandomFileName();
            var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), tempDirName));

            var packageEntries = new List<CatalogPackageEntry>();

            using (var sha = SHA256.Create())
            {
                foreach (var p in PackagesToMark)
                {
                    var packageEntry = new CatalogPackageEntry();
                    packageEntries.Add(packageEntry);
                    packageEntry.Path = p.ItemSpec;
                    using (var stream = File.OpenRead(p.ItemSpec))
                    {
                        packageEntry.OriginalHash = sha.ComputeHash(stream);
                    }
                    var packageIdentity = ReadNuGetPackageInfos.ReadIdentity(p.ItemSpec);
                    packageEntry.Id = packageIdentity.Id;
                    packageEntry.Version = packageIdentity.Version.OriginalVersion;
                    var packageTempPath = Path.Combine(tempDir.FullName, Path.GetFileName(p.ItemSpec));
                    ZipFile.ExtractToDirectory(p.ItemSpec, packageTempPath, true);

                    foreach (string f in Directory.EnumerateFiles(packageTempPath, "*", SearchOption.AllDirectories))
                    {
                        var catalogFileEntry = new CatalogFileEntry();
                        packageEntry.Files.Add(catalogFileEntry);
                        catalogFileEntry.Path = Utility.MakeRelativePath(f, packageTempPath);
                        AssemblyDefinition asm = null;

                        // There seem to be some weird issues with using a file stream both for hashing and
                        // assembly loading, even closing it in between.  Use a MemoryStream to avoid issues.
                        var memStream = new MemoryStream();
                        using (var stream = File.OpenRead(f))
                        {
                            stream.CopyTo(memStream);
                        }

                        // First get the original hash of the file
                        memStream.Seek(0, SeekOrigin.Begin);
                        catalogFileEntry.OriginalHash = sha.ComputeHash(memStream);

                        // Now try to read it as an assembly
                        memStream.Seek(0, SeekOrigin.Begin);
                        try
                        {
                            asm = AssemblyDefinition.ReadAssembly(memStream, new ReaderParameters(ReadingMode.Deferred));
                        }
                        catch
                        {
                            // this is okay, it's not an assembly we can read
                        }

                        // if we read it, now poison and write it back out
                        if (asm != null)
                        {
                            Poison(asm);

                            try
                            {
                                // Cecil doesn't try to do some modifications until it writes out the file,
                                // and then throws after we've already truncated the file if it finds out it can't do them.
                                // Write to a memory stream first and then copy to the real stream if it suceeds.  If it
                                // fails, we won't truncate the file and we will depend on hashes instead in that case.
                                using (var testMemStream = new MemoryStream())
                                {
                                    asm.Write(testMemStream);
                                    testMemStream.Seek(0, SeekOrigin.Begin);
                                    using (var stream = File.Open(f, FileMode.Create, FileAccess.ReadWrite))
                                    {
                                        testMemStream.CopyTo(stream);
                                    }
                                }

                                // then get the hash of the now-poisoned file
                                using (var stream = File.OpenRead(f))
                                {
                                    catalogFileEntry.PoisonedHash = sha.ComputeHash(stream);
                                }
                            }
                            catch
                            {
                                // see above note in the try - this is okay.
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(MarkerFileName))
                    {
                        var markerFilePath = Path.Combine(packageTempPath, MarkerFileName);

                        if (File.Exists(markerFilePath))
                        {
                            throw new ArgumentException($"Marker file name '{MarkerFileName}' is not sufficiently unique!  Exists in '{p.ItemSpec}'.", nameof(MarkerFileName));
                        }

                        // mostly we just need to write something unique to this so it's not hashed as a matching file when we check it later.
                        // but it's also convenient to have the package catalog handy.
                        File.WriteAllText(markerFilePath, packageEntry.ToXml().ToString());
                    }

                    // create a temp file for this so if something goes wrong in the process we're not in too weird of a state
                    var poisonedPackageName = Path.GetFileName(p.ItemSpec) + ".poisoned";
                    var poisonedPackagePath = Path.Combine(tempDir.FullName, poisonedPackageName);
                    ZipFile.CreateFromDirectory(packageTempPath, poisonedPackagePath);

                    // Get the hash of the poisoned package (with poisoned marker file and poisoned assemblies inside)
                    using (var stream = File.OpenRead(poisonedPackagePath))
                    {
                        packageEntry.PoisonedHash = sha.ComputeHash(stream);
                    }
                    File.Delete(p.ItemSpec);
                    File.Move(poisonedPackagePath, p.ItemSpec);
                }
            }

            // if we should write out the catalog, do that
            if (!string.IsNullOrWhiteSpace(CatalogOutputFilePath))
            {
                var outputFileDir = Path.GetDirectoryName(CatalogOutputFilePath);
                if (!Directory.Exists(outputFileDir))
                {
                    Directory.CreateDirectory(outputFileDir);
                }
                File.WriteAllText(CatalogOutputFilePath, (new XElement("HashCatalog",
                    packageEntries.Select(p => p.ToXml()))).ToString());
            }

            tempDir.Delete(true);
            return !Log.HasLoggedErrors;
        }

        private void Poison(AssemblyDefinition asm)
        {
            foreach (var attr in asm.CustomAttributes)
            {
                if (this.AssemblyPropertiesToReplace.Any(p => p.Name == attr.AttributeType.Name))
                {
                    attr.ConstructorArguments.Clear();
                    attr.ConstructorArguments.Add(new CustomAttributeArgument(asm.MainModule.TypeSystem.String, "POISONED by DotNetSourceBuild - Should not ship"));
                }
            }
        }
    }
}
