// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Utilities.Core.Tasks;

namespace BuildXL.Cache.Host.Service
{
    /// <summary>
    /// Provides implementation of <see cref="IDeploymentServiceInnerClient"/> using direct http rest operations
    /// against an azure file share or blob storage
    /// </summary>
    public class AzureDeploymentRestClient : IDeploymentServiceInnerClient, IDeploymentProcessorHost<Unit>
    {
        private readonly DeploymentProcessor<Unit> _processor;

        private readonly Uri _sasUri;
        private readonly Uri _manifestUri;

        private readonly HttpClient _client = new();

        public AzureDeploymentRestClient(Uri sasUri)
        {
            _processor = new(this);
            _sasUri = sasUri;

            _manifestUri = GetUri(DeploymentUtilities.DeploymentManifestRelativePath.Path);
        }

        public Tracer Tracer { get; } = new Tracer(nameof(AzureDeploymentRestClient));

        public void Dispose()
        {
        }

        private Uri GetUri(string relativePath)
        {
            var b = new UriBuilder(_sasUri);
            relativePath ??= "";
            relativePath = relativePath.Replace('\\', '/');
            relativePath = relativePath.TrimStart('/');
            var path = b.Path ?? "";
            var sep = path.EndsWith("/") ? "" : "/";
            b.Path = $"{path}{sep}{relativePath}";
            return b.Uri;
        }

        public Task<Unit> LoadStorageAsync(OperationContext context, ISecretsProvider secretsProvider, SecretConfiguration storageSecretInfo, string fileShare)
        {
            return Unit.VoidTask;
        }

        public Task<DownloadInfo> EnsureUploadedAndGetDownloadUrlAsync(
            OperationContext context,
            DeploymentManifest.FileSpec value,
            DeploymentConfiguration configuration,
            Unit storage)
        {
            var hash = value.Hash;
            Uri downloadUri = GetDownloadUri(hash);
            return Task.FromResult(new DownloadInfo(downloadUri.ToString()));
        }

        private Uri GetDownloadUri(ContentHash hash)
        {
            var relativePath = DeploymentUtilities.GetContentRelativePath(hash);
            var downloadUri = GetUri(relativePath.Path);
            return downloadUri;
        }

        public async Task<string> GetChangeIdAsync(OperationContext context, LauncherSettings settings)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, _manifestUri));
            response.EnsureSuccessStatusCode();
            return GetChangeId(response);
        }

        private static string GetChangeId(HttpResponseMessage response)
        {
            return response.Headers.ETag.Tag;
        }

        public async Task<LauncherManifest> GetLaunchManifestAsync(OperationContext context, LauncherSettings settings)
        {
            var manifest = await _processor.UploadFilesAndGetManifestAsync(context, settings.DeploymentParameters, waitForCompletion: true);
            return manifest;
        }

        public async Task<DeploymentManifestResult> GetManifestAsync()
        {
            var manifest = await ReadJsonAsync<DeploymentManifest>(_manifestUri, (manifest, response) =>
            {
                manifest.ChangeId = GetChangeId(response);
            });

            var configurationHash = manifest.GetDeploymentConfigurationSpec().Hash;

            var configurationUri = GetDownloadUri(configurationHash);
            var configurationJson = await ReadJsonAsync<string>(configurationUri);

            return new DeploymentManifestResult(manifest, configurationJson);
        }

        private async Task<T> ReadJsonAsync<T>(Uri uri, Action<T, HttpResponseMessage> handleResponse = null, HostParameters preprocessParameters = null)
        {
            var response = await _client.GetAsync(uri);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            if (preprocessParameters != null)
            {
                json = DeploymentUtilities.Preprocess(json, preprocessParameters);
            }

            var result = typeof(T) == typeof(string)
                ? (T)(object)json
                : JsonSerializer.Deserialize<T>(json, DeploymentUtilities.ConfigurationSerializationOptions);
            handleResponse?.Invoke(result, response);

            return result;
        }

        public Task<string> GetSecretAsync(OperationContext context, ISecretsProvider secretsProvider, SecretConfiguration secretInfo)
        {
            return secretsProvider.GetPlainSecretAsync(secretInfo.Name, context.Token);
        }

        public async Task<ISecretsProvider> GetSecretsProviderAsync(OperationContext context, string keyVaultUri, HostParameters parameters)
        {
            IReadOnlyDictionary<string, string> secrets = ImmutableDictionary<string, string>.Empty;
            if (!string.IsNullOrEmpty(keyVaultUri))
            {
                var uri = new Uri(keyVaultUri, UriKind.RelativeOrAbsolute);
                if (!uri.IsAbsoluteUri || uri.IsFile)
                {
                    var secretsFileUri = GetUri(uri.IsAbsoluteUri ? uri.LocalPath : keyVaultUri);
                    secrets = await ReadJsonAsync<CaseInsensitiveMap>(secretsFileUri, preprocessParameters: parameters);
                }

                // TODO: Query blob storage to get key map?
            }

            return new InMemorySecretsProvider(secrets);
        }

        public Task<Stream> GetStreamAsync(OperationContext context, string downloadUrl)
        {
            return _client.GetStreamAsync(downloadUrl);
        }

        public Task<string> GetProxyBaseAddressAsync(OperationContext context, DeploymentConfigurationResult configuration, HostParameters parameters)
        {
            return Task.FromResult((string)null);
        }

        public Task<string> GetProxyBaseAddress(OperationContext context, string serviceUrl, HostParameters parameters, string token)
        {
            // This should not actually be called by the launcher
            throw Contract.AssertFailure("This method should not be called.");
        }

        private class CaseInsensitiveMap() : Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
        }
    }
}
