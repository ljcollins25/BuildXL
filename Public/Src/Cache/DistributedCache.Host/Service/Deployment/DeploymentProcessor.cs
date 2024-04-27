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
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.MemoizationStore.Interfaces;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.ParallelAlgorithms;
using static BuildXL.Cache.Host.Configuration.DeploymentManifest;
using static BuildXL.Cache.Host.Service.DeploymentUtilities;

namespace BuildXL.Cache.Host.Service
{

    /// <summary>
    /// Service used ensure deployments are uploaded to target storage accounts and provide manifest for with download urls and tools to launch
    /// </summary>
    public record DeploymentProcessor<TStorage>(IDeploymentProcessorHost<TStorage> Host)
    {
        private Tracer Tracer => Host.Tracer;

        /// <summary>
        /// Gets the deployment configuration based on the manifest, preprocesses it, and returns the deserialized value
        /// </summary>
        public async Task<DeploymentConfigurationResult> ReadDeploymentConfigurationAsync(HostParameters parameters)
        {
            var (manifest, configJson) = await Host.GetManifestAsync();

            var preprocessor = DeploymentUtilities.GetHostJsonPreprocessor(parameters);

            var preprocessedConfigJson = preprocessor.Preprocess(configJson);
            var contentId = ComputeShortContentId(preprocessedConfigJson);

            var config = JsonUtilities.JsonDeserialize<DeploymentConfiguration>(preprocessedConfigJson);

            return new(config, manifest, contentId);
        }

        /// <summary>
        /// Uploads the deployment files to the target storage account and returns the launcher manifest for the given deployment parameters
        /// </summary>
        public Task<LauncherManifest> UploadFilesAndGetManifestAsync(OperationContext context, DeploymentParameters parameters, bool waitForCompletion)
        {
            int pendingFiles = 0;
            int totalFiles = 0;
            int completedFiles = 0;
            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var configResult = await ReadDeploymentConfigurationAsync(parameters);
                    var (deployConfig, deploymentManifest, contentId) = configResult;
                    var resultManifest = new LauncherManifest()
                    {
                        ContentId = contentId,
                        DeploymentManifestChangeId = deploymentManifest.ChangeId
                    };

                    var uploadTasks = new List<Task<(string targetPath, FileSpec spec)>>();

                    resultManifest.Tool = deployConfig.Tool;
                    resultManifest.Drops = deployConfig.Drops.Where(d => d.Url != null).ToList();

                    var secretsProvider = await Host.GetSecretsProviderAsync(context, deployConfig.KeyVaultUri, parameters);

                    if (deployConfig.Tool?.SecretEnvironmentVariables != null)
                    {
                        // Populate environment variables from secrets.
                        foreach (var secretEnvironmentVariable in deployConfig.Tool.SecretEnvironmentVariables)
                        {
                            // Default to using environment variable name as the secret name
                            secretEnvironmentVariable.Value.Name ??= secretEnvironmentVariable.Key;

                            var secretValue = await Host.GetSecretAsync(context, secretsProvider, secretEnvironmentVariable.Value);
                            resultManifest.Tool.EnvironmentVariables[secretEnvironmentVariable.Key] = secretValue;

                            if (secretEnvironmentVariable.Value.Kind == SecretKind.SasToken)
                            {
                                // Currently, all sas tokens are assumed to be storage secrets
                                // This is passed so that EnvironmentVariableHost can interpret the secret
                                // as a connection string and create a sas token on demand
                                resultManifest.Tool.EnvironmentVariables[$"{secretEnvironmentVariable.Key}_ResourceType"] = "storagekey";
                            }
                        }

                        var secretsContentId = ComputeShortContentId(JsonSerialize(resultManifest.Tool.EnvironmentVariables));

                        // NOTE: We append the content id so that we can distinguish purely secrets changes in logging
                        resultManifest.ContentId += $"_{secretsContentId}";
                    }

                    var storage = await Host.LoadStorageAsync(context, secretsProvider, deployConfig.AzureStorageSecretInfo, deployConfig.AzureFileShareName);

                    var proxyBaseAddress = await Host.GetProxyBaseAddressAsync(context, configResult, parameters);

                    var filesAndTargetPaths = resultManifest.Drops
                        .Where(drop => drop.Url != null)
                        .SelectMany(drop => deploymentManifest.Drops[drop.Url]
                            .Select(fileEntry => (fileSpec: fileEntry.Value, targetPath: Path.Combine(drop.TargetRelativePath ?? string.Empty, fileEntry.Key))))
                        .ToList();

                    if (deployConfig.Proxy != null)
                    {
                        // If proxy is enabled, add deployment configuration file to deployment so it can be read by
                        // deployment proxy service
                        filesAndTargetPaths.Add((
                            deploymentManifest.GetDeploymentConfigurationSpec(),
                            deployConfig.Proxy.TargetRelativePath));
                    }

                    foreach ((var fileSpec, var targetPath) in filesAndTargetPaths)
                    {
                        resultManifest.Deployment[targetPath] = fileSpec;

                        // Queue file for deployment
                        uploadTasks.Add(ensureUploadedAndGetEntry());

                        async Task<(string targetPath, FileSpec entry)> ensureUploadedAndGetEntry()
                        {
                            var downloadInfo = parameters.GetContentInfoOnly
                                ? null
                                : await Host.EnsureUploadedAndGetDownloadUrlAsync(context, fileSpec, deployConfig, storage);

                            var downloadUrl = downloadInfo?.GetUrl(context, hash: fileSpec.Hash, proxyBaseAddress: proxyBaseAddress);

                            // Compute and record path in final layout
                            return (targetPath, new FileSpec()
                            {
                                Hash = fileSpec.Hash,
                                Size = fileSpec.Size,
                                DownloadUrl = downloadUrl
                            });
                        }
                    }

                    var deploymentContentId = ComputeShortContentId(JsonSerialize(resultManifest.Deployment));

                    // NOTE: We append the content id so that we can distinguish purely secrets changes in logging
                    resultManifest.ContentId += $"_{deploymentContentId}";

                    var uploadCompletion = Task.WhenAll(uploadTasks);
                    if (waitForCompletion)
                    {
                        await uploadCompletion;
                    }
                    else
                    {
                        uploadCompletion.FireAndForget(context);
                    }

                    foreach (var uploadTask in uploadTasks)
                    {
                        totalFiles++;
                        if (uploadTask.IsCompleted)
                        {
                            completedFiles++;
                            var entry = await uploadTask;
                            resultManifest.Deployment[entry.targetPath] = entry.spec;
                        }
                        else
                        {
                            pendingFiles++;
                        }
                    }

                    resultManifest.IsComplete = pendingFiles == 0;

                    return Result.Success(resultManifest);
                },
                extraStartMessage: $"Machine={parameters.Machine} Stamp={parameters.Stamp} Wait={waitForCompletion}",
                extraEndMessage: r => $"Machine={parameters.Machine} Stamp={parameters.Stamp} Id=[{r.GetValueOrDefault()?.ContentId}] Drops={r.GetValueOrDefault()?.Drops.Count ?? 0} Files[Total={totalFiles}, Pending={pendingFiles}, Completed={completedFiles}] Wait={waitForCompletion}"
                ).ThrowIfFailureAsync();
        }

        private static string ComputeShortContentId(string value)
        {
            return DeploymentUtilities.ComputeContentId(value).Substring(0, 8);
        }
    }
}
