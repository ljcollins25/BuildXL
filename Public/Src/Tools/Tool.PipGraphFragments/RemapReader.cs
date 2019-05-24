using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Serialization;

namespace PipGraphFragments
{

    public class RemapReader : PipReader
    {
        public readonly PipFragmentContext Context;
        private readonly PipExecutionContext ExecutionContext;
        private SymbolTable SymbolTable;
        private InliningReader InliningReader;

        public RemapReader(PipFragmentContext fragmentContext, Stream stream, PipExecutionContext context, bool debug = false, bool leaveOpen = true) 
            : base(debug, context.StringTable, stream, leaveOpen)
        {
            Context = fragmentContext;
            ExecutionContext = context;
            InliningReader = new InnerInliningReader(stream, context.PathTable, debug, leaveOpen);
            SymbolTable = context.SymbolTable;
        }

        public override DirectoryArtifact ReadDirectoryArtifact()
        {
            var directoryArtifact = base.ReadDirectoryArtifact();
            return Context.Remap(directoryArtifact);
        }

        public override AbsolutePath ReadAbsolutePath()
        {
            return InliningReader.ReadAbsolutePath();
        }

        public override StringId ReadStringId()
        {
            return InliningReader.ReadStringId();
        }

        public override PathAtom ReadPathAtom()
        {
            return InliningReader.ReadPathAtom();
        }

        public DirectoryArtifact ReadUnmappedDirectoryArtifact()
        {
            return base.ReadDirectoryArtifact();
        }

        public override FullSymbol ReadFullSymbol()
        {
            return FullSymbol.Create(SymbolTable, ReadString());
        }

        public override StringId ReadPipDataId()
        {
            return InliningReader.ReadStringId(InlinedStringKind.PipData);
        }

        //public override PipData ReadPipData()
        //{
        //    var start = BaseStream.Position;
        //    try
        //    {
        //        var pipData = base.ReadPipData();
        //        if (pipData.IsValid)
        //        {
        //            var stringValue = pipData.ToString(ExecutionContext.PathTable);
        //        }

        //        return pipData;
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debugger.Launch();
        //        BaseStream.Position = start;
        //        var pipData = base.ReadPipData();
        //        var stringValue = pipData.ToString(ExecutionContext.PathTable);

        //        Analysis.IgnoreArgument(ex);

        //        return pipData;
        //    }
        //}

        private class InnerInliningReader : InliningReader
        {
            private byte[] pipDatabuffer = new byte[1024];

            public InnerInliningReader(Stream stream, PathTable pathTable, bool debug = false, bool leaveOpen = true)
                : base(stream, pathTable, debug, leaveOpen)
            {
            }

            public override BinaryStringSegment ReadStringIdValue(InlinedStringKind kind, ref byte[] buffer)
            {
                if (kind == InlinedStringKind.PipData)
                {
                    int count = ReadInt32Compact();

                    return PipDataBuilder.WriteEntries(getEntries(), count, ref pipDatabuffer);

                    IEnumerable<PipDataEntry> getEntries()
                    {
                        for (int i = 0; i < count; i++)
                        {
                            yield return PipDataEntry.Deserialize(this);
                        }
                    }
                }
                else
                {
                    return base.ReadStringIdValue(kind, ref buffer);
                }
            }
        }
    }

    public class RemapWriter : PipWriter
    {
        private InliningWriter InliningWriter;
        private SymbolTable SymbolTable;

        public RemapWriter(Stream stream, PipExecutionContext context, bool debug = false, bool leaveOpen = true, bool logStats = false) 
            : base(debug, stream, leaveOpen, logStats)
        {
            InliningWriter = new InnerInliningWriter(stream, context.PathTable, debug, leaveOpen, logStats);
            SymbolTable = context.SymbolTable;
        }

        //public override void Write(DirectoryArtifact value)
        //{
        //    base.Write(value);
        //}

        public override void Write(AbsolutePath value)
        {
            InliningWriter.Write(value);
        }

        public override void Write(PathAtom value)
        {
            InliningWriter.Write(value);
        }

        public override void Write(StringId value)
        {
            InliningWriter.Write(value);
        }

        public override void WritePipDataId(in StringId value)
        {
            InliningWriter.WriteAndGetIndex(value, InlinedStringKind.PipData);
        }

        //public override void Write(in PipData value)
        //{
        //    if (BaseStream.Position == 1121)
        //    {
        //        // TODO:
        //        Debugger.Launch();
        //    }

        //    base.Write(value);
        //}

        private class InnerInliningWriter : InliningWriter
        {
            public InnerInliningWriter(Stream stream, PathTable pathTable, bool debug = false, bool leaveOpen = true, bool logStats = false) 
                : base(stream, pathTable, debug, leaveOpen, logStats)
            {
            }

            public override void WriteStringIdValue(in StringId stringId, InlinedStringKind kind)
            {
                if (kind == InlinedStringKind.PipData)
                {
                    var binaryString = PathTable.StringTable.GetBinaryString(stringId);
                    var entries = new PipDataEntryList(binaryString.UnderlyingBytes);
                    WriteCompact(entries.Count);
                    foreach (var e in entries)
                    {
                        e.Serialize(this);
                    }
                }
                else
                {
                    base.WriteStringIdValue(stringId, kind);
                }
            }
        }

        public override void Write(FullSymbol value)
        {
            Write(value.ToString(SymbolTable));
        }
    }
}
