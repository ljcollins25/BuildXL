// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Cache.ContentStore.Service.Grpc;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service.Internal;
using BuildXL.Native.IO;
using BuildXL.Processes;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.ParallelAlgorithms;
using BuildXL.Utilities.Core.Tasks;
using static BuildXL.Cache.Host.Configuration.DeploymentManifest;
using BuildXL.Cache.ContentStore.Distributed.Utilities;

namespace BuildXL.Cache.Host.Service
{
    /// <summary>
    /// Deploys drops/files from a given deployment configuration to a CAS store and writes manifests describing contents
    /// so that subsequent process (i.e. Deployment Service) can read files and proffer deployments to clients.
    /// </summary>
    public partial class DeploymentLauncher : StartupShutdownBase, ICacheServer
    {
        /// <summary>
        /// For testing purposes only.
        /// </summary>
        public static IDeploymentLauncherHost OverrideHost { get; set; }

        #region Configuration

        /// <summary>
        /// The deployment root directory under which CAS and deployments will be stored
        /// </summary>
        public DisposableDirectory DeploymentDirectory { get; }

        private LauncherSettings Settings { get; }

        #endregion

        private ActionQueue DownloadQueue { get; }

        private IAbsFileSystem FileSystem { get; }

        /// <summary>
        /// Content store used to store files in content addressable layout under deployment root
        /// </summary>
        internal FileSystemContentStoreInternal Store { get; }

        protected override Tracer Tracer { get; } = new Tracer(nameof(DeploymentLauncher));

        private DeployedTool _currentRun;

        private readonly SemaphoreSlim _mutex = TaskUtilities.CreateMutex();

        public IDeployedTool CurrentRun => _currentRun;

        private IRetryPolicy RetryPolicy { get; }

        /// <summary>
        /// Lifetime manager used to signal shutdown of launched services
        /// </summary>
        public ServiceLifetimeManager LifetimeManager { get; }

        /// <summary>
        /// For testing purposes only. Used to intercept launch of drop.exe process and run custom logic in its place
        /// </summary>
        public Func<(string exePath, string args, string dropUrl, string targetDirectory, string relativeRoot), BoolResult> OverrideLaunchDropProcess { get; set; }

        private readonly IDeploymentLauncherHost _host;

        private readonly ISecretsProvider _secretsProvider;

        /// <inheritdoc />
        bool ICacheServer.IsProxy => true;

        /// <inheritdoc />
        TStore ICacheServer.GetDefaultStore<TStore>() => throw new NotSupportedException();

        /// <inheritdoc />
        IPushFileHandler ICacheServer.PushFileHandler => throw new NotSupportedException();

        /// <inheritdoc />
        IDistributedStreamStore ICacheServer.StreamStore => throw new NotSupportedException();

        /// <inheritdoc />
        IEnumerable<IGrpcServiceEndpoint> ICacheServer.GrpcEndpoints => throw new NotSupportedException();

        /// <nodoc />
        public DeploymentLauncher(
            LauncherSettings settings,
            IAbsFileSystem fileSystem,
            IDeploymentLauncherHost host = null,
            ISecretsProvider secretsProvider = null)
        {
            Settings = settings;
            _secretsProvider = secretsProvider;
            var targetDirectory = new AbsolutePath(settings.TargetDirectory);
            DeploymentDirectory = new DisposableDirectory(fileSystem, targetDirectory / "bin");
            _host = host ?? OverrideHost ?? (settings.IsFileServer
                ? new DeploymentLauncherHost(new FileServerDeploymentClient(new Uri(settings.ServiceUrl)))
                : DeploymentLauncherHost.Instance);

            LifetimeManager = new ServiceLifetimeManager(targetDirectory / "lifetime", TimeSpan.FromSeconds(Settings.ServiceLifetimePollingIntervalSeconds));

            Store = new FileSystemContentStoreInternal(
                fileSystem,
                SystemClock.Instance,
                DeploymentUtilities.GetCasRootPath(targetDirectory),
                new ConfigurationModel(new ContentStoreConfiguration(new MaxSizeQuota($"{settings.RetentionSizeGb}GB"))),
                settings: new ContentStoreSettings()
                          {
                              TraceFileSystemContentStoreDiagnosticMessages = true,

                              // Disable empty file shortcuts to ensure all content is always placed on disk
                              UseEmptyContentShortcut = false
                          });

            FileSystem = fileSystem;

            DownloadQueue = new ActionQueue(settings.DownloadConcurrency);
        }

