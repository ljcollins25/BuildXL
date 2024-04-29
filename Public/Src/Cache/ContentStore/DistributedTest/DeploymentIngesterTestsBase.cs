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
    public abstract partial class DeploymentIngesterTestsBase : TestBase
    {
        public OperationContext Context;

        protected readonly AbsolutePath deploymentRoot;
        protected readonly DeploymentIngesterBaseConfiguration baseConfig;
        protected DeploymentIngesterConfiguration configuration;
        protected DeploymentIngester ingester;
        protected FuncDeploymentIngesterUrlHander dropHandler;
        protected DeploymentManifest deploymentManifest;

        public const string DropToken = "FAKE_DROP_TOKEN";
        public DeploymentIngesterTestsBase(ITestOutputHelper output)
            : base(TestGlobal.Logger, output)
        {
            Context = new OperationContext(new Interfaces.Tracing.Context(Logger));

            InitializeLayout();

            deploymentRoot = TestRootDirectoryPath / "deploy";

            baseConfig = new DeploymentIngesterBaseConfiguration(
                SourceRoot: base.TestRootDirectoryPath / "src",
                DeploymentRoot: deploymentRoot,
                DeploymentConfigurationPath: base.TestRootDirectoryPath / "DeploymentConfiguration.json",
                FileSystem);
        }
        [Fact]
        [Trait("Category", "WindowsOSOnly")] // TODO: investigate why
        public virtual Task TestFullDeployment()
        {
            return RunIngestorAndVerifyAsync();
        }

        protected virtual DeploymentIngesterConfiguration ConfigureIngester()
        {
            return new DeploymentIngesterConfiguration(
                baseConfig,
                new FileSystemDeploymentContentStore(baseConfig, retentionSizeGb: 1));
        }

        public async Task RunIngestorAndVerifyAsync()
        {
            var configuration = ConfigureIngester();

            ingester = new DeploymentIngester(
                Context,
                configuration);

            // Write source files
            WriteFiles(ingester.SourceRoot, sources);

            FileSystem.WriteAllText(ingester.DeploymentConfigurationPath, ConfigString);

            dropHandler = new FuncDeploymentIngesterUrlHander(configuration, "TestDropHandler", t =>
            {
                var dropContents = getDropContents(t.url.EffectiveUrl.ToString(), t.url.RelativeRoot);
                AbsolutePath root = t.tempDirectory / (t.url.RelativeRoot ?? "");
                WriteFiles(root, dropContents);
                return Result.SuccessTask(root);
            });

            configuration.HandlerByScheme["https"] = dropHandler;

            await ingester.RunAsync().ShouldBeSuccess();

            var manifestText = FileSystem.ReadAllText(ingester.DeploymentManifestPath);
            deploymentManifest = JsonUtilities.JsonDeserialize<DeploymentManifest>(manifestText);

            foreach (var drop in drops)
            {
                var uri = new Uri(drop.Key);
                var expectedDropContents = drops[drop.Key];
                var layoutSpec = deploymentManifest.Drops[drop.Key];
                layoutSpec.Count.Should().Be(expectedDropContents.Count);
                foreach (var fileAndContent in expectedDropContents)
                {
                    var hash = layoutSpec[fileAndContent.Key].Hash;

                    await VerifyContentAsync(hash, expectedContent: fileAndContent.Value, deploymentPath: fileAndContent.Key);
                }
            }
        }

        protected virtual Task VerifyContentAsync(ContentHash hash, string expectedContent, string deploymentPath)
        {
            var expectedPath = ingester.DeploymentRoot / DeploymentUtilities.GetContentRelativePath(hash);

            var text = FileSystem.ReadAllText(expectedPath);
            text.Should().Be(expectedContent);

            return Task.CompletedTask;
        }

        protected IReadOnlyList<AbsolutePath> WriteFiles(AbsolutePath root, Dictionary<string, string> files)
        {
            var paths = new List<AbsolutePath>();
            foreach (var file in files)
            {
                var path = root / file.Key;
                paths.Add(path);
                FileSystem.CreateDirectory(path.Parent);
                FileSystem.WriteAllText(path, file.Value);
            }

            return paths;
        }

        protected class TestSecretsProvider : ISecretsProvider
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
