// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NETCOREAPP

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using BuildXL.Cache.ContentStore.InterfacesTest.Time;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service;
using BuildXL.Launcher.Server;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using ContentStoreTest.Test;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;
using RelativePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.RelativePath;
using BuildXL.Cache.Host.Service.Deployment;
using BuildXL.Cache.ContentStore.Distributed.Utilities;

namespace BuildXL.Cache.ContentStore.Distributed.Test
{
    public class DeploymentIngesterTests : DeploymentIngesterTestsBase
    {
        public DeploymentIngesterTests(ITestOutputHelper output)
            : base(output)
        {
        }

        public override async Task TestFullDeployment()
        {
            await base.TestFullDeployment();

            var clock = new MemoryClock();
            var deploymentService = new DeploymentService(new DeploymentServiceConfiguration(), deploymentRoot, _ => new TestSecretsProvider(), clock);

            BoxRef<Task> uploadFileCompletion = Task.CompletedTask;

            deploymentService.OverrideCreateCentralStorage = t => new DeploymentTestCentralStorage(t.storageSecretName)
            {
                UploadFileCompletion = uploadFileCompletion
            };

            await verifyLaunchManifestAsync(new DeploymentParameters()
            {
                Stamp = "ST_S3",
                Ring = "Ring_1",
                Properties =
                    {
                        { "Stage", "1" }
                    }
            },
                new HashSet<(string targetPath, string drop)>()
                {
                    ("bin", "https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop2?root=debug"),
                    ("", "file://Env"),
                    ("info", "file://Stamp3"),
                });

            await verifyLaunchManifestAsync(new DeploymentParameters()
            {
                Environment = "MyEnvRunningOnWindows",
                Properties =
                    {
                        { "Stage", "2" },
                        { "Tool", "A" }
                    }
            },
                new HashSet<(string targetPath, string drop)>()
                {
                    ("bin", "https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage2?root=tools/toola"),
                    ("", "file://Env"),
                });

            async Task<LauncherManifest> verifyLaunchManifestAsync(DeploymentParameters parameters, HashSet<(string targetPath, string drop)> expectedDrops)
            {
                var launchManifest = await deploymentService.UploadFilesAndGetManifestAsync(Context, parameters, waitForCompletion: true);

                var expectedDeploymentPathToHashMap = new Dictionary<string, ContentHash>();

                launchManifest.Drops.Count.Should().Be(expectedDrops.Count);
                foreach (var drop in launchManifest.Drops)
                {
                    var targetRelativePath = drop.TargetRelativePath ?? string.Empty;
                    expectedDrops.Should().Contain((targetRelativePath, drop.Url));

                    var dropSpec = deploymentManifest.Drops[drop.Url];
                    foreach (var dropFile in getDropContents(drop.Url))
                    {
                        expectedDeploymentPathToHashMap[Path.Combine(targetRelativePath, dropFile.Key)] = dropSpec[dropFile.Key].Hash;
                    }
                }

                launchManifest.Deployment.Count.Should().Be(expectedDeploymentPathToHashMap.Count);

                foreach (var file in launchManifest.Deployment)
                {
                    var expectedHash = expectedDeploymentPathToHashMap[file.Key];
                    file.Value.Hash.Should().Be(expectedHash);
                }

                return launchManifest;
            }
        }

        private class DeploymentTestCentralStorage : CentralStorage
        {
            public BoxRef<Task> UploadFileCompletion { get; set; } = Task.CompletedTask;

            protected override Tracer Tracer { get; } = new Tracer(nameof(DeploymentTestCentralStorage));

            public override bool SupportsSasUrls => true;

            private ConcurrentDictionary<string, AsyncLazy<string>> SasUrls { get; } = new ConcurrentDictionary<string, AsyncLazy<string>>();

            private string Name { get; }

            public DeploymentTestCentralStorage(string name)
            {
                Name = name;
            }

            protected override async Task<Result<string>> TryGetSasUrlCore(OperationContext context, string storageId, DateTime expiry)
            {
                if (SasUrls.TryGetValue(storageId, out var lazySasUrl) && lazySasUrl.GetValueAsync().IsCompleted)
                {
                    return await lazySasUrl.GetValueAsync();
                }

                return new ErrorResult("Blob is not present in storage");
            }

            protected override Task<BoolResult> TouchBlobCoreAsync(OperationContext context, AbsolutePath file, string storageId, bool isUploader, bool isImmutable)
            {
                throw new NotImplementedException();
            }

            protected override Task<BoolResult> TryGetFileCoreAsync(OperationContext context, string storageId, AbsolutePath targetFilePath, bool isImmutable)
            {
                throw new NotImplementedException();
            }

            protected override async Task<Result<string>> UploadFileCoreAsync(OperationContext context, AbsolutePath file, string name, bool garbageCollect = false)
            {
                var lazySasUrl = SasUrls.GetOrAdd(name, new AsyncLazy<string>(async () =>
                {
                    await UploadFileCompletion.Value;
                    return $"https://{Name}.azure.blob.com?blobName={name}&token={Guid.NewGuid()}&file={Uri.EscapeDataString(file.Path)}";
                }));

                await lazySasUrl.GetValueAsync();

                return name;
            }
        }

        private class TestSecretsProvider : ISecretsProvider
        {
            public Task<RetrievedSecrets> RetrieveSecretsAsync(List<RetrieveSecretsRequest> requests, CancellationToken token)
            {
                var secrets = new Dictionary<string, Secret>();

                foreach (var request in requests)
                {
                    if (request.Kind == SecretKind.PlainText)
                    {
                        secrets.Add(request.Name, new PlainTextSecret($"https://{request.Name}.azure.blob.com/{Guid.NewGuid()}"));
                    }
                    else
                    {
                        request.Kind.Should().Be(SecretKind.SasToken);
                        secrets.Add(
                            request.Name, 
                            new UpdatingSasToken(
                                new SasToken(
                                    storageAccount: $"https://{request.Name}.azure.blob.com/",
                                    resourcePath: "ResourcePath",
                                    token: Guid.NewGuid().ToString())));
                    }
                }

                return Task.FromResult(new RetrievedSecrets(secrets));
            }
        }
    }
}

#endif
