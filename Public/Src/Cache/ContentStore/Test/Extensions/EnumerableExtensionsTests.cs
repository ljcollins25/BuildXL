// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Extensions;
using ContentStoreTest.Test;
using FluentAssertions;
using Xunit;

namespace ContentStoreTest.Extensions
{
    public class EnumerableExtensionsTests : TestBase
    {
        private const int Size = 100;

        public EnumerableExtensionsTests()
            : base(TestGlobal.Logger)
        {
        }

        [Fact]
        public void TestPseudoRandomEnumerate()
        {
            for (int length = 0; length < 1024; length++)
            {
                var sortedRange = Enumerable.Range(0, length).ToArray();
                var randomRange = EnumerableExtensions.PseudoRandomEnumerateRange(length).ToArray();
                var randomEnumeration = sortedRange.PseudoRandomEnumerate().ToArray();

                // Technically, there is a possibility that the random range will match the sorted
                // range. Use a high enough length so that possibility is excluded.
                if (length > 10)
                {
                    Assert.NotEqual(sortedRange, randomRange);
                    Assert.NotEqual(sortedRange, randomEnumeration);
                }

                Assert.Equal(sortedRange, randomRange.OrderBy(i => i));
                Assert.Equal(sortedRange, randomEnumeration.OrderBy(i => i));
            }
        }

        [Fact]
        public async Task ParallelForEachActionAsync()
        {
            var flags = new bool[Size];
            await Enumerable.Range(0, Size).ParallelForEachAsync(i => { flags[i] = true; });
            flags.All(flag => flag).Should().BeTrue();
        }

        [Fact]
        public async Task ParallelForEachFuncAsync()
        {
            var flags = new bool[Size];
            await Enumerable.Range(0, Size).ParallelForEachAsync(async i =>
            {
                await Task.Run(() => flags[i] = true);
            });
            flags.All(flag => flag).Should().BeTrue();
        }

        [Fact]
        public async Task ParallelToConcurrentDictionaryValuesAsync()
        {
            const int valueMultiplier = 2;
            var dictionary =
                await Enumerable.Range(0, Size).ParallelToConcurrentDictionaryAsync(i => i * valueMultiplier);
            dictionary.Count.Should().Be(Size);
            dictionary.All(pair => pair.Value == pair.Key * valueMultiplier).Should().BeTrue();
        }

        [Fact]
        public async Task ParallelToConcurrentDictionaryKeysAndValuesAsync()
        {
            const int keyMultiplier = 3;
            const int valueMultiplier = 6;
            var dictionary = await Enumerable.Range(0, Size)
                .ParallelToConcurrentDictionaryAsync(i => i * keyMultiplier, i => i * valueMultiplier);
            dictionary.Count.Should().Be(Size);
            dictionary.All(pair => pair.Value == pair.Key * (valueMultiplier / keyMultiplier)).Should().BeTrue();
        }

        [Fact]
        public async Task ParallelAddToConcurrentDictionaryAsync()
        {
            const int keyMultiplier = 3;
            const int valueMultiplier = 6;
            var dictionary = await Enumerable.Range(0, Size)
                .ParallelToConcurrentDictionaryAsync(i => i * keyMultiplier, i => i * valueMultiplier);
            await Enumerable.Range(Size + 1, Size)
                .ParallelAddToConcurrentDictionaryAsync(dictionary, i => i * keyMultiplier, i => i * valueMultiplier);
            dictionary.All(pair => pair.Value == pair.Key * (valueMultiplier / keyMultiplier)).Should().BeTrue();
        }
    }
}
