using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;

namespace FModel.ViewModels;

/// <summary>
/// Retrieves a shader's compiled bytecode from the UE5 IoStore shared shader library. The material
/// shader map (when it shares code) carries a ResourceHash that keys into the library's shader-map
/// table; a shader's in-map index (FShader.ResourceIndex) then selects its global entry, whose
/// shader group is an IoStore chunk we read, Oodle-decompress, and slice. Everything is decoded 1:1
/// from the serialized library — see Engine/Source/Runtime/RenderCore ShaderCodeArchive.cpp.
/// </summary>
internal static class MaterialDxilLibrary
{
    private static readonly ConditionalWeakTable<IVfsFileProvider, List<(FIoStoreShaderCodeArchive Lib, IoStoreReader Reader)>> _cache = new();

    private static List<(FIoStoreShaderCodeArchive Lib, IoStoreReader Reader)> GetLibraries(IVfsFileProvider provider)
    {
        if (_cache.TryGetValue(provider, out var cached)) return cached;
        var libs = new List<(FIoStoreShaderCodeArchive, IoStoreReader)>();
        foreach (var reader in provider.MountedVfs.OfType<IoStoreReader>())
        {
            foreach (var chunkId in reader.TocResource.ChunkIds)
            {
                if (chunkId.ChunkType != (byte) EIoChunkType5.ShaderCodeLibrary) continue;
                try
                {
                    var archive = new FShaderCodeArchive(new FByteArchive("ShaderCodeLibrary", reader.Read(chunkId), provider.Versions));
                    if (archive.SerializedShaders is FIoStoreShaderCodeArchive io) libs.Add((io, reader));
                }
                catch { /* a malformed library must not break material loading */ }
            }
        }
        _cache.Add(provider, libs);
        return libs;
    }

    public static byte[] TryGetShaderCode(IVfsFileProvider provider, FSHAHash resourceHash, int resourceIndex, out string error)
    {
        error = null;
        var libs = GetLibraries(provider);
        if (libs.Count == 0) { error = "no IoStore shader library is mounted"; return null; }

        var readers = provider.MountedVfs.OfType<IoStoreReader>().ToList();
        foreach (var (lib, _) in libs)
        {
            var mapIdx = Array.IndexOf(lib.ShaderMapHashes, resourceHash);
            if (mapIdx < 0) continue;
            var mapEntry = lib.ShaderMapEntries[mapIdx];
            if (resourceIndex < 0 || resourceIndex >= mapEntry.NumShaders) { error = "shader index out of the map's range"; return null; }

            var gIdx = (int) lib.ShaderIndices[(int) mapEntry.ShaderIndicesOffset + resourceIndex];
            var entry = lib.ShaderEntries[gIdx];
            var group = lib.ShaderGroupEntries[(int) entry.ShaderGroupIndex];
            var groupChunk = lib.ShaderGroupIoHashes[(int) entry.ShaderGroupIndex];

            var owner = readers.FirstOrDefault(r => r.DoesChunkExist(groupChunk));
            if (owner == null) { error = "the shader group chunk is not present in any container"; return null; }

            byte[] uncompressed;
            var groupBytes = owner.Read(groupChunk);
            if (group.CompressedSize != group.UncompressedSize)
            {
                uncompressed = null;
                foreach (var method in new[] { CompressionMethod.Oodle, CompressionMethod.LZ4, CompressionMethod.Zlib })
                {
                    try { uncompressed = Compression.Decompress(groupBytes, (int) group.UncompressedSize, method); break; }
                    catch { /* try the next shader compression format */ }
                }
                if (uncompressed == null) { error = "the shader group could not be decompressed"; return null; }
            }
            else uncompressed = groupBytes;

            // this shader's uncompressed size = gap to the next member offset within the group
            var myOff = (uint) entry.UncompressedOffsetInGroup;
            var next = group.UncompressedSize;
            for (var mm = 0; mm < group.NumShaders; mm++)
            {
                var member = (int) lib.ShaderIndices[(int) group.ShaderIndicesOffset + mm];
                var mo = (uint) lib.ShaderEntries[member].UncompressedOffsetInGroup;
                if (mo > myOff && mo < next) next = mo;
            }
            if (myOff >= uncompressed.Length) { error = "the shader offset is outside the group"; return null; }
            var size = (int) Math.Min(next - myOff, (uint) (uncompressed.Length - myOff));
            var shader = new byte[size];
            Array.Copy(uncompressed, myOff, shader, 0, size);
            return shader;
        }
        error = "this material's shader map was not found in any shader library";
        return null;
    }

