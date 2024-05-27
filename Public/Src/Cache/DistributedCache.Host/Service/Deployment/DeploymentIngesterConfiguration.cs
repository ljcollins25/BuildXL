// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Web;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.Host.Service;
using BuildXL.Utilities.Collections;
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
            if (!string.IsNullOrEmpty(dropExeFilePath) && !string.IsNullOrEmpty(dropToken))
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
            ZipDeploymentIngesterUrlHander.AddHandlers(this);
            return this;
        }
    }

    public record DeploymentIngesterBaseConfiguration(
        AbsolutePath SourceRoot,
        AbsolutePath DeploymentRoot,
        AbsolutePath DeploymentConfigurationPath,
        IAbsFileSystem FileSystem,
        ActionQueue ActionQueue = null,
        string DeploymentManifestFileName = null)
    {
        /// <summary>
        /// Indicates whether ingester should skip Pin check to see if content exists in store and always attempt to upload content.
        /// </summary>
        public bool ForceUploadContent { get; set; }

        public string DeploymentManifestFileName { get; init; } = DeploymentManifestFileName.NullIfEmpty() ?? DeploymentUtilities.DeploymentManifestFileName;

        public AbsolutePath DeploymentManifestPath => DeploymentRoot / DeploymentManifestFileName;

        public AbsolutePath GetDeploymentManifestIdPath() => DeploymentUtilities.GetDeploymentManifestIdPath(DeploymentManifestPath);

        public string GetDeploymentManifestIdFileName() => DeploymentUtilities.GetDeploymentManifestIdPath(DeploymentManifestFileName);

        public ActionQueue ActionQueue { get; } = ActionQueue ?? new ActionQueue(Environment.ProcessorCount);
    }

}
