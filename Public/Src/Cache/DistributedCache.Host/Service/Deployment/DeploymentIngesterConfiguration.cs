// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.Host.Service;

namespace BuildXL.Cache.Host.Service.Deployment
{
    public record DeploymentIngesterConfiguration(
        AbsolutePath SourceRoot,
        AbsolutePath DeploymentRoot,
        AbsolutePath DeploymentConfigurationPath,
        IAbsFileSystem FileSystem,
        int RetentionSizeGb)
    {
        public Dictionary<string, IDeploymentIngesterUrlHandler> HandlerByScheme { get; } = new Dictionary<string, IDeploymentIngesterUrlHandler>();

        public DeploymentIngesterConfiguration PopulateDefaultHandlers(AbsolutePath dropExeFilePath, string dropToken)
        {
            HandlerByScheme["https"] = new DropDeploymentIngesterUrlHandler(dropExeFilePath, dropToken, this);
            return TryPopulateDefaultHandlers();
        }

        public DeploymentIngesterConfiguration TryPopulateDefaultHandlers()
        {
            var fileHandler = new FileDeploymentIngesterUrlHander(this);
            HandlerByScheme.TryAdd("config", fileHandler);
            HandlerByScheme.TryAdd("file", fileHandler);
            HandlerByScheme.TryAdd("zip", new RemoteZipDeploymentIngesterUrlHander(this));
            return this;
        }
    }

}
