// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.Host.Service;

namespace BuildXL.Cache.ContentStore.Distributed.Test;

public partial class DeploymentIngesterTestsBase
{
    private static readonly string ConfigString = @"
    {
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
                'TargetRelativePath': 'info',
                'Url [Stamp:ST_S1]': 'file://Files/Foo.txt',
                'Url [Stamp:ST_S2]': 'file://Env/Foo.txt',
                'Url [Stamp:ST_S3]': 'file://Stamp3',
            }
        ],
        'AzureStorageSecretInfo': { 'Name': 'myregionalStorage{Region:LA}', 'TimeToLive':'60m' },
        'SasUrlTimeToLive': '3m',
        'Tool [Environment:MyEnvRunningOnWindows]': {
            'Executable': 'bin/service.exe',
            'Arguments': [ 'myargs' ],
            'EnvironmentVariables': {
                'ConfigPath': '../Foo.txt'
            }
        }
    }
    ".Replace("'", "\"");
}