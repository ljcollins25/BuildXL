// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Engine;
using BuildXL.Engine.Serialization;
using BuildXL.FrontEnd.Sdk.FileSystem;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Filter;
using BuildXL.Scheduler.Graph;
using BuildXL.Storage;
using BuildXL.ToolSupport;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.ParallelAlgorithms;
using Newtonsoft.Json;
using PipGraphFragments;
using static BuildXL.Scheduler.Graph.PipGraph;

namespace BuildXL.FrontEnd.PipGraphFragments
{
    /// <summary>
    /// Main entry point for Tool.CMakeRunner.
    /// This tool invokes our modified cmake.exe and generates a Ninja build in a subdirectory
    /// </summary>
    /// <param name="args">
    /// args[0]: path to the file where the arguments are serialized
    /// </param>
    public class Program : ToolProgram<PipGraphFragmentsArguments>
    {
        private static string Usage => $"Usage: {System.Diagnostics.Process.GetCurrentProcess().MainModule.ModuleName} <path to argument file>";

        private Program()
            : base("PipGraphFragmentRunner")
        { }

        public static int Main(string[] arguments)
        {
            try
            {
                new Program().Run(arguments[0]);// return new Program().MainHandler(arguments);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Unexpected exception: {e}");
            }

            return -1;
        }

        public override int Run(PipGraphFragmentsArguments args)
        {
            return 0;
        }

