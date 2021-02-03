﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Pips;
using BuildXL.Pips.Graph;
using BuildXL.Utilities;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Scheduler.Tracing
{
    /// <summary>
    /// Logging target for events triggered when a Pip fails.
    /// </summary>
    public sealed class DumpPipLiteExecutionLogTarget : ExecutionLogTargetBase
    {
        /// <summary>
        /// Used to hydrate pips from <see cref="PipId"/>s.
        /// </summary>
        private readonly PipTable m_pipTable;

        /// <summary>
        /// Execution context pointer for path table, symbol table, and string table.
        /// </summary>
        private readonly PipExecutionContext m_pipExecutionContext;

        /// <summary>
        /// Context for logging methods.
        /// </summary>
        private readonly LoggingContext m_loggingContext;

        /// <summary>
        /// Pip graph used to extract information from failing pip.
        /// </summary>
        private readonly PipGraph m_pipGraph;

        /// <summary>
        /// Path to the folder containing the failed pip logs - LogFolder/FailedPips.
        /// </summary>
        /// <remarks> This path is only created on the first Analyze call, if the analyzer is not called then it will not be created. </remarks>
        private readonly AbsolutePath m_logPath;

        /// <summary>
        /// Indicates whether the log path was already created.
        /// </summary>
        private bool m_logPathCreated;

        /// <summary>
        /// The maximum amount of log files that should be generated by this analyzer per run
        /// </summary>
        private readonly int m_maxLogFiles;

        /// <summary>
        /// The number of log files that have already been generated by this analyzer
        /// </summary>
        private int m_numLogFilesGenerated;

        /// <summary>
        /// If an error occured while logging, this will be set to true, and future logging requests will be skipped.
        /// </summary>
        private bool m_loggingErrorOccured;

        /// <summary>
        /// Intialize execution log target and create log directory.
        /// </summary>
        /// <remarks>
        /// If log directory creation fails, then runtime failed pip analysis will be disabled for this build.
        /// </remarks>
        public DumpPipLiteExecutionLogTarget(PipExecutionContext context, 
                                             PipTable pipTable, 
                                             LoggingContext loggingContext,
                                             IConfiguration configuration,
                                             PipGraph graph)
        {
            m_pipTable = pipTable;
            m_pipExecutionContext = context;
            m_loggingContext = loggingContext;
            m_pipGraph = graph;
            m_logPath = configuration.Logging.LogsDirectory.Combine(context.PathTable, "FailedPips"); // This path has not been created yet
            m_logPathCreated = false;
            m_loggingErrorOccured = false;
            m_maxLogFiles = 50; //TODO: Update this variable with the value from the command line argument
            m_numLogFilesGenerated = 0;
        }

        /// <inheritdoc/>
        public override bool CanHandleWorkerEvents => true;

        /// <inheritdoc/>
        public override IExecutionLogTarget CreateWorkerTarget(uint workerId) => this;

        /// <summary>
        /// Hooks into the log target for pip execution performance data which will be called
        /// when a pip fails. This will then dump relevant information on the failing pip
        /// to a JSON file specified under <see cref="m_logPath"/>.
        /// The maximum number of logs generated can be specified using the 
        /// /DumpFailedPipsLogCount parameter.
        /// </summary>
        /// <remarks>
        /// If an error occurs while serializing/dumping the specified pip,
        /// then this analyzer will be disabled for the remainder of this build and
        /// a warning will be logged with more details.
        /// </remarks>
        public override void PipExecutionPerformance(PipExecutionPerformanceEventData data)
        {
            if (data.ExecutionPerformance.ExecutionLevel == PipExecutionLevel.Failed)
            {
                if (m_numLogFilesGenerated < m_maxLogFiles && !m_loggingErrorOccured)
                {
                    if (!m_logPathCreated)
                    {
                        // A log entry should have been generated already if this fails
                        m_logPathCreated = DumpPipLiteAnalysisUtilities.CreateLoggingDirectory(m_logPath.ToString(m_pipExecutionContext.PathTable), m_loggingContext);
                    }

                    if (m_logPathCreated)
                    {
                        var pip = m_pipTable.HydratePip(data.PipId, PipQueryContext.RuntimeDumpPipLiteAnalyzer);

                        // A log entry should have been generated already if this fails
                        var dumpPipResult = DumpPipLiteAnalysisUtilities.DumpPip(pip,
                                                                                 m_logPath.ToString(m_pipExecutionContext.PathTable),
                                                                                 m_pipExecutionContext.PathTable,
                                                                                 m_pipExecutionContext.StringTable,
                                                                                 m_pipExecutionContext.SymbolTable,
                                                                                 m_pipGraph);

                        if (dumpPipResult)
                        {
                            m_numLogFilesGenerated++;

                            if (m_numLogFilesGenerated >= m_maxLogFiles)
                            {
                                // Log limit reached, log this once
                                Logger.Log.RuntimeDumpPipLiteLogLimitReached(m_loggingContext, m_maxLogFiles);
                            }
                        }
                        else
                        {
                            // This failure was already logged in DumpPipLiteAnalysisUtilies
                            m_loggingErrorOccured = true;
                        }
                    }
                    else
                    {
                        // This failure was already logged in DumpPipLiteAnalysisUtilies
                        m_loggingErrorOccured = true;
                    }
                }
            }
        }
    }
}
