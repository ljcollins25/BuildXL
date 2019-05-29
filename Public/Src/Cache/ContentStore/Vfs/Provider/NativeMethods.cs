// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Windows.ProjFS;

namespace BuildXL.Cache.ContentStore.Vfs.Provider
{
    /// <summary>
    /// Implements IComparer using <see cref="Microsoft.Windows.ProjFS.Utils.FileNameCompare(string, string)"/>.
    /// </summary>
    internal static class NativeMethods
    {
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool CreateHardLink(
              string lpFileName,
              string lpExistingFileName,
              IntPtr lpSecurityAttributes
              );
    }
}
