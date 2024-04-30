// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Tracing.Internal;

namespace BuildXL.Cache.Host.Service
{
    public interface IDeploymentIngestorTargetStore : IStartupShutdownSlim
    {
        /// <summary>
        /// Checks if hashes from loaded deployment manifest exist in the store
        /// </summary>
        Task<IEnumerable<Indexed<bool>>> PinAsync(OperationContext context, IReadOnlyList<ContentHash> hashes);

        Task<PutResult> PutFileAsync(OperationContext context, AbsolutePath sourcePath);

        Task<BoolResult> FinalizeIngestionAsync(OperationContext context);
    }
}
