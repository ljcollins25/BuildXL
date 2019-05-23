// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Microsoft.Windows.ProjFS;
using System.Diagnostics;
using BuildXL.Cache.ContentStore.Logging;
using System.Threading.Tasks;
using BuildXL.Utilities.Collections;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Native.IO;
using System.Diagnostics.ContractsLight;

namespace BuildXL.Cache.ContentStore.Vfs.Managed
{
    using AbsolutePath = Interfaces.FileSystem.AbsolutePath;
    using VirtualPath = Utilities.AbsolutePath;
    using Utils = Microsoft.Windows.ProjFS.Utils;

    /// <summary>
    /// This is a simple file system "reflector" provider.  It projects files and directories from
    /// a directory called the "layer root" into the virtualization root, also called the "scratch root".
    /// </summary>
    public class VfsProvider
    {
        private Logger Log;
        private VirtualizationRegistry Registry;
        private VfsTree Tree;

        private string _casRelativePrefix;
        private VfsCasConfiguration _configuration;

        // These variables hold the layer and scratch paths.
        private readonly int currentProcessId = Process.GetCurrentProcess().Id;

        private readonly VirtualizationInstance virtualizationInstance;

        // TODO: Cache enumeration listings
        private readonly ObjectCache<string, List<VfsNode>> enumerationCache;
        private readonly ConcurrentDictionary<Guid, ActiveEnumeration> activeEnumerations = new ConcurrentDictionary<Guid, ActiveEnumeration>();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> activeCommands = new ConcurrentDictionary<int, CancellationTokenSource>();

        private NotificationCallbacks notificationCallbacks;

        public VfsProvider(VfsCasConfiguration configuration)
        {
            // Enable notifications if the user requested them.
            var notificationMappings = new List<NotificationMapping>();

            try
            {
                // This will create the virtualization root directory if it doesn't already exist.
                virtualizationInstance = new VirtualizationInstance(
                    configuration.VfsRootPath.Path,
                    poolThreadCount: 0,
                    concurrentThreadCount: 0,
                    enableNegativePathCache: false,
                    notificationMappings: notificationMappings);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed to create VirtualizationInstance.");
                throw;
            }

            // Set up notifications.
            notificationCallbacks = new NotificationCallbacks(
                this,
                virtualizationInstance,
                notificationMappings);
        }

        public bool StartVirtualization()
        {
            // Optional callbacks
            virtualizationInstance.OnQueryFileName = QueryFileNameCallback;

            RequiredCallbacks requiredCallbacks = new RequiredCallbacks(this);
            HResult hr = virtualizationInstance.StartVirtualizing(requiredCallbacks);
            if (hr != HResult.Ok)
            {
                Log.Error("Failed to start virtualization instance: {Result}", hr);
                return false;
            }

            return true;
        }

        private HResult IgnoreReentrantFileOperations(string relativePath, Func<HResult> action)
        {
            throw new NotImplementedException();
        }

        private HResult HandleCommandAsynchronously(int commandId, Func<CancellationToken, Task<HResult>> handleAsync)
        {
            var cts = new CancellationTokenSource();
            if (!activeCommands.TryAdd(commandId, cts))
            {
                cts.Dispose();
                return Placeholder.Todo<HResult>("How to handle this case? Certainly need to log.");
            }

            runAsync();
            return HResult.Pending;

            async void runAsync()
            {
                try
                {
                    using (cts)
                    {
                        var result = await Task.Run(() => handleAsync(cts.Token));
                        completeCommand(result);
                    }
                }
                catch (Exception ex)
                {
                    completeCommand(Placeholder.Todo<HResult>("How to handle this case? Certainly need to log.", HResult.InternalError));
                }
            }

            void completeCommand(HResult result)
            {
                if (activeCommands.TryRemove(commandId, out _))
                {
                    var completionResult = virtualizationInstance.CompleteCommand(commandId, result);
                    if (completionResult != HResult.Ok)
                    {
                        Placeholder.Todo<bool>("What to do in this case? Try reporting result after an interval to ensure pending result has already been reported??");
                    }
                }
            }
        }

        protected List<VfsNode> GetChildItemsSorted(string relativePath)
        {
            if (!enumerationCache.TryGetValue(relativePath, out var items))
            {
                items = EnumerateChildItems(relativePath).ToListSorted(ProjectedFileNameSorter.Instance);
                enumerationCache.AddItem(relativePath, items);
            }

            return items;
        }

        protected IEnumerable<VfsNode> EnumerateChildItems(string relativePath)
        {
            if (Tree.TryGetNode(relativePath, out var node) && node is VfsDirectoryNode dirNode)
            {
                return dirNode.EnumerateChildren();
            }

            return Enumerable.Empty<VfsNode>();
        }

        #region Callback implementations

        // To keep all the callback implementations together we implement the required callbacks in
        // the SimpleProvider class along with the optional QueryFileName callback.  Then we have the
        // IRequiredCallbacks implementation forward the calls to here.

