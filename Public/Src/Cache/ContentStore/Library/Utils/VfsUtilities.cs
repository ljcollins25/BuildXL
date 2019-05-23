// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;

namespace BuildXL.Cache.ContentStore.Utils
{
    /// <summary>
    /// Defines utility methods for working with VFS
    /// </summary>
    public static class VfsUtilities
    {
        public static bool TryParseCasRelativePath(string casRelativePath, out ContentHash hash, out int nodeIndex)
        {
            throw new NotImplementedException();
        }

        public static AbsolutePath CreateCasRelativePath(ContentHash hash, int nodeIndex)
        {
            throw new NotImplementedException();
        }
    }
}
