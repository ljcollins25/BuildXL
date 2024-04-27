// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service.Deployment;
using BuildXL.Cache.MemoizationStore.Interfaces;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.ParallelAlgorithms;
using static BuildXL.Cache.Host.Configuration.DeploymentManifest;
//using static BuildXL.Cache.Host.Service.DeploymentUtilities;

namespace BuildXL.Cache.Host.Service
{
    public interface IDeploymentIngestorTargetStore : IStartupShutdownSlim
    {
        Task<IEnumerable<Indexed<bool>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> hashes);

        Task<PutResult> PutFileAsync(OperationContext context, AbsolutePath sourcePath);
    }

    public class StorageDeploymentTargetStore : StartupShutdownComponentBase, IDeploymentIngestorTargetStore
    {
        public record Configuration(DeploymentIngesterBaseConfiguration BaseConfiguration)
            : DeploymentIngesterBaseConfiguration(BaseConfiguration)
        {
            public IDictionary<string, string[]> StorageAccountsByRegion { get; set; }

            public string ContentContainerName { get; set; }
        }

        /// <summary>
        /// Describes a file in drops
        /// </summary>
        private record struct FileSpec(AbsolutePath SourcePath, ContentHash Md5ChecksumForBlob);

        private record StorageAccountsByRegion(string Region, BlobContainerClient[] Accounts);

        protected override Tracer Tracer { get; } = new Tracer(nameof(StorageDeploymentTargetStore));

        private IReadOnlyList<StorageAccountsByRegion> StorageAccounts { get; set; }

        private readonly Configuration _configuration;

        public IAbsFileSystem FileSystem => _configuration.FileSystem;

        public AbsolutePath DeploymentManifestPath => _configuration.DeploymentManifestPath;

        private ConcurrentBigSet<(string Region, string BlobName)> UploadedContent { get; } = new();

        public Func<(string accountName, string containerName), Task<BlobContainerClient>> OverrideGetContainer { get; set; }

        public StorageDeploymentTargetStore(Configuration configuration)
        {
            _configuration = configuration;
        }

        protected override async Task<BoolResult> StartupComponentAsync(OperationContext context)
        {
            StorageAccounts = await ConstructStorageAccounts();

            return BoolResult.Success;
        }

        protected override async Task<BoolResult> ShutdownComponentAsync(OperationContext context)
        {
            return await context.PerformOperationAsync(
                Tracer,
                () => UploadFileAsync(context, DeploymentManifestPath, DeploymentUtilities.DeploymentManifestRelativePath.Path),
                caller: "UploadDeploymentManifest");
        }

        private async Task<IReadOnlyList<StorageAccountsByRegion>> ConstructStorageAccounts()
        {
            async ValueTask<BlobContainerClient> getContainerClient(string accountName)
            {
                if (OverrideGetContainer != null)
                {
                    return await OverrideGetContainer((accountName, _configuration.ContentContainerName));
                }
                else
                {
                    var containerClient = new BlobContainerClient(new Uri($"https://{accountName}.blob.core.windows.net/{_configuration.ContentContainerName}"), new DefaultAzureCredential());
                    await containerClient.CreateIfNotExistsAsync();
                    return new BlobContainerClient(await GetUserDelegationContainerSasUri(containerClient), null);
                }
            }

            // Get a credential and create a service client object for the blob container.
            return await _configuration.StorageAccountsByRegion.ToAsyncEnumerable().SelectAwait(
                        async kv => new StorageAccountsByRegion(kv.Key,
                        await kv.Value.ToAsyncEnumerable().SelectAwait(async accountName => await getContainerClient(accountName)).ToArrayAsync())).ToListAsync();
        }

        private async static Task<Uri> GetUserDelegationContainerSasUri(BlobContainerClient blobContainerClient)
        {
            BlobServiceClient blobServiceClient = blobContainerClient.GetParentBlobServiceClient();

            // Get a user delegation key for the Blob service that's valid for seven days.
            // You can use the key to generate any number of shared access signatures 
            // over the lifetime of the key.
            Azure.Storage.Blobs.Models.UserDelegationKey userDelegationKey =
                await blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow,
                                                                  DateTimeOffset.UtcNow.AddDays(1));

            // Create a SAS token that's also valid for seven days.
            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = blobContainerClient.Name,
                Resource = "c",
                StartsOn = DateTimeOffset.UtcNow,
                ExpiresOn = DateTimeOffset.UtcNow.AddDays(7)
            };

            sasBuilder.SetPermissions(BlobAccountSasPermissions.All);

            // Add the SAS token to the container URI.
            BlobUriBuilder blobUriBuilder = new BlobUriBuilder(blobContainerClient.Uri)
            {
                // Specify the user delegation key.
                Sas = sasBuilder.ToSasQueryParameters(userDelegationKey,
                                                      blobServiceClient.AccountName)
            };

            Console.WriteLine("Container user delegation SAS URI: {0}", blobUriBuilder);
            Console.WriteLine();
            return blobUriBuilder.ToUri();
        }

        public Task<IEnumerable<Indexed<bool>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> hashes)
        {
            return Task.FromResult(Enumerable.Empty<Indexed<bool>>());
        }

        public Task<PutResult> PutFileAsync(OperationContext context, AbsolutePath sourcePath)
        {
            return UploadFileAsync(context, sourcePath);
        }

        private async Task<PutResult> UploadFileAsync(OperationContext context, AbsolutePath sourcePath, string overrideBlobName = null)
        {
            ContentHash? hash = overrideBlobName == null ? null : ContentHashingHelper.HashFile(sourcePath.ToString(), HashType.SHA256);

            string blobName = overrideBlobName ?? DeploymentUtilities.GetContentRelativePath(hash.Value).ToString();
            var file = new FileSpec()
            {
                SourcePath = sourcePath,
                Md5ChecksumForBlob = ContentHashingHelper.HashFile(sourcePath.ToString(), HashType.MD5)
            };

            foreach (var regionalAccounts in StorageAccounts)
            {
                if (UploadedContent.Add((regionalAccounts.Region, blobName)))
                {
                    var sasUrlToFile = await UploadFileToFirstStorageAccountAsync(context, file, blobName, regionalAccounts);
                    await ReplicateFileToOtherStorageAccountsAsync(context, blobName, sasUrlToFile, regionalAccounts);
                }
            }

            var size = FileSystem.GetFileSize(sourcePath);

            return new PutResult(hash ?? file.Md5ChecksumForBlob, size);
        }

        private Task ReplicateFileToOtherStorageAccountsAsync(OperationContext context, string blobName, Uri sasUrlToFile, StorageAccountsByRegion regionalAccounts)
        {
            var otherAccounts = regionalAccounts.Accounts.Skip(1);
            return context.PerformOperationAsync(Tracer, async () =>
            {
                foreach (BlobContainerClient container in otherAccounts)
                {
                    await container.GetBlockBlobClient(blobName).SyncUploadFromUriAsync(sasUrlToFile);
                }
                return BoolResult.Success;
            },
            extraEndMessage: r => $"Blob={blobName} Region={regionalAccounts.Region} OtherAccounts={otherAccounts.Count()}").ThrowIfFailureAsync();
        }

        private Task<Uri> UploadFileToFirstStorageAccountAsync(OperationContext context, FileSpec file, string blobName, StorageAccountsByRegion regionalAccounts)
        {
            var container = regionalAccounts.Accounts[0];
            return context.PerformOperationAsync(Tracer, async () =>
            {
                var uploadOptions = new BlobUploadOptions
                {
                    // Verify content on upload
                    HttpHeaders = new BlobHttpHeaders { ContentHash = file.Md5ChecksumForBlob.ToHashByteArray() }
                };

                using var fileStream = FileSystem.OpenReadOnly(file.SourcePath, FileShare.Read | FileShare.Delete);
                var blobClient = container.GetBlobClient(blobName);
                Azure.Response<BlobContentInfo> result = await blobClient.UploadAsync(fileStream, uploadOptions);

                return Result.Success(blobClient.Uri);
            },
            extraStartMessage: $"Blob={blobName} Region={regionalAccounts.Region} Account={container.AccountName}",
            extraEndMessage: r => $"Blob={blobName} Region={regionalAccounts.Region} Account={container.AccountName}").ThrowIfFailureAsync();
        }
    }

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
    }
}
