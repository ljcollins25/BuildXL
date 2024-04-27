// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BuildXL.Cache.Host.Service.Deployment
{
    public class StorageIngesterConfiguration
    {
        public IDictionary<string, string[]> StorageAccountsByRegion { get; set; }

        public string ContentContainerName { get; set; }
    }
}
