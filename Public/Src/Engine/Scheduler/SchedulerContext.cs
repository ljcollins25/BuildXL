// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using System.Threading;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Qualifier;

namespace BuildXL.Scheduler
{
    /// <summary>
    /// Context used by the Scheduler for scheduling and executing pips
    /// </summary>
    public sealed class SchedulerContext : PipExecutionContext
    {
        /// <summary>
        /// Context used during schedule execution
        /// </summary>
        public SchedulerContext(
            CancellationToken cancellationToken,
            StringTable stringTable,
            PathTable pathTable,
            SymbolTable symbolTable,
            QualifierTable qualifierTable)
            : base(
                cancellationToken,
                stringTable,
                pathTable,
                symbolTable,
                qualifierTable)
        {
            Contract.Requires(stringTable != null);
            Contract.Requires(pathTable != null);
            Contract.Requires(symbolTable != null);
            Contract.Requires(qualifierTable != null);
        }

        /// <summary>
        /// Context used during schedule execution
        /// </summary>
        public SchedulerContext(BuildXLContext context)
            : this(
                context.CancellationToken,
                context.StringTable,
                context.PathTable,
                context.SymbolTable,
                context.QualifierTable)
        {
            Contract.Requires(context != null);
        }
    }
}
