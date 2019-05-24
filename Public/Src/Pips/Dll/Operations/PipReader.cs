// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Utilities;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// An extended binary writer that can read Pips
    /// </summary>
    /// <remarks>
    /// This type is internal, as the serialization/deserialization functionality is encapsulated by the PipTable.
    /// </remarks>
    public class PipReader : BuildXLReader
    {
        /// <summary>
        /// PipReader
        /// </summary>
        public PipReader(bool debug, StringTable stringTable, Stream stream, bool leaveOpen)
            : base(debug, stream, leaveOpen)
        {
            Contract.Requires(stringTable != null);
            StringTable = stringTable;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public StringTable StringTable { get; }

        /// <summary>
        /// PipReader
        /// </summary>
        public Pip ReadPip()
        {
            Contract.Ensures(Contract.Result<Pip>() != null);
            Start<Pip>();
            Pip value = Pip.Deserialize(this);
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public PipProvenance ReadPipProvenance()
        {
            Contract.Ensures(Contract.Result<PipProvenance>() != null);
            Start<PipProvenance>();
            PipProvenance value = PipProvenance.Deserialize(this);
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public virtual StringId ReadPipDataId()
        {
            return ReadStringId();
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public virtual PipData ReadPipData()
        {
            Start<PipData>();
            PipData value = PipData.Deserialize(this);
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public EnvironmentVariable ReadEnvironmentVariable()
        {
            Start<EnvironmentVariable>();
            EnvironmentVariable value = EnvironmentVariable.Deserialize(this);
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public RegexDescriptor ReadRegexDescriptor()
        {
            Start<RegexDescriptor>();
            RegexDescriptor value = RegexDescriptor.Deserialize(this);
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public virtual PipId ReadPipId()
        {
            Start<PipId>();
            var value = new PipId(ReadUInt32());
            End();
            return value;
        }

        /// <summary>
        /// PipReader
        /// </summary>
        public ProcessSemaphoreInfo ReadProcessSemaphoreInfo()
        {
            Start<ProcessSemaphoreInfo>();
            var value = ProcessSemaphoreInfo.Deserialize(this);
            End();
            return value;
        }
    }
}
