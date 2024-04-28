// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable disable
#nullable enable annotations

namespace BuildXL.Cache.Host.Configuration
{
    /// <summary>
    /// Used to extract user-defined parameters inline in json files (in particular DeploymentConfiguration.json)
    /// which can be used for preprocessing the json file in a second pass.
    /// </summary>
    public class ConfigurationParameters
    {
        public PreprocessorParameters Parameters { get; }
    }
}
