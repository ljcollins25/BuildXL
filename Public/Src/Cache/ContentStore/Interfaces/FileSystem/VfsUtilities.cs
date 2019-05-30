// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Utils;

namespace BuildXL.Cache.ContentStore.Interfaces.FileSystem
{
    /// <summary>
    /// Defines utility methods for working with VFS
    /// </summary>
    public static class VfsUtilities
    {
        private static readonly string DirectorySeparatorCharString = Path.DirectorySeparatorChar.ToString();
        private static readonly char[] PathSplitChars = new[] { Path.DirectorySeparatorChar };
        private static readonly char[] FilePlacementInfoFileNameSplitChars = new[] { '_' };

        public static bool IsPathWithin(this string path, string candidateParent)
        {
            if (!path.StartsWith(candidateParent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (candidateParent.EndsWith(DirectorySeparatorCharString))
            {
                return true;
            }

            return path.Length > candidateParent.Length &&
                path[candidateParent.Length] == Path.DirectorySeparatorChar;
        }

        public static bool TryGetRelativePath(this string path, string candidateParent, out string relativePath)
        {
            if (path.IsPathWithin(candidateParent))
            {
                relativePath = path.Substring(candidateParent.Length + (candidateParent.EndsWith(DirectorySeparatorCharString) ? 0 : 1));
                return true;
            }
            else
            {
                relativePath = default;
                return false;
            }
        }

        public static bool TryParseVfsCasRelativePath(string casRelativePath, out FilePlacementData data)
        {
            data = default;
            if (!casRelativePath.EndsWith(".blob", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = casRelativePath.Split(PathSplitChars);
            if (parts.Length != 3)
            {
                return false;
            }
            
            if (Enum.TryParse<HashType>(parts[0], ignoreCase: true, out var hashType))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(parts[2]);
                    parts = fileName.Split(FilePlacementInfoFileNameSplitChars);
                    if (parts.Length != 3 || parts[1].Length != 2)
                    {
                        return false;
                    }

                    var modesString = parts[1];

                    FileRealizationMode realizationMode = (FileRealizationMode)(byte)(modesString[0] - '0');
                    FileAccessMode accessMode = (FileAccessMode)(byte)(modesString[1] - '0');

                    ContentHash hash = new ContentHash(hashType, HexUtilities.HexToBytes(parts[0]));
                    data = new FilePlacementData(hash, realizationMode, accessMode);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        public static string CreateCasRelativePath(FilePlacementData data, int nodeIndex)
        {
            var hashHex = data.Hash.ToHex();
            return $@"{data.Hash.HashType}\{hashHex.Substring(0, 3)}\{hashHex}_{(byte)data.RealizationMode}{(byte)data.AccessMode}_{nodeIndex}.blob";
        }
    }

    public readonly struct FilePlacementData
    {
        public readonly ContentHash Hash;
        public readonly FileRealizationMode RealizationMode;
        public readonly FileAccessMode AccessMode;

        public FilePlacementData(ContentHash hash, FileRealizationMode realizationMode, FileAccessMode accessMode)
        {
            Hash = hash;
            RealizationMode = realizationMode;
            AccessMode = accessMode;
        }
    }
}
