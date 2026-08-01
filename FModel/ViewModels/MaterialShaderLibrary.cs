using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;

namespace FModel.ViewModels;

/// <summary>
/// Retrieves a shader's compiled bytecode from a UE4 pak-cooked shared shader library
/// (.ushaderbytecode, GShaderCodeArchiveVersion == 2 - the FSerializedShaderArchive format used from
/// 4.25 on; the 4.23-era version-1 format is handled separately by
/// <see cref="PixelShaderDecompiler"/>'s own output-hash resolver, and the UE5 IoStore one by
/// <see cref="MaterialDxilLibrary"/>).
///
/// <para>
/// A 4.25+ material shader map that shares its code carries only a ResourceHash; that hash selects a
/// shadermap entry in the library, and a shader's own <c>FShader.ResourceIndex</c> then selects its
/// position within that entry's slice of the flat ShaderIndices array - exactly the lookup
/// FShaderCodeArchive::GetShaderMapIndex/GetShaderIndex performs (ShaderCodeArchive.cpp).
/// </para>
/// <para>
/// Only each library's header is parsed and cached; code is read by ranged seek on demand, because
/// these libraries are routinely hundreds of megabytes (Fortnite 14.40's is ~820 MB) and a decompile
/// needs a few kilobytes out of one of them.
/// </para>
/// </summary>
internal static class MaterialShaderLibrary
{
    private sealed record Library(string Path, FSerializedShaderArchive Archive, long CodeStart);

    private static readonly ConditionalWeakTable<IFileProvider, List<Library>> _cache = new();

    private static List<Library> GetLibraries(IFileProvider provider)
    {
        lock (_cache)
        {
            return _cache.GetValue(provider, static p =>
            {
                var libraries = new List<Library>();
                foreach (var (path, file) in p.Files)
                {
                    if (!path.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var ar = file.CreateReader();
                        // ShaderCodeLibrary.cpp: the archive version precedes the serialized shader table
                        if (ar.Read<uint>() != 2) continue;
                        var archive = new FSerializedShaderArchive(ar);
                        libraries.Add(new Library(path, archive, ar.Position));
                    }
                    catch
                    {
                        // an unreadable library only costs us the shaders it holds
                    }
                }
                return libraries;
            });
        }
    }

    /// <summary>
    /// Returns the decompressed bytecode for the shader at <paramref name="resourceIndex"/> of the
    /// shader map identified by <paramref name="resourceHash"/>, or null with the reason why not.
    /// </summary>
    public static byte[]? TryGetShaderCode(IFileProvider provider, FSHAHash resourceHash, int resourceIndex, out string? error)
    {
        error = null;
        var libraries = GetLibraries(provider);
        if (libraries.Count == 0)
        {
            error = "no pak-cooked shader library (.ushaderbytecode) is mounted";
            return null;
        }

        foreach (var library in libraries)
        {
            var archive = library.Archive;
            var mapIndex = Array.IndexOf(archive.ShaderMapHashes, resourceHash);
            if (mapIndex < 0) continue;

            var map = archive.ShaderMapEntries[mapIndex];
            if (resourceIndex < 0 || resourceIndex >= map.NumShaders)
            {
                error = $"shader index {resourceIndex} is outside this shader map's {map.NumShaders} shaders";
                return null;
            }

            var entryIndex = (int) archive.ShaderIndices[map.ShaderIndicesOffset + resourceIndex];
            if (entryIndex < 0 || entryIndex >= archive.ShaderEntries.Length)
            {
                error = "the shader map references a shader entry outside the library";
                return null;
            }

            var entry = archive.ShaderEntries[entryIndex];
            try
            {
                using var ar = provider.Files[library.Path].CreateReader();
                ar.Position = library.CodeStart + (long) entry.Offset;
                var stored = ar.ReadBytes((int) entry.Size);
                // ShaderCodeArchive.cpp: ShaderLibraryCompressionFormat is LZ4, and an entry is only
                // stored compressed when that actually made it smaller
                return entry.Size == entry.UncompressedSize
                    ? stored
                    : Compression.Decompress(stored, (int) entry.UncompressedSize, CompressionMethod.LZ4);
            }
            catch (Exception e)
            {
                error = $"the shader could not be read from {library.Path}: {e.Message}";
                return null;
            }
        }

        error = "this material's shader map was not found in any shader library";
        return null;
    }
}
