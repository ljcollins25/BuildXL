// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Distributed.Utilities;

namespace BuildXL.Cache.ContentStore.Distributed.Test;

public partial class DeploymentIngesterTestsBase
{
    private static readonly string ConfigString = @"
    {
        '#Parameters': {
            'Flags': {

            },
            'Properties': {
                'Stage [RunKind:Stage2C]': '2',
                'Tool  [RunKind:Stage2C]': 'C'
            }
        },
        'Drops': [
            {
                'BaseUrl[Stage:1]': 'https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage1',
                'BaseUrl[Stage:2]': 'https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage2',

                'RelativeRoot[Tool:A]' : 'tools/toola',
                'RelativeRoot[Tool:B]' : 'app/appb',
                'RelativeRoot[Tool:C]' : 'c',


                'Url [Ring:Ring_0]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64',
                'Url [Ring:Ring_1]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop2?root=debug',
                'Url [Ring:Ring_2]': 'https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64',
                'TargetRelativePath': 'bin'
            },
            {
                'Url': 'file://Env',
            },
            {
                'Url [RunKind:Stage2C]': 'zip.file://zips/app.zip',
                'Url [RunKind:Stage2C][Stamp:ST_S1]': 'zip.file://zips/app.zip?root=d1',
                'Url [RunKind:Stage2C][Stamp:ST_S2]': 'zip.file://zips/app.zip?__root=d1/d2&__snapshot=20240430',
                'Url [RunKind:Stage2C][Stamp:ST_S3]': 'zip.file://zips/app.zip?__root=d1/b.txt',
            },
            {
                'TargetRelativePath': 'info',
                'Url [Stamp:ST_S1]': 'file://Files/Foo.txt',
                'Url [Stamp:ST_S2]': 'file://Env/Foo.txt',
                'Url [Stamp:ST_S3]': 'file://Stamp3',
            }
        ],
        'KeyVaultUri[ServiceVersion:10]': 'rel/Keys.json',
        'AzureStorageSecretInfo': { 'Name': 'myregionalStorage{Region:LA}', 'TimeToLive':'60m' },
        'SasUrlTimeToLive': '3m',
        'Tool [Environment:MyEnvRunningOnWindows]': {
            'Executable': 'bin/service.exe',
            'ServiceId': 'testservice',
            'Arguments': [ 'myargs' ],
            'EnvironmentVariables': {
                'ConfigPath': '../Foo.txt'
            },
            'SecretEnvironmentVariables': {
                'TestSecret': { 'TimeToLive':'60m' }
            }
        }
    }
    ".Replace("'", "\"");

    protected const string ExpectedStage2CTestSecretValue = nameof(ExpectedStage2CTestSecretValue);
    protected const string TestSecretName = "TestSecret";

    protected static readonly string KeysJson = JsonUtilities.JsonSerialize(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { $"{TestSecretName}", "Not overridden secret" },
        { $"{TestSecretName}[RunKind:Stage2C]", ExpectedStage2CTestSecretValue },
        { $"{TestSecretName}[RunKind:Stage2D]", "Overridden but not selected" },
    });
}