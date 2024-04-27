// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using BuildXL.Cache.ContentStore.Distributed;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.Host.Configuration;
using BuildXL.Cache.Host.Service.Deployment;
using BuildXL.Cache.MemoizationStore.Interfaces;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.ParallelAlgorithms;
using static BuildXL.Cache.Host.Configuration.DeploymentManifest;
//using static BuildXL.Cache.Host.Service.DeploymentUtilities;

namespace BuildXL.Cache.Host.Service
{
    public interface IDeploymentIngestorTargetStore : IStartupShutdownSlim
    {
        Task<IEnumerable<Indexed<bool>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> hashes);

        Task<PutResult> PutFileAsync(OperationContext context, AbsolutePath sourcePath);
    }
}
