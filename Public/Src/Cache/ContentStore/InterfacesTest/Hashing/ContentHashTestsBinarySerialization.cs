// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using BuildXL.Cache.ContentStore.Hashing;
using Xunit;

namespace BuildXL.Cache.ContentStore.InterfacesTest.Hashing
{
    public partial class ContentHashTests
    {
        [Theory]
        [InlineData(HashType.MD5)]
        [InlineData(HashType.SHA1)]
        [InlineData(HashType.SHA256)]
        [InlineData(HashType.Vso0)]
        [InlineData(HashType.Dedup64K)]
        [InlineData(HashType.Dedup1024K)]
        public void RoundtripFullBinary(HashType hashType)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new BinaryWriter(ms))
                {
                    var h1 = ContentHash.Random(hashType);
                    h1.Serialize(writer);
                    Assert.Equal(ContentHash.SerializedLength, ms.Length);
                    ms.Position = 0;

                    using (var reader = new BinaryReader(ms))
                    {
                        var h2 = new ContentHash(reader);
                        Assert.Equal(hashType, h2.HashType);
                        Assert.Equal(h1.ToString(), h2.ToString());
                    }
                }
            }
        }

        [Theory]
        [InlineData(HashType.MD5)]
        [InlineData(HashType.SHA1)]
        [InlineData(HashType.SHA256)]
        [InlineData(HashType.Vso0)]
        [InlineData(HashType.Dedup64K)]
        [InlineData(HashType.Dedup1024K)]
        public void RoundtripShortHashBinary(HashType hashType)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new BinaryWriter(ms))
                {
                    var h1 = ContentHash.Random(hashType);
                    var shortHash1 = new ShortHash(h1);
                    shortHash1.Serialize(writer);
                    Assert.Equal(ShortHash.SerializedLength, ms.Length);
                    ms.Position = 0;

                    using (var reader = new BinaryReader(ms))
                    {
                        var data = reader.ReadBytes(ShortHash.SerializedLength);
                        var shortHash2 = ShortHash.FromBytes(data);
                        Assert.Equal(hashType, shortHash2.HashType);
                        Assert.Equal(shortHash1.ToString(), shortHash2.ToString());
                    }
                }
            }
        }

        [Theory]
        [InlineData(HashType.MD5)]
        [InlineData(HashType.SHA1)]
        [InlineData(HashType.SHA256)]
        [InlineData(HashType.Vso0)]
        [InlineData(HashType.Dedup64K)]
        [InlineData(HashType.Dedup1024K)]
        public void RoundtripPartialBinary(HashType hashType)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new BinaryWriter(ms))
                {
                    var h1 = ContentHash.Random(hashType);
                    h1.SerializeHashBytes(writer);
                    Assert.Equal(HashInfoLookup.Find(hashType).ByteLength, ms.Length);
                    ms.Position = 0;

                    using (var reader = new BinaryReader(ms))
                    {
                        var h2 = new ContentHash(hashType, reader);
                        Assert.Equal(hashType, h2.HashType);
                        Assert.Equal(h1.ToHex(), h2.ToHex());
                    }
                }
            }
        }
    }
}
