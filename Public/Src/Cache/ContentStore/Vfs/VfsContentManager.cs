// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.ContentStore.Sessions.Internal;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;

namespace BuildXL.Cache.ContentStore.Vfs
{
    using FullPath = Interfaces.FileSystem.AbsolutePath;
    using VirtualPath = System.String;

    /// <summary>
    /// A store which virtualizes calls to an underlying content store (i.e. content will
    /// be lazily materialized using the projected file system filter driver)
    /// </summary>
    public class VfsContentManager : IDisposable
    {
        // TODO: Track stats about file materialization (i.e. how much content was hydrated)
        // On Domino side, track how much requested total requested file content size would be.

        // TODO: Allow switching between hydration on CreatePlaceholder and GetFileData

        private readonly VfsTree _tree;
        private readonly VfsCasConfiguration _configuration;
        private readonly Logger _logger;

        /// <summary>
        /// Unique integral id for files under vfs cas root
        /// </summary>
        private int _nextVfsCasTargetFileUniqueId;

        private readonly IContentSession _contentSession;
        private readonly DisposableDirectory _tempDirectory;
        private readonly PassThroughFileSystem _fileSystem;

        public VfsContentManager(Logger logger, VfsCasConfiguration configuration, VfsTree tree, IContentSession contentSession)
        {
            _logger = logger;
            _configuration = configuration;
            _tree = tree;
            _contentSession = contentSession;
            _fileSystem = new PassThroughFileSystem();
            _tempDirectory = new DisposableDirectory(_fileSystem, configuration.DataRootPath / "temp");
        }

        internal VirtualPath ToVirtualPath(FullPath path)
        {
            foreach (var mount in _configuration.VirtualizationMounts)
            {
                if (path.TryGetRelativePath(mount.Value, out var mountRelativePath))
                {
                    RelativePath relativePath = _configuration.VfsMountRelativeRoot / mount.Key / mountRelativePath;
                    return relativePath.Path;
                }
            }

            if (path.TryGetRelativePath(_configuration.VfsRootPath, out var rootRelativePath))
            {
                return rootRelativePath;
            }

            return null;
        }

        internal FullPath ToFullPath(string relativePath)
        {
            return _configuration.VfsRootPath / relativePath;
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

        public BoolResult TryCreateSymlink(AbsolutePath sourcePath, FilePlacementData data)
        {
            return _logger.PerformOperation($"SourcePath={sourcePath}, Hash={data.Hash}",
                () =>
                {
                    var index = Interlocked.Increment(ref _nextVfsCasTargetFileUniqueId);
                    var casRelativePath = VfsUtilities.CreateCasRelativePath(data, index);
                    var fullTargetPath = _configuration.VfsCasRootPath / casRelativePath;
                    var result = FileUtilities.TryCreateSymbolicLink(symLinkFileName: sourcePath.Path, targetFileName: fullTargetPath.Path, isTargetFile: true);
                    if (result.Succeeded)
                    {
                        return BoolResult.Success;
                    }
                    else
                    {
                        return new BoolResult(result.Failure.DescribeIncludingInnerFailures());
                    }
                });
        }

        internal async Task PlaceHydratedFileAsync(VirtualPath relativePath, FilePlacementData data, CancellationToken token)
        {
            var tempFilePath = _tempDirectory.CreateRandomFileName();
            var result = await _contentSession.PlaceFileAsync(
                Placeholder.Todo<Context>("Should we capture the context id of the original place file?", new Context(_logger)),
                data.Hash,
                tempFilePath,
                data.AccessMode,
                FileReplacementMode.ReplaceExisting,
                data.RealizationMode,
                token).ThrowIfFailure();

            var fullPath = ToFullPath(relativePath);

            _fileSystem.MoveFile(tempFilePath, fullPath, true);
        }

        public void Dispose()
        {
            _tempDirectory.Dispose();
        }
    }
}
