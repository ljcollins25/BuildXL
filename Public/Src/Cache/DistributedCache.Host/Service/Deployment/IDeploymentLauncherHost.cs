// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Configuration;

namespace BuildXL.Cache.Host.Service
{
    /// <summary>
    /// Host for providing ability to launch processes and contact deployment service
    /// </summary>
    public interface IDeploymentLauncherHost
    {
        /// <summary>
        /// Creates an unstarted process using the given start info.
        /// </summary>
        ILauncherProcess CreateProcess(ProcessStartInfo info);

        /// <summary>
        /// Creates a client for communicating with deployment service
        /// </summary>
        IDeploymentServiceClient CreateServiceClient();
    }

    public interface IDeploymentServiceInnerClient : IDeploymentServiceClient
    {
        /// <summary>
        /// Quickly query the change id which indicates if the result of <see cref="GetLaunchManifestAsync"/>
        /// may have changed
        /// </summary>
        /// <param name="settings"></param>
        /// <returns></returns>
        Task<string> GetChangeIdAsync(OperationContext context, LauncherSettings settings);
    }

    /// <summary>
    /// Client for communicating with deployment service
    /// </summary>
    public interface IDeploymentServiceClient : IDisposable
    {
        /// <summary>
        /// Retrieves launch manifest
        /// </summary>
        Task<LauncherManifest> GetLaunchManifestAsync(OperationContext context, LauncherSettings settings);

        /// <summary>
        /// Retrieves the proxy address for the given machine
        /// </summary>
        Task<string> GetProxyBaseAddress(OperationContext context, string serviceUrl, HostParameters parameters, string token);

        /// <summary>
        /// Retrieves stream for given file
        /// </summary>
        Task<Stream> GetStreamAsync(OperationContext context, string downloadUrl);
    }

    /// <summary>
    /// Represents a light-weight wrapper around launched system process.
    /// </summary>
    public interface ILauncherProcess
    {
        /// <summary>
        /// Starts the process
        /// </summary>
        void Start(OperationContext context);

        /// <summary>
        /// Event triggered when process exits
        /// </summary>
        event Action Exited;

        /// <summary>
        /// Terminates the process
        /// </summary>
        void Kill(OperationContext context);

        /// <summary>
        /// The exit code of the process
        /// </summary>
        int ExitCode { get; }

        /// <summary>
        /// The exit time of the process, null if the process is still running.
        /// </summary>
        DateTime? ExitTime { get; }

        /// <summary>
        /// The id of the process
        /// </summary>
        int Id { get; }

        /// <summary>
        /// Indicates if the process has exited
        /// </summary>
        bool HasExited { get; }

        /// <summary>
        /// Blocks the current thread until the process exits.
        /// </summary>
        bool WaitForExit(TimeSpan timeout);
    }

    /// <summary>
    /// Represents a tool deployed and launched by the <see cref="DeploymentLauncher"/>
    /// </summary>
    public interface IDeployedTool
    {
        /// <summary>
        /// The running system process (if any)
        /// </summary>
        ILauncherProcess RunningProcess { get; }

        /// <summary>
        /// Indicates whether the process is running
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// The manifest used to launch the process
        /// </summary>
        LauncherManifest Manifest { get; }

        /// <summary>
        /// The directory under which the deployment is layed out
        /// </summary>
        AbsolutePath DirectoryPath { get; }
    }
}
