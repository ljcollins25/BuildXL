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
using System.Text;
using ContentStoreTest.Distributed.Redis;
using Azure.Storage.Blobs;
using System.Diagnostics;
using static BuildXL.Cache.ContentStore.Distributed.Test.DeploymentLauncherTests;

namespace BuildXL.Cache.ContentStore.Distributed.Test
{
    /// <summary>
    /// Test deployment to azure storage and using strictly access to storage to construct launcher manifest
    /// and deploy and run specified deployment. We deploy to azure storage here, but accessing via Azure files
    /// should be equivalent. There is no available emulator for azure files, but the operations performed by launcher (i.e. simple
    /// HEAD and GET calls) are equivalent between the two technologies.
    /// </summary>
    [Collection("Redis-based tests")]
    public class ServerlessDeploymentLauncherTests : StorageDeploymentIngesterTests
    {
        public ServerlessDeploymentLauncherTests(LocalRedisFixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
        }

        protected override Dictionary<string, StorageAccountInfo[]> CreateStorageMap()
        {
            var s1 = CreateStorageProcess();
            var s2 = CreateStorageProcess();

            return new Dictionary<string, StorageAccountInfo[]>()
            {
                {
                    "westus2",
                    new StorageAccountInfo[]
                    {
                        new(s1.ConnectionString, "container1"),
                    }
                },
                {
                    "centralus",
                    new StorageAccountInfo[]
                    {
                        new(s2.ConnectionString, "container4"),
                    }
                }
            };
        }

        protected override DeploymentIngesterConfiguration ConfigureIngester()
        {
            var result = base.ConfigureIngester();

            var tempZipDir = TestRootDirectoryPath / "tempzip";
            WriteFiles(tempZipDir, new()
            {
                { "a.txt", "A" },
            });

            return result;
        }

        public override Task TestFullDeployment()
        {
            return base.TestFullDeployment();
        }

        protected override async Task PostDeploymentVerifyAsync()
        {
            var files = WriteFiles(deploymentRoot, new()
            {
                { "rel/keys.json", KeysJson }
            });

            await StorageTargetStore.UploadFileAsync(Context, files[0], "rel/Keys.json").ShouldBeSuccess();

            string serviceUrl = "casaas://service";

            var settings = new LauncherSettings()
            {
                ServiceUrl = serviceUrl,
                RetentionSizeGb = 1,
                RunInBackgroundOnStartup = false,
                DeploymentParameters = new DeploymentParameters()
                {
                    Environment = "MyEnvRunningOnWindows",
                    ServiceVersion = "10",
                    Properties =
                    {
                        { "RunKind", "Stage2C" }
                    }
                },
                ServiceLifetimePollingIntervalSeconds = 0.01,
                DownloadConcurrency = 1,
                TargetDirectory = TestRootDirectoryPath.Path
            };

            var deploymentContainer = await StorageByAccountName.First().Value.GetContainerAsync();

            var host = new TestHost(deploymentContainer.Uri);
            var launcher = new DeploymentLauncher(
                settings,
                FileSystem,
                host);

            using var cts = new CancellationTokenSource();
            var context = new OperationContext(new Context(Logger), cts.Token);

            await launcher.StartupAsync(context).ThrowIfFailureAsync();

            await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();

            host.Process.VerifyEnvVar(TestSecretName, ExpectedStage2CTestSecretValue);

            await launcher.ShutdownAsync(context).ThrowIfFailureAsync();
        }
    }

    public class TestHost(Uri deploymentRootUri)
        : DeploymentLauncherHost(new AzureDeploymentRestClient(deploymentRootUri))
    {
        public TestProcess Process { get; set; }

        public override ILauncherProcess CreateProcess(ProcessStartInfo info)
        {
            Process = new TestProcess(info);
            return Process;
        }
    }
}

#endif
