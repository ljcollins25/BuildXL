// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service.Internal;
using BuildXL.Native.IO;
using BuildXL.Processes;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tasks;

namespace BuildXL.Cache.Host.Service
{
    public partial class DeploymentLauncher
    {
        /// <summary>
        /// Describes location of a tool deployment with ability to run the tool
        /// </summary>
        private class DeployedTool : StartupShutdownSlimBase, IDeployedTool
        {
            private readonly SemaphoreSlim _mutex = TaskUtilities.CreateMutex();

            private LauncherManagedProcess _runningProcess;

            /// <summary>
            /// The active process for the tool
            /// </summary>
            public ILauncherProcess RunningProcess => _runningProcess?.Process;

            /// <summary>
            /// Gets whether the tool process is running
            /// </summary>
            public bool IsActive => !RunningProcess?.HasExited ?? false;

            /// <summary>
            /// The launcher manifest used to create tool deployment
            /// </summary>
            public LauncherManifest Manifest
            {
                get => Info.Manifest;
                set => Info.Manifest = value;
            }

            /// <summary>
            /// The directory containing the tool deployment
            /// </summary>
            public DisposableDirectory Directory { get; }

            private DeploymentLauncher Launcher { get; }

            public DeploymentInfo Info { get; }

            public PinRequest PinRequest { get; set; }

            public AbsolutePath DirectoryPath => Directory.Path;

            private IDisposable _secretsExposer;

            protected override Tracer Tracer { get; } = new Tracer(nameof(DeployedTool));

            public DeployedTool(DeploymentLauncher launcher, DeploymentInfo info, DisposableDirectory directory, PinContext pinContext)
            {
                Launcher = launcher;
                Info = info;
                Directory = directory;
                PinRequest = new PinRequest(pinContext);
            }

            /// <summary>
            /// Starts the tool. Assumes tool has already been deployed.
            /// </summary>
            protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
            {
                int? processId = null;
                var tool = Manifest.Tool;
                return context.PerformOperationAsync(
                    Tracer,
                    async () =>
                    {
                        // TODO: Wait for health signal?
                        //       Or maybe process should terminate itself if its not healthy?
                        using (await _mutex.AcquireAsync(context.Token))
                        {
                            if (_runningProcess == null)
                            {
                                var executablePath = ExpandTokens(tool.Executable);

                                if (tool.ResolveToolPath)
                                {
                                    if (!Path.IsPathRooted(executablePath))
                                    {
                                        executablePath = (Directory.Path / executablePath).Path;
                                    }

                                    if (!File.Exists(executablePath))
                                    {
                                        return new BoolResult($"Executable '{executablePath}' does not exist.");
                                    }

                                    if (!FileUtilities.SetExecutePermissionIfNeeded(executablePath).Succeeded)
                                    {
                                        return new BoolResult($"Executable permissions could not be set on '{executablePath}'.");
                                    }
                                }

                                var arguments = tool.Arguments.Select(arg => QuoteArgumentIfNecessary(ExpandTokens(arg))).ToList();

                                if (tool.UseInterProcSecretsCommunication)
                                {
                                    Contract.Requires(Launcher._secretsProvider != null, "Secrets provider must be specified when using inter-process secrets communication.");

                                    var secretsVariables = tool.SecretEnvironmentVariables.ToList();
                                    var secretRequests = secretsVariables.SelectList(s => (key: s.Key, request: CreateSecretsRequest(s.Key, s.Value)));

                                    var secretsResult = await Launcher._secretsProvider.RetrieveSecretsAsync(secretRequests.Select(r => r.request).ToList(), context.Token);

                                    // Secrets may be renamed, so recreate with configured names
                                    secretsResult = secretsResult with
                                    {
                                        Secrets = secretRequests.ToDictionarySafe(r => r.key, r => secretsResult.Secrets[r.request.Name])
                                    };

                                    _secretsExposer = InterProcessSecretsCommunicator.Expose(context, secretsResult, tool.InterprocessSecretsFileName);
                                }

                                var process = Launcher._host.CreateProcess(
                                    new ProcessStartInfo()
                                    {
                                        UseShellExecute = false,
                                        FileName = executablePath,
                                        Arguments = string.Join(" ", arguments),
                                        Environment =
                                        {
                                            new ClearEntries(ShouldClear: tool.ResetEnvironmentVariables),

                                            // Launcher hashes the configuration file and computes the ConfigurationId properly manually
                                            // because the launcher manages its own configuration in a separate repo,
                                            // so we don't need to propagate the ConfigurationId from CloudBuildConfig repo.
                                            Launcher.Settings.DeploymentParameters.ToEnvironment(saveConfigurationId: false).ToAddOrSetEntries(),
                                            tool.EnvironmentVariables.ToDictionary(kvp => kvp.Key, kvp => ExpandTokens(kvp.Value)).ToAddOrSetEntries(),
                                            Launcher.LifetimeManager.GetDeployedInterruptableServiceVariables(tool.ServiceId).ToAddOrSetEntries()
                                        }
                                    });
                                _runningProcess = new LauncherManagedProcess(process, tool.ServiceId, Launcher.LifetimeManager);

                                _runningProcess.Start(context).ThrowIfFailure();
                                processId = RunningProcess.Id;
                            }

                            return BoolResult.Success;
                        }
                    },
                    traceOperationStarted: true,
                    extraStartMessage: $"ServiceId={tool.ServiceId}",
                    extraEndMessage: r => $"ProcessId={processId ?? -1}, ServiceId={tool.ServiceId}");
            }

            private RetrieveSecretsRequest CreateSecretsRequest(string key, SecretConfiguration value)
            {
                return new RetrieveSecretsRequest(value.Name ?? key, value.Kind);
            }

            public static string QuoteArgumentIfNecessary(string arg)
            {
                return arg.Contains(' ') ? $"\"{arg}\"" : arg;
            }

            public string ExpandTokens(string value)
            {
                value = ExpandToken(value, "ServiceDir", DirectoryPath.Path);
                value = ExpandToken(value, "LauncherHostDir", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                value = Environment.ExpandEnvironmentVariables(value);
                return value;
            }

            public static string ExpandToken(string value, string tokenName, string tokenValue)
            {
                if (!string.IsNullOrEmpty(tokenValue))
                {
                    return Regex.Replace(value, Regex.Escape($"%{tokenName}%"), tokenValue, RegexOptions.IgnoreCase);
                }

                return value;
            }

            /// <summary>
            /// Terminates the tool process
            /// </summary>
            protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
            {
                var tool = Manifest.Tool;

                using (await _mutex.AcquireAsync(context.Token))
                {
                    try
                    {
                        if (_runningProcess != null)
                        {
                            return await _runningProcess
                                .StopAsync(context, TimeSpan.FromSeconds(tool.ShutdownTimeoutSeconds), TimeSpan.FromSeconds(5))
                                .ThrowIfFailure();
                        }
                    }
                    finally
                    {
                        _secretsExposer?.Dispose();

                        await PinRequest.PinContext.DisposeAsync();

                        Directory.Dispose();
                    }

                    return BoolResult.Success;
                }
            }
        }
    }
}
