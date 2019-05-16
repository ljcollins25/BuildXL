// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;

namespace BuildXL.Cache.ContentStore.Vfs
{
    using VirtualPath = Utilities.AbsolutePath;
    using AbsPath = Interfaces.FileSystem.AbsolutePath;

    /// <summary>
    /// A store which virtualizes calls to an underlying content store (i.e. content will
    /// be lazily materialized using the projected file system filter driver)
    /// </summary>
    public class VirtualizationRegistry
    {
        // TODO: Track stats about file materialization (i.e. how much content was hydrated)
        // On Domino side, track how much requested total requested file content size would be.

        private readonly ConcurrentBigMap<VirtualPath, ContentInfo> _contentInfoByPath
            = new ConcurrentBigMap<VirtualPath, ContentInfo>();

        private readonly PathTable _pathTable;
        private readonly BigBuffer<PathExistence> _existenceBuffer;

        private IContentSession FullSession;
        private ILocalFileSystemContentSession LocalOnlySession;
        private bool eagerlyPlaceLocallyAvailableFiles;

        internal VirtualPath ToVirtualPath(Interfaces.FileSystem.AbsolutePath path)
        {
            throw new NotImplementedException();
        }

        internal VirtualPath ToVirtualPath(string relativePath)
        {
            throw new NotImplementedException();
        }

        internal void MarkFileExistence(VirtualPath path)
        {
            var index = path.Value.Index;
            _existenceBuffer.Initialize(index + 1, initializeSequentially: true);
            _existenceBuffer[index] = PathExistence.ExistsAsFile;

            while (true)
            {
                path = path.GetParent(_pathTable);
                if (path.IsValid && _existenceBuffer[path.Value.Index] == PathExistence.Nonexistent)
                {
                    _existenceBuffer[path.Value.Index] = PathExistence.ExistsAsDirectory;
                }
                else
                {
                    break;
                }
            }
        }

        public IEnumerable<(string name, PathExistence existence)> GetChildren(string relativePath)
        {
            
        }

        public IEnumerable<(string name, PathExistence existence)> GetChildren(VirtualPath path)
        {
            foreach (var item in _pathTable.EnumerateImmediateChildren(path.Value))
            {
                var existence = GetExistence(item);
            }
        }

        private PathExistence GetExistence(HierarchicalNameId item)
        {
            if (_existenceBuffer.Capacity >= item.Index)
            {

            }
            throw new NotImplementedException();
        }

        internal async Task<PlaceFileResult> TryPlaceFileAsync(
            OperationContext context,
            AbsPath path,
            ContentHash contentHash,
            FileAccessMode accessMode,
            FileReplacementMode replacementMode,
            FileRealizationMode realizationMode,
            UrgencyHint urgencyHint)
        {
            // TODO: If path isn't under virtualized mount, we should
            // create a hard link or symlink to a virtualized CAS directory under virtualization root

            var virtualPath = ToVirtualPath(path);

            var result = CanPlaceFile(virtualPath, replacementMode);
            if (!result.Succeeded)
            {
                return result;
            }

            if (eagerlyPlaceLocallyAvailableFiles)
            {
                result = await LocalOnlySession.PlaceLocalFileAsync(
                    context,
                    contentHash,
                    path,
                    accessMode,
                    replacementMode,
                    realizationMode,
                    context.Token,
                    urgencyHint);

                if (result.Code != PlaceFileResult.ResultCode.NotPlacedContentNotFound)
                {
                    return result;
                }
            }

            return PlaceVirtualFile(virtualPath, accessMode, realizationMode);
        }

        internal PlaceFileResult CanPlaceFile(
            VirtualPath virtualPath,
            FileReplacementMode replacementMode)
        {
            throw new NotImplementedException();
        }

        internal PlaceFileResult PlaceVirtualFile(
            VirtualPath virtualPath,
            FileAccessMode accessMode,
            FileRealizationMode realizationMode)
        {
            MarkFileExistence(virtualPath);
            throw new NotImplementedException();
        }

        internal bool TryCreateHardlink(VirtualPath virtualPath)
        {

        }

        internal bool TryGetVirtualFile(string relativePath, out VirtualFileBase virtualFile)
        {

        }

        internal bool TryGetVirtualItem(string relativePath, out VirtualItem virtualFile)
        {

        }

        internal VirtualFileBase TryGetVirtualFile()
        {
            throw new NotImplementedException();
        }

        internal Task<OpenStreamResult> OpenFileAsync(VirtualPath virtualPath)
        {
            // 1. Get content hash for path
            // 1a. If content not registered return content not found
            // 1b. Else call open stream

            var hash = Placeholder.Todo<ContentHash>("Get content hash at path");

            throw new NotImplementedException();
        }

        public class VirtualFile
        {
            public VirtualizationRegistry Overlay;
            public ContentInfo ContentInfo;
        }
    }

    public abstract class VirtualItem
    {
        public abstract bool IsDirectory { get; }
        public abstract IEnumerable<VirtualPath> Children { get; }
    }

    public abstract class VirtualFileBase
    {
        public ContentInfo Info;
        public IContentSession Session;

        public Task<bool> TryCreateHardlinkAsync()
        {
            // Don't allow hardlink creation if file has realization mode which is not hardlink
            // Call PlaceFileAsync2
        }

        public Task<OpenStreamResult> TryOpenStreamAsync()
        {

        }
    }

    public class ContentInfo
    {
        public readonly ContentHash Hash;
        public readonly FileRealizationMode RealizationMode;
        public readonly FileAccessMode AccessMode;
    }

}
