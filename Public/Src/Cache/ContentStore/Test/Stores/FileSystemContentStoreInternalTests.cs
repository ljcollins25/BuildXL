// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Exceptions;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.InterfacesTest.FileSystem;
using BuildXL.Cache.ContentStore.InterfacesTest.Results;
using BuildXL.Cache.ContentStore.InterfacesTest.Time;
using BuildXL.Cache.ContentStore.InterfacesTest.Utils;
using BuildXL.Cache.ContentStore.Utils;
using ContentStoreTest.Test;
using FluentAssertions;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using Xunit.Abstractions;
using static BuildXL.Cache.ContentStore.Stores.FileSystemContentStoreInternalChecker;
using BuildXL.Cache.ContentStore.Tracing;

namespace ContentStoreTest.Stores
{
    public sealed class FileSystemContentStoreInternalTests : ContentStoreInternalTests<TestFileSystemContentStoreInternal>
    {
        private static readonly MemoryClock Clock = new MemoryClock();
        private static readonly ContentStoreConfiguration Config = ContentStoreConfiguration.CreateWithMaxSizeQuotaMB(1);

        public FileSystemContentStoreInternalTests(ITestOutputHelper output)
            : base(() => new MemoryFileSystem(Clock), TestGlobal.Logger, output)
        {
        }

        protected override void CorruptContent(TestFileSystemContentStoreInternal store, ContentHash contentHash)
        {
            store.CorruptContent(contentHash);
        }

        protected override TestFileSystemContentStoreInternal CreateStore(DisposableDirectory testDirectory)
        {
            return new TestFileSystemContentStoreInternal(FileSystem, Clock, testDirectory.Path, Config);
        }

        [Fact]
        public async Task OpenEmptyStreamShouldWorkIfCalledMultipleTimes()
        {
            using (var testDirectory = new DisposableDirectory(FileSystem))
            {
                var context = new Context(Logger);

                var store = CreateStore(testDirectory);
                var emptyHash = VsoHashInfo.Instance.EmptyHash;

                var openStreamResult = await store.OpenStreamAsync(context, emptyHash, pinRequest: null).ShouldBeSuccess();
                // Normal use case, "using" the stream.
                using (openStreamResult.Stream)
                {
                }

                // Even though the stream was closed, we should be able to get an empty stream again
                await store.OpenStreamAsync(context, emptyHash, pinRequest: null).ShouldBeSuccess();
            }
        }

        // TODO: Flaky, sometimes breaks in memoryFS with an NRE
        // [Fact]  
        public async Task TurningOffDirectoryCreationDoesNotCreateDirForPlace()
        {
            using var testDirectory = new DisposableDirectory(FileSystem);
            var context = new Context(Logger);

            var config = ContentStoreConfiguration.CreateWithMaxSizeQuotaMB(1);
            var settings = new ContentStoreSettings
            {
                AssumeCallerCreatesDirectoryForPlace = true,
            };
            var store = new TestFileSystemContentStoreInternal(FileSystem, Clock, testDirectory.Path, Config, settings: settings);

            var emptyHash = VsoHashInfo.Instance.EmptyHash;

            // Expect success - place via hardlink in the already-created temp dir.
            await store.PutFileAsync(
                context,
                testDirectory.CreateRandomFileName(),
                FileRealizationMode.HardLink,
                emptyHash)
                .ShouldBeSuccess();

            // Expect failure - parent dir does not exist, should fail with inability to create link.
            var nonexistentSubDirFile = testDirectory.Path / "nonexistent" / "nofile";
            var putResult = await store.PutFileAsync(
                context,
                nonexistentSubDirFile,
                FileRealizationMode.HardLink,
                emptyHash)
                .ShouldBeError();
        }

        [Fact]
        public void PathWithTildaShouldNotCauseArgumentException()
        {
            var path = PathGeneratorUtilities.GetAbsolutePath("e", @".BuildXLCache\Shared\VSO0\364\~DE-1");

            var context = new Context(Logger);
            var tracer = new Tracer(string.Empty);

            FileSystemContentStoreInternal.TryGetHashFromPath(context, tracer, new AbsolutePath(path), out _).Should().BeFalse();
        }

