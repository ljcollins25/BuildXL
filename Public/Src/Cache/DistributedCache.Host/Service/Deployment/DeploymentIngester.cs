// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service.Deployment;
using BuildXL.Utilities.ParallelAlgorithms;

namespace BuildXL.Cache.Host.Service
{
    /// <summary>
    /// Deploys drops/files from a given deployment configuration to a CAS store and writes manifests describing contents
    /// so that subsequent process (i.e. Deployment Service) can read files and proffer deployments to clients.
    /// </summary>
    public class DeploymentIngester
    {
        #region Configuration

        /// <summary>
        /// The deployment root directory under which CAS and deployment manifests will be stored
        /// </summary>
        public AbsolutePath DeploymentRoot => Configuration.DeploymentRoot;

        /// <summary>
        /// The path to the configuration file describing urls of deployed drops/files
        /// </summary>
        public AbsolutePath DeploymentConfigurationPath => Configuration.DeploymentConfigurationPath;

        /// <summary>
        /// The path to the manifest file describing contents of deployed drops/files
        /// </summary>
        public AbsolutePath DeploymentManifestPath => Configuration.DeploymentManifestPath;

        /// <summary>
        /// The path to the source root from while files should be pulled
        /// </summary>
        public AbsolutePath SourceRoot => Configuration.SourceRoot;

        #endregion

        private OperationContext Context { get; }

        private IAbsFileSystem FileSystem => Configuration.FileSystem;

        private PinRequest PinRequest { get; set; }

        /// <summary>
        /// Content store used to store files in content addressable layout under deployment root
        /// </summary>
        private IDeploymentIngestorTargetStore Store => Configuration.Store;

        private Tracer Tracer { get; } = new Tracer(nameof(DeploymentIngester));

        /// <summary>
        /// Describes the drops specified in deployment configuration
        /// </summary>
        private Dictionary<string, DropLayout> Drops { get; } = new Dictionary<string, DropLayout>();

        private HashSet<ContentHash> PinHashes { get; } = new HashSet<ContentHash>();

        private ActionQueue ActionQueue => Configuration.ActionQueue;

        private readonly JsonPreprocessor _preprocessor = new JsonPreprocessor(new ConstraintDefinition[0], new Dictionary<string, string>());

        private DeploymentIngesterConfiguration Configuration { get; }

        /// <nodoc />
        public DeploymentIngester(
            OperationContext context,
            DeploymentIngesterConfiguration configuration)
        {
            Context = context;
            Configuration = configuration;

            configuration.TryPopulateDefaultHandlers();
        }

        /// <summary>
        /// Describes a file in drops
        /// </summary>
        private class FileSpec
        {
            public string Path { get; set; }

            public long Size { get; set; }

            public ContentHash Hash { get; set; }
        }

        /// <summary>
        /// Describes the files in a drop 
        /// </summary>
        private class DropLayout
        {
            public string Url { get; set; }
            public Uri Uri { get; set; }

            public ParsedDropUrl ParsedUrl { get; set; }

            public bool IsLoadedFromPriorManifest { get; set; }

            public List<FileSpec> Files { get; } = new List<FileSpec>();

            public string ToHeaderString()
            {
                if (ParsedUrl?.EffectiveUrl == null)
                {
                    return $"Url={Url}";
                }

                return $"Url={Url} EffectiveUrl={ParsedUrl?.EffectiveUrl}";
            }

            public override string ToString()
            {
                return $"{ToHeaderString()} FileCount={Files.Count}";
            }
        }

        /// <summary>
        /// Run the deployment runner workflow to ingest drop files into deployment root
        /// </summary>
        public Task<BoolResult> RunAsync()
        {
            return Context.PerformOperationAsync(Tracer, async () =>
            {
                var context = Context.TracingContext;

                try
                {
                    await Store.StartupAsync(Context).ThrowIfFailure();

                    // Read drop urls from deployment configuration
                    ReadAndDeployDeploymentConfiguration();

                    // Read deployment manifest to ascertain which drops have already been downloaded
                    // along with their contents
                    ReadDeploymentManifest();

                    // Download drops and store files into CAS
                    await DownloadAndStoreDropsAsync();

                    // Write the updated deployment describing files in drop
                    WriteDeploymentManifest();

                    await Store.FinalizeIngestionAsync(Context).ThrowIfFailure();
                }
                finally
                {
                    await Store.ShutdownAsync(context).ThrowIfFailure();
                }

                return BoolResult.Success;
            });
        }