    /// <summary>
    /// Concise, honest summary of a compiled SM6 DXIL shader for the Shaders panel: container
    /// chunks, entry instruction count, the render targets it writes, and a histogram of the
    /// dx.op intrinsics it uses. Everything comes from the decoded bitstream — no invented text.
    /// A full LLVM-IR disassembly is out of scope; this describes what the analyzer actually reads.
    /// </summary>
    public static string Describe(byte[] blob)
    {
        var sb = new StringBuilder();
        sb.Append("// SM6 DXIL shader — ").Append(blob.Length.ToString("N0")).AppendLine(" bytes (from the IoStore shared shader library)");

        var dxbcAt = -1;
        for (var i = 0; i + 4 <= Math.Min(blob.Length, 4096); i++)
            if (blob[i] == 'D' && blob[i + 1] == 'X' && blob[i + 2] == 'B' && blob[i + 3] == 'C') { dxbcAt = i; break; }
        if (dxbcAt >= 0)
        {
            try
            {
                var p = dxbcAt + 4 + 16 + 4 + 4; // magic + hash + version + total size
                int chunkCount = BitConverter.ToInt32(blob, p); p += 4;
                var names = new List<string>();
                for (var i = 0; i < chunkCount && i < 24; i++)
                {
                    int off = BitConverter.ToInt32(blob, p + i * 4);
                    if (dxbcAt + off + 4 > blob.Length) break;
                    names.Add(new string(new[] { (char) blob[dxbcAt + off], (char) blob[dxbcAt + off + 1], (char) blob[dxbcAt + off + 2], (char) blob[dxbcAt + off + 3] }));
                }
                sb.Append("// DXBC container chunks: ").AppendLine(string.Join(", ", names));
            }
            catch { /* the histogram below is the real payload */ }
        }

        DxilModule m;
        try { m = DxilModule.Parse(blob); }
        catch (Exception e) { sb.Append("// DXIL bitcode parse failed: ").AppendLine(e.Message); return sb.ToString(); }

        var fn = m.Functions.FirstOrDefault();
        if (fn == null) { sb.AppendLine("// no function body in the bitcode"); return sb.ToString(); }
        sb.Append("// entry function: ").Append(fn.Instructions.Count).AppendLine(" instructions");

        var stores = fn.Instructions.Where(i => i.Callee != null && i.Callee.StartsWith("dx.op.storeOutput")).ToList();
        if (stores.Count > 0)
        {
            sb.Append("// writes ").Append(stores.Count).AppendLine(" output component(s):");
            foreach (var st in stores)
            {
                var sig = DxilTaint.ConstOp(fn, st, 1);
                var col = DxilTaint.ConstOp(fn, st, 3);
                sb.Append("//   storeOutput  SV_Target").Append(sig?.ToString() ?? "?").Append('.').AppendLine(col?.ToString() ?? "?");
            }
        }

        var intrinsics = fn.Instructions
            .Where(i => i.Callee != null && i.Callee.StartsWith("dx.op."))
            .GroupBy(i => i.Callee).OrderByDescending(g => g.Count()).ToList();
        if (intrinsics.Count > 0)
        {
            sb.AppendLine("// DXIL intrinsics used:");
            foreach (var g in intrinsics)
                sb.Append("//   ").Append(g.Count().ToString().PadLeft(4)).Append("x  ").AppendLine(g.Key);
        }
        return sb.ToString();
    }
}

// Minimal LLVM 3.7 bitstream reader + DXIL module decoder, enough to run cbuffer/texture -> output
// taint on UE5 SM6 pixel shaders. Everything is decoded 1:1 from the bitstream; anything
// unrecognized becomes an opaque leaf. Builtin abbrev ids: 0 END_BLOCK, 1 ENTER_SUBBLOCK,
// 2 DEFINE_ABBREV, 3 UNABBREV_RECORD.

internal sealed class BitReader
{
    private readonly byte[] _data;
    private int _bitPos;
    public readonly int StartBit;
    public readonly int EndBit;

    public BitReader(byte[] data, int byteOffset, int byteLen)
    {
        _data = data;
        StartBit = byteOffset * 8;
        _bitPos = StartBit;
        EndBit = (byteOffset + byteLen) * 8;
    }

    public int BitPos => _bitPos;
    public bool AtEnd => _bitPos >= EndBit;

    public uint Read(int numBits) => (uint) Read64(numBits);

    public ulong Read64(int numBits)
    {
        ulong result = 0;
        for (var i = 0; i < numBits; i++)
        {
            var byteIndex = _bitPos >> 3;
            var bitIndex = _bitPos & 7;
            ulong bit = (ulong) ((_data[byteIndex] >> bitIndex) & 1);
            result |= bit << i;
            _bitPos++;
        }
        return result;
    }

    public ulong ReadVBR64(int numBits)
    {
        ulong piece = Read64(numBits);
        ulong hiMask = 1UL << (numBits - 1);
        if ((piece & hiMask) == 0) return piece;
        ulong result = piece & (hiMask - 1);
        var shift = numBits - 1;
        while (true)
        {
            piece = Read64(numBits);
            result |= (piece & (hiMask - 1)) << shift;
            if ((piece & hiMask) == 0) return result;
            shift += numBits - 1;
        }
    }

    public uint ReadVBR(int numBits) => (uint) ReadVBR64(numBits);
    public void Align32() => _bitPos = (_bitPos + 31) & ~31;
}

internal enum AbbrevEncoding { Fixed = 1, VBR = 2, Array = 3, Char6 = 4, Blob = 5 }

internal struct AbbrevOp
{
    public bool IsLiteral;
    public ulong LiteralValue;
    public AbbrevEncoding Encoding;
    public ulong EncodingData;
}

internal sealed class Abbrev { public List<AbbrevOp> Ops = new(); }

internal sealed class BitRecord
{
    public ulong Code;
    public List<ulong> Fields = new();
    public byte[] Blob;
    public static char Char6(int v) => v switch
    {
        >= 0 and < 26 => (char) ('a' + v),
        >= 26 and < 52 => (char) ('A' + v - 26),
        >= 52 and < 62 => (char) ('0' + v - 52),
        62 => '.',
        63 => '_',
        _ => '?'
    };
}

internal sealed class BlockContext
{
    public uint BlockId;
    public int AbbrevWidth;
    public List<Abbrev> Abbrevs = new();
}

internal sealed class BitstreamReader
{
    private readonly BitReader _r;
    private readonly Dictionary<uint, List<Abbrev>> _blockInfoAbbrevs = new();

    public BitstreamReader(byte[] data, int byteOffset, int byteLen) => _r = new BitReader(data, byteOffset, byteLen);

    public void Walk(Action<uint, BitRecord> onRecord, Action<uint> onEnterBlock = null)
    {
        WalkBlockContents(new BlockContext { BlockId = uint.MaxValue, AbbrevWidth = 2 }, onRecord, onEnterBlock, topLevel: true);
    }

