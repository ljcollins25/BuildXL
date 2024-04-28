﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.Host.Service;

namespace BuildXL.Cache.ContentStore.Distributed.Test;

public partial class DeploymentIngesterTestsBase
{
    protected readonly Dictionary<string, string> sources = new()
    {
        { @"Stamp3\info.txt", "" },

        { @"Env\RootFile.json", "{ 'key1': 1, 'key2': 2 }" },
        { @"Env\Subfolder\Hello.txt", "Hello world" },
        { @"Env\Foo.txt", "Baz" },

        { @"Files\Foo.txt", "Bar" },
    };

    protected readonly Dictionary<string, Dictionary<string, string>> baseUrlDrops = new()
    {
        {
            "https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage1",
            new Dictionary<string, string>
            {
                { @"tools\toola\info.txt", "" },
                { @"app\appb\subfolder\file.json", "{ 'key1': 1, 'key2': 2 }" },
                { @"app\appb\Hello.txt", "Hello world" },
                { @"c\Foo.txt", "Baz" },
                { @"c\Bar.txt", "Bar" },
            }
        },
        {
            "https://dev.azure.com/buildxlcachetest/drop/drops/deployment/stage2",
            new Dictionary<string, string>
            {
                { @"tools\toola\info.txt", "Information appears hear now" },
                { @"app\appb\subfolder\file.json", "{ 'key1': 3, 'key2': 4 }" },
                { @"app\appb\subfolder\newfile.json", "{ 'prop': 'this is a new file', 'key2': 4 }" },
                { @"app\appb\Hello.txt", "Hello world" },
                { @"c\Foo.txt", "Baz" },
                { @"c\Bar.txt", "Bar" },
            }
        },
    };

    protected Dictionary<string, Dictionary<string, string>> drops;

    public void InitializeLayout()
    {
        drops = new()
        {
            {
                "https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop1?root=release/win-x64",
                new Dictionary<string, string>
                {
                    { @"file1.bin", "File content 1" },
                    { @"file2.txt", "File content 2" },
                    { @"sub\file3.dll", "File content 3" }
                }
            },
            {
                "https://dev.azure.com/buildxlcachetest/drop/drops/dev/testdrop2?root=debug",
                new Dictionary<string, string>
                {
                    { @"file1.bin", "File content 1" },
                    { @"file2.txt", "File content 2 changed" },
                    { @"sub\file5.dll", "File content 5" }
                }
            },
            {
                DeploymentUtilities.ConfigDropUri.OriginalString,
                new Dictionary<string, string>()
                {
                    { DeploymentUtilities.DeploymentConfigurationFileName, ConfigString }
                }
            },
            {
                "file://Env", getSourceDrop(@"Env\", @"Env\")
            },
            {
                "file://Files/Foo.txt", getSourceDrop(@"Files\Foo.txt", @"Files\")
            },
            {
                "file://Env/Foo.txt", getSourceDrop(@"Env\Foo.txt", @"Env\")
            },
            {
                "file://Stamp3", getSourceDrop(@"Stamp3\", @"Stamp3\")
            }
        };
    }

    protected static Dictionary<string, string> getSubDrop(Dictionary<string, string> dropContents, string root, string prefix)
    {
        return dropContents.Where(e => e.Key.StartsWith(root.Replace("/", "\\")))
            .ToDictionary(e => e.Key.Substring((prefix ?? root).Length), e => e.Value);
    }

    protected Dictionary<string, string> getSourceDrop(string root, string prefix)
    {
        return getSubDrop(sources, root, prefix);
    }

    protected Dictionary<string, string> getDropContents(string dropUrl, string relativeRoot = null)
    {
        var uri = new UriBuilder(dropUrl);
        var query = uri.Query;
        uri.Query = null;

        if (relativeRoot == null && query != null)
        {
            relativeRoot = HttpUtility.ParseQueryString(query)["root"];
        }

        return baseUrlDrops.TryGetValue(uri.Uri.ToString(), out var contents)
            ? getSubDrop(contents, relativeRoot, prefix: "")
            : drops[dropUrl];
    }
}