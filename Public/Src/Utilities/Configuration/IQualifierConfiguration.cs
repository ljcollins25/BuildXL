// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Qualifier settings
    /// </summary>
    public interface IQualifierConfiguration
    {
        /// <summary>
        /// The command line default qualifier to use in the build
        /// </summary>
        [NotNull]
        IReadOnlyDictionary<string, string> DefaultQualifier { get; }

        /// <summary>
        /// A set of named qualifiers as convenient for the commandline build.
        /// </summary>
        [NotNull]
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NamedQualifiers { get; }
    }
}
