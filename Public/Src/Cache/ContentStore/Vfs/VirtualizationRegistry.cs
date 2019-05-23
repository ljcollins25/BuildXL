// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.FileSystem;
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
    using VirtualPath = System.String;
    using FullPath = Interfaces.FileSystem.AbsolutePath;

    /// <summary>
    /// A store which virtualizes calls to an underlying content store (i.e. content will
    /// be lazily materialized using the projected file system filter driver)
    /// </summary>
    public class VirtualizationRegistry
    {
        // TODO: Track stats about file materialization (i.e. how much content was hydrated)
        // On Domino side, track how much requested total requested file content size would be.

        // TODO: Allow switching between hydration on CreatePlaceholder and GetFileData

        private readonly VfsTree _tree;

        private IContentSession FullSession;
        private ILocalFileSystemContentSession LocalOnlySession;
        private bool eagerlyPlaceLocallyAvailableFiles;
        private DisposableDirectory _tempDirectory;
        private PassThroughFileSystem _fileSystem;

        internal VirtualPath ToVirtualPath(Interfaces.FileSystem.AbsolutePath path)
        {
            throw new NotImplementedException();
        }

        internal VirtualPath ToVirtualPath(string relativePath)
        {
            throw new NotImplementedException();
        }

        internal FullPath ToFullPath(string relativePath)
        {
            throw new NotImplementedException();
        }

        internal async Task<PlaceFileResult> TryPlaceFileAsync(
            OperationContext context,
            FullPath path,
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

            if (eagerlyPlaceLocallyAvailableFiles && LocalOnlySession != null)
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

            _tree.AddFileNode(virtualPath, DateTime.UtcNow, contentHash, realizationMode, accessMode);
            return new PlaceFileResult(GetPlaceResultCode(realizationMode, accessMode));
        }

        private PlaceFileResult.ResultCode GetPlaceResultCode(FileRealizationMode realizationMode, FileAccessMode accessMode)
        {
            if (realizationMode == FileRealizationMode.Copy
                || realizationMode == FileRealizationMode.CopyNoVerify
                || accessMode == FileAccessMode.Write)
            {
                return PlaceFileResult.ResultCode.PlacedWithCopy;
            }

            return PlaceFileResult.ResultCode.PlacedWithHardLink;
        }

        internal PlaceFileResult CanPlaceFile(
            VirtualPath virtualPath,
            FileReplacementMode replacementMode)
        {
            throw new NotImplementedException();
        }

        internal async Task PlaceVirtualFileAsync(VirtualPath relativePath, VfsFileNode node, CancellationToken token)
        {
            var tempFilePath = _tempDirectory.CreateRandomFileName();
            var result = await FullSession.PlaceFileAsync(
                Placeholder.Todo<Context>("Should we capture the context id of the original place file?"),
                node.Hash,
                tempFilePath,
                node.AccessMode,
                FileReplacementMode.ReplaceExisting,
                node.RealizationMode,
                token).ThrowIfFailure();

            var fullPath = ToFullPath(relativePath);

            _fileSystem.MoveFile(tempFilePath, fullPath, true);
        }
    }
}
