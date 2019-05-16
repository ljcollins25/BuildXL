// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;

namespace BuildXL.Cache.ContentStore.Sessions.Internal
{
    /// <summary>
    /// Represents access local store for a distributed content store.
    /// </summary>
    public interface ILocalFileSystemContentSession
    {
        /// <summary>
        /// <see cref="IReadOnlyContentSession.PlaceFileAsync(Context, IReadOnlyList{ContentHashWithPath}, FileAccessMode, FileReplacementMode, FileRealizationMode, CancellationToken, UrgencyHint)"/>
        /// </summary>
        Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceLocalFilesAsync(
            Context context,
            IReadOnlyList<ContentHashWithPath> hashesWithPaths,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            CancellationToken cts);

        /// <summary>
        /// <see cref="IReadOnlyContentSession.PlaceFileAsync(Context, ContentHash, AbsolutePath, FileAccessMode, FileReplacementMode, FileRealizationMode, CancellationToken, UrgencyHint)"/>
        /// </summary>
        Task<PlaceFileResult> PlaceLocalFileAsync(
            Context context,
            ContentHash contentHash,
            AbsolutePath path,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            CancellationToken cts,
            UrgencyHint urgencyHint = UrgencyHint.Nominal);
    }
}
