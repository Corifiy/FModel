using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.Material;

public class FMaterialResourceProxyReader : FArchive
{
    protected readonly FArchive InnerArchive;
    public bool bUseNewFormat;
    /// <summary>
    /// Position of the first resource, captured after the name map/locs header.
    /// In the legacy (&lt; 4.25) format all cooked Seek/Tell offsets (shader end offsets,
    /// vertex factory parameter skip offsets) are relative to this position.
    /// </summary>
    public long OffsetToFirstResource;
    /// <summary>
    /// Which byte layout the legacy (&lt; 4.25) shader map uses. The legacy format carries no
    /// version information (the engine relies on the DDC key instead), so branches cut before
    /// the 4.23 virtual-texture merge (e.g. Fortnite Season X) serialize differently and are
    /// detected by parse-validate-retry in FMaterial.DeserializeInlineShaderMap.
    /// </summary>
    public ELegacyShaderMapProfile LegacyProfile = ELegacyShaderMapProfile.UE4_23;
    private readonly FNameEntrySerialized[]? _nameMap;
    private readonly bool _readNameMap;
    private readonly bool _passthrough;
    /// <summary>True for the pre-proxy-reader (~4.19-era) format constructed via <see cref="CreatePassthrough"/>.</summary>
    public bool IsPassthrough => _passthrough;

    public FMaterialResourceProxyReader(FArchive Ar, bool bReadNameMap = true) : base(Ar.Versions)
    {
        InnerArchive = Ar;
        bUseNewFormat = Ar.Versions["ShaderMap.UseNewCookedFormat"];
        _readNameMap = bReadNameMap;

        if (!bReadNameMap && Ar is FAssetArchive assetArchive)
        {
            _nameMap = assetArchive.Owner?.NameMap;
            _readNameMap = true;
            OffsetToFirstResource = Ar.Position;
            return;
        }

        if (_readNameMap)
        {
            _nameMap = InnerArchive.ReadArray(() => new FNameEntrySerialized(Ar));
            var num = Ar.Read<int>();
            Ar.Position += num * Unsafe.SizeOf<FMaterialResourceLocOnDisk>(); // Locs
            if (Ar.Game is EGame.GAME_ArenaBreakoutInfinite or EGame.GAME_ArenaBreakoutMobile) Ar.Position += num;
            if (Ar.Game is EGame.GAME_RocoKingdomWorld) Ar.Position += num * 5;
            Ar.Position += 4; // NumBytes
        }

        OffsetToFirstResource = Ar.Position;
    }

    /// <summary>
    /// Marker-only constructor for <see cref="CreatePassthrough"/> - distinct signature from the
    /// public (Ar, bReadNameMap) constructor so the two never collide by overload resolution.
    /// </summary>
    private FMaterialResourceProxyReader(FArchive Ar, bool _, bool __) : base(Ar.Versions)
    {
        InnerArchive = Ar;
        bUseNewFormat = Ar.Versions["ShaderMap.UseNewCookedFormat"];
        _readNameMap = false;
        _passthrough = true;
        OffsetToFirstResource = Ar.Position;
    }

