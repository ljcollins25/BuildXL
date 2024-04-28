// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using BuildXL.Cache.Host.Configuration;
using Xunit;
using ContentStoreTest.Test;
using BuildXL.Cache.Host.Service;
using Xunit.Abstractions;
#if SERVERLESS

using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using FluentAssertions;
using BuildXL.Cache.ContentStore.Service;
using BuildXL.Utilities.Core.Tasks;
using System.Text.Json;
using System.Runtime.Serialization;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using System.Diagnostics.ContractsLight;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using System.Linq;
using BuildXL.Cache.Host.Service.Internal;
using System.Reflection;
using Test.BuildXL.TestUtilities.Xunit;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using ContentStoreTest.Distributed.Redis;

namespace BuildXL.Cache.ContentStore.Distributed.Test;

using TestProcess = DeploymentLauncherTests.TestProcess;

[Collection("Redis-based tests")]
public partial class ServerlessDeploymentLauncherTests : TestBase
{
    public const string ConfigurationPathEnvironmentVariableName = "ConfigurationPath";

    private readonly LocalRedisFixture _fixture;

    public ServerlessDeploymentLauncherTests(LocalRedisFixture fixture, ITestOutputHelper output)
        : base(TestGlobal.Logger, output)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TestDeployAndRun()
    {
        var host = new TestHost();

        string serviceUrl = "casaas://service";

        var settings = new LauncherSettings()
        {
            ServiceUrl = serviceUrl,
            RetentionSizeGb = 1,
            RunInBackgroundOnStartup = false,
            DeploymentParameters = new DeploymentParameters()
            {
                ServiceVersion = "10"
            },
            ServiceLifetimePollingIntervalSeconds = 0.01,
            DownloadConcurrency = 1,
            TargetDirectory = TestRootDirectoryPath.Path
        };

        var executableRelativePath = @"bin\casaas.exe";
        var serviceId = "testcasaas";
        var firstRunExecutableContent = "This is the content of casaas.exe for run 1";

        var watchedConfigPath = "config/toolconfig.json";

        var toolConfiguration = new ServiceLaunchConfiguration()
        {
            ServiceId = serviceId,
            WatchedFiles = new[]
                {
                        // Changing path case and separator to verify path is normalized
                        // for categorizing watched files
                        watchedConfigPath.Replace('/', '\\').ToUpper()
                    },
            Arguments = new[]
                {
                        "arg1",
                        "arg2",
                        "arg3 with spaces"
                    },
            EnvironmentVariables =
                    {
                        { "hello", "world" },
                        { "foo", "bar" },
                        { ConfigurationPathEnvironmentVariableName, $"%ServiceDir%/{watchedConfigPath}" }
                    },
            Executable = @"bin\casaas.exe",
            ShutdownTimeoutSeconds = 60,
        };

        var configContent1 = "Config content 1";
        var pendingFileSpec = host.TestClient.AddContent("pending file content", pending: true);
        var manifest = new LauncherManifestWithExtraMembers()
        {
            ContentId = "Deployment 1",
            Deployment = new DeploymentManifest.LayoutSpec()
                {
                    { executableRelativePath, host.TestClient.AddContent(firstRunExecutableContent) },
                    { @"bin\lib.dll", host.TestClient.AddContent("This is the content of lib.dll") },
                    { watchedConfigPath, host.TestClient.AddContent(configContent1) },
                    { "bin/pending.json", pendingFileSpec },
                },
            Tool = new ServiceLaunchConfiguration()
            {
                ServiceId = serviceId,
                WatchedFiles = new[]
                {
                        // Changing path case and separator to verify path is normalized
                        // for categorizing watched files
                        watchedConfigPath.Replace('/', '\\').ToUpper()
                    },
                Arguments = new[]
                {
                        "arg1",
                        "arg2",
                        "arg3 with spaces"
                    },
                EnvironmentVariables =
                    {
                        { "hello", "world" },
                        { "foo", "bar" },
                        { ConfigurationPathEnvironmentVariableName, $"%ServiceDir%/{watchedConfigPath}" }
                    },
                Executable = @"bin\casaas.exe",
                ShutdownTimeoutSeconds = 60,
            }
        };

        host.TestClient.GetManifest = launcherSettings =>
        {
            //  Use JSON serialization and deserialization to clone manifest
            // Also tests JSON roundtripping
            var manifestText = JsonUtilities.JsonSerialize(manifest);
            return JsonUtilities.JsonDeserialize<LauncherManifest>(manifestText);
        };

        var launcher = new DeploymentLauncher(settings, FileSystem, host);

        using var cts = new CancellationTokenSource();
        var context = new OperationContext(new Context(Logger), cts.Token);

        await launcher.StartupAsync(context).ThrowIfFailureAsync();

        await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();

        // Incomplete manifest does not launch process, but should download all available content
        launcher.CurrentRun.Should().BeNull();
        manifest.Deployment.All(e => e.Value.DownloadUrl == null || launcher.Store.Contains(e.Value.Hash));

        manifest.IsComplete = true;
        pendingFileSpec.Finish();
        await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();

        // Test the process is launched.
        (launcher.CurrentRun?.RunningProcess).Should().NotBeNull();
        launcher.CurrentRun.IsActive.Should().BeTrue();
        var testProcess1 = (TestProcess)launcher.CurrentRun.RunningProcess;
        testProcess1.IsRunningService.Should().BeTrue();

        // Verify executable, arguments, environment variables
        ReadAllText(testProcess1.StartInfo.FileName).Should().Be(firstRunExecutableContent);
        testProcess1.StartInfo.Arguments.Should().Be("arg1 arg2 \"arg3 with spaces\"");
        testProcess1.StartInfo.Environment["hello"].Should().Be("world");
        testProcess1.StartInfo.Environment["foo"].Should().Be("bar");

        // Verify that same manifest does not launch new process
        await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();
        (launcher.CurrentRun?.RunningProcess).Should().Be(testProcess1);
        testProcess1.IsRunningService.Should().BeTrue();

        // Modify manifest and mark as incomplete to signify case where all files have not yet been
        // replicated to region-specific storage
        manifest.IsComplete = false;
        manifest.ContentId = "Deployment 2";
        var secondRunExecutableContent = "This is the content of casaas.exe for run 2";
        pendingFileSpec = host.TestClient.AddContent(secondRunExecutableContent, pending: true);
        manifest.Deployment[executableRelativePath] = pendingFileSpec;

        // Verify that incomplete updated manifest does not launch new process
        await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();
        ReadAllText(testProcess1.StartInfo.FileName).Should().Be(firstRunExecutableContent);
        (launcher.CurrentRun?.RunningProcess).Should().Be(testProcess1);
        testProcess1.IsRunningService.Should().BeTrue();

        // Verify that complete updated manifest launches new process
        manifest.IsComplete = true;
        pendingFileSpec.Finish();
        await launcher.GetDownloadAndRunDeployment(context).ShouldBeSuccess();
        (launcher.CurrentRun?.RunningProcess).Should().NotBe(testProcess1);
        testProcess1.IsRunningService.Should().BeFalse();
        var testProcess2 = (TestProcess)launcher.CurrentRun.RunningProcess;
        testProcess2.IsRunningService.Should().BeTrue();

        // Verify updated casaas.exe file
        ReadAllText(testProcess2.StartInfo.FileName).Should().Be(secondRunExecutableContent);

        // Verify shutdown launches new processes
        await launcher.LifetimeManager.GracefulShutdownServiceAsync(context, serviceId).ShouldBeSuccess();

        await launcher.ShutdownAsync(context).ThrowIfFailureAsync();
    }

    private string ReadAllText(string path)
    {
        return File.ReadAllText(path);
    }

    public class TestHost : DeploymentLauncherHost, ISecretsProvider
    {
        public TestProcess Process { get; set; }

        public Dictionary<(string, SecretKind), Secret> Secrets = new Dictionary<(string, SecretKind), Secret>();

        public ILauncherProcess CreateProcess(ProcessStartInfo info)
        {
            Process = new TestProcess(info);
            return Process;
        }

        public IDeploymentServiceClient CreateServiceClient()
        {
            return TestClient;
        }

        public Task<RetrievedSecrets> RetrieveSecretsAsync(List<RetrieveSecretsRequest> requests, CancellationToken token)
        {
            return Task.FromResult(new RetrievedSecrets(requests.ToDictionary(r => r.Name, r => Secrets[(r.Name, r.Kind)])));
        }
    }
}

#endif