// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NETCOREAPP

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.Host.Service;
using BuildXL.Cache.Host.Service.Deployment;
using BuildXL.Utilities.Collections;
using ContentStoreTest.Distributed.Redis;
using ContentStoreTest.Test;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BuildXL.Cache.ContentStore.Distributed.Test
{
    [Collection("Redis-based tests")]
    public class StorageDeploymentIngesterTests : DeploymentIngesterTestsBase
    {
        protected readonly LocalRedisFixture _fixture;
        protected Dictionary<string, StorageAccountInfo> StorageByAccountName;
        protected StorageDeploymentTargetStore StorageTargetStore;

        private readonly List<AzuriteStorageProcess> _storageProcesses = new List<AzuriteStorageProcess>();

        public StorageDeploymentIngesterTests(LocalRedisFixture fixture, ITestOutputHelper output)
            : base(output)
        {
            _fixture = fixture;
        }

        protected virtual Dictionary<string, StorageAccountInfo[]> CreateStorageMap()
        {
            var s1 = CreateStorageProcess();
            var s2 = CreateStorageProcess();
            var s3 = CreateStorageProcess();

            return new Dictionary<string, StorageAccountInfo[]>()
            {
                {
                    "westus2",
                    new StorageAccountInfo[]
                    {
                        new(s1.ConnectionString, "container1"),
                        new(s1.ConnectionString, "container2") { UseSas = false },
                        new(s1.ConnectionString, "container3"),
                    }
                },
                {
                    "centralus",
                    new StorageAccountInfo[]
                    {
                        new(s2.ConnectionString, "container4"),
                        new(s2.ConnectionString, "container5") { UseSas = false },
                    }
                },
                {
                    "eastus2",
                    new StorageAccountInfo[]
                    {
                        new(s3.ConnectionString, "container6"),
                        new(s3.ConnectionString, "container7"),
                    }
                }
            };
        }

        protected AzuriteStorageProcess CreateStorageProcess()
        {
            var process = AzuriteStorageProcess.CreateAndStartEmpty(_fixture, TestGlobal.Logger);
            _storageProcesses.Add(process);
            return process;
        }

        public override async Task TestFullDeployment()
        {
            try
            {
                StorageByAccountName ??= CreateStorageMap()
                    .SelectMany(kvp => kvp.Value.Select((account, index) => account with { VirtualAccountName = $"{kvp.Key}_{account.ContainerName}", Region = kvp.Key }))
                    .ToDictionary(a => a.VirtualAccountName);

                await base.TestFullDeployment();

                await PostDeploymentVerifyAsync();
            }
            finally
            {
                foreach (var process in _storageProcesses)
                {
                    process.Dispose();
                }
            }
        }

        protected virtual Task PostDeploymentVerifyAsync() => Task.CompletedTask;

        protected override DeploymentIngesterConfiguration ConfigureIngester()
        {
            if (StorageByAccountName.Count == 0)
            {
                return base.ConfigureIngester();
            }

            StorageTargetStore = new StorageDeploymentTargetStore(new(
                baseConfig,
                new StorageIngesterConfiguration()
                {
                    StorageAccountsByRegion = StorageByAccountName.Values.GroupBy(a => a.Region).ToDictionary(e => e.Key, e => e.Select(a => a.VirtualAccountName).ToArray()),
                    ContentContainerName = "testcontainer"
                }));

            StorageTargetStore.OverrideGetContainer = t =>
            {
                return StorageByAccountName[t.accountName].GetContainerAsync();
            };

            return new DeploymentIngesterConfiguration(
                baseConfig,
                StorageTargetStore);
        }

        protected override async Task VerifyContentAsync(ContentHash hash, string expectedContent, string deploymentPath)
        {
            foreach (var account in StorageByAccountName.Values)
            {
                var blob = account.Container.GetBlobClient(DeploymentUtilities.GetContentRelativePath(hash).ToString());
                var content = await blob.DownloadContentAsync();
                content.Value.Details.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(expectedContent));
                if (expectedContent.Length > 0)
                {
                    var text = content.Value.Content.ToString();
                    text.Should().Be(expectedContent);
                }
            }
        }

        protected record StorageAccountInfo(string ConnectionString, string ContainerName)
        {
            public string Region { get; set; }
            public string VirtualAccountName { get; set; }

            // Set this to try against real storage.
            public static string OverrideConnectionString { get; } = null;

            public bool UseSas { get; set; } = true;

            public string ConnectionString { get; } = OverrideConnectionString ?? ConnectionString;

            // The current Azurite version we use supports up to this version
            public BlobContainerClient Container { get; } = new BlobContainerClient(ConnectionString, ContainerName, new BlobClientOptions(BlobClientOptions.ServiceVersion.V2020_02_10));

            public async Task<BlobContainerClient> GetContainerAsync()
            {
                await Container.CreateIfNotExistsAsync();

                if (!UseSas)
                {
                    return Container;
                }

                var uri = Container.GenerateSasUri(Azure.Storage.Sas.BlobContainerSasPermissions.All, DateTimeOffset.Now.AddDays(1));
                return new BlobContainerClient(uri, null);
            }
        }
    }
}

#endif
