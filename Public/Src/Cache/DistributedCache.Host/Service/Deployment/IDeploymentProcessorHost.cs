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
    public interface IDeploymentProcessorHost<TStorage>
    {
        Tracer Tracer { get; }

        Task<DeploymentManifestResult> GetManifestAsync();

        Task<ISecretsProvider> GetSecretsProviderAsync(OperationContext context, string keyVaultUri, HostParameters parameters);

        Task<TStorage> LoadStorageAsync(OperationContext context, ISecretsProvider secretsProvider, SecretConfiguration storageSecretInfo, string fileShare);

        Task<string> GetSecretAsync(OperationContext context, ISecretsProvider secretsProvider, SecretConfiguration secretInfo);

        Task<DownloadInfo> EnsureUploadedAndGetDownloadUrlAsync(OperationContext context, FileSpec value, DeploymentConfiguration configuration, TStorage storage);

        Task<string> GetProxyBaseAddressAsync(OperationContext context, DeploymentConfigurationResult configuration, HostParameters parameters);
    }

    public record DeploymentManifestResult(DeploymentManifest Manifest, string ConfigurationJson);

    public record struct DeploymentConfigurationResult(DeploymentConfiguration Configuration, DeploymentManifest Manifest, string ContentId, HostParameters AugmentedParameters);

    public class DownloadInfo
    {
        public string DownloadUrl { get; }
        public string AccessToken { get; }

        public DownloadInfo(string downloadUrl)
        {
            DownloadUrl = downloadUrl;
            AccessToken = ContentHash.Random().ToHex();
        }

        internal string GetUrl(Context context, ContentHash hash, [MaybeNull] string proxyBaseAddress)
        {
            if (proxyBaseAddress == null)
            {
                return DownloadUrl;
            }

            return DeploymentProxyService.GetContentUrl(context, baseAddress: proxyBaseAddress, hash: hash, accessToken: AccessToken);
        }
    }
}