        internal HResult StartDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            Log.Info("----> StartDirectoryEnumerationCallback Path [{Path}]", relativePath);

            // Enumerate the corresponding directory in the layer and ensure it is sorted the way
            // ProjFS expects.
            ActiveEnumeration activeEnumeration = new ActiveEnumeration(GetChildItemsSorted(relativePath));

            // Insert the layer enumeration into our dictionary of active enumerations, indexed by
            // enumeration ID.  GetDirectoryEnumerationCallback will be able to find this enumeration
            // given the enumeration ID and return the contents to ProjFS.
            if (!activeEnumerations.TryAdd(enumerationId, activeEnumeration))
            {
                return HResult.InternalError;
            }

            Log.Info("<---- StartDirectoryEnumerationCallback {Result}", HResult.Ok);

            return HResult.Ok;
        }

        internal HResult GetDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string filterFileName,
            bool restartScan,
            IDirectoryEnumerationResults enumResult)
        {
            Log.Info("----> GetDirectoryEnumerationCallback filterFileName [{Filter}]", filterFileName);

            // Find the requested enumeration.  It should have been put there by StartDirectoryEnumeration.
            if (!activeEnumerations.TryGetValue(enumerationId, out ActiveEnumeration enumeration))
            {
                return HResult.InternalError;
            }

            if (restartScan)
            {
                // The caller is restarting the enumeration, so we reset our ActiveEnumeration to the
                // first item that matches filterFileName.  This also saves the value of filterFileName
                // into the ActiveEnumeration, overwriting its previous value.
                enumeration.RestartEnumeration(filterFileName);
            }
            else
            {
                // The caller is continuing a previous enumeration, or this is the first enumeration
                // so our ActiveEnumeration is already at the beginning.  TrySaveFilterString()
                // will save filterFileName if it hasn't already been saved (only if the enumeration
                // is restarting do we need to re-save filterFileName).
                enumeration.TrySaveFilterString(filterFileName);
            }

            bool entryAdded = false;
            HResult hr = HResult.Ok;

            while (enumeration.IsCurrentValid)
            {
                VfsNode node = enumeration.Current;

                if (enumResult.Add(
                    fileName: node.Name,
                    fileSize: node.Size,
                    isDirectory: node.IsDirectory,
                    fileAttributes: node.Attributes,
                    creationTime: node.Timestamp,
                    lastAccessTime: node.Timestamp,
                    lastWriteTime: node.Timestamp,
                    changeTime: node.Timestamp))
                {
                    entryAdded = true;
                    enumeration.MoveNext();
                }
                else
                {
                    if (entryAdded)
                    {
                        hr = HResult.Ok;
                    }
                    else
                    {
                        hr = HResult.InsufficientBuffer;
                    }

                    break;
                }
            }

            Log.Info("<---- GetDirectoryEnumerationCallback {Result}", hr);
            return hr;
        }

        internal HResult EndDirectoryEnumerationCallback(
            Guid enumerationId)
        {
            Log.Info("----> EndDirectoryEnumerationCallback");

            if (!activeEnumerations.TryRemove(enumerationId, out ActiveEnumeration enumeration))
            {
                return HResult.InternalError;
            }

            Log.Info("<---- EndDirectoryEnumerationCallback {Result}", HResult.Ok);

            return HResult.Ok;
        }

        internal HResult GetPlaceholderInfoCallback(
            int commandId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            Log.Info("----> GetPlaceholderInfoCallback [{Path}]", relativePath);
            Log.Info("  Placeholder creation triggered by [{ProcName} {PID}]", triggeringProcessImageFileName, triggeringProcessId);

            if (triggeringProcessId == currentProcessId)
            {
                // The current process cannot trigger placeholder creation to prevent deadlock do to re-entrancy
                // Just pretend the file doesn't exist.
                return HResult.FileNotFound;
            }

            // TODO: Prevent recursion for creation of placeholder for CAS relative path and potentially when replacing symlink at real location

            if (relativePath.StartsWith(_casRelativePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var casRelativePath = relativePath.Substring(_casRelativePrefix.Length);
                if (VfsUtilities.TryParseCasRelativePath(casRelativePath, out var hash, out var index)
                    && Tree.TryGetSpecificFileNode(hash, index, out var fileNode))
                {
                    return HandleCommandAsynchronously(commandId, async token =>
                    {
                        await Registry.PlaceVirtualFileAsync(relativePath, fileNode, token);

                        // TODO: Create hardlink / move to original location to replace symlink?
                        return HResult.Ok;
                    });
                }
                else
                {
                    return HResult.FileNotFound;
                }
            }

            // FileRealizationMode.Copy = just create a normal placeholder

            // FileRealizationMode.Hardlink:
            // 1. Try to create a placeholder by hardlinking from local CAS
            // 2. Create hardlink in VFS unified CAS

            HResult hr = HResult.Ok;
            if (!Tree.TryGetNode(relativePath, out var node, out var nodeIndex))
            {
                hr = HResult.FileNotFound;
            }
            else if (node.IsDirectory)
            {
                hr = virtualizationInstance.WritePlaceholderInfo(
                    relativePath: relativePath.EndsWith(node.Name) ? relativePath : Path.Combine(Path.GetDirectoryName(relativePath), node.Name),
                    creationTime: node.Timestamp,
                    lastAccessTime: node.Timestamp,
                    lastWriteTime: node.Timestamp,
                    changeTime: node.Timestamp,
                    fileAttributes: node.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                    endOfFile: node.Size,
                    isDirectory: node.IsDirectory,
                    contentId: new byte[] { 0 },
                    providerId: new byte[] { 1 });
            }
            else
            {
                hr = IgnoreReentrantFileOperations(relativePath, () =>
                {
                    var fileNode = (VfsFileNode)node;
                    var result = Registry.TryCreateSymlink(nodeIndex, fileNode);

                    return result ? HResult.Ok : HResult.InternalError;
                });
            }

            Log.Info("<---- GetPlaceholderInfoCallback {Result}", hr);
            return hr;
        }

        internal HResult GetFileDataCallback(
            int commandId,
            string relativePath,
            ulong byteOffset,
            uint length,
            Guid dataStreamId,
            byte[] contentId,
            byte[] providerId,
            uint triggeringProcessId,
            string triggeringProcessImageFileName)
        {
            Log.Info("----> GetFileDataCallback relativePath [{Path}]", relativePath);
            Log.Info("  triggered by [{ProcName} {PID}]", triggeringProcessImageFileName, triggeringProcessId);

            // We should never create file placeholders so this should not be necessary
            return HResult.FileNotFound;
        }

        private HResult QueryFileNameCallback(string relativePath)
        {
            Log.Info("----> QueryFileNameCallback relativePath [{Path}]", relativePath);

            // First try normal lookup
            if (Tree.TryGetNode(relativePath, out var node))
            {
                return HResult.Ok;
            }

            if (!Utils.DoesNameContainWildCards(relativePath))
            {
                // No wildcards and normal lookup failed so file must not exist
                return HResult.FileNotFound;
            }

            // There are wildcards, enumerate and try to find a matching child
            string parentPath = Path.GetDirectoryName(relativePath);

            if (Tree.TryGetNode(parentPath, out var parent) && parent is VfsDirectoryNode parentDirectory)
            {
                string childName = Path.GetFileName(relativePath);
                if (parentDirectory.EnumerateChildren().Any(child => Utils.IsFileNameMatch(child.Name, childName)))
                {
                    return HResult.Ok;
                }
            }

            return HResult.FileNotFound;
        }

        #endregion


        private class RequiredCallbacks : IRequiredCallbacks
        {
            private readonly VfsProvider provider;

            public RequiredCallbacks(VfsProvider provider) => this.provider = provider;

            // We implement the callbacks in the SimpleProvider class.

            public HResult StartDirectoryEnumerationCallback(
                int commandId,
                Guid enumerationId,
                string relativePath,
                uint triggeringProcessId,
                string triggeringProcessImageFileName)
            {
                return provider.StartDirectoryEnumerationCallback(
                    commandId,
                    enumerationId,
                    relativePath,
                    triggeringProcessId,
                    triggeringProcessImageFileName);
            }

            public HResult GetDirectoryEnumerationCallback(
                int commandId,
                Guid enumerationId,
                string filterFileName,
                bool restartScan,
                IDirectoryEnumerationResults enumResult)
            {
                return provider.GetDirectoryEnumerationCallback(
                    commandId,
                    enumerationId,
                    filterFileName,
                    restartScan,
                    enumResult);
            }

            public HResult EndDirectoryEnumerationCallback(
                Guid enumerationId)
            {
                return provider.EndDirectoryEnumerationCallback(enumerationId);
            }

            public HResult GetPlaceholderInfoCallback(
                int commandId,
                string relativePath,
                uint triggeringProcessId,
                string triggeringProcessImageFileName)
            {
                return provider.GetPlaceholderInfoCallback(
                    commandId,
                    relativePath,
                    triggeringProcessId,
                    triggeringProcessImageFileName);
            }

            public HResult GetFileDataCallback(
                int commandId,
                string relativePath,
                ulong byteOffset,
                uint length,
                Guid dataStreamId,
                byte[] contentId,
                byte[] providerId,
                uint triggeringProcessId,
                string triggeringProcessImageFileName)
            {
                return provider.GetFileDataCallback(
                    commandId,
                    relativePath,
                    byteOffset,
                    length,
                    dataStreamId,
                    contentId,
                    providerId,
                    triggeringProcessId,
                    triggeringProcessImageFileName);
            }
        }
    }
}
