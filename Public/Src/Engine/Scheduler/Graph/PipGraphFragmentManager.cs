using System;
using System.Collections.Concurrent;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Tracing;
using BuildXL.Utilities;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Scheduler.Graph
{
    /// <summary>
    /// Manager which controls adding pip fragments to the graph.
    /// </summary>
    public class PipGraphFragmentManager : IPipGraphFragmentManager
    {
        private ConcurrentDictionary<string, Task<bool>> m_readFragmentTasks = new ConcurrentDictionary<string, Task<bool>>();

        private IPipGraph m_pipGraph;

        private PipExecutionContext m_context;

        private PipGraphFragmentContext m_fragmentContext;

        private LoggingContext m_loggingContext;

        /// <summary>
        /// PipGraphFragmentManager
        /// </summary>
        public PipGraphFragmentManager(LoggingContext loggingContext, PipExecutionContext context, IPipGraph pipGraph)
        {
            m_loggingContext = loggingContext;
            m_context = context;
            m_pipGraph = pipGraph;
            m_fragmentContext = new PipGraphFragmentContext();
        }

        /// <summary>
        /// PipGraphFragmentManager
        /// </summary>
        public Task<bool> AddFragmentFileToGraph(string name, AbsolutePath filePath, string[] dependencyNames)
        {
            foreach (var dependencyName in dependencyNames)
            {
                Contract.Assert(m_readFragmentTasks.ContainsKey(dependencyName), "Fragment has not yet been added for dependency: " + dependencyName);
            }

            Task<bool> readFragmentTask = Task.Run(() =>
            {
                Task.WaitAll(dependencyNames.Select(dependencyName => m_readFragmentTasks[dependencyName]).ToArray());
                return ReadPipGraphFragment(m_loggingContext, m_context, m_pipGraph, m_fragmentContext, filePath);
            });

            m_readFragmentTasks[name] = readFragmentTask;
            return readFragmentTask;
        }

        /// <summary>
        /// PipGraphFragmentManager
        /// </summary>
        public async Task<bool> WaitForAllFragmentsToLoad()
        {
            var task = await Task.FromResult(m_readFragmentTasks.Count == 0 ? true : (await Task.WhenAll(m_readFragmentTasks.Values.ToArray())).All(x => x));
            return task;
        }

        /// <summary>
        /// PipGraphFragmentManager
        /// </summary>
        public (int, int) GetPipsAdded(string name)
        {
            throw new NotImplementedException();
        }

        private static bool ReadPipGraphFragment(LoggingContext loggingContext, PipExecutionContext context, IPipGraph pipGraphBuilder, PipGraphFragmentContext pipFragmentContext, AbsolutePath filePath)
        {
            int pipsAdded = 0;
            string fileName = filePath.ToString(context.PathTable);
            using (FileStream stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (RemapReader reader = new RemapReader(pipFragmentContext, stream, context))
            {
                while (reader.ReadBoolean())
                {
                    pipsAdded++;
                    var pip = Pip.Deserialize(reader);
                    pip.ResetPipIdForTesting();

                    bool added = false;
                    switch (pip.PipType)
                    {
                        case PipType.Module:
                            var modulePip = pip as ModulePip;
                            added = pipGraphBuilder.AddModule(modulePip);
                            break;
                        case PipType.SpecFile:
                            var specFilePip = pip as SpecFilePip;
                            added = pipGraphBuilder.AddSpecFile(specFilePip);
                            break;
                        case PipType.Value:
                            var valuePIp = pip as ValuePip;
                            added = pipGraphBuilder.AddOutputValue(valuePIp);
                            break;
                        case PipType.Process:
                            var p = pip as Process;
                            added = pipGraphBuilder.AddProcess(p, default);
                            break;
                        case PipType.CopyFile:
                            var copyFile = pip as CopyFile;
                            added = pipGraphBuilder.AddCopyFile(copyFile, default);
                            break;
                        case PipType.WriteFile:
                            var writeFile = pip as WriteFile;
                            added = pipGraphBuilder.AddWriteFile(writeFile, default);
                            break;
                        case PipType.SealDirectory:
                            var sealDirectory = pip as SealDirectory;
                            if (sealDirectory.Kind == SealDirectoryKind.Opaque || sealDirectory.Kind == SealDirectoryKind.SharedOpaque)
                            {
                                continue;
                            }

                            added = true;
                            var oldDirectory = sealDirectory.Directory;
                            sealDirectory.ResetDirectoryArtifact();
                            var mappedDirectory = pipGraphBuilder.AddSealDirectory(sealDirectory, default);
                            reader.Context.AddMapping(oldDirectory, mappedDirectory);
                            break;
                    }

                    if (!added)
                    {
                        Logger.Log.FailedToAddFragmentPipToGraph(loggingContext);
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
