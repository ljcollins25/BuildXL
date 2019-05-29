// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Utils;

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <summary>
    /// Defines utility methods for working with VFS
    /// </summary>
    public static class VfsUtilities
    {
        public static bool TryParseCasRelativePath(string casRelativePath, out ContentHash hash, out int nodeIndex)
        {
            hash = default;
            nodeIndex = default;

            var parts = casRelativePath.Split('\\');
            if (parts.Length != 3)
            {
                return false;
            }

            if (Enum.TryParse<HashType>(parts[0], ignoreCase: true, out var hashType))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(parts[2]);
                    var separatorIndex = fileName.IndexOf('_');
                    string hashHexString = fileName.Substring(0, separatorIndex);
                    string nodeIndexString = fileName.Substring(separatorIndex + 1);

                    hash = new ContentHash(hashType, HexUtilities.HexToBytes(hashHexString));

                    if (!int.TryParse(nodeIndexString, out nodeIndex))
                    {
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    return false;
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        public static string CreateCasRelativePath(ContentHash hash, int nodeIndex)
        {
            var hashHex = hash.ToHex();
            return $@"{hash.HashType}\{hashHex.Substring(0, 3)}\{hashHex}_{nodeIndex}.blob";
        }
    }
}