    /// <summary>
    /// Pre-proxy-reader era (confirmed against real engine source: still present verbatim in
    /// UE_4.19\Engine\Source\Runtime\Engine\Private\Materials\Material.cpp's
    /// SerializeInlineShaderMaps/FMaterial::SerializeInlineShaderMap) - the archive serializes
    /// FriendlyName/DebugDescription/etc. directly through the engine's own normal FString/FName
    /// mechanism. There is no embedded local name map, locs table, or NumBytes preamble at all -
    /// that whole wrapper (this class's default constructor) was introduced later specifically to
    /// let the engine skip loading the full package name table just to peek at shader data, and
    /// attempting to read it against an archive that never wrote it is exactly what previously threw
    /// "Invalid FString length" - a stream-misalignment symptom, not a coincidence, confirmed by
    /// reading the very next bytes as if they were a length-prefixed local name-map entry that never
    /// existed. Every deserialization method in LegacyShaderMap.cs is typed to
    /// FMaterialResourceProxyReader specifically (not a generic FArchive), so this preserves the
    /// exact same public surface - ReadFString/ReadFName just delegate straight through to the real
    /// underlying archive's own implementation - letting that whole codebase be reused unchanged for
    /// this older era rather than duplicated.
    /// </summary>
    public static FMaterialResourceProxyReader CreatePassthrough(FArchive Ar) => new(Ar, false, false);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct FMaterialResourceLocOnDisk
    {
        /** Relative offset to package (uasset/umap + uexp) beginning */
        public readonly uint Offset;
        public readonly ERHIFeatureLevel FeatureLevel;
        public readonly EMaterialQualityLevel QualityLevel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override FName ReadFName()
    {
        if (_passthrough) return InnerArchive.ReadFName();

        var nameIndex = InnerArchive.Read<int>();
        var number = InnerArchive.Read<int>();
#if !NO_FNAME_VALIDATION
        if (nameIndex < 0 || nameIndex >= _nameMap!.Length)
        {
            throw new ParserException(InnerArchive, $"FName could not be read, requested index {nameIndex}, name map size {_nameMap.Length}");
        }
#endif
        return new FName(_nameMap[nameIndex], nameIndex, number);
    }

    /// <summary>DEBUG ONLY: exposes the global (passthrough) name map for diagnostic byte-offset probing.</summary>
    public FNameEntrySerialized[]? DebugGlobalNameMap => (InnerArchive as CUE4Parse.UE4.Assets.Readers.FAssetArchive)?.Owner?.NameMap;

    public string ReadFString(bool bReadNameMap) => bReadNameMap ? ReadFName().PlainText : base.ReadFString();
    public override string ReadFString() => _passthrough ? InnerArchive.ReadFString() : ReadFString(_readNameMap);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int Read(byte[] buffer, int offset, int count)
        => InnerArchive.Read(buffer, offset, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override long Seek(long offset, SeekOrigin origin)
        => InnerArchive.Seek(offset, origin);

    public override bool CanSeek => InnerArchive.CanSeek;
    public override long Length => InnerArchive.Length;

    public override long Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => InnerArchive.Position;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => InnerArchive.Position = value;
    }

    public override string Name => InnerArchive.Name;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override T Read<T>()
        => InnerArchive.Read<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override byte[] ReadBytes(int length)
        => InnerArchive.ReadBytes(length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override unsafe void Serialize(byte* ptr, int length)
        => InnerArchive.Serialize(ptr, length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override T[] ReadArray<T>(int length)
        => InnerArchive.ReadArray<T>(length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void ReadArray<T>(T[] array)
        => InnerArchive.ReadArray(array);

    public override object Clone()
    {
        var clonedInner = (FArchive) InnerArchive.Clone();
        FMaterialResourceProxyReader clone = _passthrough
            ? new FMaterialResourceProxyReader(clonedInner, false, false)
            : new FMaterialResourceProxyReader(clonedInner);
        clone.LegacyProfile = LegacyProfile;
        return clone;
    }
}

/// <summary>Byte-layout profiles of the unversioned legacy (&lt; 4.25) inline shader map.</summary>
public enum ELegacyShaderMapProfile
{
    /// <summary>Stock UE 4.23/4.24: virtual-texture era expression set, packed compilation flags byte.</summary>
    UE4_23,
    /// <summary>
    /// Branches cut before the 4.23 virtual-texture merge (e.g. Fortnite Season X):
    /// texture expressions carry only TextureIndex+SamplerSource, the expression set has no
    /// virtual texture arrays/stacks and compilation output flags are separate 4-byte bools.
    /// </summary>
    PreVirtualTexture,
    /// <summary>
    /// Real UE 4.19 (confirmed against the engine source, MaterialUniformExpressions.cpp:102 /
    /// MaterialShared.cpp:346): texture expressions match PreVirtualTexture's TextureIndex+
    /// SamplerSource shape, but FUniformExpressionSet::Serialize has no volume-texture array and no
    /// reserved 2D-texture-array slot, ParameterCollections comes right after ExternalTexture
    /// expressions, and is followed by four (unused-here, still on-disk) PerFrame/PerFramePrev
    /// arrays. FMaterialCompilationOutput::Serialize is nine separate 4-byte bools with no leading
    /// UsedSceneTextures/estimate fields at all - so, unlike PreVirtualTexture, no anchor scan is
    /// needed to find the end of the tail.
    /// </summary>
    UE4_19
}

public enum EMaterialQualityLevel : byte
{
    Low,
    High,
    Medium,
    Epic,
    Num
}

public enum ERHIFeatureLevel : byte
{
    /** Feature level defined by the core capabilities of OpenGL ES2. Deprecated */
    ES2_REMOVED,

    /** Feature level defined by the core capabilities of OpenGL ES3.1 & Metal/Vulkan. */
    ES3_1,

    /**
         * Feature level defined by the capabilities of DX10 Shader Model 4.
         * SUPPORT FOR THIS FEATURE LEVEL HAS BEEN ENTIRELY REMOVED.
         */
    SM4_REMOVED,

    /**
         * Feature level defined by the capabilities of DX11 Shader Model 5.
         *   Compute shaders with shared memory, group sync, UAV writes, integer atomics
         *   Indirect drawing
         *   Pixel shaders with UAV writes
         *   Cubemap arrays
         *   Read-only depth or stencil views (eg read depth buffer as SRV while depth test and stencil write)
         * Tessellation is not considered part of Feature Level SM5 and has a separate capability flag.
         */
    SM5,

    /**
         * Feature level defined by the capabilities of DirectX 12 hardware feature level 12_2 with Shader Model 6.5
         *   Raytracing Tier 1.1
         *   Mesh and Amplification shaders
         *   Variable rate shading
         *   Sampler feedback
         *   Resource binding tier 3
         */
    SM6,
    Num
}
