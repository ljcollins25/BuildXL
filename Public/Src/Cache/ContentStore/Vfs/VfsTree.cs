// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
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
    public class VfsTree
    {
        private readonly ConcurrentBigMap<string, VfsNode> _nodeMap = new ConcurrentBigMap<string, VfsNode>(keyComparer: StringComparer.OrdinalIgnoreCase);

        public VfsDirectoryNode Root;

        public VfsTree()
        {
            Root = new VfsDirectoryNode(string.Empty, DateTime.UtcNow, null);
            _nodeMap[string.Empty] = Root;
        }

        public bool TryGetSpecificFileNode(ContentHash hash, int index, out VfsFileNode node)
        {
            node = _nodeMap.BackingSet[index].Value as VfsFileNode;
            return node != null && node.Hash == hash;
        }

        public bool TryGetNode(string relativePath, out VfsNode node)
        {
            return _nodeMap.TryGetValue(relativePath, out node);
        }

        public bool TryGetNode(string relativePath, out VfsNode node, out int nodeIndex)
        {
            var result = _nodeMap.TryGet(relativePath);
            node = result.Item.Value;
            nodeIndex = result.Index;
            return result.IsFound;
        }

        public VfsFileNode AddFileNode(string relativePath, DateTime timestamp, ContentHash hash, FileRealizationMode realizationMode, FileAccessMode accessMode)
        {
            if (_nodeMap.TryGetValue(relativePath, out var node))
            {
                return (VfsFileNode)node;
            }
            else
            {
                var parent = GetOrAddDirectoryNode(Path.GetDirectoryName(relativePath), allowAdd: true);
                node = _nodeMap.GetOrAdd(relativePath, (parent, timestamp, hash, realizationMode, accessMode), (l_relativePath, l_data) =>
                {
                    (parent, timestamp, hash, realizationMode, accessMode) = l_data;
                    return new VfsFileNode(Path.GetFileName(l_relativePath), timestamp, parent, hash, realizationMode, accessMode);
                }).Item.Value;


                return (VfsFileNode)node;
            }
        }

        public VfsDirectoryNode GetOrAddDirectoryNode(string relativePath, bool allowAdd = true)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return Root;
            }

            if (_nodeMap.TryGetValue(relativePath, out var node))
            {
                return (VfsDirectoryNode)node;
            }
            else if (allowAdd)
            {
                var parent = GetOrAddDirectoryNode(Path.GetDirectoryName(relativePath), allowAdd: true);
                node = _nodeMap.GetOrAdd(relativePath, parent, (l_relativePath, l_parent) =>
                {
                    return new VfsDirectoryNode(Path.GetFileName(relativePath), DateTime.UtcNow, parent);
                }).Item.Value;

                return (VfsDirectoryNode)node;
            }

            return null;
        }
    }

    public abstract class VfsNode
    {
        public readonly string Name;
        public readonly DateTime Timestamp;
        public VfsNode NextSibling;
        public VfsNode PriorSibling;
        public readonly VfsDirectoryNode Parent;

        public virtual long Size => -1;
        public bool IsDirectory => Size >= 0;

        public FileAttributes Attributes => IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;

        public VfsNode(string name, DateTime timestamp, VfsDirectoryNode parent)
        {
            Name = name;
            Timestamp = timestamp;
            Parent = parent;

            if (parent != null)
            {
                lock (parent)
                {
                    NextSibling = parent.FirstChild;
                    parent.FirstChild.PriorSibling = this;
                    Parent.FirstChild = this;
                }
            }
        }

        public void Remove()
        {
            lock (Parent)
            {
                if (Parent.FirstChild == this)
                {
                    Parent.FirstChild = NextSibling;
                    NextSibling.PriorSibling = null;
                }
                else
                {
                    PriorSibling.NextSibling = NextSibling;
                    NextSibling.PriorSibling = PriorSibling;
                }
            }
        }
    }

    public class VfsDirectoryNode : VfsNode
    {
        public VfsNode FirstChild;

        public VfsDirectoryNode(string name, DateTime timestamp, VfsDirectoryNode parent)
            : base(name, timestamp, parent)
        {
        }

        public IEnumerable<VfsNode> EnumerateChildren()
        {
            var child = FirstChild;
            while (child != null)
            {
                yield return child;
                child = child.NextSibling;
            }
        }
    }

    public class VfsFileNode : VfsNode
    {
        public readonly ContentHash Hash;
        public readonly FileRealizationMode RealizationMode;
        public readonly FileAccessMode AccessMode;
        public override long Size => 0;

        public VfsFileNode(string name, DateTime timestamp, VfsDirectoryNode parent, ContentHash hash, FileRealizationMode realizationMode, FileAccessMode accessMode)
            : base(name, timestamp, parent)
        {
        }
    }
}