    private void WalkBlockContents(BlockContext ctx, Action<uint, BitRecord> onRecord, Action<uint> onEnterBlock, bool topLevel)
    {
        while (!_r.AtEnd)
        {
            if (topLevel && _r.EndBit - _r.BitPos < ctx.AbbrevWidth) return;
            var abbrevId = _r.Read(ctx.AbbrevWidth);
            switch (abbrevId)
            {
                case 0: _r.Align32(); return;                                    // END_BLOCK
                case 1:                                                           // ENTER_SUBBLOCK
                {
                    var blockId = _r.ReadVBR(8);
                    var newWidth = (int) _r.ReadVBR(4);
                    _r.Align32();
                    _r.Read(32); // block length in words
                    var child = new BlockContext { BlockId = blockId, AbbrevWidth = newWidth };
                    if (_blockInfoAbbrevs.TryGetValue(blockId, out var inherited)) child.Abbrevs.AddRange(inherited);
                    onEnterBlock?.Invoke(blockId);
                    if (blockId == 0) WalkBlockInfo(child);
                    else WalkBlockContents(child, onRecord, onEnterBlock, topLevel: false);
                    break;
                }
                case 2: ctx.Abbrevs.Add(ReadAbbrevDef()); break;                  // DEFINE_ABBREV
                case 3:                                                           // UNABBREV_RECORD
                {
                    var rec = new BitRecord { Code = _r.ReadVBR64(6) };
                    var numOps = _r.ReadVBR64(6);
                    for (ulong i = 0; i < numOps; i++) rec.Fields.Add(_r.ReadVBR64(6));
                    onRecord(ctx.BlockId, rec);
                    break;
                }
                default:
                {
                    var abIdx = (int) abbrevId - 4;
                    if (abIdx < 0 || abIdx >= ctx.Abbrevs.Count) throw new Exception($"bad abbrev id {abbrevId} in block {ctx.BlockId}");
                    onRecord(ctx.BlockId, ReadAbbreviatedRecord(ctx.Abbrevs[abIdx]));
                    break;
                }
            }
        }
    }

    private void WalkBlockInfo(BlockContext ctx)
    {
        uint curBid = 0;
        while (!_r.AtEnd)
        {
            var abbrevId = _r.Read(ctx.AbbrevWidth);
            switch (abbrevId)
            {
                case 0: _r.Align32(); return;
                case 1:
                {
                    var blockId = _r.ReadVBR(8); var newWidth = (int) _r.ReadVBR(4); _r.Align32(); _r.Read(32);
                    WalkBlockContents(new BlockContext { BlockId = blockId, AbbrevWidth = newWidth }, (_, __) => { }, null, false);
                    break;
                }
                case 2:
                {
                    var ab = ReadAbbrevDef();
                    if (!_blockInfoAbbrevs.TryGetValue(curBid, out var list)) _blockInfoAbbrevs[curBid] = list = new List<Abbrev>();
                    list.Add(ab);
                    break;
                }
                case 3:
                {
                    var code = _r.ReadVBR64(6);
                    var numOps = _r.ReadVBR64(6);
                    var fields = new List<ulong>();
                    for (ulong i = 0; i < numOps; i++) fields.Add(_r.ReadVBR64(6));
                    if (code == 1 && fields.Count > 0) curBid = (uint) fields[0]; // SETBID
                    break;
                }
                default: ReadAbbreviatedRecord(ctx.Abbrevs[(int) abbrevId - 4]); break;
            }
        }
    }

    private Abbrev ReadAbbrevDef()
    {
        var ab = new Abbrev();
        var numOps = _r.ReadVBR64(5);
        for (ulong i = 0; i < numOps; i++)
        {
            if (_r.Read(1) == 1) ab.Ops.Add(new AbbrevOp { IsLiteral = true, LiteralValue = _r.ReadVBR64(8) });
            else
            {
                var enc = (AbbrevEncoding) _r.Read(3);
                ulong data = 0;
                if (enc is AbbrevEncoding.Fixed or AbbrevEncoding.VBR) data = _r.ReadVBR64(5);
                ab.Ops.Add(new AbbrevOp { IsLiteral = false, Encoding = enc, EncodingData = data });
            }
        }
        return ab;
    }

    private BitRecord ReadAbbreviatedRecord(Abbrev ab)
    {
        var rec = new BitRecord();
        var first = true;
        void Put(ulong v) { if (first) { rec.Code = v; first = false; } else rec.Fields.Add(v); }
        for (var i = 0; i < ab.Ops.Count; i++)
        {
            var op = ab.Ops[i];
            if (op.IsLiteral) { Put(op.LiteralValue); continue; }
            switch (op.Encoding)
            {
                case AbbrevEncoding.Fixed: Put(op.EncodingData == 0 ? 0UL : _r.Read64((int) op.EncodingData)); break;
                case AbbrevEncoding.VBR: Put(op.EncodingData == 0 ? 0UL : _r.ReadVBR64((int) op.EncodingData)); break;
                case AbbrevEncoding.Char6: Put(_r.Read64(6)); break;
                case AbbrevEncoding.Array:
                {
                    var elt = ab.Ops[++i];
                    var count = _r.ReadVBR64(6);
                    for (ulong e = 0; e < count; e++)
                    {
                        ulong v = elt.Encoding switch
                        {
                            AbbrevEncoding.Fixed => elt.EncodingData == 0 ? 0UL : _r.Read64((int) elt.EncodingData),
                            AbbrevEncoding.VBR => _r.ReadVBR64((int) elt.EncodingData),
                            AbbrevEncoding.Char6 => _r.Read64(6),
                            _ => throw new Exception("bad array elt encoding")
                        };
                        rec.Fields.Add(v);
                    }
                    break;
                }
                case AbbrevEncoding.Blob:
                {
                    var count = _r.ReadVBR64(6);
                    _r.Align32();
                    var bytes = new byte[count];
                    for (ulong b = 0; b < count; b++) bytes[b] = (byte) _r.Read(8);
                    _r.Align32();
                    rec.Blob = bytes;
                    break;
                }
            }
        }
        return rec;
    }
}

