// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Service {
    export declare const qualifier : BuildXLSdk.DefaultQualifierWithNet472AndNetStandard20;

    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.Host.Service",
        sources: globR(d`.`, "*.cs"),
        skipDocumentationGeneration: true,
        addPolySharpAttributes: true,
        references: [
            Configuration.dll,
            ...importFrom("BuildXL.Cache.ContentStore").getSerializationPackages(true),

            importFrom("BuildXL.Cache.ContentStore").Library.dll,
            importFrom("BuildXL.Cache.ContentStore").Grpc.dll,
            importFrom("BuildXL.Cache.ContentStore").Distributed.dll,
            importFrom("BuildXL.Cache.ContentStore").Interfaces.dll,
            importFrom("BuildXL.Cache.ContentStore").Hashing.dll,
            importFrom("BuildXL.Cache.ContentStore").UtilitiesCore.dll,
            
            importFrom("BuildXL.Cache.MemoizationStore").Library.dll,
            ...addIfLazy(BuildXLSdk.Flags.isVstsArtifactsEnabled, () => [ importFrom("BuildXL.Cache.MemoizationStore").Vsts.dll ]),
            importFrom("BuildXL.Cache.MemoizationStore").Distributed.dll,
            importFrom("BuildXL.Cache.MemoizationStore").Interfaces.dll,

            importFrom("BuildXL.Utilities").dll,
            importFrom("BuildXL.Utilities").Branding.dll,
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Utilities.Core.dll,

            importFrom("BuildXL.Cache.Logging").Library.dll,
            ...importFrom("BuildXL.Cache.ContentStore").getAzureBlobStorageSdkPackages(true),
            importFrom("NLog").pkg,
            importFrom("Azure.Identity").pkg,

            ...BuildXLSdk.bclAsyncPackages,

            ...addIf(BuildXLSdk.isFullFramework,
                NetFx.System.IO.Compression.dll,
                NetFx.System.IO.Compression.FileSystem.dll,
                NetFx.System.Web.dll,
                NetFx.System.Net.Http.dll,
                NetFx.System.Xml.dll
            ),
        ],
        allowUnsafeBlocks: false,
        addNotNullAttributeFile: true,
        internalsVisibleTo: [
            "BuildXL.Cache.ContentStore.Distributed.Test",
            "BuildXL.Cache.Host.Test",
        ]
    });
}
