// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;

namespace BuildXL.Cache.ContentStore.Vfs
{
    /// <summary>
    /// A store which virtualizes calls to an underlying content store (i.e. content will
    /// be lazily materialized using the projected file system filter driver)
    /// </summary>
    public class VirtualizationRegistry
    {
        private readonly ConcurrentBigMap<AbsolutePath, ContentInfo> _contentInfoByPath 
            = new ConcurrentBigMap<AbsolutePath, ContentInfo>();

        private readonly PathTable _pathTable;

    }

    public class ContentInfo
    {
        public readonly ContentHash Hash;
        public readonly FileRealizationMode RealizationMode;
    }
}