        public int Run(string cachedDirectory)
        {
            if (!WriteModuleDepsAndFragments(cachedDirectory))
            {
                return 1;
            }

            Stopwatch sw = Stopwatch.StartNew();
            var moduleDependencies = ReadModuleDependencies();

            Console.WriteLine("Done get in order modules in " + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();

            LoggingContext logContext = CreateLoggingContextForTest();

            var configuration = new ConfigurationImpl();
            configuration.Schedule.SkipHashSourceFile = true;

            var pathTable = new PathTable();
            var fileSystem = new PassThroughFileSystem(pathTable);
            var engineContext = EngineContext.CreateNew(CancellationToken.None, pathTable, fileSystem);
            PipFragmentContext pipFragmentContext = new PipFragmentContext();

            MountPathExpander mountPathExpander = ReadMountPathExpander(pipFragmentContext, engineContext);
            ConfigFileState configFileState = ReadConfigFileState(pipFragmentContext, engineContext);

            PipGraph.Builder pg = new PipGraph.Builder(
                CreatePipTable(engineContext),
                engineContext,
                Scheduler.Tracing.Logger.Log,
                logContext,
                configuration,
                mountPathExpander
            );
            ReadPipGraphFragments(engineContext, moduleDependencies, pg, pipFragmentContext);

            Console.WriteLine("Done reading pip fragments. " + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();

            PipGraph completedPipGraph = pg.Build();
            Console.WriteLine("Done building pip graph. " + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();

            string correlationId = ReadCorrelationId();

            bool success = EngineSchedule.SaveExecutionStateToDiskAsync(
                    new EngineSerializer(
                        logContext,
                        @"d:\engineCache",
                        correlationId: new FileEnvelopeId(correlationId)
                    ),
                    engineContext,
                    pg.PipTable,
                    completedPipGraph,
                    mountPathExpander,
                    new HistoricTableSizes(new HistoricDataPoint[0]),
                    configFileState
                    ).Result;

            Console.WriteLine("Done writing new pip graph." + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();

            CachedGraph graph2;
            bool loadedCachedGraph = LoadCacheGraph(@"d:\engineCache", out graph2);
            if (!loadedCachedGraph)
            {
                Console.WriteLine("Could not load cached graph 2");
                return 1;
            }

            Console.WriteLine("Done loading new graph." + sw.ElapsedMilliseconds / 1000.0);

            return 0;
        }

        private string ReadCorrelationId()
        {
            return File.ReadAllText(@"d:\modules\correlationid");
        }

        private void WriteCorrelationId(CachedGraph graph)
        {
            File.WriteAllText(@"d:\modules\correlationid", graph.CorrelationId);
        }

        private bool WriteModuleDepsAndFragments(string cachedDirectory)
        {
            Stopwatch sw = Stopwatch.StartNew();
            CachedGraph graph;
            ContentHashingUtilities.SetDefaultHashType(HashType.Vso0);
            // Console.WriteLine("Starting to load cached graph.");
            bool loadedCachedGraph = LoadCacheGraph(cachedDirectory, out graph);
            Console.WriteLine("Done loading cached graph. " + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();
            if (!loadedCachedGraph)
            {
                Console.WriteLine("Could not load cached graph");
                return false;
            }

            var pipsToModule = WritePipGraphFragments(graph);
            var moduleDependencies = GetModuleDependencies(graph, pipsToModule);
            WriteModuleDependencies(moduleDependencies);
            WriteMountPathExpander(graph.MountPathExpander, graph.Context);
            WriteConfigFileState(graph.ConfigFileState, graph.Context);
            WriteCorrelationId(graph);

            Console.WriteLine("Done writing pip fragments. in " + sw.ElapsedMilliseconds / 1000.0);
            sw.Restart();
            return true;
        }

        private void WriteConfigFileState(ConfigFileState configFileState, PipExecutionContext context)
        {
            using (FileStream stream = new FileStream(@"d:\modules\configFileState", FileMode.Create))
            using (RemapWriter writer = new RemapWriter(stream, context))
            {
                configFileState.Serialize(writer);
            }
        }

        private ConfigFileState ReadConfigFileState(PipFragmentContext pipFragmentContext, PipExecutionContext context)
        {
            using (FileStream stream = new FileStream(@"d:\modules\configFileState", FileMode.Open))
            using (RemapReader reader = new RemapReader(pipFragmentContext, stream, context))
            {
                return ConfigFileState.DeserializeAsync(reader, Task.FromResult(context)).Result;
            }
        }

        private void WriteModuleDependencies(ConcurrentBigMap<string, HashSet<string>> moduleDependencies)
        {
            using (var stream = File.Open(@"d:\modules\moduleDeps.bin", FileMode.Create))
            using (var writer = new BuildXLWriter(false, stream, true, false))
            {
                moduleDependencies.Serialize(
                    writer,
                    kvp =>
                    {
                        writer.Write(kvp.Key);
                        writer.Write(kvp.Value.ToReadOnlyArray(), (w, item) => w.Write(item));
                    });
            }
        }

        private MountPathExpander ReadMountPathExpander(PipFragmentContext pipFragmentContext, PipExecutionContext context)
        {
            using (FileStream stream = new FileStream(@"d:\modules\mountpathexpander", FileMode.Open))
            using (RemapReader reader = new RemapReader(pipFragmentContext, stream, context))
            {
                return MountPathExpander.DeserializeAsync(reader, Task.FromResult(context.PathTable)).Result;
            }
        }

        private void WriteMountPathExpander(MountPathExpander mountPathExpander, PipExecutionContext context)
        {
            using (FileStream stream = new FileStream(@"d:\modules\mountpathexpander", FileMode.Create))
            using (RemapWriter writer = new RemapWriter(stream, context))
            {
                mountPathExpander.Serialize(writer);
            }
        }

        private ConcurrentBigMap<string, HashSet<string>> ReadModuleDependencies()
        {
            using (var stream = File.Open(@"d:\modules\moduleDeps.bin", FileMode.Open))
            using (var reader = new BuildXLReader(false, stream, true))
            {
                return ConcurrentBigMap<string, HashSet<string>>.Deserialize(
                    reader,
                    () =>
                    {
                        return new KeyValuePair<string, HashSet<string>>(
                            reader.ReadString(),
                            new HashSet<string>(reader.ReadArray<string>(r => r.ReadString())));
                    });
            }
        }

        private static void ReadPipGraphFragments(PipExecutionContext context, ConcurrentBigMap<string, HashSet<string>> moduleDependencies, Builder pg, PipFragmentContext pipFragmentContext)
        {
            var moduleDependents = moduleDependencies.SelectMany(kvp => kvp.Value.Select(dependency => (dependent: kvp.Key, dependency))).ToLookup(t => t.dependency, t => t.dependent);
            int total = 0;
            long time = 0;
            var visitedCount = new ConcurrentDictionary<string, int>(moduleDependencies.Select(kvp => new KeyValuePair<string, int>(kvp.Key, kvp.Value.Count)));

            var moduleCount = moduleDependencies.Count;
            var initialModules = moduleDependencies.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToArray();

            int readyCount = initialModules.Length;
            int executingCount = 0;
            int schedulingCount = 0;

            ParallelAlgorithms.WhenDone(1, (scheduleItem, moduleId) =>
            {
                Interlocked.Increment(ref executingCount);
                Interlocked.Decrement(ref readyCount);

                Stopwatch sw = Stopwatch.StartNew();
                ReadPipGraphFragment(context, pg, pipFragmentContext, @"d:\modules\" + moduleId + ".bin");
                Interlocked.Add(ref time, sw.ElapsedTicks);
                if (Interlocked.Increment(ref total) % 100 == 0)
                {
                    Console.WriteLine($"Done {total}/{moduleCount} in: {TimeSpan.FromTicks(time)}.  Read time: {TimeSpan.FromTicks(readTime)} Proc ATG: {TimeSpan.FromTicks(processAddToGraphTime)} Sealled ATG: {TimeSpan.FromTicks(sealDirectoryAddToGraphTime)} Other ATG: {TimeSpan.FromTicks(otherAddToGraphTime)}"
                        + $"E:{executingCount,3}, R:{readyCount,4}, S:{schedulingCount,3}");
                }

                Interlocked.Increment(ref schedulingCount);

                foreach (var dependent in moduleDependents[moduleId])
                {
                    var count = visitedCount.AddOrUpdate(dependent, 0, (k, v) => v - 1);
                    if (count == 0)
                    {
                        Interlocked.Increment(ref readyCount);
                        scheduleItem(dependent);
                    }
                }

                Interlocked.Decrement(ref executingCount);
                Interlocked.Decrement(ref schedulingCount);
            },
            initialModules);
        }

        private static void ReadPipGraphFragments2(PipExecutionContext context, ConcurrentBigMap<string, HashSet<string>> moduleDependencies, Builder pg)
        {
            var pipFragmentContext = new PipFragmentContext();
            int total = 0;
            long time = 0;
            ConcurrentDictionary<string, Task> readTasks = new ConcurrentDictionary<string, Task>();
            ConcurrentDictionary<string, int> visitedCount = new ConcurrentDictionary<string, int>();
            int goneTo = 0;
            foreach (var x in moduleDependencies.Keys)
            {
                visitedCount[x] = 0;
            }

            while (readTasks.Count < moduleDependencies.Count)
            {
                var toRemove = new HashSet<string>(moduleDependencies.Where(x => x.Value.Count == visitedCount[x.Key]).Select(x => x.Key));
                goneTo += toRemove.Count;
                foreach (var moduleId in toRemove)
                {
                    visitedCount[moduleId] = -42;
                    readTasks[moduleId] = Task.Run(async () =>
                    {
                        foreach (var dependentModule in moduleDependencies[moduleId])
                        {
                            await readTasks[dependentModule];
                        }

                        Stopwatch sw = Stopwatch.StartNew();
                        ReadPipGraphFragment(context, pg, pipFragmentContext, @"d:\modules\" + moduleId + ".bin");
                        Interlocked.Add(ref time, sw.ElapsedMilliseconds);
                        if (Interlocked.Increment(ref total) % 100 == 0)
                        {
                            Console.WriteLine("Done with: " + total + " of: " + moduleDependencies.Count + " in: " + time / 1000 + " seconds, " + time / 1000.0 / 60.0 + " minutes.  Read time: " + readTime / 1000 + " process atg: " + processAddToGraphTime / 1000 + " sealled atg: " + sealDirectoryAddToGraphTime / 1000 + " other atg: " + otherAddToGraphTime / 1000);
                        }
                    });
                    foreach (var module in moduleDependencies.Where(x => x.Value.Contains(moduleId)))
                    {
                        visitedCount[module.Key]++;
                    }
                }
            }

            Task.WaitAll(readTasks.Values.ToArray());
        }

        private static long processAddToGraphTime;
        private static long sealDirectoryAddToGraphTime;
        private static long readTime;
        private static long otherAddToGraphTime;

        private static void ReadPipGraphFragment(PipExecutionContext context, Builder pg, PipFragmentContext pipFragmentContext, string file)
        {
            using (FileStream stream = new FileStream(file, FileMode.Open))
            using (RemapReader reader = new RemapReader(pipFragmentContext, stream, context))
            {
                Console.WriteLine("Reading: " + file);
                Stopwatch sw = new Stopwatch();
                while (reader.ReadBoolean())
                {
                    var pip = Pip.Deserialize(reader);
                    Interlocked.Add(ref readTime, sw.ElapsedTicks);
                    sw.Restart();
                    pip.ResetPipIdForTesting();

                    bool added = false;
                    switch (pip.PipType)
                    {
                        case PipType.Module:
                            var modulePip = pip as Pips.Operations.ModulePip;
                            added = pg.AddModule(modulePip);
                            Interlocked.Add(ref otherAddToGraphTime, sw.ElapsedTicks);

                            break;
                        case PipType.SpecFile:
                            var specFilePip = pip as Pips.Operations.SpecFilePip;
                            added = pg.AddSpecFile(specFilePip);
                            Interlocked.Add(ref otherAddToGraphTime, sw.ElapsedTicks);

                            break;
                        case PipType.Value:
                            var valuePIp = pip as Pips.Operations.ValuePip;
                            added = pg.AddOutputValue(valuePIp);
                            Interlocked.Add(ref otherAddToGraphTime, sw.ElapsedTicks);

                            break;
                        case PipType.Process:
                            var p = pip as Pips.Operations.Process;
                            added = pg.AddProcess(p, default);
                            Interlocked.Add(ref processAddToGraphTime, sw.ElapsedTicks);

                            break;
                        case PipType.CopyFile:
                            var copyFile = pip as CopyFile;
                            added = pg.AddCopyFile(copyFile, default);
                            Interlocked.Add(ref otherAddToGraphTime, sw.ElapsedTicks);

                            break;
                        case PipType.WriteFile:
                            var writeFile = pip as WriteFile;
                            added = pg.AddWriteFile(writeFile, default);
                            Interlocked.Add(ref otherAddToGraphTime, sw.ElapsedTicks);

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
                            var mappedDirectory = pg.AddSealDirectory(sealDirectory, default);
                            reader.Context.AddMapping(oldDirectory, mappedDirectory);
                            Interlocked.Add(ref sealDirectoryAddToGraphTime, sw.ElapsedTicks);

                            break;
                    }

                    sw.Restart();

                    if (!added)
                    {
                        Console.WriteLine("Error, pip not added");
                    }
                }
            }
        }

        private static ConcurrentBigMap<NodeId, string> WritePipGraphFragments(CachedGraph graph)
        {
            ConcurrentBigMap<NodeId, string> pipsToModule = new ConcurrentBigMap<NodeId, string>();
            long hydrateTime = 0;
            long findTime = 0;
            long writeTime = 0;
            int done = 0;
            int num = graph.PipGraph.Modules.Keys.Count();
            foreach(var moduleId in graph.PipGraph.Modules.Keys)
            {
                string moduleName = GetModuleName(moduleId, graph.PipTable, graph.PipGraph, graph.Context.StringTable);
                using (FileStream stream = new FileStream(@"d:\modules\" + moduleName + ".bin", FileMode.Create))
                using (RemapWriter writer = new RemapWriter(stream, graph.Context))
                {
                    Stopwatch moduleSW = Stopwatch.StartNew();
                    var pipFilterContext = new PipFilterContext(graph.PipGraph);
                    var pipsInThisModule = PipFilter.GetDependenciesWithOutputsForModulePips(pipFilterContext, new[] { graph.PipGraph.Modules[moduleId].ToPipId() }, true);
                    Interlocked.Add(ref findTime, moduleSW.ElapsedMilliseconds);
                    moduleSW.Restart();

                    List<Pip> hydratedPips = new List<Pip>();
                    foreach (var pipId in graph.PipTable.Keys)
                    {
                        if (pipsInThisModule.Contains(pipId))
                        {
                            hydratedPips.Add(graph.PipTable.HydratePip(pipId, PipQueryContext.PipGraphRetrieveAllPips));
                            pipsToModule[pipId.ToNodeId()] = moduleName;
                        }
                    }

                    Interlocked.Add(ref hydrateTime, moduleSW.ElapsedMilliseconds);
                    moduleSW.Restart();

                    foreach (var pip in hydratedPips)
                    {
                        writer.Write(true);
                        pip.ResetPipIdForTesting();
                        pip.Serialize(writer);
                    }

                    writer.Write(false);
                    Interlocked.Add(ref writeTime, moduleSW.ElapsedMilliseconds);
                    Interlocked.Increment(ref done);
                    if (done % 100 == 0)
                    {
                        Console.WriteLine("Done: " + done + " of " + num + " find time: " + findTime / 1000 + "seconds,  write time: " + writeTime / 1000 + " seconds, hydratetime: " + hydrateTime / 1000 + " seconds = " + hydrateTime / 1000.0 / 60.0 + " minutes");
                    }
                }
            } //);

            return pipsToModule;
        }

        private static ConcurrentBigMap<string, HashSet<string>> GetModuleDependencies(CachedGraph graph, ConcurrentBigMap<NodeId, string> pipsToModule)
        {
            ConcurrentBigMap<string, HashSet<string>> moduleDependecies = new ConcurrentBigMap<string, HashSet<string>>();
            foreach (var moduleId in graph.PipGraph.Modules.Keys)
            {
                string moduleName = GetModuleName(moduleId, graph.PipTable, graph.PipGraph, graph.Context.StringTable);
                moduleDependecies[moduleName] = new HashSet<string>();
            }
            foreach (var pipModule in pipsToModule)
            {
                foreach (var incomingEdge in graph.DataflowGraph.GetIncomingEdges(pipModule.Key))
                {
                    if (pipsToModule.ContainsKey(incomingEdge.OtherNode) && pipsToModule[incomingEdge.OtherNode] != pipModule.Value)
                    {
                        moduleDependecies[pipModule.Value].Add(pipsToModule[incomingEdge.OtherNode]);
                    }
                }
            }

            return moduleDependecies;
        }

        public static string GetModuleName(ModuleId moduleId, PipTable pipTable, PipGraph pipgraph, StringTable stringTable)
        {
            if (!moduleId.IsValid)
            {
                return "Invalid";
            }

            var pip = (ModulePip)pipTable.HydratePip(
                pipgraph.Modules[moduleId].ToPipId(),
                PipQueryContext.SchedulerAreInputsPresentForSkipDependencyBuild);
            return pip.Identity.ToString(stringTable);
        }

        private PipTable CreatePipTable(PipExecutionContext context)
        {
            return new PipTable(
                context.PathTable,
                context.SymbolTable,
                initialBufferSize: 16,
                maxDegreeOfParallelism: Environment.ProcessorCount,
                debug: true);
        }

        /// <summary>
        /// Creates a LoggingContext for use by tests
        /// </summary>
        public static LoggingContext CreateLoggingContextForTest()
        {
            return new LoggingContext(loggerComponentInfo: "BuildXLTest", environment: "BuildXLTest");
        }

        public bool LoadCacheGraph(string cachedGraphDirectory, out CachedGraph cachedGraph)
        {
            cachedGraph = null;
            if (string.IsNullOrEmpty(cachedGraphDirectory))
            {
                return false;
            }

            // Dummy logger that nothing listens to but is needed for cached graph API
            LoggingContext loggingContext = new LoggingContext("BuildXL.PipGraphFragments");
            cachedGraph = CachedGraph.LoadAsync(cachedGraphDirectory, loggingContext, preferLoadingEngineCacheInMemory: true, readStreamProvider: FileSystemStreamProvider.Default).GetAwaiter().GetResult();
            if (cachedGraph == null)
            {
                return false;
            }

            return true;
        }

        public override bool TryParse(string[] rawArgs, out PipGraphFragmentsArguments arguments)
        {
            if (rawArgs.Length < 1)
            {
                Console.Error.WriteLine(Usage);
                arguments = default;
                return false;
            }

            arguments = DeserializeArguments(rawArgs[0]);
            return true;
        }

        private static PipGraphFragmentsArguments DeserializeArguments(string file)
        {
            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                NullValueHandling = NullValueHandling.Include,
                Formatting = Formatting.Indented
            });

            PipGraphFragmentsArguments arguments;
            using (var sr = new StreamReader(file))
            using (var reader = new JsonTextReader(sr))
            {
                arguments = serializer.Deserialize<PipGraphFragmentsArguments>(reader);
            }

            return arguments;
        }
    }
}
