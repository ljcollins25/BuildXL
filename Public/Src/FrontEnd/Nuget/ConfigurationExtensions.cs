// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Native.IO;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;

namespace BuildXL.FrontEnd.Nuget
{
    internal static class ConfigurationExtensions
    {
        /// <summary>
        /// Checks to see if a source drive has the seek penalty property set.
        /// </summary>
        public static bool DoesSourceDiskDriveHaveSeekPenalty(this IConfiguration configuration, PathTable pathTable)
        {
            var sourceDirectory = configuration.Layout.SourceDirectory.ToString(pathTable);
            Contract.Assume(
                Path.IsPathRooted(sourceDirectory) && !string.IsNullOrWhiteSpace(sourceDirectory),
                "Config file path should be absolute");
            char driveLetter = sourceDirectory[0];
            return (char.IsLetter(driveLetter) && driveLetter > 64 && driveLetter < 123)
                ? FileUtilities.DoesLogicalDriveHaveSeekPenalty(driveLetter) == true
                : false;
        }
    }
}
