// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace BlockAccesses {
    @@public
    export const exe = BuildXLSdk.executable({
        assemblyName: "BlockAccesses",
        sources: globR(d`.`, "*.cs"),
        references: [
            importFrom("BuildXL.Engine").Processes.dll,
            importFrom("BuildXL.Engine").ProcessPipExecutor.dll,
            importFrom("BuildXL.Pips").dll,
            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,
        ],
    });
}