internal enum DxValKind { Global, Function, Constant, Arg, Instr }

internal sealed class DxValue
{
    public int Id;
    public DxValKind Kind;
    public string Name;
    public bool HasConstInt;
    public long ConstInt;
    public bool HasConstFloat;
    public float ConstFloat;
    public bool IsAggregate;
    public List<int> AggregateElems;
    public DxInstr Instr;
}

internal enum MdKind { String, Value, Node }
internal sealed class MdEntry { public MdKind Kind; public string Str; public int ValueId = -1; public List<int> Operands; }

internal sealed class DxInstr
{
    public int ValueId = -1;
    public string Code;
    public List<int> Operands = new();
    public long Aux;
    public string Callee;
}

internal sealed class DxFunction
{
    public List<DxInstr> Instructions = new();
    public List<DxValue> Values;
}

internal sealed class DxilModule
{
    public readonly List<DxValue> ModuleValues = new();
    public readonly Dictionary<int, string> ValueNames = new();
    public readonly List<DxFunction> Functions = new();
    public int ModuleValueCount;
    public readonly List<MdEntry> Md = new();
    public readonly Dictionary<string, List<int>> NamedMd = new();

    private static readonly HashSet<string> VoidOps = new()
    {
        "dx.op.storeOutput", "dx.op.discard", "dx.op.storePatchConstant", "dx.op.emitStream",
        "dx.op.cutStream", "dx.op.emitThenCutStream", "dx.op.textureStore", "dx.op.bufferStore", "dx.op.rawBufferStore"
    };

    // locate the LLVM bitcode module inside a UE D3D shader blob (SRT prefix + DXBC container + DXIL chunk)
    public static (int start, int len) LocateBitcode(byte[] blob)
    {
        static string FourCC(byte[] d, int o) => o + 4 <= d.Length ? new string(new[] { (char) d[o], (char) d[o + 1], (char) d[o + 2], (char) d[o + 3] }) : "";
        static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);

