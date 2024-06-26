// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace Tool.ServicePipDaemon
{
    /// <summary>
    /// Generic ServicePipDaemon exception.
    /// </summary>
    public sealed class DaemonException : Exception
    {
        /// <nodoc/>
        public DaemonException(string message)
            : base(message) { }
    }
}
