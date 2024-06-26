// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using BuildXL.Cache.ContentStore.Interfaces.Utils;

#pragma warning disable CS3001 // CLS
#pragma warning disable CS3003

namespace BuildXL.Cache.ContentStore.Hashing
{
    /// <summary>
    /// A node in the deduplication Merkle graph.
    /// </summary>
    [DebuggerDisplay("Type = {Type} ChildCount = {ChildNodes?.Count} Hash = {HashString}")]
    public readonly struct DedupNode : IEquatable<DedupNode>
    {
        /// <summary>
        /// Type of the node in the graph
        /// </summary>
        public enum NodeType : byte
        {
            /// <summary>
            /// A node that represents a chunk of content and has no children.
            /// </summary>
            ChunkLeaf = 0,

            /// <summary>
            /// A node that references other nodes as children.
            /// </summary>
            InnerNode = 1,
        }

        /// <summary>
        /// Length of the hashes
        /// </summary>
        private const int HashLength = 32;

        /// <summary>
        /// The maxmium number of children allowed in a single node.
        /// </summary>
        /// <remarks>Limited to put a cap on the amount of work the server must do for a single request.</remarks>
        public const int MaxDirectChildrenPerNode = 512;

        private static readonly IContentHasher NodeHasher = DedupSingleChunkHashInfo.Instance.CreateContentHasher();

        /// <summary>
        /// The type of this node.
        /// </summary>
        public readonly NodeType Type;

        /// <summary>
        /// The number of bytes that this node recursively references.
        /// </summary>
        /// <remarks>
        /// Equal to the sum of the size of all the chunks that recursively references. Chunks that are references multiple times are counted multiple times.
        /// </remarks>
        public readonly ulong TransitiveContentBytes;

        /// <summary>
        /// Hash of this node.
        /// </summary>
        public readonly byte[] Hash;

        /// <summary>
        /// Entries for the direct children of this node.
        /// </summary>
        public readonly IReadOnlyList<DedupNode>? ChildNodes;

        /// <summary>
        /// If known, the height of the node in the tree.
        /// </summary>
        /// <remarks>
        /// This will have a value when building the tree from the bottom up.
        /// Height: The maximum distance of any node from the root. If a tree has only one node (the root), the height is zero.
        /// https://xlinux.nist.gov/dads/HTML/height.html
        /// </remarks>
        public readonly uint? Height;

        /// <summary>
        /// Gets the hexadecimal string of the hash of this node.
        /// </summary>
        public string HashString => Hash.ToHex();

        /// <summary>
        /// Deserializes a node from the provided bytestream.
        /// </summary>
        public static DedupNode Deserialize(byte[] serialized)
        {
            using (var ms = new MemoryStream(serialized, writable: false))
            {
                return Deserialize(ms);
            }
        }

        /// <summary>
        /// Deserializes a node from the provided bytestream.
        /// </summary>
        public static DedupNode Deserialize(ArraySegment<byte> serialized)
        {
            Contract.Requires(serialized.Array != null);
            using (var ms = new MemoryStream(serialized.Array, serialized.Offset, serialized.Count, writable: false))
            {
                return Deserialize(ms);
            }
        }

        /// <summary>
        /// Deserializes a node from the provided bytestream.
        /// </summary>
        public static DedupNode Deserialize(Stream serialized)
        {
            using (var br = new BinaryReader(serialized))
            {
                return Deserialize(br);
            }
        }

        private static void AssertChunkSize(ulong chunkSize)
        {
            if (chunkSize > ((1 << (3 * 8)) - 1)) // ~16 MB max size allowed.
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "A node cannot refer to a chunk of size greater than (2^24 - 1).");
            }
        }

        private static void AssertNodeSize(ulong nodeSize)
        {
            if (nodeSize > (((ulong)1 << (7 * 8)) - 1)) // ~72 PB max size allowed.
            {
                throw new ArgumentOutOfRangeException(
                    nameof(nodeSize),
                    nodeSize,
                    "A node cannot refer to a node of size greater than (2^56 - 1).");
            }
        }

        private static void AssertDirectChildrenCount(int childrenCount)
        {
            if (childrenCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childrenCount),
                    childrenCount,
                    "A node must have at least one child.");
            }

            if (childrenCount > MaxDirectChildrenPerNode)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childrenCount),
                    childrenCount,
                    $"A node cannot directly refer to more than {MaxDirectChildrenPerNode} children.");
            }
        }

        /// <summary>
        /// Deserializes a node from the provided reader.
        /// </summary>
        private static DedupNode Deserialize(BinaryReader br)
        {
            ushort version = br.ReadUInt16();
            if (version != 0)
            {
                throw new ArgumentException($"Unknown node version:{version}");
            }

            int childCount = br.ReadUInt16() + 1;
            AssertDirectChildrenCount(childCount);
            var childNodes = new List<DedupNode>(childCount);
            for (int i = 0; i < childCount; i++)
            {
                ulong size = 0;
                NodeType type = (NodeType)br.ReadByte();
                uint? height;
                switch (type)
                {
                    case NodeType.ChunkLeaf:
                        size = ReadTruncatedUlongLittleEndian(br, 3);
                        height = 0;
                        break;
                    case NodeType.InnerNode:
                        size = ReadTruncatedUlongLittleEndian(br, 7);
                        height = null;
                        break;
                    default:
                        throw new ArgumentException($"Unknown node type: {size}");
                }

                byte[] hash = br.ReadBytes(HashLength);
                childNodes.Add(new DedupNode(type, size, hash, height));
            }

            return new DedupNode(childNodes);
        }

        private static ulong ReadTruncatedUlongLittleEndian(BinaryReader br, int bytes)
        {
            ulong value = 0;
            for (int i = 0; i < bytes; i++)
            {
                int bitShift = 8 * i;
                ulong b = br.ReadByte();
                unchecked
                {
                    b <<= bitShift;
                }
                
                value |= b;
            }

            return value;
        }

        private static void WriteTruncatedUlongLittleEndian(BinaryWriter bw, ulong value, int bytes)
        {
            for (int i = 0; i < bytes; i++)
            {
                int bitShift = 8 * i;
                bw.Write((byte)((value >> bitShift) & 0xFF));
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DedupNode"/> struct from raw components.
        /// </summary>
        public DedupNode(NodeType type, ulong size, byte[] hash, uint? height)
        {
            ChildNodes = null;
            Hash = hash;
            TransitiveContentBytes = size;
            Type = type;
            Height = height;
            Validate();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DedupNode"/> struct from the given chunk.
        /// </summary>
        public DedupNode(ChunkInfo chunk)
            : this(NodeType.ChunkLeaf, chunk.Size, chunk.Hash, 0)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DedupNode"/> struct from the given child nodes.
        /// </summary>
        public DedupNode(IEnumerable<DedupNode> childNodes)
        {
            ChildNodes = childNodes.ToList();
            AssertDirectChildrenCount(ChildNodes.Count);
            TransitiveContentBytes = 0;
            Hash = null!; // Need to convince the compiler that all the fields are initialized.
            Type = NodeType.InnerNode;
            Height = 0;

            foreach (var childNode in ChildNodes)
            {
                TransitiveContentBytes += childNode.TransitiveContentBytes;
                if (Height.HasValue)
                {
                    if (childNode.Height.HasValue)
                    {
                        Height = Math.Max(Height.Value, childNode.Height.Value + 1);
                    }
                    else
                    {
                        Height = null;
                    }
                }
            }

            using (var hasher = NodeHasher.CreateToken())
            {
                Hash = hasher.Hasher.ComputeHash(Serialize());
            }

            Validate();
        }

        private void Validate()
        {
            if (Hash.Length != HashLength)
            {
                throw new ArgumentException("Node hashes must be 32 bytes.");
            }

            switch (Type)
            {
                case NodeType.ChunkLeaf:
                    AssertChunkSize(TransitiveContentBytes);
                    break;
                case NodeType.InnerNode:
                    AssertNodeSize(TransitiveContentBytes);
                    break;
                default:
                    throw new ArgumentException($"Unknown node type: {Type}");
            }
        }

        /// <summary>
        /// Canonically serializes this node to a byte array.
        /// </summary>
        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            {
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    SerializeNode(bw);
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Canonically serializes this node to the given writer.
        /// </summary>
        private void SerializeNode(BinaryWriter writer)
        {
            if (Type != NodeType.InnerNode)
            {
                throw new InvalidOperationException();
            }

            Contract.Requires(ChildNodes != null);

            writer.Write((ushort)0); // magic/version number
            writer.Write((ushort)(ChildNodes.Count - 1)); // 1-512 nodes -> 0-511
            foreach (DedupNode childNode in ChildNodes)
            {
                childNode.SerializeAsEntry(writer);
            }
        }

        private void SerializeAsEntry(BinaryWriter bw)
        {
            bw.Write((byte)Type);
            switch (Type)
            {
                case NodeType.ChunkLeaf:
                    WriteTruncatedUlongLittleEndian(bw, TransitiveContentBytes, 3);
                    break;
                case NodeType.InnerNode:
                    WriteTruncatedUlongLittleEndian(bw, TransitiveContentBytes, 7);
                    break;
                default:
                    throw new ArgumentException($"Unknown node type: {Type}");
            }

            bw.Write(Hash);
        }

        /// <summary>
        /// Traverses the node tree in a pre-order traversal.
        /// </summary>
        public void VisitPreorder(Func<DedupNode, bool> visitFunc)
        {
            if (!visitFunc(this))
            {
                return;
            }

            if (Type == NodeType.InnerNode)
            {
                if (ChildNodes == null)
                {
                    throw new ArgumentException("Node does not contain in-memory references to the child nodes.");
                }

                foreach (DedupNode nodeEntry in ChildNodes)
                {
                    nodeEntry.VisitPreorder(visitFunc);
                }
            }
        }

        /// <summary>
        /// Enumerates all nodes where Type=ChunkLeaf in their original order.
        /// </summary>
        public IEnumerable<DedupNode> EnumerateChunkLeafsInOrder()
        {
            if (Type == NodeType.ChunkLeaf)
            {
                yield return this;
            }
            else
            {
                foreach (DedupNode node in EnumerateInnerNodesDepthFirst())
                {
                    foreach (DedupNode chunk in node.ChildNodes!.Where(n => n.Type == NodeType.ChunkLeaf))
                    {
                        yield return chunk;
                    }
                }
            }
        }

        /// <summary>
        /// Enumerates all nodes where Type=InnerNode in a depth-first order.
        /// </summary>
        public IEnumerable<DedupNode> EnumerateInnerNodesDepthFirst()
        {
            if (Type == NodeType.InnerNode)
            {
                if (ChildNodes == null)
                {
                    throw new ArgumentException("Node does not contain in-memory references to the child nodes.");
                }

                foreach (DedupNode node in ChildNodes.Where(n => n.Type == NodeType.InnerNode))
                {
                    foreach (DedupNode child in node.EnumerateInnerNodesDepthFirst())
                    {
                        yield return child;
                    }
                }
            }

            yield return this;
        }

        /// <summary>
        /// Gets all chunk nodes under the current node in their chunk representation.
        /// </summary>
        /// <param name="startOffset">
        /// The starting offset of chunks under the current node. Defaults to 0.
        /// </param>
        /// <returns>
        /// An enumerable of <see cref="ChunkInfo"/>.
        /// </returns>
        /// <remarks>
        /// In order for the operation to succeed, the node must be completely filled with child node references.
        /// </remarks>
        public IEnumerable<ChunkInfo> GetChunks(ulong startOffset = 0)
        {
            foreach (DedupNode chunkNode in EnumerateChunkLeafsInOrder())
            {
                yield return new ChunkInfo(
                    startOffset,
                    (uint)chunkNode.TransitiveContentBytes,
                    chunkNode.Hash.ToArray());

                startOffset += chunkNode.TransitiveContentBytes;
            }
        }

        /// <summary>
        /// Creates a tree or a single node containing the given chunk(s).
        /// </summary>
        /// <returns>
        /// The root of the tree, or a single chunk node encapsulating the chunk.
        /// </returns>
        public static DedupNode Create(IList<ChunkInfo> chunks)
        {
            if (chunks.Count == 0)
            {
                return new DedupNode(new ChunkInfo(0, 0, DedupSingleChunkHashInfo.Instance.EmptyHash.ToHashByteArray()));
            }
            else if (chunks.Count == 1)
            {
                // Content is small enough to track as a chunk.
                var node = new DedupNode(chunks.Single());
                Contract.Assert(node.Type == DedupNode.NodeType.ChunkLeaf, $"{nameof(Create)}: expected chunk leaf: {DedupNode.NodeType.ChunkLeaf} got {node.Type} instead.");

                return node;
            }
            else
            {
                return DedupNodeTree.Create(chunks);
            }
        }

        /// <inheritdoc/>
        public bool Equals([AllowNull]DedupNode other)
        {
            return ByteArrayComparer.ArraysEqual(Hash, other.Hash);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (!(obj is DedupNode))
            {
                return false;
            }

            return Equals((DedupNode)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return ByteArrayComparer.Instance.GetHashCode(Hash);
        }

        /// <inheritxml/>
        public static bool operator ==(DedupNode left, DedupNode right)
        {
            return left.Equals(right);
        }

        /// <inheritxml/>
        public static bool operator !=(DedupNode left, DedupNode right)
        {
            return !(left == right);
        }
    }
}