        var baseOff = -1;
        for (var i = 0; i + 4 <= blob.Length; i++)
            if (FourCC(blob, i) == "DXBC") { baseOff = i; break; }
        if (baseOff < 0) throw new Exception("no DXBC container");
        var chunkCount = (int) U32(blob, baseOff + 28);
        for (var i = 0; i < chunkCount; i++)
        {
            var off = (int) U32(blob, baseOff + 32 + i * 4);
            var fourcc = FourCC(blob, baseOff + off);
            if (fourcc != "DXIL" && fourcc != "ILDB") continue;
            var bcHeader = baseOff + off + 8 + 8;                   // chunk hdr(8) + ProgramVersion/SizeInUint32(8)
            var bitcodeOffset = (int) U32(blob, bcHeader + 8);      // DxilBitcodeHeader.BitcodeOffset
            var bitcodeSize = (int) U32(blob, bcHeader + 12);
            return (bcHeader + bitcodeOffset, bitcodeSize);
        }
        throw new Exception("no DXIL chunk");
    }

    public static DxilModule Parse(byte[] blob)
    {
        var (start, len) = LocateBitcode(blob);
        // slice the bitcode (skipping the 4-byte 'BC C0 DE' magic) so 32-bit alignment is relative to the stream origin
        var bc = new byte[len - 4];
        Array.Copy(blob, start + 4, bc, 0, len - 4);
        var m = new DxilModule();

        // ---- Pass 1: module value numbering + metadata + VST names ----
        var inFunctionBody = false;
        string pendingMdName = null;
        new BitstreamReader(bc, 0, bc.Length).Walk(
            onRecord: (blockId, rec) =>
            {
                if (blockId == 8)
                {
                    switch (rec.Code)
                    {
                        case 7: m.ModuleValues.Add(new DxValue { Kind = DxValKind.Global }); break;   // GLOBALVAR
                        case 8: m.ModuleValues.Add(new DxValue { Kind = DxValKind.Function }); break;  // FUNCTION
                        case 9: case 14: m.ModuleValues.Add(new DxValue { Kind = DxValKind.Global }); break; // ALIAS
                    }
                }
                else if (blockId == 11 && !inFunctionBody)
                {
                    if (rec.Code != 1) m.ModuleValues.Add(MakeConst(rec));
                }
                else if (blockId == 14)
                {
                    if ((rec.Code == 1 || rec.Code == 3) && rec.Fields.Count > 1)
                    {
                        var id = (int) rec.Fields[0];
                        var name = DecodeName(rec.Fields, rec.Code == 3 ? 2 : 1);
                        if (!string.IsNullOrEmpty(name)) m.ValueNames[id] = name;
                    }
                }
                else if (blockId == 15)
                {
                    switch (rec.Code)
                    {
                        case 1: m.Md.Add(new MdEntry { Kind = MdKind.String, Str = DecodeAsciiName(rec.Fields, 0) }); break;
                        case 2: m.Md.Add(new MdEntry { Kind = MdKind.Value, ValueId = rec.Fields.Count > 1 ? (int) rec.Fields[1] : -1 }); break;
                        case 3: case 5: m.Md.Add(new MdEntry { Kind = MdKind.Node, Operands = rec.Fields.Select(f => (int) f - 1).ToList() }); break;
                        case 4: pendingMdName = DecodeAsciiName(rec.Fields, 0); break;
                        case 10: if (pendingMdName != null) m.NamedMd[pendingMdName] = rec.Fields.Select(f => (int) f).ToList(); pendingMdName = null; break;
                    }
                }
            },
            onEnterBlock: bid => { if (bid == 12) inFunctionBody = true; });

        m.ModuleValueCount = m.ModuleValues.Count;
        for (var i = 0; i < m.ModuleValues.Count; i++)
        {
            m.ModuleValues[i].Id = i;
            if (m.ValueNames.TryGetValue(i, out var nm)) m.ModuleValues[i].Name = nm;
        }

        // ---- Pass 2: decode function bodies ----
        DxFunction curFn = null;
        var funcValues = new List<DxValue>();
        var inFunc = false;
        new BitstreamReader(bc, 0, bc.Length).Walk(
            onRecord: (blockId, rec) =>
            {
                if (blockId == 11 && inFunc)
                {
                    if (rec.Code == 1) return;
                    var v = MakeConst(rec);
                    v.Id = funcValues.Count;
                    funcValues.Add(v);
                }
                else if (blockId == 12)
                {
                    DecodeFunctionRecord(m, rec, curFn, funcValues);
                }
            },
            onEnterBlock: bid =>
            {
                if (bid == 12)
                {
                    inFunc = true;
                    curFn = new DxFunction();
                    m.Functions.Add(curFn);
                    funcValues = new List<DxValue>(m.ModuleValues);
                    curFn.Values = funcValues;
                }
            });

        return m;
    }

    private static void DecodeFunctionRecord(DxilModule m, BitRecord rec, DxFunction fn, List<DxValue> vals)
    {
        int NextId() => vals.Count;
        int Rel(ulong relField) => (int) (NextId() - (long) relField);
        DxInstr AddValue(DxInstr instr)
        {
            fn.Instructions.Add(instr);
            instr.ValueId = NextId();
            vals.Add(new DxValue { Kind = DxValKind.Instr, Id = instr.ValueId, Instr = instr });
            return instr;
        }

        switch (rec.Code)
        {
            case 34: // INST_CALL
            {
                var idx = 0;
                idx++; // paramattrs
                var cc = rec.Fields[idx++];
                if ((cc & (1 << 15)) != 0) idx++; // explicit fnty
                var calleeId = (int) (NextId() - (long) rec.Fields[idx++]);
                var instr = new DxInstr { Code = "CALL", Callee = calleeId >= 0 && calleeId < vals.Count ? vals[calleeId].Name : m.ValueNames.GetValueOrDefault(calleeId) };
                for (; idx < rec.Fields.Count; idx++) instr.Operands.Add((int) (NextId() - (long) rec.Fields[idx]));
                var isVoid = instr.Callee != null && VoidOps.Any(vo => instr.Callee.StartsWith(vo));
                fn.Instructions.Add(instr);
                if (!isVoid) { instr.ValueId = NextId(); vals.Add(new DxValue { Kind = DxValKind.Instr, Id = instr.ValueId, Instr = instr }); }
                return;
            }
            case 26: // EXTRACTVAL
            {
                var instr = new DxInstr { Code = "EXTRACTVAL" };
                instr.Operands.Add(Rel(rec.Fields[0]));
                instr.Aux = rec.Fields.Count > 1 ? (long) rec.Fields[1] : 0;
                AddValue(instr);
                return;
            }
            case 2: // BINOP
            {
                var instr = new DxInstr { Code = "BINOP" };
                instr.Operands.Add(Rel(rec.Fields[0]));
                instr.Operands.Add(Rel(rec.Fields[1]));
                instr.Aux = rec.Fields.Count > 2 ? (long) rec.Fields[2] : -1; // LLVM binary opcode
                AddValue(instr);
                return;
            }
            case 3: // CAST
            {
                var instr = new DxInstr { Code = "CAST" };
                instr.Operands.Add(Rel(rec.Fields[0]));
                AddValue(instr);
                return;
            }
            case 6: case 29: // SELECT / VSELECT
            {
                var instr = new DxInstr { Code = "SELECT" };
                instr.Operands.Add(Rel(rec.Fields[0]));
                instr.Operands.Add(Rel(rec.Fields[1]));
                if (rec.Fields.Count > 2) instr.Operands.Add(Rel(rec.Fields[2]));
                AddValue(instr);
                return;
            }
            case 28: case 9: // CMP2 / (shuffle-ish)
            {
                var instr = new DxInstr { Code = "CMP" };
                if (rec.Fields.Count > 0) instr.Operands.Add(Rel(rec.Fields[0]));
                if (rec.Fields.Count > 1) instr.Operands.Add(Rel(rec.Fields[1]));
                AddValue(instr);
                return;
            }
            case 7: case 8: // EXTRACTELT / INSERTELT
            {
                var instr = new DxInstr { Code = "VEC" };
                foreach (var f in rec.Fields) instr.Operands.Add(Rel(f));
                AddValue(instr);
                return;
            }
            case 16: // PHI
            {
                var instr = new DxInstr { Code = "PHI" };
                for (var i = 1; i < rec.Fields.Count; i += 2)
                    instr.Operands.Add((int) (NextId() - DecodeSignedVBR(rec.Fields[i])));
                AddValue(instr);
                return;
            }
            case 20: // LOAD
            {
                var instr = new DxInstr { Code = "LOAD" };
                instr.Operands.Add(Rel(rec.Fields[0]));
                AddValue(instr);
                return;
            }
            case 4: case 43: // GEP
            {
                var instr = new DxInstr { Code = "GEP" };
                var startF = rec.Code == 43 ? 2 : 0;
                for (var i = startF; i < rec.Fields.Count; i++) instr.Operands.Add(Rel(rec.Fields[i]));
                AddValue(instr);
                return;
            }
            case 10: case 11: case 12: case 15: case 33: case 35: case 44: case 36: case 1:
                return; // void / non-value
            default:
                return; // unknown -> assume void (verified: real PS have 0 unknown codes)
        }
    }

    private static DxValue MakeConst(BitRecord rec)
    {
        var v = new DxValue { Kind = DxValKind.Constant };
        switch (rec.Code)
        {
            case 2: v.HasConstInt = true; v.ConstInt = 0; break;                                    // NULL
            case 4: v.HasConstInt = true; v.ConstInt = DecodeSignedVBR(rec.Fields.ElementAtOrDefault(0)); break; // INTEGER
            case 6: // FLOAT — the field is the raw IEEE-754 bit pattern; width is untracked so 32-bit floats
            {       // (the norm in material math) decode exact, wider constants fall back to a double read
                var bits = rec.Fields.ElementAtOrDefault(0);
                v.HasConstFloat = true;
                v.ConstFloat = bits <= uint.MaxValue
                    ? BitConverter.Int32BitsToSingle((int) (uint) bits)
                    : (float) BitConverter.Int64BitsToDouble((long) bits);
                break;
            }
            case 7: v.IsAggregate = true; v.AggregateElems = rec.Fields.Select(f => (int) f).ToList(); break;    // AGGREGATE
        }
        return v;
    }

    private static long DecodeSignedVBR(ulong v)
    {
        var val = (long) (v >> 1);
        return (v & 1) != 0 ? -val : val;
    }

    private static string DecodeName(List<ulong> fields, int skip)
    {
        var isChar6 = true;
        for (var i = skip; i < fields.Count; i++) if (fields[i] >= 64) { isChar6 = false; break; }
        var sb = new StringBuilder();
        for (var i = skip; i < fields.Count; i++)
        {
            var f = fields[i];
            if (isChar6) sb.Append(BitRecord.Char6((int) f));
            else if (f is >= 32 and < 127) sb.Append((char) f);
        }
        return sb.ToString();
    }

    private static string DecodeAsciiName(List<ulong> fields, int skip)
    {
        var sb = new StringBuilder();
        for (var i = skip; i < fields.Count; i++) if (fields[i] is >= 32 and < 127) sb.Append((char) fields[i]);
        return sb.ToString();
    }
}