        [Fact]
        public async Task TestReconstruction()
        {
            using (var testDirectory = new DisposableDirectory(FileSystem))
            {
                var context = new Context(Logger);

                // Create a store with random content
                // Shut down the store correctly
                var store1 = CreateStore(testDirectory);
                await store1.StartupAsync(context).ShouldBeSuccess();
                var putResult = await store1.PutRandomAsync(context, ValueSize).ShouldBeSuccess();

                await store1.ShutdownAsync(context).ShouldBeSuccess();

                // Recreate a store and assert that the content is present
                // Put additional content
                var store2 = CreateStore(testDirectory);
                await store2.StartupAsync(context).ShouldBeSuccess();
                var putResult2 = await store2.PutRandomAsync(context, ValueSize).ShouldBeSuccess();
                // The first content should be in the second store.
                await store2.OpenStreamAsync(context, putResult.ContentHash, null).ShouldBeSuccess();

                // Creating a third store without shutting down the second one.
                var store3 = CreateStore(testDirectory);
                await store3.StartupAsync(context).ShouldBeSuccess();
                // The content from the first and the second stores should be available.
                await store3.OpenStreamAsync(context, putResult.ContentHash, null).ShouldBeSuccess();
                await store3.OpenStreamAsync(context, putResult2.ContentHash, null).ShouldBeSuccess();
            }
        }

        [Fact]
        public async Task TestSelfCheckAsync()
        {
            var mockDistributedLocationStore = new MockDistributedLocationStore();

            using (var testDirectory = new DisposableDirectory(FileSystem))
            {
                var context = new Context(Logger);

                var store = CreateStore(testDirectory, Settings, mockDistributedLocationStore);

                await store.StartupAsync(context).ShouldBeSuccess();

                var result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();
                result.Value.TotalProcessedFiles.Should().Be(0, "TotalProcessedFiles should be 0 for an empty store.");

                // Running self check, but it should be up-to-date
                result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();
                result.Value.TotalProcessedFiles.Should().Be(-1, "SelfCheck should be skipped due to up-to-date check.");

                // Now we're going to mess up with the content by putting the file and changing it on disk.
                var putResult = await store.PutRandomAsync(context, ValueSize).ShouldBeSuccess();

                var currentSize = store.ContentDirectorySize();

                var pathInCache = store.GetPrimaryPathFor(putResult.ContentHash);
                FileSystem.WriteAllText(pathInCache, "Definitely wrong content");

                // Moving the time forward to make self check is not up-to-date.
                Clock.UtcNow = Clock.UtcNow + TimeSpan.FromDays(2);

                result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();

                result.Value.InvalidFiles.Should().Be(1);

                // An invalid file should be removed from:

                // 1. File system
                FileSystem.FileExists(pathInCache).Should().BeFalse("The store should delete the file with invalid content.");

                // 2. Content directory
                store.ContentDirectorySize().Should().Be(currentSize - ValueSize);

                // 3. Quota Keeper
                store.QuotaKeeperSize().Should().Be(currentSize - ValueSize);

                // 4. The content should be removed from distributed store.
                mockDistributedLocationStore.UnregisteredHashes.Should().NotBeNullOrEmpty();
            }
        }

        [Fact]
        public async Task TestSelfCheckIncrementality()
        {
            // This test checks that an incremental state is saved if self check operation takes too much time.
            // (the test relies on SelfCheckBatchLimit instead of time though.)
            using (var testDirectory = new DisposableDirectory(FileSystem))
            {
                var context = new Context(Logger);

                var settings = new SelfCheckSettings()
                               {
                                   InvalidFilesLimit = 1,
                                   // If SelfCheckEnabled is false then self check is not run within startup.
                                   StartSelfCheckInStartup = false,
                               };

                var store = CreateStore(testDirectory, ContentStoreSettings(settings));

                await store.StartupAsync(context).ShouldBeSuccess();

                var putResult0 = await store.PutRandomAsync(context, ValueSize).ShouldBeSuccess();
                var putResult1 = await store.PutRandomAsync(context, ValueSize).ShouldBeSuccess();
                var putResult2 = await store.PutRandomAsync(context, ValueSize).ShouldBeSuccess();

                var hashes = new List<ContentHash> {putResult0.ContentHash, putResult1.ContentHash, putResult2.ContentHash}.OrderBy(h => h).ToList();

                // Explicitly making the second hash incorrect keeping the first one as valid one.
                var pathInCache = store.GetPrimaryPathFor(hashes[1]);
                FileSystem.WriteAllText(pathInCache, "Definitely wrong content");

                var pathInCache2 = store.GetPrimaryPathFor(hashes[2]);
                FileSystem.WriteAllText(pathInCache2, "Definitely wrong content");

                // Moving the time forward to make self check is not up-to-date.
                Clock.UtcNow = Clock.UtcNow + TimeSpan.FromDays(2);

                var result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();
                // The self check should stop after the limit of 1 hash is reached.
                result.Value.InvalidFiles.Should().Be(1);
                // Because the first hash is correct, we should've enumerate 2 entries because only the second one is wrong.
                result.Value.TotalProcessedFiles.Should().Be(2);

                // Now the self check should continue from the previous step
                result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();
                result.Value.InvalidFiles.Should().Be(1);
                result.Value.TotalProcessedFiles.Should().Be(1, "Because of incrementality we should check only one file.");

                result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();
                result.Value.InvalidFiles.Should().Be(0);
                result.Value.TotalProcessedFiles.Should().Be(-1, "Self check procedure should be skipped due to up-to-date check.");
            }
        }