        /// <summary>
        /// Uploads the deployment files to the target storage account and returns the launcher manifest for the given deployment parameters
        /// </summary>
        public Task<BoolResult> RunAsync(OperationContext context)
        {
            return WithOperationContext(context, context.Token, ctx => ctx.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    while (!ctx.Token.IsCancellationRequested)
                    {
                        using var timeoutSource = new CancellationTokenSource();
                        timeoutSource.CancelAfter(Settings.DeployTimeout);

                        using var timeoutContext = ctx.WithCancellationToken(timeoutSource.Token);

                        await GetDownloadAndRunDeployment(timeoutContext).IgnoreFailure();

                        // Wait before querying for deployment updates again
                        await Task.Delay(TimeSpan.FromSeconds(Settings.QueryIntervalSeconds));
                    }

                    return BoolResult.Success;
                }));
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            var result = await Store.StartupAsync(context);

            await context.PerformNonResultOperationAsync(
                Tracer,
                () =>
                {
                    // Clear deployment directory on startup
                    FileSystem.DeleteDirectory(DeploymentDirectory.Path, DeleteOptions.All);
                    return BoolResult.SuccessTask;
                },
                caller: "ClearDeploymentDirectory").IgnoreFailure();

            if (Settings.CreateJobObject)
            {
                JobObject.SetTerminateOnCloseOnCurrentProcessJob();
            }

            if (Settings.RunInBackgroundOnStartup)
            {
                RunAsync(context).IgnoreTaskResult();
            }

            return result;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            using var releaser = await _mutex.AcquireAsync(context.Token);

            var success = BoolResult.Success;
            if (_currentRun != null)
            {
                success &= await _currentRun.ShutdownAsync(context);
            }

            DeploymentDirectory.Dispose();