internal sealed class DxResource
{
    public int Id;
    public int Space;
    public int Register;
    public int RangeSize;
    public int ResClass; // 0 SRV, 1 UAV, 2 CBV, 3 Sampler
}

internal struct DxBinding { public int ResClass; public int Space; public int Register; public int ResId; }

// A taint leaf reaching an output component.
internal sealed class DxLeaf
{
    public string Kind;   // cbuffer / texture / input / raw / const / opaque
    public int Register;
    public int Row = -1;
    public int Component = -1;
}

internal static class DxilTaint
{
    public static List<DxResource> BuildResources(DxilModule m)
    {
        var result = new List<DxResource>();
        if (!m.NamedMd.TryGetValue("dx.resources", out var roots) || roots.Count == 0) return result;
        var top = MdNode(m, roots[0]);
        if (top == null) return result;
        for (var cls = 0; cls < top.Count && cls < 4; cls++)
        {
            var listNode = MdNode(m, top[cls]);
            if (listNode == null) continue;
            foreach (var resMdId in listNode)
            {
                var res = MdNode(m, resMdId);
                if (res == null || res.Count < 6) continue;
                result.Add(new DxResource
                {
                    ResClass = cls,
                    Id = (int) (ConstFromMd(m, res[0]) ?? -1),
                    Space = (int) (ConstFromMd(m, res[3]) ?? -1),
                    Register = (int) (ConstFromMd(m, res[4]) ?? -1),
                    RangeSize = (int) (ConstFromMd(m, res[5]) ?? 1),
                });
            }
        }
        return result;
    }

    public static Dictionary<int, DxBinding> ResolveHandles(DxilModule m, DxFunction fn)
    {
        var handles = new Dictionary<int, DxBinding>();
        foreach (var instr in fn.Instructions)
        {
            if (instr.Code != "CALL" || instr.Callee == null || instr.ValueId < 0) continue;
            if (instr.Callee.StartsWith("dx.op.createHandleFromBinding"))
            {
                var bindVal = ValAt(fn, instr, 1);
                var idxConst = ConstOperand(fn, instr, 2);
                if (bindVal is { IsAggregate: true, AggregateElems: { Count: >= 4 } el })
                    handles[instr.ValueId] = new DxBinding
                    {
                        ResClass = (int) (ConstOfValue(fn, el[3]) ?? -1),
                        Space = (int) (ConstOfValue(fn, el[2]) ?? -1),
                        Register = (int) (idxConst ?? ConstOfValue(fn, el[0]) ?? -1),
                        ResId = -1
                    };
            }
            else if (instr.Callee.StartsWith("dx.op.createHandle") && !instr.Callee.Contains("FromBinding") && !instr.Callee.Contains("ForLib"))
            {
                handles[instr.ValueId] = new DxBinding
                {
                    ResClass = (int) (ConstOperand(fn, instr, 1) ?? -1),
                    ResId = (int) (ConstOperand(fn, instr, 2) ?? -1),
                    Space = -1, Register = -1
                };
            }
            else if (instr.Callee.StartsWith("dx.op.annotateHandle"))
            {
                if (instr.Operands.Count > 1 && handles.TryGetValue(instr.Operands[1], out var b)) handles[instr.ValueId] = b;
            }
        }
        return handles;
    }

    public static DxResource ResolveResource(List<DxResource> resources, DxBinding b)
    {
        if (b.ResId >= 0) return resources.FirstOrDefault(r => r.ResClass == b.ResClass && r.Id == b.ResId);
        return resources.FirstOrDefault(r => r.ResClass == b.ResClass && r.Space == b.Space &&
                                             b.Register >= r.Register && b.Register < r.Register + Math.Max(1, r.RangeSize));
    }

