// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;

namespace BuildXL.Cache.Host.Configuration
{
    /// <summary>
    /// Describes settings used by launcher application
    /// </summary>
    public record LauncherApplicationSettings : LauncherSettings
    {
        /// <summary>
        /// Configure the logging behavior for the service
        /// </summary>
        public LoggingSettings? LoggingSettings { get; set; } = null;

        /// <summary>
        /// Indicates whether environment variables should be expanded in the LauncherSettings file
        /// </summary>
        public bool ExpandEnvironmentVariables { get; set; }
    }
}
