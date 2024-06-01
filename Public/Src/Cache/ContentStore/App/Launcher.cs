// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service;
using BuildXL.Utilities.Core.Tasks;
using CLAP;

// ReSharper disable once UnusedMember.Global
namespace BuildXL.Cache.ContentStore.App
{
    internal sealed partial class Application
    {
        /// <summary>
        /// Run the Deployment launcher verb for downloading and running deployments.
        /// </summary>
        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
        [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        [Verb(Description = "Run deployment launcher")]
        internal void Launcher
            (
            [Required, Description("Path to LauncherSettings file")] string settingsPath,
            [DefaultValue(false)] bool debug,
            [DefaultValue(false)] bool shutdown = false,
            [Description("Override url for service")] string serviceUrl = null
            )
        {
            Initialize();

            if (debug)
            {
                System.Diagnostics.Debugger.Launch();
            }

            try
            {
                var configJson = File.ReadAllText(settingsPath);

                configJson = DeploymentUtilities.Preprocess(
                    configJson,
                    HostParameters.FromEnvironment(addEnvironmentProperties: true),
                    useInlinedParameters: true);

                var settings = JsonUtilities.JsonDeserialize<LauncherApplicationSettings>(configJson);

                settings.ServiceUrl = serviceUrl ?? settings.ServiceUrl;
                settings.DeploymentParameters ??= new();

                var launcher = new DeploymentLauncher(settings, _fileSystem);

                runAsync().GetAwaiter().GetResult();
                async Task runAsync()
                {
                    var tracingContext = new Context(_logger);
                    if (shutdown)
                    {
                        var context = new OperationContext(tracingContext, _cancellationToken);
                        await launcher.LifetimeManager.GracefulShutdownServiceAsync(context, settings.LauncherServiceId).IgnoreFailure(); // The error was already been logged.
                        return;
                    }

                    
                    var host = new EnvironmentVariableHost(tracingContext);

                    if (!string.IsNullOrEmpty(settings.DeploymentParameters.AuthorizationSecretName))
                    {
                        settings.DeploymentParameters.AuthorizationSecret ??= await host.GetPlainSecretAsync(settings.DeploymentParameters.AuthorizationSecretName, _cancellationToken);
                    }
                    
                    var telemetryFieldsProvider = new HostTelemetryFieldsProvider(settings.DeploymentParameters)
                    {
                        ServiceName = "DeploymentLauncher"
                    };
                    var arguments = new LoggerFactoryArguments(tracingContext, host, settings.LoggingSettings, telemetryFieldsProvider);

                    var replacementLogger = await LoggerFactory.ReplaceLoggerAsync(arguments);
                    using (replacementLogger.DisposableToken)
                    {
                        var token = _cancellationToken;
                        var context = new OperationContext(new Context(replacementLogger.Logger), token);

                        await launcher.LifetimeManager.RunInterruptableServiceAsync(context, settings.LauncherServiceId, async token =>
                        {
                            try
                            {
                                await launcher.StartupAsync(context).ThrowIfFailureAsync();
                                using var tokenAwaitable = token.ToAwaitable();
                                await tokenAwaitable.CompletionTask;
                            }
                            finally
                            {
                                await launcher.ShutdownAsync(context).ThrowIfFailureAsync();
                            }

                            return true;
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
    }
}
