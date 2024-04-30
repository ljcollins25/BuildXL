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

public record ParsedDropUrl(IDeploymentIngesterUrlHandler Handler, Uri OriginalUri, Uri EffectiveUrl, string RelativeRoot)
{
    public bool IsFile => !EffectiveUrl.IsAbsoluteUri || EffectiveUrl.IsFile || OriginalUri == DeploymentUtilities.ConfigDropUri;

    /// <summary>
    /// Indicates whether the url represents content which may change.
    /// </summary>
    public bool HasMutableContent { get; init; }

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
                AddFilesUnderDirectory(targetDirectory, deploymentFiles);
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

        AddFilesUnderDirectory(outputDir, deploymentFiles);

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
            if (FileSystem.DirectoryExists(path))
            {
                AddFilesUnderDirectory(path, files);
            }
            else
            {
                Contract.Assert(FileSystem.FileExists(path));
                files.Add(new(new RelativePath(path.FileName), path));
            }
        }

        return BoolResult.Success;
    }
}

public class DropDeploymentIngesterUrlHandler(AbsolutePath dropExeFilePath, string dropToken, DeploymentIngesterBaseConfiguration configuration)
    : DeploymentIngesterUrlHandlerBase(configuration)
{
    public override string Name => "drop";

    public override Tracer Tracer { get; } = new Tracer(nameof(DropDeploymentIngesterUrlHandler));

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

        var filesRoot = tempDirectory / url.RelativeRoot;
        AddFilesUnderDirectory(filesRoot, deploymentFiles);

        return BoolResult.Success;
    }
}

public abstract class DeploymentIngesterUrlHandlerBase(DeploymentIngesterBaseConfiguration configuration) : IDeploymentIngesterUrlHandler
{
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
        bool hasSnapshot = false;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            relativeRoot = query.Get("__root") ?? query.Get("root") ?? "";
            hasSnapshot = query.Get("__snapshot") != null;
            foreach (string key in query.AllKeys)
            {
                if (key.StartsWith("__"))
                {
                    query.Remove(key);
                }
            }

            uri.Query = query.ToString();
        }

        var result = new ParsedDropUrl(this, OriginalUri: originalUri, EffectiveUrl: uri.Uri, relativeRoot);
        if (result.IsFile && !hasSnapshot)
        {
            // Unless snapshot parameters is added. File uris are considered to represent mutable content
            // by default.
            result = result with { HasMutableContent = true };
        }

        return result;
    }

    protected void AddFilesUnderDirectory(AbsolutePath filesRoot, List<DeploymentFile> deploymentFiles)
    {
        foreach (var file in FileSystem.EnumerateFiles(filesRoot, EnumerateOptions.Recurse))
        {
            deploymentFiles.Add(new(GetRelativePath(file.FullPath, parent: filesRoot), file.FullPath));
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