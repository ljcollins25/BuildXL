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

namespace BuildXL.Cache.ContentStore.Vfs.Managed
{
    using VirtualPath = Utilities.AbsolutePath;
    using Utils = Microsoft.Windows.ProjFS.Utils;

    /// <summary>
    /// This is a simple file system "reflector" provider.  It projects files and directories from
    /// a directory called the "layer root" into the virtualization root, also called the "scratch root".
    /// </summary>
    public class SimpleProvider
    {
        private Logger Log;
        private VirtualizationRegistry Registry;
        private VfsTree Tree;

        // These variables hold the layer and scratch paths.
        private readonly string scratchRoot;
        private readonly string layerRoot;
        private readonly int currentProcessId = Process.GetCurrentProcess().Id;

        private readonly VirtualizationInstance virtualizationInstance;

        // TODO: Cache enumeration listings
        private readonly ObjectCache<string, List<VfsNode>> enumerationCache;
        private readonly ConcurrentDictionary<Guid, ActiveEnumeration> activeEnumerations;
        private readonly ConcurrentDictionary<int, CancellationTokenSource> activeCommands = new ConcurrentDictionary<int, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, Lazy<HResult>> pendingHardlinkCreations = new ConcurrentDictionary<string, Lazy<HResult>>(
            StringComparer.OrdinalIgnoreCase);

        private NotificationCallbacks notificationCallbacks;

        public ProviderOptions Options { get; }

        public SimpleProvider(ProviderOptions options)
        {
            scratchRoot = options.VirtRoot;
            layerRoot = options.SourceRoot;

            Options = options;

            // If in test mode, enable notification callbacks.
            if (Options.TestMode)
            {
                Options.EnableNotifications = true;
            }

            // Enable notifications if the user requested them.
            List<NotificationMapping> notificationMappings;
            if (Options.EnableNotifications)
            {
                notificationMappings = new List<NotificationMapping>()
                {
                    new NotificationMapping(
                        NotificationType.FileOpened
                        | NotificationType.NewFileCreated
                        | NotificationType.FileOverwritten
                        | NotificationType.PreDelete
                        | NotificationType.PreRename
                        | NotificationType.PreCreateHardlink
                        | NotificationType.FileRenamed
                        | NotificationType.HardlinkCreated
                        | NotificationType.FileHandleClosedNoModification
                        | NotificationType.FileHandleClosedFileModified
                        | NotificationType.FileHandleClosedFileDeleted
                        | NotificationType.FilePreConvertToFull,
                        string.Empty)
                };
            }
            else
            {
                notificationMappings = new List<NotificationMapping>();
            }

            try
            {
                // This will create the virtualization root directory if it doesn't already exist.
                virtualizationInstance = new VirtualizationInstance(
                    scratchRoot,
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

            Log.Info("Created instance. Layer [{Layer}], Scratch [{Scratch}]", layerRoot, scratchRoot);

            if (Options.TestMode)
            {
                Log.Info("Provider started in TEST MODE.");
            }

            activeEnumerations = new ConcurrentDictionary<Guid, ActiveEnumeration>();
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

        protected async Task<HResult> HydrateFileAsync(VirtualFileBase virtualFile, uint bufferSize, Func<byte[], uint, bool> tryWriteBytes)
        {
            var openStreamResult = await virtualFile.TryOpenStreamAsync();
            if (!openStreamResult.Succeeded)
            {
                return HResult.InternalError;
            }

            // Open the file in the layer for read.
            using (var fs = openStreamResult.Stream)
            {
                long remainingDataLength = fs.Length;
                byte[] buffer = new byte[bufferSize];

                while (remainingDataLength > 0)
                {
                    // Read from the file into the read buffer.
                    int bytesToCopy = (int)Math.Min(remainingDataLength, buffer.Length);
                    if (fs.Read(buffer, 0, bytesToCopy) != bytesToCopy)
                    {
                        return HResult.InternalError;
                    }

                    // Write the bytes we just read into the scratch.
                    if (!tryWriteBytes(buffer, (uint)bytesToCopy))
                    {
                        return HResult.InternalError;
                    }

                    remainingDataLength -= bytesToCopy;
                }
            }

            return HResult.Ok;
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

            // FileRealizationMode.Copy = just create a normal placeholder

            // FileRealizationMode.Hardlink:
            // 1. Try to create a placeholder by hardlinking from local CAS
            // 2. Create hardlink in VFS unified CAS

            HResult hr = HResult.Ok;
            if (!Tree.TryGetNode(relativePath, out var node))
            {
                hr = HResult.FileNotFound;
            }
            else
            {
                hr = virtualizationInstance.WritePlaceholderInfo(
                    relativePath: Path.Combine(Path.GetDirectoryName(relativePath), node.Name),
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

            HResult hr = HResult.Ok;
            if (!Registry.TryGetVirtualFile(relativePath, out var virtualFile))
            {
                return HResult.FileNotFound;
            }

            return HandleCommandAsynchronously(commandId, token =>
            {
                // We'll write the file contents to ProjFS no more than 64KB at a time.
                uint desiredBufferSize = Math.Min(64 * 1024, length);
                // We could have used VirtualizationInstance.CreateWriteBuffer(uint), but this 
                // illustrates how to use its more complex overload.  This method gets us a 
                // buffer whose underlying storage is properly aligned for unbuffered I/O.
                using (IWriteBuffer writeBuffer = virtualizationInstance.CreateWriteBuffer(
                    byteOffset,
                    desiredBufferSize,
                    out ulong alignedWriteOffset,
                    out uint alignedBufferSize))
                {
                    // Get the file data out of the layer and write it into ProjFS.
                    return HydrateFileAsync(
                        virtualFile,
                        alignedBufferSize,
                        (readBuffer, bytesToCopy) =>
                        {
                            // readBuffer contains what HydrateFile() read from the file in the
                            // layer.  Now seek to the beginning of the writeBuffer and copy the
                            // contents of readBuffer into writeBuffer.
                            writeBuffer.Stream.Seek(0, SeekOrigin.Begin);
                            writeBuffer.Stream.Write(readBuffer, 0, (int)bytesToCopy);

                            // Write the data from the writeBuffer into the scratch via ProjFS.
                            HResult writeResult = virtualizationInstance.WriteFileData(
                                dataStreamId,
                                writeBuffer,
                                alignedWriteOffset,
                                bytesToCopy);

                            if (writeResult != HResult.Ok)
                            {
                                Log.Error("VirtualizationInstance.WriteFileData failed: {Result}", writeResult);
                                return false;
                            }

                            alignedWriteOffset += bytesToCopy;
                            return true;
                        });
                }
            });
        }

        private HResult QueryFileNameCallback(string relativePath)
        {
            Log.Info("----> QueryFileNameCallback relativePath [{Path}]", relativePath);

            string parentPath = Path.GetDirectoryName(relativePath);
            string childName = Path.GetFileName(relativePath);
            if (!Utils.DoesNameContainWildCards(childName))
            {
                // No wildcards just do normal lookup of the file
                if (Tree.TryGetNode(relativePath, out var node))
                {
                    return HResult.Ok;
                }
                else
                {
                    return HResult.FileNotFound;
                }
            }

            if (Tree.TryGetNode(parentPath, out var parent) && parent is VfsDirectoryNode parentDirectory)
            {
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
            private readonly SimpleProvider provider;

            public RequiredCallbacks(SimpleProvider provider) => this.provider = provider;

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
