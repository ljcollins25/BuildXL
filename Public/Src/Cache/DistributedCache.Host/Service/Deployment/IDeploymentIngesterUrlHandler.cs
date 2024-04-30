// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.Host.Service.Deployment;

namespace BuildXL.Cache.Host.Service;

public interface IDeploymentIngesterUrlHandler
{
    ParsedDropUrl Parse(Uri url);

    Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> files);
}

public record struct DeploymentFile(RelativePath DeployedPath, AbsolutePath SourcePath);

public record ParsedDropUrl(IDeploymentIngesterUrlHandler Handler, Uri OriginalUri, Uri EffectiveUrl, string RelativeRoot, bool HasMutableContent = true)
{
    public bool IsFile => !EffectiveUrl.IsAbsoluteUri || EffectiveUrl.IsFile || OriginalUri == DeploymentUtilities.ConfigDropUri;

    public AbsolutePath GetFullPath(AbsolutePath root)
    {
        return root / EffectiveUrl.LocalPath.TrimStart('\\', '/');
    }
}

public class FuncDeploymentIngesterUrlHander(
    DeploymentIngesterBaseConfiguration configuration,
    string name,
    Func<(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> deploymentFiles), Task<Result<AbsolutePath>>> getFilesAsync)
    : DeploymentIngesterUrlHandlerBase(configuration)
{
    public override string Name => name;

    public override Tracer Tracer { get; } = new Tracer(name);

    public override Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> deploymentFiles)
    {
        return getFilesAsync((context, url, tempDirectory, deploymentFiles)).ThenAsync(r =>
        {
            if (r.Value is AbsolutePath targetDirectory)
            {
                AddFilesUnderDirectory(targetDirectory, deploymentFiles, relativeRoot: null);
            }

            return BoolResult.Success;
        });
    }
}

public class ZipDeploymentIngesterUrlHander(DeploymentIngesterBaseConfiguration configuration)
    : DeploymentIngesterUrlHandlerBase(configuration)
{
    public const string ZipFileScheme = "zip.file";
    public const string ZipHttpsScheme = "zip.https";

    public HttpClient Client { get; } = new();

    public override string Name => "zip";

    public override Tracer Tracer { get; } = new Tracer(nameof(ZipDeploymentIngesterUrlHander));

    protected override void UpdateUriScheme(UriBuilder uri)
    {
        if (uri.Scheme == ZipFileScheme)
        {
            uri.Scheme = "file";
            return;
        }

        base.UpdateUriScheme(uri);
    }

    public static void AddHandlers(DeploymentIngesterConfiguration configuration)
    {
        var handler = new ZipDeploymentIngesterUrlHander(configuration);
        configuration.HandlerByScheme.TryAdd(ZipFileScheme, handler);
        configuration.HandlerByScheme.TryAdd(ZipHttpsScheme, handler);
    }

    public override async Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> deploymentFiles)
    {
        var targetPath = tempDirectory / "download.zip";

        using (var target = FileSystem.OpenForWrite(targetPath, null, FileMode.Create, FileShare.Delete))
        using (var source = url.IsFile
            ? FileSystem.OpenReadOnly(url.GetFullPath(SourceRoot), FileShare.Read)
            : await Client.GetStreamAsync(url.EffectiveUrl))
        {
            await source.CopyToAsync(target, ushort.MaxValue);
        }

        var outputDir = tempDirectory / "extract";
        FileSystem.CreateDirectory(outputDir);
        ZipFile.ExtractToDirectory(targetPath.Path, outputDir.Path);

        AddFilesUnderDirectory(outputDir, deploymentFiles, url.RelativeRoot);

        return BoolResult.Success;
    }
}

public class FileDeploymentIngesterUrlHander(DeploymentIngesterBaseConfiguration configuration)
    : DeploymentIngesterUrlHandlerBase(configuration)
{
    public override string Name => "file";

    public override Tracer Tracer { get; } = new Tracer(nameof(FileDeploymentIngesterUrlHander));

    protected override void UpdateUriScheme(UriBuilder uri)
    {
        // Don't modify the scheme
    }

    public override async Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> files)
    {
        if (url.OriginalUri == DeploymentUtilities.ConfigDropUri)
        {
            files.Add(new(new RelativePath(DeploymentUtilities.DeploymentConfigurationFileName), Configuration.DeploymentConfigurationPath));
        }
        else
        {
            var path = url.GetFullPath(SourceRoot);
            AddFilesUnderDirectory(path, files, url.RelativeRoot);
        }

        return BoolResult.Success;
    }
}

