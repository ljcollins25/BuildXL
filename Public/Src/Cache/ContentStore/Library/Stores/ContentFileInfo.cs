// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using BuildXL.Cache.ContentStore.Interfaces.Time;

namespace BuildXL.Cache.ContentStore.Stores
{
    /// <summary>
    ///     Metadata for a piece of content in the CAS.
    /// </summary>
    public class ContentFileInfo : IEquatable<ContentFileInfo>
    {
        /// <summary>
        ///     Gets its size in bytes.
        /// </summary>
        public long LogicalFileSize { get; }

        /// <summary>
        ///     Gets last time it was accessed.
        /// </summary>
        public long LastAccessedFileTimeUtc { get; private set; }

        /// <summary>
        ///     Gets last time it was accessed.
        /// </summary>
        public DateTime LastAccessedTimeUtc => DateTime.FromFileTimeUtc(LastAccessedFileTimeUtc);

        /// <summary>
        ///     Gets or sets number of known replicas.
        /// </summary>
        public int ReplicaCount { get; set; }

        /// <summary>
        /// Returns physical size of the content on disk.
        /// </summary>
        public long PhysicalFileSize { get; }

        /// <summary>
        /// Returns a total physical size of the content on disk.
        /// </summary>
        public long TotalPhysicalSize => PhysicalFileSize * ReplicaCount; 

        /// <summary>
        ///     Initializes a new instance of the <see cref="ContentFileInfo" /> class.
        /// </summary>
        public ContentFileInfo(long logicalFileSize, long lastAccessedFileTimeUtc, int replicaCount, long clusterSize)
        {
            LogicalFileSize = logicalFileSize;
            LastAccessedFileTimeUtc = lastAccessedFileTimeUtc;
            ReplicaCount = replicaCount;
            PhysicalFileSize = GetPhysicalSize(logicalFileSize, clusterSize);
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="ContentFileInfo" /> class for newly inserted content
        /// </summary>
        /// <param name="clock">Clock to use for the current time.</param>
        /// <param name="logicalFileSize">Size of the content itself (different from the physical size).</param>
        /// <param name="clusterSize">Size of each cluster.</param>
        /// <param name="replicaCount">Number of replicas.</param>
        public ContentFileInfo(IClock clock, long logicalFileSize, int replicaCount, long clusterSize)
        {
            LogicalFileSize = logicalFileSize;
            UpdateLastAccessed(clock);
            ReplicaCount = replicaCount;
            PhysicalFileSize = GetPhysicalSize(logicalFileSize, clusterSize);
        }

        /// <inheritdoc />
        public bool Equals(ContentFileInfo? other)
        {
            return other != null &&
                LogicalFileSize == other.LogicalFileSize &&
                ReplicaCount == other.ReplicaCount &&
                LastAccessedFileTimeUtc == other.LastAccessedFileTimeUtc;
        }

        /// <summary>
        ///     Updates the last accessed time to now and increments the access count.
        /// </summary>
        /// <param name="clock">Clock to use for the current time.</param>
        public void UpdateLastAccessed(IClock clock)
        {
            lock (this)
            {
                LastAccessedFileTimeUtc = clock.UtcNow.ToFileTimeUtc();
            }
        }

        /// <summary>
        ///     Updates the last accessed time to provided time.
        /// </summary>
        public void UpdateLastAccessed(DateTime dateTime)
        {
            lock (this)
            {
                var updatedFileTimeUtc = dateTime.ToFileTimeUtc();

                // Don't update LastAccessFileTimeUtc if dateTime is outdated
                if (updatedFileTimeUtc > LastAccessedFileTimeUtc)
                {
                    LastAccessedFileTimeUtc = updatedFileTimeUtc;
                }
            }
        }

        /// <summary>
        ///     Returns the physical size based on <param ref="logicalFileSize"/> and <param ref="clusterSize"/>
        /// </summary>
        public static long GetPhysicalSize(long logicalFileSize, long clusterSize)
        {
            return (logicalFileSize % clusterSize) == 0 ? logicalFileSize : (logicalFileSize / clusterSize + 1) * clusterSize;
        }
    }
}
