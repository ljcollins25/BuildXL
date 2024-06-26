// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Ninja.Serialization {
    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.FrontEnd.Ninja.Serialization",
        generateLogs: false,
        sources: globR(d`.`, "*.cs"),
        references: [
            importFrom("Newtonsoft.Json").pkg,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
        ],
    });
}
