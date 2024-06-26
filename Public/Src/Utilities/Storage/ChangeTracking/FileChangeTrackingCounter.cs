// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Core;

namespace BuildXL.Storage.ChangeTracking
{
    /// <summary>
    /// Counters for <see cref="FileChangeTrackingSet" />.
    /// </summary>
    public enum FileChangeTrackingCounter
    {
        /// <summary>
        /// The number of probes through tracker, which ends up in an IO call.
        /// </summary>
        FileSystemProbeCount,

        /// <summary>
        /// The number of directory membership fingerprinting that results in conflict hash.
        /// </summary>
        /// <remarks>
        /// The conflict hash indicates that the directory is enumerated more than once with different resulting fingerprints.
        /// </remarks>
        ConflictDirectoryMembershipFingerprintCount,

        /// <nodoc/>
        [CounterType(CounterType.Stopwatch)]
        TryEstablishStrongTime,

        /// <nodoc/>
        [CounterType(CounterType.Stopwatch)]
        TryTrackChangesToFileTime,

        /// <nodoc/>
        [CounterType(CounterType.Stopwatch)]
        TryTrackChangesToFileInternalTime,

        /// <nodoc/>
        [CounterType(CounterType.Stopwatch)]
        TryProbeAndTrackPathTime,

        /// <nodoc/>
        [CounterType(CounterType.Stopwatch)]
        TryEnumerateDirectoryAndTrackMembershipTime,
    }
}