            return success & await Store.ShutdownAsync(context);
        }

        public Task<BoolResult> GetDownloadAndRunDeployment(OperationContext context)
        {
            context = context.CreateNested(Tracer.Name);

            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    context.Token.ThrowIfCancellationRequested();

                    using var releaser = await _mutex.AcquireAsync(context.Token);

                    using var client = _host.CreateServiceClient();

                    // Get the launch manifest with only content id populated
                    var manifest = await GetLaunchManifestAsync(context, client).ThrowIfFailureAsync();

                    var pinContext = Store.CreatePinContext();
                    using var pinContextDisposer = new Disposable(pinContext);
                    var hashes = GetManifestHashes(manifest);
                    var pinResults = await Store.PinAsync(context, hashes, pinContext, options: null);
                    var deploymentInfo = new DeploymentInfo(manifest);

                    var downloadResult = await DownloadFilesAsync(context, client, deploymentInfo, pinContext, watchedFilesOnly: false);
                    if (!downloadResult)
                    {
                        return downloadResult;
                    }

                    // The content has changed from the active run. Get full manifest.
                    var filesExistence = CheckManifestReferencedFileExistence(manifest);
                    if (filesExistence.MissingFileCount > 0)
                    {
                        return BoolResult.WithSuccessMessage($"Skipped because manifest ingestion is not complete. Id={manifest.ContentId}. {filesExistence}");
                    }
                    else
                    {
                        Tracer.Debug(context, $"Manifest is not completed but all files are present locally. Attempting to launch process. {filesExistence}");
                    }

                    if (manifest.ContentId == _currentRun?.Manifest.ContentId && _currentRun.IsActive)
                    {
                        return BoolResult.WithSuccessMessage($"Skipped because retrieved content id match matches active run. Id={manifest.ContentId}");
                    }

                    if (_currentRun != null && _currentRun.IsActive && _currentRun.Info.HasOnlyWatchedFileUpdates(manifest))
                    {
                        var deployResult = await DeployFilesAsync(context, client, _currentRun, watchedFilesOnly: true);
                        if (deployResult.Succeeded)
                        {
                            return BoolResult.WithSuccessMessage($"Manifest only has watched file changes versus active run. Id={manifest.ContentId}");
                        }
                    }

                    var deploymentTargetDirectoryPath =
                        Settings.OverrideServiceDeploymentLocation ??
                        (DeploymentDirectory.Path / manifest.Tool.ServiceId / $"{DateTime.Now.ToReadableString()}_{manifest.ContentId}").Path;

                    if (_currentRun != null && Settings.OverrideServiceDeploymentLocation != null)
                    {
                        // Stop the currently active run
                        await _currentRun.ShutdownAsync(context).IgnoreFailure();
                        _currentRun = null;
                    }

                    var directory = new DisposableDirectory(FileSystem, new AbsolutePath(deploymentTargetDirectoryPath));
                    var deployedTool = new DeployedTool(this, deploymentInfo, directory, pinContext);

                    var result = await DeployFilesAsync(context, client, deployedTool, watchedFilesOnly: false);
                    if (!result)
                    {
                        directory.Dispose();
                        return result;
                    }

                    if (_currentRun != null)
                    {
                        // Stop the currently active run
                        await _currentRun.ShutdownAsync(context).IgnoreFailure();
                        _currentRun = null;
                    }

                    pinContextDisposer.PreventDispose = true;

                    // Start up the tool
                    var startResult = await deployedTool.StartupAsync(context);
                    if (startResult)
                    {
                        _currentRun = deployedTool;
                    }
                    else
                    {
                        await deployedTool.ShutdownAsync(context).IgnoreFailure();
                    }

                    return startResult;
                });
        }

        private static List<ContentHash> GetManifestHashes(LauncherManifest manifest)
        {
            return manifest.Deployment.Select(f => f.Value.Hash).Distinct().ToList();
        }

        private ManifestFilesExistence CheckManifestReferencedFileExistence(LauncherManifest manifest)
        {
            var result = new ManifestFilesExistence();
            var hashes = GetManifestHashes(manifest);
            foreach (var hash in hashes)
            {
                if (Store.Contains(hash, out var size))
                {
                    result.ExistingFileCount += 1;
                    result.ExistsSize += size;
                }
                else
                {
                    result.MissingFileCount += 1;
                }
            }

            return result;
        }

        private record ManifestFilesExistence
        {
            public int ExistingFileCount;
            public long ExistsSize;
            public int MissingFileCount;
        }

        private Task<Result<LauncherManifest>> GetLaunchManifestAsync(OperationContext context, IDeploymentServiceClient client)
        {
            return context.PerformOperationAsync(Tracer, async () =>
            {
                // Set the trace id
                Settings.DeploymentParameters.ContextId = context.TracingContext.TraceId;

                // Query for launcher manifest from remote service
                var manifest = await client.GetLaunchManifestAsync(context, Settings);

                // Reset ForceUpdate now that launch manifest has been retrieved
                Settings.DeploymentParameters.ForceUpdate = false;
                return Result.Success(manifest);
            });
        }

        /// <summary>
        /// Download and store a single drop to CAS
        /// </summary>
        private Task<Result<int>> DownloadFilesAsync(
            OperationContext context,
            IDeploymentServiceClient client,
            DeploymentInfo deploymentInfo,
            PinContext pinContext,
            bool watchedFilesOnly)
        {
            var manifest = deploymentInfo.Manifest;
            var pinRequest = new PinRequest(pinContext);

            return context.PerformOperationWithTimeoutAsync(Tracer, async context =>
            {
                // Stores files into CAS and populate file specs with hash and size info
                var results = await DownloadQueue.SelectAsync(
                    deploymentInfo.GetFilesToDeploy(watchedFilesOnly).GroupBy(kvp => kvp.Value.Hash),
                    async (filesByHash, index) =>
                    {
                        var fileInfo = filesByHash.First().Value;
                        var file = filesByHash.First().Key;
                        var count = filesByHash.Count();

                        context.Token.ThrowIfCancellationRequested();
                        var hash = filesByHash.Key;
                        if (pinContext.Contains(hash))
                        {
                            return Result.Success(0);
                        }

                        // Hash is not pinned. Need to download into cache
                        return await context.PerformOperationAsync(
                            Tracer,
                            async () =>
                            {
                                if (fileInfo.DownloadUrl == null)
                                {
                                    // File doesn't have download. Return count of pending files for this hash
                                    return Result.Success(count);
                                }

                                // Download the file matching the hash
                                await DownloadFileAsync(context, client, fileInfo, pinRequest, file);

                                return Result.Success(0);
                            },
                            caller: "DownloadFileAsync",
                            extraEndMessage: r => $"Hash={fileInfo.Hash}, IsPending={fileInfo.DownloadUrl == null}, FirstFile={file}, Count={count}");
                    });

                return results.FirstOrDefault(r => !r.Succeeded) ?? Result.Success(results.Sum(r => r.Value));
            },
            timeout: Settings.DeployTimeout,
            extraStartMessage: $"Id={manifest.ContentId}, Files={manifest.Deployment.Count}",
            extraEndMessage: r => $"Id={manifest.ContentId}, PendingFiles={r.GetValueOr(-1)}, Files={manifest.Deployment.Count}");
        }

        /// <summary>
        /// Download and store a single drop to CAS
        /// </summary>
        private Task<BoolResult> DeployFilesAsync(
            OperationContext context,
            IDeploymentServiceClient client,
            DeployedTool deploymentInfo,
            bool watchedFilesOnly)
        {
            var manifest = deploymentInfo.Manifest;

            return context.PerformOperationWithTimeoutAsync(Tracer, async context =>
            {
                // Stores files into CAS and populate file specs with hash and size info
                var results = await DownloadQueue.SelectAsync(
                    deploymentInfo.Info.GetFilesToDeploy(watchedFilesOnly),
                    (fileEntry, index) =>
                    {
                        var fileInfo = fileEntry.Value;
                        var file = fileEntry.Key;

                        context.Token.ThrowIfCancellationRequested();

                        return context.PerformOperationAsync(
                            Tracer,
                            async () =>
                            {
                                var hash = fileInfo.Hash;

                                // Copy the file to additional deployment locations
                                await Store.PlaceFileAsync(
                                    context,
                                    hash,
                                    deploymentInfo.Directory.Path / file,
                                    deploymentInfo.Info.IsWatchedFile(file) ? FileAccessMode.Write : FileAccessMode.ReadOnly,
                                    FileReplacementMode.ReplaceExisting,
                                    FileRealizationMode.Any,
                                    deploymentInfo.PinRequest).ThrowIfFailureAsync();

                                return BoolResult.Success;
                            },
                            caller: "DeployFileAsync",
                            extraEndMessage: r => $"Hash={fileInfo.Hash}, File={file}");
                    });

                return results.FirstOrDefault(r => !r.Succeeded) ?? BoolResult.Success;
            },
            timeout: Settings.DeployTimeout,
            extraStartMessage: $"Id={manifest.ContentId}, Files={manifest.Deployment.Count}",
            extraEndMessage: r => $"Id={manifest.ContentId}, Files={manifest.Deployment.Count}");
        }

        private Task DownloadFileAsync(OperationContext context, IDeploymentServiceClient client, FileSpec fileInfo, PinRequest pinRequest, string firstFile)
        {
            var url = new Uri(fileInfo.DownloadUrl);

            var prunedUrl = url.GetLeftPart(UriPartial.Authority);

            return context.PerformOperationAsync<BoolResult>(Tracer, async () =>
            {
                try
                {
                    var hash = fileInfo.Hash;

                    using (var downloadStream = await client.GetStreamAsync(context, fileInfo.DownloadUrl))
                    {
                        await Store.PutStreamAsync(
                            context,
                            downloadStream,
                            hash,
                            pinRequest).ThrowIfFailureAsync();
                    }
                }
                catch (Exception) when (forceUpdateOnDownloadFailure())
                {
                    // This code should never be reached since exception filter returns false
                    throw;
                }

                return BoolResult.Success;
            },
            extraStartMessage: $"Hash={fileInfo.Hash}, Size={fileInfo.Size}, Host={prunedUrl}, FirstTarget={firstFile}",
            extraEndMessage: r => $"Hash={fileInfo.Hash}, Size={fileInfo.Size}, Host={prunedUrl}, FirstTarget={firstFile}"
            ).ThrowIfFailureAsync();

            bool forceUpdateOnDownloadFailure()
            {
                // Force update of manifest on download failure to try a new proxy address if available
                Settings.DeploymentParameters.ForceUpdate = true;

                // Return false to allow exception to propagate
                return false;
            }
        }

        /// <summary>
        /// Describes location of a tool deployment with ability to run the tool
        /// </summary>
        private class DeploymentInfo
        {
            /// <summary>
            /// The launcher manifest used to create tool deployment
            /// </summary>
            public LauncherManifest Manifest { get; set; }


            private HashSet<string> WatchedFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public DeploymentInfo(LauncherManifest manifest)
            {
                Manifest = manifest;

                foreach (var watchedFile in manifest.Tool.WatchedFiles)
                {
                    WatchedFiles.Add(NormalizeFilePath(watchedFile));
                }
            }

            public IEnumerable<KeyValuePair<string, FileSpec>> GetFilesToDeploy(bool watchedFilesOnly)
            {
                if (!watchedFilesOnly)
                {
                    return Manifest.Deployment;
                }
                else
                {
                    return Manifest.Deployment.Where(kvp => IsWatchedFile(kvp.Key));
                }
            }

            public bool IsWatchedFile(string path)
            {
                return WatchedFiles.Contains(NormalizeFilePath(path));
            }

            private string NormalizeFilePath(string path)
            {
#if NET8_0_OR_GREATER
                Span<char> pathChars = stackalloc char[path.Length];
                pathChars.Replace('\\', '/');
                pathChars.Trim();
                pathChars.TrimStart('/');
                return pathChars.ToString();
#else
                return path.Replace("\\", "/").Trim().TrimStart('/');
#endif
            }

            private string Normalize(LauncherManifest manifest)
            {
                var normalizedManifest = new LauncherManifest();

                normalizedManifest.Tool = manifest.Tool;

                foreach (var entry in manifest.Deployment)
                {
                    normalizedManifest.Deployment[NormalizeFilePath(entry.Key)] = new FileSpec()
                    {
                        Hash = entry.Value.Hash
                    };
                }

                foreach (var watchedFile in manifest.Tool.WatchedFiles)
                {
                    normalizedManifest.Deployment.Remove(NormalizeFilePath(watchedFile));
                }

                return JsonUtilities.JsonSerialize(normalizedManifest);
            }

            /// <summary>
            /// Checks whether the only changes to manifest file are changes to watched files
            /// </summary>
            public bool HasOnlyWatchedFileUpdates(LauncherManifest newManifest)
            {
                if (newManifest.Tool.WatchedFiles.Count == 0)
                {
                    return false;
                }

                var normalizedToolManifest = Normalize(Manifest);
                var normalizedNewManifest = Normalize(newManifest);
                return normalizedNewManifest == normalizedToolManifest;
            }
        }

        private record Disposable(IDisposable disposable) : IDisposable
        {
            public bool PreventDispose { get; set; }

            public void Dispose()
            {
                if (!PreventDispose)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}