        /// <summary>
        /// Read drop urls from deployment configuration
        /// </summary>
        private void ReadAndDeployDeploymentConfiguration()
        {
            Context.PerformOperation(Tracer, () =>
            {
                string text = FileSystem.ReadAllText(DeploymentConfigurationPath);
                var document = JsonDocument.Parse(text, DeploymentUtilities.ConfigurationDocumentOptions);

                var urls = document.RootElement
                    .EnumerateObject()
                    .Where(e => e.Name.StartsWith(nameof(DeploymentConfiguration.Drops)))
                    .SelectMany(d => d.Value
                        .EnumerateArray()
                        .SelectMany(e => GetUrlsFromDropElement(e)));

                foreach (var url in new[] { DeploymentUtilities.ConfigDropUri.ToString() }.Concat(urls))
                {
                    Drops[url] = ParseDropUrl(url);
                }

                return BoolResult.Success;
            },
           extraStartMessage: DeploymentConfigurationPath.ToString()).ThrowIfFailure();
        }

        private IEnumerable<string> GetUrlsFromDropElement(JsonElement e)
        {
            IEnumerable<string> getValuesWithName(string name)
            {
                return e.EnumerateObject()
                .Where(p => _preprocessor.ParseNameWithoutConstraints(p).Equals(name))
                .Select(p => p.Value.GetString());
            }

            var baseUrls = getValuesWithName(nameof(DropDeploymentConfiguration.BaseUrl));
            var relativeRoots = getValuesWithName(nameof(DropDeploymentConfiguration.RelativeRoot));
            var fullUrls = getValuesWithName(nameof(DropDeploymentConfiguration.Url));

            var dropConfiguration = new DropDeploymentConfiguration();
            foreach (var baseUrl in baseUrls)
            {
                dropConfiguration.BaseUrl = baseUrl;
                foreach (var relativeRoot in relativeRoots)
                {
                    dropConfiguration.RelativeRoot = relativeRoot;
                    yield return dropConfiguration.Url;
                }
            }

            foreach (var fullUrl in fullUrls)
            {
                yield return fullUrl;
            }
        }

        private DropLayout ParseDropUrl(string url)
        {
            return new DropLayout()
            {
                Url = url,
                Uri = new Uri(url)
            };
        }

        /// <summary>
        /// Write out updated deployment manifest
        /// </summary>
        private void WriteDeploymentManifest()
        {
            Context.PerformOperation<BoolResult>(Tracer, () =>
            {
                var deploymentManifest = new DeploymentManifest();
                foreach (var drop in Drops.Values)
                {
                    var layout = new DeploymentManifest.LayoutSpec();
                    foreach (var file in drop.Files)
                    {
                        layout[file.Path] = new DeploymentManifest.FileSpec()
                        {
                            Hash = file.Hash,
                            Size = file.Size
                        };
                    }

                    deploymentManifest.Drops[drop.Url] = layout;
                }

                var manifestText = JsonUtilities.JsonSerialize(deploymentManifest, indent: true);

                var path = DeploymentManifestPath;

                FileSystem.CreateDirectory(DeploymentManifestPath.Parent);

                // Write deployment manifest under deployment root for access by deployment service
                // NOTE: This is done as two step process to ensure file is replaced atomically.
                AtomicWriteFileText(path, manifestText);

                // Write the deployment manifest id file used for up to date check by deployment service
                AtomicWriteFileText(DeploymentUtilities.GetDeploymentManifestIdPath(DeploymentRoot), DeploymentUtilities.ComputeContentId(manifestText));
                return BoolResult.Success;
            },
            extraStartMessage: $"DropCount={Drops.Count}").ThrowIfFailure<BoolResult>();
        }

        private void AtomicWriteFileText(AbsolutePath path, string manifestText)
        {
            var tempDeploymentManifestPath = new AbsolutePath(path.Path + ".tmp");
            FileSystem.WriteAllText(tempDeploymentManifestPath, manifestText);
            FileSystem.MoveFile(tempDeploymentManifestPath, path, replaceExisting: true);
        }

        /// <summary>
        /// Read prior deployment manifest with description of drop contents
        /// </summary>
        private void ReadDeploymentManifest()
        {
            int foundDrops = 0;
            Context.PerformOperation(Tracer, () =>
            {
                if (!FileSystem.FileExists(DeploymentManifestPath))
                {
                    Tracer.Debug(Context, $"No deployment manifest found at '{DeploymentManifestPath}'");
                    return BoolResult.Success;
                }

                var manifestText = FileSystem.ReadAllText(DeploymentManifestPath);
                var deploymentManifest = DeploymentUtilities.JsonDeserialize<DeploymentManifest>(manifestText);

                foundDrops = deploymentManifest.Drops.Count;
                foreach (var dropEntry in deploymentManifest.Drops)
                {
                    if (Drops.TryGetValue(dropEntry.Key, out var layout))
                    {
                        layout.IsLoadedFromPriorManifest = true;
                        foreach (var fileEntry in dropEntry.Value)
                        {
                            var fileSpec = fileEntry.Value;
                            layout.Files.Add(new FileSpec()
                            {
                                Path = fileEntry.Key,
                                Hash = fileSpec.Hash,
                                Size = fileSpec.Size,
                            });
                        }

                        Tracer.Debug(Context, $"Loaded drop '{dropEntry.Key}' with {dropEntry.Value.Count} files");
                    }
                    else
                    {
                        Tracer.Debug(Context, $"Discarded drop '{dropEntry.Key}' with {dropEntry.Value.Count} files");
                    }
                }

                return BoolResult.Success;
            },
            messageFactory: r => $"ReadDropCount={foundDrops}").ThrowIfFailure();
        }

