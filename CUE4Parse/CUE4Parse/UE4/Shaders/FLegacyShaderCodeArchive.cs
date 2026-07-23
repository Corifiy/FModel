using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;

namespace CUE4Parse.UE4.Shaders;

/// <summary>
/// UE4 pak-cooked shared shader code library (.ushaderbytecode, GShaderCodeArchiveVersion == 1,
/// ShaderCodeLibrary.cpp): shaders are keyed by their output hash, code is stored back to back
/// after the entry map and each blob is platform-compressed (Zlib) when the stored size differs
/// from the uncompressed size, exactly like inline FShaderResource code.
/// </summary>
public class FLegacyShaderCodeArchive : FRHIShaderLibrary
{
    public readonly Dictionary<FSHAHash, FShaderCodeEntryLegacy> Shaders;
    /// <summary>Raw code blob; entry offsets are relative to its start.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public readonly byte[] Code;

    public FLegacyShaderCodeArchive(FArchive Ar)
    {
        var count = Ar.Read<int>();
        Shaders = new Dictionary<FSHAHash, FShaderCodeEntryLegacy>(count);
        for (var i = 0; i < count; i++)
        {
            var hash = new FSHAHash(Ar);
            Shaders[hash] = Ar.Read<FShaderCodeEntryLegacy>();
        }
        Code = Ar.ReadBytes((int) (Ar.Length - Ar.Position));
    }

    /// <summary>Returns the decompressed bytecode for a shader by its output hash, or null.</summary>
    public byte[]? TryGetCode(FSHAHash hash)
    {
        if (!Shaders.TryGetValue(hash, out var entry) ||
            entry.Offset + entry.Size > (ulong) Code.Length)
            return null;

        var stored = new byte[entry.Size];
        Array.Copy(Code, (long) entry.Offset, stored, 0, entry.Size);
        return entry.Size == entry.UncompressedSize
            ? stored
            : Compression.Compression.Decompress(stored, (int) entry.UncompressedSize, CompressionMethod.Zlib);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FShaderCodeEntryLegacy
{
    public readonly ulong Offset;
    public readonly uint Size;
    public readonly uint UncompressedSize;
    public readonly byte Frequency;
}
