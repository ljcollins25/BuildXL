// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Service.Deployment;
//using static BuildXL.Cache.Host.Service.DeploymentUtilities;

namespace BuildXL.Cache.Host.Service
{
    public class FileSystemDeploymentContentStore : StartupShutdownComponentBase, IDeploymentIngestorTargetStore
    {
        protected override Tracer Tracer { get; } = new Tracer(nameof(FileSystemDeploymentContentStore));

        private PinRequest PinRequest { get; set; }

        /// <summary>
        /// Content store used to store files in content addressable layout under deployment root
        /// </summary>
        private FileSystemContentStoreInternal Store { get; }

        private readonly DeploymentIngesterBaseConfiguration _configuration;

        private AbsolutePath DeploymentRoot => _configuration.DeploymentRoot;

        public FileSystemDeploymentContentStore(DeploymentIngesterBaseConfiguration configuration, int retentionSizeGb)
        {
            _configuration = configuration;
            Store = new FileSystemContentStoreInternal(
                configuration.FileSystem,
                SystemClock.Instance,
                DeploymentUtilities.GetCasRootPath(configuration.DeploymentRoot),
                new ConfigurationModel(new ContentStoreConfiguration(new MaxSizeQuota($"{retentionSizeGb}GB"))),
                settings: new ContentStoreSettings()
                {
                    TraceFileSystemContentStoreDiagnosticMessages = true,
                    CheckFiles = false,

                    // Disable empty file shortcuts to ensure all content is always placed on disk
                    UseEmptyContentShortcut = false
                });

            LinkLifetime(Store);
        }

        protected override Task<BoolResult> StartupComponentAsync(OperationContext context)
        {
            PinRequest = new PinRequest(Store.CreatePinContext());

            return base.StartupComponentAsync(context);
        }

        public async Task<IEnumerable<Indexed<bool>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> hashes)
        {
            var results = await Store.PinAsync(context, hashes, PinRequest.PinContext, options: null);
            return results.Select(r => new Indexed<bool>(r.Item.Succeeded, r.Index));
        }

        public async Task<PutResult> PutFileAsync(OperationContext context, AbsolutePath sourcePath)
        {
            // Hash file before put to prevent copying file in common case where it is already in the cache
            var hash = ContentHashingHelper.HashFile(sourcePath.ToString(), HashType.MD5);

            var result = await Store.PutFileAsync(context, sourcePath, FileRealizationMode.Copy, hash, PinRequest).ThrowIfFailureAsync();

            var targetPath = DeploymentRoot / DeploymentUtilities.GetContentRelativePath(result.ContentHash);

            Contract.Assert(_configuration.FileSystem.FileExists(targetPath), $"Could not find content for hash {result.ContentHash} at '{targetPath}'");

            return result;
        }

        public Task<BoolResult> FinalizeIngestionAsync(OperationContext context) => BoolResult.SuccessTask;
    }
}