        /// <summary>
        /// Downloads and stores drops to CAS
        /// </summary>
        private Task DownloadAndStoreDropsAsync()
        {
            return Context.PerformOperationAsync(Tracer, async () =>
            {
                var hashes = Drops.Values.SelectMany(d => d.Files.Select(f => f.Hash)).ToList();

                var pinResults = await Store.PinAsync(Context, hashes);

                foreach (var pinResult in pinResults)
                {
                    if (pinResult.Item)
                    {
                        PinHashes.Add(hashes[pinResult.Index]);
                    }
                }

                foreach (var drop in Drops.Values)
                {
                    await DownloadAndStoreDropAsync(drop);
                }

                return BoolResult.Success;
            },
            extraStartMessage: $"DropCount={Drops.Count}").ThrowIfFailure();
        }

        /// <summary>
        /// Download and store a single drop to CAS
        /// </summary>
        private Task DownloadAndStoreDropAsync(DropLayout drop)
        {
            var context = Context.CreateNested(Tracer.Name);
            return context.PerformOperationAsync<BoolResult>(Tracer, async () =>
            {
                if (!Configuration.HandlerByScheme.TryGetValue(drop.Uri.Scheme, out var handler))
                {
                    return new BoolResult($"No handler for uri scheme: {drop.Uri.Scheme}");
                }

                 drop.ParsedUrl = handler.Parse(drop.Uri);

                // Can't skip mutable drops (i.e. normal local file drops) since the contents may have
                // changes from last ingestion
                if (!Configuration.ForceUploadContent && !drop.ParsedUrl.HasMutableContent)
                {
                    if (drop.IsLoadedFromPriorManifest && drop.Files.All(f => PinHashes.Contains(f.Hash)))
                    {
                        // If we loaded prior drop info and all drop contents are present in cache, just skip
                        return BoolResult.Success;
                    }
                }

                // Clear files since they will be repopulated below
                drop.Files.Clear();

                // Download and enumerate files associated with drop
                var files = await DownloadDropAsync(context, drop);

                // Add file specs to drop
                foreach (var file in files)
                {
                    drop.Files.Add(new FileSpec()
                    {
                        Path = file.DeployedPath.ToString()
                    });
                }

                // Stores files into CAS and populate file specs with hash and size info
                await ActionQueue.ForEachAsync(files, async (file, index) =>
                {
                    await DeployFileAsync(drop, file, index, context);
                });

                return BoolResult.Success;
            },
            extraStartMessage: drop.ToString(),
            extraEndMessage: r => drop.ToString()).ThrowIfFailure<BoolResult>();
        }

        private Task DeployFileAsync(DropLayout drop, DeploymentFile file, int index, OperationContext context)
        {
            return context.PerformOperationAsync(Tracer, async () =>
            {
                var result = await Store.PutFileAsync(context, file.SourcePath).ThrowIfFailure();

                var spec = drop.Files[index];
                spec.Hash = result.ContentHash;
                spec.Size = result.ContentSize;

                return result;
            },
            traceOperationStarted: true,
            extraStartMessage: $"Index={index} Path={file.SourcePath}",
            extraEndMessage: r => $"Index={index} Path={file.SourcePath}"
            ).ThrowIfFailureAsync();
        }

        private RelativePath GetRelativePath(AbsolutePath path, AbsolutePath parent)
        {
            if (path.Path.TryGetRelativePath(parent.Path, out var relativePath))
            {
                return new RelativePath(relativePath);
            }
            else
            {
                throw Contract.AssertFailure($"'{path}' not under expected parent path '{parent}'");
            }
        }

        private Task<IReadOnlyList<DeploymentFile>> DownloadDropAsync(OperationContext context, DropLayout drop)
        {
            var url = drop.ParsedUrl;
            return context.PerformOperationAsync(Tracer, async () =>
            {
                var files = new List<DeploymentFile>();

                var handler = url.Handler;

                var tempDirectory = FileSystem.GetTempPath() / Path.GetRandomFileName();
                FileSystem.CreateDirectory(tempDirectory);
                await handler.GetFilesAsync(context, url, tempDirectory, files).ThrowIfFailureAsync();

                return Result.Success<IReadOnlyList<DeploymentFile>>(files);
            },
            extraStartMessage: drop.ToHeaderString(),
            extraEndMessage: r => r.Succeeded ? $"{drop.ToHeaderString()} FileCount={r.Value.Count}" : drop.Url).ThrowIfFailureAsync();
        }
    }
}