    // backward taint from a value id to the leaves that reach it
    public static List<DxLeaf> Taint(DxFunction fn, List<DxResource> resources, Dictionary<int, DxBinding> handles, int valueId, int component)
    {
        var leaves = new List<DxLeaf>();
        var seen = new HashSet<(int, int)>();
        Walk(valueId, component);
        return leaves;

        void Walk(int id, int comp)
        {
            if (!seen.Add((id, comp))) return;
            var v = ValById(fn, id);
            if (v?.Instr == null) return; // constants / args are not material sources
            var ins = v.Instr;
            switch (ins.Code)
            {
                case "EXTRACTVAL":
                {
                    var aggId = ins.Operands.Count > 0 ? ins.Operands[0] : -1;
                    var agg = ValById(fn, aggId);
                    if (agg?.Instr is { Code: "CALL" } call && call.Callee != null) { EmitCallLeaf(call, (int) ins.Aux); return; }
                    Walk(aggId, (int) ins.Aux);
                    return;
                }
                case "CALL": EmitCallLeaf(ins, comp); return;
                case "BINOP": case "CAST": case "SELECT": case "CMP": case "PHI": case "GEP": case "VEC":
                    foreach (var op in ins.Operands) Walk(op, comp);
                    return;
            }
        }

        void EmitCallLeaf(DxInstr call, int comp)
        {
            var callee = call.Callee ?? "";
            if (callee.StartsWith("dx.op.cbufferLoad"))
            {
                var handleId = call.Operands.Count > 1 ? call.Operands[1] : -1;
                var row = (int) (ConstOperand(fn, call, 2) ?? -1);
                var res = handles.TryGetValue(handleId, out var b) ? ResolveResource(resources, b) : null;
                leaves.Add(new DxLeaf { Kind = "cbuffer", Register = res?.Register ?? (handles.TryGetValue(handleId, out var b2) ? b2.Register : -1), Row = row, Component = comp });
            }
            else if (callee.StartsWith("dx.op.sample") || callee.StartsWith("dx.op.textureLoad") || callee.StartsWith("dx.op.textureGather"))
            {
                var handleId = call.Operands.Count > 1 ? call.Operands[1] : -1;
                var reg = handles.TryGetValue(handleId, out var b) ? (ResolveResource(resources, b)?.Register ?? b.Register) : -1;
                leaves.Add(new DxLeaf { Kind = "texture", Register = reg, Component = comp });
            }
            else if (callee.StartsWith("dx.op.binary") || callee.StartsWith("dx.op.unary") || callee.StartsWith("dx.op.dot") ||
                     callee.StartsWith("dx.op.tertiary") || callee.StartsWith("dx.op.quaternary") || callee.StartsWith("dx.op.fma"))
            {
                for (var i = 1; i < call.Operands.Count; i++) Walk(call.Operands[i], comp);
            }
            // loadInput / rawBufferLoad / other: engine/vertex data, not a material source — dropped
        }
    }

    /// <summary>
    /// Builds a per-output expression DAG (for the "Expand Shader Math" view) rooted at one
    /// storeOutput value: one <see cref="PixelExpressionNode"/> per decoded DXIL instruction, with
    /// cbuffer/texture leaves mapped to material sources through <paramref name="resolveLeaf"/>.
    /// Mirrors <see cref="Taint"/>'s traversal exactly; anything unmodeled becomes an honest opaque
    /// leaf, and shared sub-values are memoized so the emitted graph reuses nodes.
    /// </summary>
    public static PixelExpressionNode BuildExpression(DxFunction fn, List<DxResource> resources,
        Dictionary<int, DxBinding> handles, int valueId, int component, Func<DxLeaf, PixelValueSource?> resolveLeaf)
    {
        var memo = new Dictionary<(int, int), PixelExpressionNode>();
        return Build(valueId, component);

        PixelExpressionNode Build(int id, int comp)
        {
            if (memo.TryGetValue((id, comp), out var cached)) return cached;
            memo[(id, comp)] = new PixelExpressionNode { Op = "opaque", Detail = "cyclic value" }; // break cycles
            var built = BuildInner(id, comp);
            memo[(id, comp)] = built;
            return built;
        }

        PixelExpressionNode BuildInner(int id, int comp)
        {
            var v = ValById(fn, id);
            if (v == null) return new PixelExpressionNode { Op = "opaque", Detail = $"value {id}" };
            if (v.HasConstFloat) return new PixelExpressionNode { Op = "imm", Constants = new[] { v.ConstFloat } };
            if (v.HasConstInt) return new PixelExpressionNode { Op = "imm", Constants = new[] { (float) v.ConstInt } };
            if (v.Instr == null) return new PixelExpressionNode { Op = "opaque", Detail = "shader input" };

            var ins = v.Instr;
            switch (ins.Code)
            {
                case "EXTRACTVAL":
                {
                    var aggId = ins.Operands.Count > 0 ? ins.Operands[0] : -1;
                    var agg = ValById(fn, aggId);
                    if (agg?.Instr is { Code: "CALL" } call && call.Callee != null)
                        return CallNode(call, (int) ins.Aux);
                    return Build(aggId, (int) ins.Aux);
                }
                case "CALL": return CallNode(ins, comp);
                case "CAST": return ins.Operands.Count > 0 ? Build(ins.Operands[0], comp) : Opaque("cast");
                case "BINOP":
                {
                    var node = new PixelExpressionNode { Op = LlvmBinOp(ins.Aux) };
                    AddArgs(node, ins.Operands, 0, comp, "A", "B");
                    return node;
                }
                case "SELECT":
                {
                    var node = new PixelExpressionNode { Op = "movc" };
                    AddArgs(node, ins.Operands, 0, comp, "Cond", "True", "False");
                    return node;
                }
                case "PHI":
                {
                    var node = new PixelExpressionNode { Op = "phi", Detail = "differs between branches" };
                    AddArgs(node, ins.Operands, 0, comp);
                    return node;
                }
                case "CMP":
                {
                    var node = new PixelExpressionNode { Op = "compare" };
                    AddArgs(node, ins.Operands, 0, comp, "A", "B");
                    return node;
                }
                case "VEC":
                {
                    var node = new PixelExpressionNode { Op = "append" };
                    AddArgs(node, ins.Operands, 0, comp);
                    return node;
                }
                default: return Opaque(ins.Code.ToLowerInvariant());
            }
        }

        PixelExpressionNode CallNode(DxInstr call, int comp)
        {
            var callee = call.Callee ?? "";
            if (callee.StartsWith("dx.op.cbufferLoad"))
            {
                var handleId = call.Operands.Count > 1 ? call.Operands[1] : -1;
                var row = (int) (ConstOperand(fn, call, 2) ?? -1);
                var reg = handles.TryGetValue(handleId, out var b) ? (ResolveResource(resources, b)?.Register ?? b.Register) : -1;
                var src = resolveLeaf(new DxLeaf { Kind = "cbuffer", Register = reg, Row = row, Component = comp });
                return new PixelExpressionNode { Op = "cbrow", Source = src, Detail = src == null ? $"cb{reg}[{row}].{Comp(comp)}" : string.Empty };
            }
            if (callee.StartsWith("dx.op.sample") || callee.StartsWith("dx.op.textureLoad") || callee.StartsWith("dx.op.textureGather"))
            {
                var handleId = call.Operands.Count > 1 ? call.Operands[1] : -1;
                var reg = handles.TryGetValue(handleId, out var b) ? (ResolveResource(resources, b)?.Register ?? b.Register) : -1;
                var src = resolveLeaf(new DxLeaf { Kind = "texture", Register = reg, Component = comp });
                return new PixelExpressionNode { Op = "sample", Source = src, Detail = src == null ? "texture sample" : string.Empty };
            }
            if (callee.StartsWith("dx.op.unary"))
            {
                var node = new PixelExpressionNode { Op = UnaryName((int) (ConstOperand(fn, call, 0) ?? -1)) };
                AddArgs(node, call.Operands, 1, comp, "X"); // operand[0] is the DXIL opcode immediate
                return node;
            }
            if (callee.StartsWith("dx.op.binary"))
            {
                var node = new PixelExpressionNode { Op = BinaryName((int) (ConstOperand(fn, call, 0) ?? -1)) };
                AddArgs(node, call.Operands, 1, comp, "A", "B");
                return node;
            }
            if (callee.StartsWith("dx.op.tertiary"))
            {
                var node = new PixelExpressionNode { Op = "mad" };
                AddArgs(node, call.Operands, 1, comp, "A", "B", "C");
                return node;
            }
            if (callee.StartsWith("dx.op.dot"))
            {
                var op = callee.Contains("dot2") ? "dp2" : callee.Contains("dot4") ? "dp4" : "dp3";
                var node = new PixelExpressionNode { Op = op };
                AddArgs(node, call.Operands, 1, comp);
                return node;
            }
            if (callee.StartsWith("dx.op.loadInput"))
                return new PixelExpressionNode { Op = "input", Detail = "vertex interpolant" };
            return Opaque(callee.Replace("dx.op.", string.Empty));
        }

        void AddArgs(PixelExpressionNode node, List<int> operands, int start, int comp, params string[] names)
        {
            for (var i = start; i < operands.Count; i++)
                node.Args.Add(new PixelExpressionArg
                {
                    Node = Build(operands[i], comp),
                    Name = i - start < names.Length ? names[i - start] : ArgName(i - start)
                });
        }

        static PixelExpressionNode Opaque(string detail) => new() { Op = "opaque", Detail = detail };
        static string Comp(int c) => c is >= 0 and <= 3 ? "xyzw"[c].ToString() : "?";
        static string ArgName(int i) => i < 26 ? ((char) ('A' + i)).ToString() : $"in{i}";
    }

