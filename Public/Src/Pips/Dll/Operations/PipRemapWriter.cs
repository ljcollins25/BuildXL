﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using System.IO;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Serialization;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Writes absolute paths, string ids, and pipdataentries so the values of each item are present inline in the stream.
    /// Format should be read by the <see cref="PipRemapReader"/>.
    /// </summary>
    internal class PipRemapWriter : PipWriter
    {
        private readonly InliningWriter m_inliningWriter;
        private readonly PipDataEntriesPointerInlineWriter m_pipDataEntriesPointerInlineWriter;
        private readonly PipGraphFragmentContext m_pipGraphFragmentContext;
        private readonly PipExecutionContext m_pipExecutionContext;
        private readonly char[] m_alternateSymbolSeparatorAsArray;
        private readonly string m_alternateSymbolSeparatorAsString;

        /// <summary>
        /// Creates an instance of <see cref="PipRemapWriter"/>.
        /// </summary>
        public PipRemapWriter(PipExecutionContext pipExecutionContext, PipGraphFragmentContext pipGraphFragmentContext, Stream stream, bool leaveOpen = true, char alternateSymbolSeparator = default)
            : base(debug: false, stream, leaveOpen, logStats: false)
        {
            Contract.Requires(pipExecutionContext != null);
            Contract.Requires(pipGraphFragmentContext != null);
            Contract.Requires(stream != null);

            m_pipExecutionContext = pipExecutionContext;
            m_pipGraphFragmentContext = pipGraphFragmentContext;
            m_inliningWriter = new InliningWriter(stream, pipExecutionContext.PathTable, debug: false, leaveOpen, logStats: false);
            m_pipDataEntriesPointerInlineWriter = new PipDataEntriesPointerInlineWriter(this, stream, pipExecutionContext.PathTable, debug: false, leaveOpen, logStats: false);

            m_alternateSymbolSeparatorAsArray = alternateSymbolSeparator != default ? new char[] { alternateSymbolSeparator } : new char[0];
            m_alternateSymbolSeparatorAsString = alternateSymbolSeparator != default ? alternateSymbolSeparator.ToString() : string.Empty;
        }

        /// <inheritdoc />
        public override void Write(AbsolutePath value) => m_inliningWriter.Write(value);

        /// <inheritdoc />
        public override void Write(PathAtom value) => m_inliningWriter.Write(value);

        /// <inheritdoc />
        public override void Write(StringId value) => m_inliningWriter.Write(value);

        /// <inheritdoc />
        public override void WritePipDataEntriesPointer(in StringId value) => m_pipDataEntriesPointerInlineWriter.Write(value);

        /// <inheritdoc />
        public override void Write(FullSymbol value)
        {
            var symbolTable = m_pipExecutionContext.SymbolTable;
            var depth = symbolTable.GetDepth(value.Value);
            var stringValue = value.ToString(symbolTable);

            // Use alternate symbol separator to 
            if (m_alternateSymbolSeparatorAsString.Length != 0 && stringValue.Contains(m_alternateSymbolSeparatorAsString))
            {
                var segments = stringValue.Split(m_alternateSymbolSeparatorAsArray, System.StringSplitOptions.None);

                if (segments.Length > depth)
                {
                    // Alternate symbol separator results in more splits of the string (i.e. there would
                    // be more symbol atom segments generated by using the alternate separator). Use it instead.
                    var stringTable = m_pipExecutionContext.StringTable;
                    Write(m_alternateSymbolSeparatorAsString[0]);
                    Write(segments, (w, s) => w.Write(stringTable.AddString(s)));
                    return;
                }
            }

            Write(default(char));
            Write(stringValue);
        }

        private class PipDataEntriesPointerInlineWriter : InliningWriter
        {
            private readonly PipRemapWriter m_baseInliningWriter;

            public PipDataEntriesPointerInlineWriter(PipRemapWriter baseInliningWriter, Stream stream, PathTable pathTable, bool debug = false, bool leaveOpen = true, bool logStats = false)
                : base(stream, pathTable, debug, leaveOpen, logStats)
            {
                m_baseInliningWriter = baseInliningWriter;
            }

            protected override void WriteBinaryStringSegment(in StringId stringId)
            {
                var binaryString = PathTable.StringTable.GetBinaryString(stringId);
                var entries = new PipDataEntryList(binaryString.UnderlyingBytes);

                // Use base inlining writer for serializing the entries because
                // this writer is only for serializing pip data entries pointer.
                entries.Serialize(m_baseInliningWriter);
            }
        }
    }
}
