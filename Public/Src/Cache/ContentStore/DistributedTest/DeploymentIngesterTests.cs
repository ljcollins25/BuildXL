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
    public class DeploymentIngesterTests : TestBase
    {
        public OperationContext Context;

        public const string DropToken = "FAKE_DROP_TOKEN";
        public DeploymentIngesterTests(ITestOutputHelper output)
            : base(TestGlobal.Logger, output)
        {
            Context = new OperationContext(new Interfaces.Tracing.Context(Logger));
        }

        private class TestDeploymentConfig
        {
            public List<DropDeploymentConfiguration> Drops { get; } = new List<DropDeploymentConfiguration>();

            public class DropDeploymentConfiguration : Dictionary<string, string>
            {
            }
        }

        private static readonly string ConfigString = @"
{
    'Drops': [
        {
            'BaseUrl[Stage:1]': 'https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage1',
            'BaseUrl[Stage:2]': 'https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage2',

            'RelativeRoot[Tool:A]' : 'tools/toola',
            'RelativeRoot[Tool:B]' : 'app/appb',
            'RelativeRoot[Tool:C]' : 'c',


            'Url [Ring:Ring_0]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64',
            'Url [Ring:Ring_1]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop2?root=debug',
            'Url [Ring:Ring_2]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64',
            'TargetRelativePath': 'bin'
        },
        {
            'Url': 'file://Env',
        },
        {
            'TargetRelativePath': 'info',
            'Url [Stamp:ST_S1]': 'file://Files/Foo.txt',
            'Url [Stamp:ST_S2]': 'file://Env/Foo.txt',
            'Url [Stamp:ST_S3]': 'file://Stamp3',
        }
    ],
    'AzureStorageSecretInfo': { 'Name': 'myregionalStorage{Region:LA}', 'TimeToLive':'60m' },
    'SasUrlTimeToLive': '3m',
    'Tool [Environment:MyEnvRunningOnWindows]': {
        'Executable': 'bin/service.exe',
        'Arguments': [ 'myargs' ],
        'EnvironmentVariables': {
            'ConfigPath': '../Foo.txt'
        }
    }
}
".Replace("'", "\"");

        [Fact]
        [Trait("Category", "WindowsOSOnly")] // TODO: investigate why
        public async Task TestFullDeployment()
        {
            var sources = new Dictionary<string, string>()
            {
                { @"Stamp3\info.txt", "" },

                { @"Env\RootFile.json", "{ 'key1': 1, 'key2': 2 }" },
                { @"Env\Subfolder\Hello.txt", "Hello world" },
                { @"Env\Foo.txt", "Baz" },

                { @"Files\Foo.txt", "Bar" },
            };

            Dictionary<string, string> getSubDrop(Dictionary<string, string> dropContents, string root, string prefix)
            {
                return dropContents.Where(e => e.Key.StartsWith(root.Replace("/", "\\")))
                    .ToDictionary(e => e.Key.Substring((prefix ?? root).Length), e => e.Value);
            }

            Dictionary<string, string> getSourceDrop(string root, string prefix)
            {
                return getSubDrop(sources, root, prefix);
            }

            var baseUrlDrops = new Dictionary<string, Dictionary<string, string>>()
            {
                {
                    "https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage1",
                    new Dictionary<string, string>
                    {
                        { @"tools\toola\info.txt", "" },
                        { @"app\appb\subfolder\file.json", "{ 'key1': 1, 'key2': 2 }" },
                        { @"app\appb\Hello.txt", "Hello world" },
                        { @"c\Foo.txt", "Baz" },
                        { @"c\Bar.txt", "Bar" },
                    }
                },
                {
                    "https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage2",
                    new Dictionary<string, string>
                    {
                        { @"tools\toola\info.txt", "Information appears hear now" },
                        { @"app\appb\subfolder\file.json", "{ 'key1': 3, 'key2': 4 }" },
                        { @"app\appb\subfolder\newfile.json", "{ 'prop': 'this is a new file', 'key2': 4 }" },
                        { @"app\appb\Hello.txt", "Hello world" },
                        { @"c\Foo.txt", "Baz" },
                        { @"c\Bar.txt", "Bar" },
                    }
                },
            };

            var drops = new Dictionary<string, Dictionary<string, string>>()
            {
                {
                    "https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64",
                    new Dictionary<string, string>
                    {
                        { @"file1.bin", "File content 1" },
                        { @"file2.txt", "File content 2" },
                        { @"sub\file3.dll", "File content 3" }
                    }
                },
                {
                    "https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop2?root=debug",
                    new Dictionary<string, string>
                    {
                        { @"file1.bin", "File content 1" },
                        { @"file2.txt", "File content 2 changed" },
                        { @"sub\file5.dll", "File content 5" }
                    }
                },
                {
                    DeploymentUtilities.ConfigDropUri.OriginalString,
                    new Dictionary<string, string>()
                    {
                        { DeploymentUtilities.DeploymentConfigurationFileName, ConfigString }
                    }
                },
                {
                    "file://Env", getSourceDrop(@"Env\", @"Env\")
                },
                {
                    "file://Files/Foo.txt", getSourceDrop(@"Files\Foo.txt", @"Files\")
                },
                {
                    "file://Env/Foo.txt", getSourceDrop(@"Env\Foo.txt", @"Env\")
                },
                {
                    "file://Stamp3", getSourceDrop(@"Stamp3\", @"Stamp3\")
                }
            };

            var deploymentRoot = TestRootDirectoryPath / "deploy";
            var baseConfig = new DeploymentIngesterBaseConfiguration(
                SourceRoot: base.TestRootDirectoryPath / "src",
                DeploymentRoot: deploymentRoot,
                DeploymentConfigurationPath: base.TestRootDirectoryPath / "DeploymentConfiguration.json",
                FileSystem);

            var configuration = new DeploymentIngesterConfiguration(
                baseConfig,
                new FileSystemDeploymentContentStore(baseConfig, retentionSizeGb: 1));

            var ingester = new DeploymentIngester(
                Context,
                configuration);

            // Write source files
            WriteFiles(ingester.SourceRoot, sources);

            FileSystem.WriteAllText(ingester.DeploymentConfigurationPath, ConfigString);

            Dictionary<string, string> getDropContents(string dropUrl, string relativeRoot = null)
            {
                var uri = new UriBuilder(dropUrl);
                var query = uri.Query;
                uri.Query = null;

                if (relativeRoot == null && query != null)
                {
                    relativeRoot = HttpUtility.ParseQueryString(query)["root"];
                }

                return baseUrlDrops.TryGetValue(uri.Uri.ToString(), out var contents)
                    ? getSubDrop(contents, relativeRoot, prefix: "")
                    : drops[dropUrl];
            }

            var dropHandler = new FuncDeploymentIngesterUrlHander(configuration, "TestDropHandler", t =>
            {
                var dropContents = getDropContents(t.url.EffectiveUrl.ToString(), t.url.RelativeRoot);
                AbsolutePath root = t.tempDirectory / (t.url.RelativeRoot ?? "");
                WriteFiles(root, dropContents);
                return Result.SuccessTask(root);
            });

            configuration.HandlerByScheme["https"] = dropHandler;

            await ingester.RunAsync().ShouldBeSuccess();

            var manifestText = FileSystem.ReadAllText(ingester.DeploymentManifestPath);
            var deploymentManifest = JsonUtilities.JsonDeserialize<DeploymentManifest>(manifestText);

            foreach (var drop in drops)
            {
                var uri = new Uri(drop.Key);
                var expectedDropContents = drops[drop.Key];
                var layoutSpec = deploymentManifest.Drops[drop.Key];
                layoutSpec.Count.Should().Be(expectedDropContents.Count);
                foreach (var fileAndContent in expectedDropContents)
                {
                    var hash = layoutSpec[fileAndContent.Key].Hash;
                    var expectedPath = ingester.DeploymentRoot / DeploymentUtilities.GetContentRelativePath(hash);

                    var text = FileSystem.ReadAllText(expectedPath);
                    text.Should().Be(fileAndContent.Value);
                }
            }

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

        private void WriteFiles(AbsolutePath root, Dictionary<string, string> files)
        {
            foreach (var file in files)
            {
                var path = root / file.Key;
                FileSystem.CreateDirectory(path.Parent);
                FileSystem.WriteAllText(path, file.Value);
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
