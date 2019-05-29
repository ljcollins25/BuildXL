using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Logging;

namespace BuildXL.Cache.ContentStore.Vfs
{
    internal static class PathUtilities
    {
        public static bool IsWithin(this AbsolutePath path, AbsolutePath candidatePrefix)
        {
            return path.Path.StartsWith(candidatePrefix.Path, StringComparison.OrdinalIgnoreCase) &&
                path.Path.Length > candidatePrefix.Path.Length &&
                path.Path[candidatePrefix.Path.Length] == Path.DirectorySeparatorChar;
        }

        public static bool TryGetRelativePath(this AbsolutePath path, AbsolutePath candidatePrefix, out string relativePath)
        {
            if (path.IsWithin(candidatePrefix))
            {
                relativePath = path.Path.Substring(candidatePrefix.Length + 1);
                return true;
            }
            else
            {
                relativePath = default;
                return false;
            }
        }
    }
}
