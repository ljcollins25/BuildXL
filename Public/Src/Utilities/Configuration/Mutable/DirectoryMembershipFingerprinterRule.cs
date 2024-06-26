// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <nodoc />
    public sealed class DirectoryMembershipFingerprinterRule : TrackedValue, IDirectoryMembershipFingerprinterRule
    {
        /// <nodoc />
        public DirectoryMembershipFingerprinterRule()
        {
            FileIgnoreWildcards = new List<PathAtom>();
        }

        /// <nodoc />
        public DirectoryMembershipFingerprinterRule(IDirectoryMembershipFingerprinterRule template, PathRemapper pathRemapper)
            : base(template, pathRemapper)
        {
            Contract.Assume(template != null);
            Contract.Assume(pathRemapper != null);

            Name = template.Name;
            Root = pathRemapper.Remap(template.Root);
            DisableFilesystemEnumeration = template.DisableFilesystemEnumeration;
            FileIgnoreWildcards = new List<PathAtom>(template.FileIgnoreWildcards.Select(pathRemapper.Remap));
            Recursive = template.Recursive;
        }

        /// <inheritdoc />
        public string Name { get; set; }

        /// <inheritdoc />
        public AbsolutePath Root { get; set; }

        /// <inheritdoc />
        public bool DisableFilesystemEnumeration { get; set; }

        /// <nodoc />
        [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public List<PathAtom> FileIgnoreWildcards { get; set; }

        /// <inheritdoc />
        public bool Recursive { get; set; }

        /// <inheritdoc />
        IReadOnlyList<PathAtom> IDirectoryMembershipFingerprinterRule.FileIgnoreWildcards => FileIgnoreWildcards;
    }
}
