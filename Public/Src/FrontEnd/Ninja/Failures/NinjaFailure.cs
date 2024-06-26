// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Core;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.FrontEnd.Ninja
{
    /// <nodoc />
    internal abstract class NinjaFailure : Failure
    {
        /// <inheritdoc/>
        public override BuildXLException CreateException() => new BuildXLException(Describe());
        
        /// <inheritdoc/>
        public override BuildXLException Throw() => throw CreateException();
    }


    internal class NinjaGraphConstructionFailure : NinjaFailure
    {
        private readonly string m_projectRoot;
        private readonly string m_moduleName;
       
        /// <nodoc/>
        public NinjaGraphConstructionFailure(string moduleName, string projectRoot)
        {
            m_projectRoot = projectRoot;
            m_moduleName = moduleName;
        }

        /// <inheritdoc/>
        public override string Describe() => I($"A project graph could not be constructed when parsing Ninja module '{m_moduleName}' starting at root '{m_projectRoot}'. Detailed errors should have already been logged.");
    }


}
