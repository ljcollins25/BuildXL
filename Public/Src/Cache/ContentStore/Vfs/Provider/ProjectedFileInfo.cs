// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.IO;

namespace BuildXL.Cache.ContentStore.Vfs.Managed
{
    public class ProjectedFileInfo
    {
        public ProjectedFileInfo(
            string fullPath,
            string name,
            long size,
            bool isDirectory,
            DateTime creationTime,
            DateTime lastAccessTime,
            DateTime lastWriteTime,
            DateTime changeTime,
            FileAttributes attributes)
        {
            Name = name;
            OriginFullPath = fullPath;
            Size = isDirectory ? 0 : size;
            IsDirectory = isDirectory;
            CreationTime = creationTime;
            LastAccessTime = lastAccessTime;
            LastWriteTime = lastWriteTime;
            ChangeTime = changeTime;
            // Make sure the directory attribute is stored properly.
            Attributes = isDirectory ? (attributes | FileAttributes.Directory) : (attributes & ~FileAttributes.Directory);
        }

        public ProjectedFileInfo(
            string fullPath,
            string name,
            long size,
            bool isDirectory) : this(
                fullPath: fullPath,
                name: name,
                size: size,
                isDirectory: isDirectory,
                creationTime: DateTime.UtcNow,
                lastAccessTime: DateTime.UtcNow,
                lastWriteTime: DateTime.UtcNow,
                changeTime: DateTime.UtcNow,
                attributes: isDirectory ? FileAttributes.Directory : FileAttributes.Normal)
        {  }

        public string Name { get; }
        public string OriginFullPath { get; }
        public long Size { get; }
        public bool IsDirectory { get; }
        public DateTime CreationTime { get; }
        public DateTime LastAccessTime { get; }
        public DateTime LastWriteTime { get; }
        public DateTime ChangeTime { get; }
        public FileAttributes Attributes { get; }
    }
}

