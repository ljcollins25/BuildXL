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
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;
using BuildXL.Utilities.Tracing;

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

        public CounterCollection<VfsCounters> Counters { get; } = new CounterCollection<VfsCounters>();

        private readonly VfsTree _tree;
        private readonly VfsCasConfiguration _configuration;
        private readonly Logger _logger;

        private readonly Tracer _tracer = new Tracer(nameof(VfsContentManager));

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

        public FullPath ToFullPath(string relativePath)
        {
            return _configuration.VfsRootPath / relativePath;
        }

        public BoolResult TryCreateSymlink(OperationContext context, AbsolutePath sourcePath, FilePlacementData data)
        {
            return context.PerformOperation(
                _tracer,
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
                },
                extraStartMessage: $"SourcePath={sourcePath}, Hash={data.Hash}",
                counter: Counters[VfsCounters.TryCreateSymlink]);
        }

        public Task PlaceHydratedFileAsync(VirtualPath relativePath, FilePlacementData data, CancellationToken token)
        {
            var context = new OperationContext(new Context(_logger), token);
            return context.PerformOperationAsync(
                _tracer,
                async () =>
                {
                    var tempFilePath = _tempDirectory.CreateRandomFileName();
                    var result = await _contentSession.PlaceFileAsync(
                        context,
                        data.Hash,
                        tempFilePath,
                        data.AccessMode,
                        FileReplacementMode.ReplaceExisting,
                        data.RealizationMode,
                        token).ThrowIfFailure();

                    var fullPath = ToFullPath(relativePath);

                    _fileSystem.MoveFile(tempFilePath, fullPath, true);

                    Counters[VfsCounters.PlaceHydratedFileBytes].Add(result.FileSize);
                    Counters[VfsCounters.PlaceHydratedFileUnknownSizeCount].Increment();
                    return BoolResult.Success;
                },
                extraStartMessage: $"RelativePath={relativePath}, Hash={data.Hash}",
                counter: Counters[VfsCounters.PlaceHydratedFile]);
        }

        public void Dispose()
        {
            _tempDirectory.Dispose();
        }
    }
}