    private static string LlvmBinOp(long aux) => aux switch
    {
        0 => "add", 1 => "sub", 2 => "mul", 3 => "div", 4 => "div", 5 => "udiv_rem", 6 => "udiv_rem",
        7 => "ishl", 8 => "ushr", 9 => "ishr", 10 => "and", 11 => "or", 12 => "xor", _ => "op"
    };

    private static string UnaryName(int op) => op switch
    {
        12 => "cos", 13 => "sin", 14 => "tan", 15 => "acos", 16 => "asin", 17 => "atan",
        21 => "exp", 22 => "frc", 23 => "log", 24 => "sqrt", 25 => "rsq",
        26 => "round_ne", 27 => "round_ni", 28 => "round_pi", 29 => "round_z", _ => "unary"
    };

    private static string BinaryName(int op) => op switch
    {
        35 => "max", 36 => "min", 37 => "imax", 38 => "imin", 39 => "umax", 40 => "umin", _ => "binary"
    };

    private static List<int> MdNode(DxilModule m, int mdId) => mdId >= 0 && mdId < m.Md.Count && m.Md[mdId].Kind == MdKind.Node ? m.Md[mdId].Operands : null;
    private static long? ConstFromMd(DxilModule m, int mdId)
    {
        if (mdId < 0 || mdId >= m.Md.Count) return null;
        var e = m.Md[mdId];
        if (e.Kind != MdKind.Value || e.ValueId < 0 || e.ValueId >= m.ModuleValues.Count) return null;
        var v = m.ModuleValues[e.ValueId];
        return v.HasConstInt ? v.ConstInt : null;
    }
    private static DxValue ValById(DxFunction fn, int id) => id >= 0 && id < fn.Values.Count ? fn.Values[id] : null;
    private static DxValue ValAt(DxFunction fn, DxInstr instr, int opIdx) => opIdx < instr.Operands.Count ? ValById(fn, instr.Operands[opIdx]) : null;
    private static long? ConstOfValue(DxFunction fn, int id) { var v = ValById(fn, id); return v is { HasConstInt: true } ? v.ConstInt : null; }
    private static long? ConstOperand(DxFunction fn, DxInstr instr, int opIdx) => opIdx < instr.Operands.Count ? ConstOfValue(fn, instr.Operands[opIdx]) : null;
    public static long? ConstOp(DxFunction fn, DxInstr instr, int opIdx) => ConstOperand(fn, instr, opIdx);
}
