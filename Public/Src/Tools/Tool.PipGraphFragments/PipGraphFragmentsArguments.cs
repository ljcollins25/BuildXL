// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace BuildXL.FrontEnd.PipGraphFragments
{
    /// <summary>
    /// Configuration of the PipGraphFragments tool
    /// </summary>
    [JsonObject(IsReference = false)]
    public struct PipGraphFragmentsArguments
    {
        /// <summary>
        /// 
        /// </summary>
        public IEnumerable<string> InputPipFragments;

        /// <summary>
        /// 
        /// </summary>
        public string OutputPipGraph;
    }
}