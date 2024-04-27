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
using BuildXL.Utilities.ParallelAlgorithms;

namespace BuildXL.Cache.Host.Service.Deployment
{
    public record DeploymentIngesterConfiguration(
        DeploymentIngesterBaseConfiguration BaseConfiguration,
        IDeploymentIngestorTargetStore Store)
        : DeploymentIngesterBaseConfiguration(BaseConfiguration)
    {
        public Dictionary<string, IDeploymentIngesterUrlHandler> HandlerByScheme { get; } = new Dictionary<string, IDeploymentIngesterUrlHandler>();

        public DeploymentIngesterConfiguration PopulateDefaultHandlers(string dropExeFilePath, string dropToken)
        {
            if (string.IsNullOrEmpty(dropExeFilePath) && !string.IsNullOrEmpty(dropToken))
            {
                HandlerByScheme["https"] = new DropDeploymentIngesterUrlHandler(new AbsolutePath(dropExeFilePath), dropToken, this);
            }

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

    public record DeploymentIngesterBaseConfiguration(
        AbsolutePath SourceRoot,
        AbsolutePath DeploymentRoot,
        AbsolutePath DeploymentConfigurationPath,
        IAbsFileSystem FileSystem,
        ActionQueue ActionQueue = null)
    {
        public AbsolutePath DeploymentManifestPath { get; } = DeploymentUtilities.GetDeploymentManifestPath(DeploymentRoot);

        public ActionQueue ActionQueue { get; } = ActionQueue ?? new ActionQueue(Environment.ProcessorCount);
    }

}