        [Fact]
        public async Task TestSelfCheckWithNewEpoch()
        {
            // This test checks that an incremental state is saved if self check operation takes too much time.
            // (the test relies on SelfCheckBatchLimit instead of time though.)
            using (var testDirectory = new DisposableDirectory(FileSystem))
            {
                var context = new Context(Logger);

                var settings = new SelfCheckSettings() { Epoch = "E1", StartSelfCheckInStartup = false};

                //
                // Using store with original settings
                //
                var store = CreateStore(testDirectory, ContentStoreSettings(settings));

                await store.StartupAsync(context).ShouldBeSuccess();
                var result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();

                var putResult = await store.PutRandomAsync(context, ValueSize).ShouldBeSuccess();
                var pathInCache = store.GetPrimaryPathFor(putResult.ContentHash);
                FileSystem.WriteAllText(pathInCache, "Definitely wrong content");

                await store.ShutdownAsync(context).ShouldBeSuccess();

                //
                // Recreating a store with a new epoch
                //

                // Disable self check won't run within startup
                settings = new SelfCheckSettings() { Epoch = "E2", StartSelfCheckInStartup = false };
                store = CreateStore(testDirectory, ContentStoreSettings(settings));

                await store.StartupAsync(context).ShouldBeSuccess();
                result = await store.SelfCheckContentDirectoryAsync(context, CancellationToken.None).ShouldBeSuccess();

                // Even though the time in marker file is the same, but epoch has change.
                // So the self check should be performed.
                result.Value.InvalidFiles.Should().Be(1);
            }
        }

        [Fact]
        public void SelfCheckStateTests()
        {
            // Reparsing date time to loose precision in order for equality to work.
            var now = DateTimeUtilities.FromReadableTimestamp(DateTime.UtcNow.ToReadableString()).Value;

            var state1 = new SelfCheckState("Epoch1", now, ContentHash.Random());
            var reparsedState1 = SelfCheckState.TryParse(state1.ToParseableString());

            reparsedState1.Should().NotBeNull();
            reparsedState1.Value.Should().Be(state1);

            var state2 = new SelfCheckState("Epoch1", now);
            var reparsedState2 = SelfCheckState.TryParse(state2.ToParseableString());

            reparsedState2.Should().NotBeNull();
            reparsedState2.Value.Should().Be(state2);
        }

        private static ContentStoreSettings ContentStoreSettings(SelfCheckSettings settings) => new ContentStoreSettings { SelfCheckSettings = settings };
        private static ContentStoreSettings Settings =>
            new ContentStoreSettings() {SelfCheckSettings = new SelfCheckSettings() {StartSelfCheckInStartup = false}};
        
        private TestFileSystemContentStoreInternal CreateStore(DisposableDirectory testDirectory, ContentStoreSettings settings, IDistributedLocationStore distributedStore)
        {
            return new TestFileSystemContentStoreInternal(FileSystem, Clock, testDirectory.Path, Config, distributedStore: distributedStore, settings: settings);
        }

        private TestFileSystemContentStoreInternal CreateStore(DisposableDirectory testDirectory, ContentStoreSettings settings)
        {
            return new TestFileSystemContentStoreInternal(FileSystem, Clock, testDirectory.Path, Config, settings: settings);
        }
    }
}
