﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.FrontEnd.Workspaces.Core;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>
    /// Encapsulates a <see cref="NinjaGraph"/> with its corresponding <see cref="ModuleDefinition"/>
    /// </summary>
    public readonly struct NinjaGraphWithModuleDefinition : ISingleModuleProjectGraphResult
    {
        /// <nodoc/>
        public NinjaGraph Graph { get; }

        /// <nodoc/>
        public ModuleDefinition ModuleDefinition { get; }

        /// <nodoc />
        public NinjaGraphWithModuleDefinition(NinjaGraph graph, ModuleDefinition moduleDefinition) 
        {
            Contract.Requires(graph != null);
            Contract.Requires(moduleDefinition != null);

            Graph = graph;
            ModuleDefinition = moduleDefinition;
        }
    }
}
