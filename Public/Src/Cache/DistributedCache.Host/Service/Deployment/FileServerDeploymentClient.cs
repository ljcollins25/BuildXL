// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Net;
using System.Net.Http;
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
    /// against an azure file share, blob storage, and other standard static file servers
    /// </summary>
    public class FileServerDeploymentClient : IDeploymentServiceInnerClient, IDeploymentProcessorHost<Unit>
    {
        public Tracer Tracer { get; } = new Tracer(nameof(FileServerDeploymentClient));

        private readonly DeploymentProcessor<Unit> _processor;
        private readonly Uri _sasUri;
        private readonly Uri _manifestUri;
        private readonly Uri _manifestIdUri;

        private readonly ClientWrapper _client = new();

        public FileServerDeploymentClient(Uri uri)
        {
            _processor = new(this);

            var rootUriBuilder = new UriBuilder(uri);

            if (string.IsNullOrEmpty(rootUriBuilder.Fragment))
            {
                rootUriBuilder.Path = rootUriBuilder.Path.Substring(0, rootUriBuilder.Path.LastIndexOf('/'));
                _sasUri = rootUriBuilder.Uri;
                _manifestUri = uri;
            }
            else
            {
                var manifestPath = rootUriBuilder.Fragment.TrimStart('#');
                rootUriBuilder.Fragment = null;
                _sasUri = rootUriBuilder.Uri;
                _manifestUri = GetUri(manifestPath);
            }

            var manifestIdUri = new UriBuilder(_manifestUri);
            var extension = Path.GetExtension(manifestIdUri.Path);
            manifestIdUri.Path = manifestIdUri.Path.Substring(0, manifestIdUri.Path.Length - extension.Length) + DeploymentUtilities.DeploymentManifestIdSuffix;

            _manifestIdUri = manifestIdUri.Uri;
        }

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

        public Task<string> GetChangeIdAsync(OperationContext context, LauncherSettings settings)
        {
            return GetChangeIdAsync();
        }

        private Task<string> GetChangeIdAsync()
        {
            return ReadJsonAsync<string>(_manifestIdUri);
        }

        public async Task<LauncherManifest> GetLaunchManifestAsync(OperationContext context, LauncherSettings settings)
        {
            var manifest = await _processor.UploadFilesAndGetManifestAsync(context, settings.DeploymentParameters, waitForCompletion: true);
            return manifest;
        }

        public async Task<DeploymentManifestResult> GetManifestAsync()
        {
            var changeId = await GetChangeIdAsync();

            var manifest = await ReadJsonAsync<DeploymentManifest>(_manifestUri, (manifest, response) =>
            {
                manifest.ChangeId = changeId;
            });

            var configurationHash = manifest.GetDeploymentConfigurationSpec().Hash;

            var configurationUri = GetDownloadUri(configurationHash);
            var configurationJson = await ReadJsonAsync<string>(configurationUri);

            return new DeploymentManifestResult(manifest, configurationJson);
        }

        private async Task<T> ReadJsonAsync<T>(Uri uri, Action<T, HttpResponseMessage> handleResponse = null, HostParameters preprocessParameters = null)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri));
            response.EnsureSuccessStatusCode();

            using var content = response.Content;
            var json = await content.ReadAsStringAsync();
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
            IReadOnlyDictionary<string, string> secrets = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(keyVaultUri))
            {
                var uri = new Uri(keyVaultUri, UriKind.RelativeOrAbsolute);
                if (!uri.IsAbsoluteUri)
                {
                    uri = GetUri(uri.LocalPath);
                }

                secrets = await ReadJsonAsync<CaseInsensitiveMap>(uri, preprocessParameters: parameters);

                // TODO: Query blob storage to get key map?
            }

            return new InMemorySecretsProvider(secrets);
        }

        public async Task<Stream> GetStreamAsync(OperationContext context, string downloadUrl)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, downloadUrl));
            return await response.Content.ReadAsStreamAsync();
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

        private class ClientWrapper
        {
            private readonly HttpClient _client = new HttpClient();

            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
            {
                if (request.RequestUri.IsFile)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    var fileInfo = new FileInfo(request.RequestUri.LocalPath);
                    var etag = fileInfo.LastWriteTimeUtc.ToReadableString();
                    response.Headers.ETag = new($@"""{etag}""");
                    if (request.Method != HttpMethod.Head)
                    {
                        Contract.Assert(request.Method == HttpMethod.Get);
                        var stream = File.OpenRead(fileInfo.FullName);
                        response.Content = new StreamContent(stream);
                    }

                    return response;
                }

                return await _client.SendAsync(request);
            }
        }

        private class CaseInsensitiveMap() : Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
        }
    }
}