public class DropDeploymentIngesterUrlHandler(AbsolutePath dropExeFilePath, string dropToken, DeploymentIngesterBaseConfiguration configuration)
    : DeploymentIngesterUrlHandlerBase(configuration)
{
    public override string Name => "drop";

    public override Tracer Tracer { get; } = new Tracer(nameof(DropDeploymentIngesterUrlHandler));

    public override ParsedDropUrl Parse(Uri url)
    {
        return base.Parse(url) with
        {
            // Drops are immutable.
            HasMutableContent = false
        };
    }

    public override async Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> deploymentFiles)
    {
        var args = $@"get -u {url.EffectiveUrl} -d ""{tempDirectory}"" --patAuth {dropToken}";

        var process = new Process()
        {
            StartInfo = new ProcessStartInfo(dropExeFilePath.Path, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.OutputDataReceived += (s, e) =>
        {
            Tracer.Debug(context, "Drop Output: " + e.Data);
        };

        process.ErrorDataReceived += (s, e) =>
        {
            Tracer.Error(context, "Drop Error: " + e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            return new BoolResult($"Process exited with code: {process.ExitCode}");
        }

        AddFilesUnderDirectory(tempDirectory, deploymentFiles, url.RelativeRoot);

        return BoolResult.Success;
    }
}

public abstract class DeploymentIngesterUrlHandlerBase(DeploymentIngesterBaseConfiguration configuration) : IDeploymentIngesterUrlHandler
{
    public const string SnapshotQueryKey = "__snapshot";

    public abstract string Name { get; }

    public abstract Tracer Tracer { get; }

    protected DeploymentIngesterBaseConfiguration Configuration { get; } = configuration;

    protected AbsolutePath DeploymentRoot => configuration.DeploymentRoot;

    protected AbsolutePath DeploymentConfigurationPath => configuration.DeploymentConfigurationPath;

    protected AbsolutePath SourceRoot => configuration.SourceRoot;

    protected IAbsFileSystem FileSystem => configuration.FileSystem;

    protected virtual void UpdateUriScheme(UriBuilder uri)
    {
        uri.Scheme = "https";
    }

    public abstract Task<BoolResult> GetFilesAsync(OperationContext context, ParsedDropUrl url, AbsolutePath tempDirectory, List<DeploymentFile> deploymentFiles);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "EPC20:Avoid using default ToString implementation", Justification = "HttpUtility.ParseQueryString returns a collection whose ToString() returns a value query string representation")]
    public virtual ParsedDropUrl Parse(Uri url)
    {
        var uri = new UriBuilder(url);
        var originalUri = uri.Uri;
        UpdateUriScheme(uri);

        string relativeRoot = "";
        bool isSnapshot = false;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            relativeRoot = query.Get("__root") ?? query.Get("root") ?? "";
            isSnapshot = query.Get(SnapshotQueryKey) != null;
            foreach (string key in query.AllKeys)
            {
                if (key.StartsWith("__"))
                {
                    query.Remove(key);
                }
            }

            uri.Query = query.ToString();
        }

        var result = new ParsedDropUrl(this, OriginalUri: originalUri, EffectiveUrl: uri.Uri, relativeRoot, HasMutableContent: !isSnapshot);
        return result;
    }

    protected void AddFilesUnderDirectory(AbsolutePath path, List<DeploymentFile> deploymentFiles, string relativeRoot)
    {
        relativeRoot ??= "";
        path = path / relativeRoot;

        if (FileSystem.DirectoryExists(path))
        {
            foreach (var file in FileSystem.EnumerateFiles(path, EnumerateOptions.Recurse))
            {
                deploymentFiles.Add(new(GetRelativePath(file.FullPath, parent: path), file.FullPath));
            }
        }
        else
        {
            Contract.Assert(FileSystem.FileExists(path));
            deploymentFiles.Add(new(new RelativePath(path.FileName), path));
        }
    }

    protected RelativePath GetRelativePath(AbsolutePath path, AbsolutePath parent)
    {
        if (path.Path.TryGetRelativePath(parent.Path, out var relativePath))
        {
            return new RelativePath(relativePath);
        }
        else
        {
            throw Contract.AssertFailure($"'{path}' not under expected parent path '{parent}'");
        }
    }
}