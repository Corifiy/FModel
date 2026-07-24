using System;
using System.Collections.Generic;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Assets.Exports.Material;

// =====================================================================================================
// UE 4.23/4.24 (pre-FMemoryImage) cooked inline shader map format.
// All layouts decoded 1:1 from the UE 4.23 engine source (E:\EpicGames\UE_4.23):
//  - FMaterial::SerializeInlineShaderMap            Engine/Private/Materials/MaterialShared.cpp
//  - FMaterialShaderMap::Serialize                  Engine/Private/Materials/MaterialShader.cpp
//  - FMaterialShaderMapId::Serialize (cooked)       Engine/Private/Materials/MaterialShader.cpp
//  - FMaterialCompilationOutput::Serialize          Engine/Private/Materials/MaterialShared.cpp
//  - FUniformExpressionSet::Serialize + expressions Engine/Private/Materials/MaterialUniformExpressions.*
//  - TShaderMap::SerializeInline                    RenderCore/Public/Shader.h
//  - FShader::SerializeBase                         RenderCore/Private/Shader.cpp
//  - FShaderResource::Serialize                     RenderCore/Private/Shader.cpp
//  - FMaterialShader/FMeshMaterialShader::Serialize Renderer/Private/ShaderBaseClasses.cpp
//  - TBasePassPixelShaderPolicyParamType::Serialize Renderer/Private/BasePassRendering.h
//  - FVertexFactoryParameterRef operator<<          RenderCore/Private/VertexFactory.cpp
// NOTE: all Seek/Tell offsets written by the cooker (shader end offsets, vertex factory parameter
// skip offsets) are RELATIVE to FMaterialResourceProxyReader::OffsetToFirstResource.
// =====================================================================================================

/// <summary>
/// Carries whatever shaders SerializeInline (UE4_19 profile) managed to parse before hitting one it
/// couldn't, since Ar.Position is left in an unrecoverable state at that point and there's no safe
/// way to keep reading (no per-shader end offset to resync against at this engine version).
/// </summary>
internal class PartialShaderArrayException(FShaderLegacy[] partialShaders, Exception inner)
    : Exception(inner.Message, inner)
{
    public FShaderLegacy[] PartialShaders { get; } = partialShaders;
}

/// <summary>FMaterialShaderMap serialized with the legacy (pre-FMemoryImage, &lt; 4.25) format.</summary>
public class FMaterialShaderMapLegacy
{
    public FMaterialShaderMapIdLegacy ShaderMapId;
    public EShaderPlatform ShaderPlatform;
    public string FriendlyName;
    public FMaterialCompilationOutputLegacy MaterialCompilationOutput;
    public string DebugDescription;
    /// <summary>Material (non-mesh) shaders of the map.</summary>
    public FShaderLegacy[] Shaders = [];
    /// <summary>Mesh material shader maps, keyed by vertex factory type name.</summary>
    public FMeshMaterialShaderMapLegacy[] MeshShaderMaps = [];
    /// <summary>
    /// Byte-layout profile the map was parsed with. On PreVirtualTexture branches the
    /// FMaterialCompilationOutput flag fields are skipped, so they keep their defaults.
    /// </summary>
    public ELegacyShaderMapProfile ParsedProfile;

    /// <summary>
    /// Deserializes the map and returns whether the stream stayed aligned all the way to the
    /// trailing bCooked flag — the caller uses this to detect a wrong byte-layout profile.
    /// </summary>
    public bool Deserialize(FMaterialResourceProxyReader Ar, string? expectedFriendlyName = null)
    {
        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var mapPos = Ar.Position;
            var peek = Ar.ReadBytes((int) Math.Min(112, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: map start at {0} ({1}): {2}", mapPos, Ar.LegacyProfile, Convert.ToHexString(peek));
            Ar.Position = mapPos;
        }

        ParsedProfile = Ar.LegacyProfile;
        ShaderMapId = new FMaterialShaderMapIdLegacy(Ar, expectedFriendlyName);

        if (ShaderMapId.ShaderPlatformFromVeryLegacyScan is { } scannedPlatform)
        {
            // Very-legacy (pre-proxy-reader) path: FMaterialShaderMapIdLegacy's own anchor scan
            // already consumed ShaderPlatform+FriendlyName while locating where its (unmodeled)
            // ShaderMapId tail ends - reuse those instead of re-reading, since the stream has already
            // moved past them.
            ShaderPlatform = (EShaderPlatform) scannedPlatform;
            FriendlyName = ShaderMapId.FriendlyNameFromVeryLegacyScan!;
        }
        else
        {
            ShaderPlatform = (EShaderPlatform) Ar.Read<int>();
            FriendlyName = Ar.ReadFString(false); // raw FString, not name-map based
        }
        var compilationOutputPos = Ar.Position;
        Log.Verbose("LegacyShaderMap '{0}': compilation output at {1}", FriendlyName, Ar.Position);
        MaterialCompilationOutput = new FMaterialCompilationOutputLegacy(Ar, FriendlyName);
        Log.Verbose("LegacyShaderMap: debug description at {0}", Ar.Position);
        DebugDescription = Ar.ReadFString(false);

        // TShaderMap<FMaterialShaderType>::SerializeInline(Ar, true, false, bLoadedByCookedMaterial)
        Log.Verbose("LegacyShaderMap: material shaders at {0}", Ar.Position);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // UE4_19's per-shader binary layout (FMaterialShader/FMeshMaterialShader field-by-field
            // serialization, with no end-offset to resync on failure - see Read/
            // ReadUnknownTypeUnbounded above) is still being reverse-engineered and can fail partway
            // through an unrelated shader (e.g. a lighting-injection or shadow-depth shader this
            // reader doesn't fully model yet). A failure here has no reliable resync point for
            // MeshShaderMaps/the trailing bCooked flag either, so there is no way to keep parsing this
            // map - but everything already parsed above (ShaderMapId, ShaderPlatform, FriendlyName,
            // and critically MaterialCompilationOutput's real uniform expression tree - the actual
            // material graph data) is real, validated data that would otherwise be thrown away.
            // Keep it: report success with an empty Shaders/MeshShaderMaps array rather than losing
            // the whole map to a DXBC-reconstruction-only failure.
            try
            {
                Shaders = SerializeInline(Ar);
            }
            catch (PartialShaderArrayException e)
            {
                Shaders = e.PartialShaders;
                Log.Warning(e, "LegacyShaderMap (UE4_19): shader binary section failed to parse; keeping the {0} material shader(s) already parsed.", Shaders.Length);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(e, "LegacyShaderMap (UE4_19): shader binary section failed to parse; keeping the uniform expression tree without DXBC data.");
                return true;
            }
        }
        else
        {
            Shaders = SerializeInline(Ar);
        }

        // Mesh material shaders: count + (VFType name + inline map) per entry
        Log.Verbose("LegacyShaderMap: mesh shader maps at {0}", Ar.Position);
        var numMeshShaderMaps = Ar.Read<int>();
        var meshShaderMapsList = new List<FMeshMaterialShaderMapLegacy>(numMeshShaderMaps);
        var resyncedMeshShaderMaps = false;
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // Same non-fatal handling as the material shaders array above: a mesh shader (e.g.
            // TBasePassPS*) can still fail to parse under UE4_19 with no reliable resync point within
            // its own array, but everything already parsed (including, now, the material shaders array
            // above) is real data worth keeping rather than losing to a DXBC-reconstruction-only
            // failure. Built as a list rather than a pre-sized array so a partial failure never leaves
            // null entries for callers to trip over.
            //
            // A single VF entry failing (e.g. a genuinely unmodeled VF type like
            // FMeshParticleVertexFactory) used to abandon every VF entry after it too, even ones with
            // perfectly good, fully-parseable data - a real character material can carry a dozen+ VF
            // entries (static mesh, several skeletal-mesh variants, particles, ...) and the one this
            // decompiler actually needs (e.g. TGPUSkinVertexFactory's TBasePassPS) could easily sit
            // after the one it doesn't understand yet. Recover instead: scan forward (bounded, same
            // idea as ReadUnknownTypeUnbounded/TryResyncViaBaseMaterialId) for the next FName that
            // resolves to a string containing "VertexFactory" - a reliable, VF-type-specific substring
            // essentially never produced by unrelated data - and resume the loop there, so only the one
            // bad entry is lost rather than everything after it.
            var i = 0;
            var unrecoverable = false;
            while (i < numMeshShaderMaps)
            {
                var vertexFactoryTypeName = Ar.ReadFName().Text;
                try
                {
                    meshShaderMapsList.Add(new FMeshMaterialShaderMapLegacy
                    {
                        VertexFactoryTypeName = vertexFactoryTypeName,
                        Shaders = SerializeInline(Ar)
                    });
                    i++;
                }
                catch (Exception e)
                {
                    var partialShaders = (e as PartialShaderArrayException)?.PartialShaders ?? [];
                    meshShaderMapsList.Add(new FMeshMaterialShaderMapLegacy
                    {
                        VertexFactoryTypeName = vertexFactoryTypeName,
                        Shaders = partialShaders
                    });
                    Log.Warning(e, "LegacyShaderMap (UE4_19): mesh shader map entry {0}/{1} (VF={2}) failed to parse; keeping the {3} shader(s) already parsed for it.",
                        i + 1, numMeshShaderMaps, vertexFactoryTypeName, partialShaders.Length);

                    resyncedMeshShaderMaps = true;
                    if (!TryResyncToNextVertexFactoryEntry(Ar))
                    {
                        Log.Warning("LegacyShaderMap (UE4_19): could not resync to a later mesh shader map entry; keeping everything parsed so far without full DXBC data for the rest.");
                        unrecoverable = true;
                        break;
                    }
                    i++;
                }
            }
            if (unrecoverable)
            {
                MeshShaderMaps = meshShaderMapsList.ToArray();
                return true;
            }
        }
        else
        {
            for (var i = 0; i < numMeshShaderMaps; i++)
            {
                meshShaderMapsList.Add(new FMeshMaterialShaderMapLegacy
                {
                    VertexFactoryTypeName = Ar.ReadFName().Text,
                    Shaders = SerializeInline(Ar)
                });
            }
        }
        MeshShaderMaps = meshShaderMapsList.ToArray();

        // A VF-entry resync above validates its own landing spot (a plausible shader count right after
        // the candidate name), but that's a heuristic, not a guarantee - if it still landed wrong, the
        // rest of the parse from that point on is unreliable. Rather than let a bad trailing bCooked
        // read (or anything it can throw) take down data that was genuinely recovered earlier in this
        // same resource, treat a resynced parse as complete and correct up to this point regardless.
        if (resyncedMeshShaderMaps) return true;

        var bCookedPos = Ar.Position;
        var bCooked = Ar.ReadBoolean();
        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            Log.Verbose("LegacyShaderMap: trailing bCooked={0} at {1}, posAfter={2}", bCooked, bCookedPos, Ar.Position);
        // cooked data always writes bCooked=true here; reading anything else means the
        // stream drifted and the byte-layout profile does not match this branch's format
        if (!bCooked && Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            Ar.Position = compilationOutputPos;
            var context = Ar.ReadBytes((int) Math.Min(bCookedPos + 96 - compilationOutputPos, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: compilation output bytes ({0}..{1}): {2}", compilationOutputPos, bCookedPos, Convert.ToHexString(context));
            Ar.Position = bCookedPos + 4;
        }
        return bCooked;
    }

    /// <summary>TShaderMap::SerializeInline load path: shaders + shader pipelines (pipelines are consumed but skipped).</summary>
    internal static FShaderLegacy[] SerializeInline(FMaterialResourceProxyReader Ar)
    {
        var numShaders = Ar.Read<int>();
        Log.Verbose("LegacyShaderMap: SerializeInline {0} shaders at {1}", numShaders, Ar.Position);
        var shaders = new List<FShaderLegacy>(numShaders);
        for (var i = 0; i < numShaders; i++)
        {
            if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
            {
                // UE4_19 has no per-shader end offset (see FShaderLegacy.Read), so a shader that
                // fails to deserialize - whether from an unmodeled type-specific layout or a real
                // parsing bug - leaves no way to resync to the next one in this array, and Ar.Position
                // is left in an unknown state. Rather than silently continuing to read numPipelines
                // (below) from that unknown position - which previously corrupted the rest of the
                // parse instead of failing loudly - wrap the shaders parsed so far into the exception
                // so the caller (which already knows how to discard just the current array/entry and
                // keep everything before it) can preserve them instead of losing this array entirely.
                try
                {
                    var shader19 = FShaderLegacy.Read(Ar);
                    if (shader19 != null) shaders.Add(shader19);
                }
                catch (Exception e)
                {
                    Log.Warning(e, "LegacyShaderMap (UE4_19): shader {0}/{1} in this array failed to parse; keeping the {2} shader(s) already parsed.",
                        i + 1, numShaders, shaders.Count);
                    throw new PartialShaderArrayException(shaders.ToArray(), e);
                }
                continue;
            }
            var shader = FShaderLegacy.Read(Ar);
            if (shader != null) shaders.Add(shader);
        }

        var numPipelines = Ar.Read<int>();
        for (var p = 0; p < numPipelines; p++)
        {
            Ar.ReadFName(); // FShaderPipelineType name
            var numStages = Ar.Read<int>();
            for (var s = 0; s < numStages; s++)
            {
                var shader = FShaderLegacy.Read(Ar);
                if (shader != null) shaders.Add(shader);
            }
        }

        return shaders.ToArray();
    }

    /// <summary>
    /// Scans forward from the current position for the next FName that resolves to a string containing
    /// "VertexFactory" - a substring essentially never produced by anything else in this stream, making
    /// it a reliable anchor for the start of some later mesh shader map entry (see the caller for why
    /// this recovery exists). Bounded to 512KB, matching the scale of a single VF entry's own shader
    /// array (which can itself run past 128KB per shader per ReadUnknownTypeUnbounded's own window).
    ///
    /// A name containing "VertexFactory" can also legitimately appear elsewhere (e.g. as part of a
    /// shader's own bound uniform buffer name, "FMeshParticleVertexFactoryUniformShaderParameters",
    /// deep inside an entry that's already failing) - so every candidate is validated the same way
    /// SerializeInline's own entry point would consume it: the 4 bytes immediately after the FName are
    /// TShaderMap::SerializeInline's own shader count, which is never remotely large in real data.
    /// Rejecting implausible counts and continuing the scan is what keeps a false match (which would
    /// otherwise send the whole rest of the parse into the weeds, losing far more than the one entry
    /// this recovery is trying to save) from ever being accepted. Leaves the position unchanged and
    /// returns false if nothing plausible is found in the whole window.
    /// </summary>
    private static bool TryResyncToNextVertexFactoryEntry(FMaterialResourceProxyReader Ar)
    {
        var nameMap = Ar.DebugGlobalNameMap;
        if (nameMap == null) return false;

        var start = Ar.Position;
        var scanLen = (int) Math.Min(512 * 1024, Ar.Length - start - 12);
        if (scanLen <= 0) return false;
        var buf = Ar.ReadBytes(scanLen + 12);
        Ar.Position = start;

        for (var offset = 0; offset <= scanLen; offset++)
        {
            var idx = BitConverter.ToInt32(buf, offset);
            var number = BitConverter.ToInt32(buf, offset + 4);
            if (number != 0 || idx < 0 || idx >= nameMap.Length) continue;
            if (nameMap[idx].Name?.Contains("VertexFactory", StringComparison.Ordinal) != true) continue;

            var numShaders = BitConverter.ToInt32(buf, offset + 8);
            if (numShaders is < 0 or > 200)
            {
                Log.Verbose("LegacyShaderMap: candidate resync to '{0}' at {1} rejected, implausible shader count {2}", nameMap[idx].Name, start + offset, numShaders);
                continue;
            }

            Ar.Position = start + offset;
            Log.Verbose("LegacyShaderMap: resynced to next mesh shader map entry ('{0}') by {1} bytes at {2}, numShaders={3}", nameMap[idx].Name, offset, start, numShaders);
            return true;
        }
        return false;
    }
}

public class FMeshMaterialShaderMapLegacy
{
    public string VertexFactoryTypeName;
    public FShaderLegacy[] Shaders = [];
}

/// <summary>Cooked FMaterialShaderMapId (&lt; 4.25): quality + feature level + id hash, no layout params.</summary>
public class FMaterialShaderMapIdLegacy
{
    public EMaterialQualityLevel QualityLevel;
    public ERHIFeatureLevel FeatureLevel;
    public FSHAHash CookedShaderMapIdHash;

    /// <summary>
    /// Only populated on the very-legacy (pre-proxy-reader, ~4.19-era) anchor-scan path below - that
    /// era's FMaterialShaderMapId::Serialize (confirmed against UE_4.19\...\MaterialShader.cpp:480)
    /// carries far more fields than the simplified cooked form used from roughly 4.22 onward: Usage,
    /// BaseMaterialId, a full FStaticParameterSet (4 sub-arrays: StaticSwitchParameters/
    /// StaticComponentMaskParameters/TerrainLayerWeightParameters/MaterialLayersParameters, the last
    /// itself containing a version-gated nested FMaterialLayersFunctions), ReferencedFunctions,
    /// ReferencedParameterCollections, ShaderTypeDependencies, ShaderPipelineTypeDependencies,
    /// VertexFactoryTypeDependencies, and a trailing hash - none of which this decompiler needs the
    /// actual values of. Modeling every one of those precisely (each with its own version gates) is a
    /// large amount of new code for data this decompiler would only ever discard, so instead this
    /// reads the one meaningful, fixed-size fact worth keeping this method's own way (QualityLevel/
    /// FeatureLevel), then scans forward for the FriendlyName FString that always immediately follows
    /// ShaderPlatform right after ShaderMapId ends - the same anchor-recovery technique
    /// SkipToDebugDescription below already relies on. If the anchor is wrong, the trailing bCooked
    /// validation this whole parser already depends on fails cleanly rather than silently producing
    /// wrong data - this is a real risk reduction, not just a shortcut.
    /// </summary>
    public int? ShaderPlatformFromVeryLegacyScan;
    public string? FriendlyNameFromVeryLegacyScan;

    /// <summary>
    /// Only populated on the very-legacy path (see above). Every resource belonging to the same
    /// UMaterial writes the same BaseMaterialId, which makes it a reliable resync anchor for
    /// recovering the start of a later resource in <see cref="UMaterialInterface.DeserializeInlineShaderMaps"/>
    /// after an earlier resource in the same array leaves the stream at an unrecoverable position -
    /// scan forward for this exact 16-byte sequence reappearing, then walk back 8 bytes (bCooked+bValid)
    /// to land on the next resource's own start.
    /// </summary>
    public FGuid? BaseMaterialIdFromVeryLegacyScan;

    public FMaterialShaderMapIdLegacy(FArchive Ar, string? expectedFriendlyName = null)
    {
        if (Ar is FMaterialResourceProxyReader { IsPassthrough: true })
        {
            DeserializeVeryLegacy(Ar, expectedFriendlyName);
            return;
        }

        QualityLevel = (EMaterialQualityLevel) Ar.Read<int>();
        FeatureLevel = (ERHIFeatureLevel) Ar.Read<int>();
        CookedShaderMapIdHash = new FSHAHash(Ar);
    }

    private void DeserializeVeryLegacy(FArchive Ar, string? expectedFriendlyName)
    {
        Ar.Position += 4; // Usage (uint32) - not needed
        BaseMaterialIdFromVeryLegacyScan = Ar.Read<FGuid>();
        // Confirmed via engine source: PURGED_FMATERIAL_COMPILE_OUTPUTS already gates QualityLevel/
        // FeatureLevel as two plain int32s (vs. a single legacy uint8) even at this era, and this
        // parser is only ever reached once that same threshold has already been confirmed by the
        // outer Ar.Ver check in UMaterial/UMaterialInstance.Deserialize.
        QualityLevel = (EMaterialQualityLevel) Ar.Read<int>();
        FeatureLevel = (ERHIFeatureLevel) Ar.Read<int>();

        if (!TryScanForShaderPlatformAndFriendlyName(Ar, expectedFriendlyName, out var shaderPlatform, out var friendlyName))
            throw new ParserException(Ar, "Could not locate the ShaderPlatform/FriendlyName anchor after a very-legacy FMaterialShaderMapId");
        ShaderPlatformFromVeryLegacyScan = shaderPlatform;
        FriendlyNameFromVeryLegacyScan = friendlyName;
    }

    /// <summary>
    /// Scans forward for ShaderPlatform+FriendlyName (see the class summary above). When
    /// <paramref name="expectedFriendlyName"/> is known (the owning material's own short Name - see
    /// FMaterial.DeserializeInlineShaderMap), this requires an EXACT match against it, which is
    /// effectively false-positive-proof: a specific, non-trivial identifier string (typically 15-40
    /// characters) occurring by coincidence inside unrelated binary data (the unmodeled
    /// FStaticParameterSet/dependency-array bytes preceding it) is not realistically possible. Only
    /// falls back to the generic "plausible short printable-ASCII FString" heuristic when no expected
    /// name was supplied - weaker, but still bounded by the trailing bCooked check the caller
    /// validates against, so a wrong match still fails cleanly rather than silently producing wrong
    /// data.
    /// </summary>
    private static bool TryScanForShaderPlatformAndFriendlyName(FArchive Ar, string? expectedFriendlyName, out int shaderPlatform, out string friendlyName)
    {
        var start = Ar.Position;
        var window = (int) Math.Min(8192, Ar.Length - start);
        var bytes = Ar.ReadBytes(window);

        if (!string.IsNullOrEmpty(expectedFriendlyName))
        {
            var expectedBytes = System.Text.Encoding.ASCII.GetBytes(expectedFriendlyName);
            var expectedLength = expectedBytes.Length + 1; // FString ANSI length includes the null terminator
            for (var offset = 0; offset + 8 + expectedLength <= window; offset++)
            {
                if (BitConverter.ToInt32(bytes, offset + 4) != expectedLength) continue;
                var matches = true;
                for (var i = 0; i < expectedBytes.Length; i++)
                {
                    if (bytes[offset + 8 + i] != expectedBytes[i]) { matches = false; break; }
                }
                if (!matches || bytes[offset + 8 + expectedLength - 1] != 0) continue;

                shaderPlatform = BitConverter.ToInt32(bytes, offset);
                friendlyName = expectedFriendlyName;
                Ar.Position = start + offset + 8 + expectedLength;
                Log.Verbose("LegacyShaderMap: anchor scan matched '{0}' at absolute offset {1} (scan start {2}, offset {3}), shaderPlatform={4}, resulting position {5}",
                    expectedFriendlyName, start + offset, start, offset, shaderPlatform, Ar.Position);
                return true;
            }
        }

        // Fallback: no expected name (or no exact match found) - generic "plausible short
        // printable-ASCII FString" heuristic, weaker but still bounded by the caller's trailing
        // bCooked validation.
        for (var offset = 0; offset + 8 <= window; offset++)
        {
            var length = BitConverter.ToInt32(bytes, offset + 4);
            if (length is <= 0 or > 260 || offset + 8 + length > window) continue;

            var plausible = true;
            for (var i = 0; i < length - 1; i++)
            {
                var b = bytes[offset + 8 + i];
                if (b is < 0x20 or > 0x7E) { plausible = false; break; }
            }
            if (!plausible || bytes[offset + 8 + length - 1] != 0) continue;

            shaderPlatform = BitConverter.ToInt32(bytes, offset);
            friendlyName = System.Text.Encoding.ASCII.GetString(bytes, offset + 8, length - 1);
            Ar.Position = start + offset + 8 + length;
            return true;
        }

        shaderPlatform = 0;
        friendlyName = "";
        Ar.Position = start;
        return false;
    }
}

/// <summary>FMaterialCompilationOutput (&lt; 4.25).</summary>
public class FMaterialCompilationOutputLegacy
{
    public FUniformExpressionSetLegacy UniformExpressionSet;
    public uint UsedSceneTextures;
    public bool bUsesEyeAdaptation;
    public bool bModifiesMeshPosition;
    public bool bUsesWorldPositionOffset;
    public bool bUsesGlobalDistanceField;
    public bool bUsesPixelDepthOffset;
    public bool bUsesDistanceCullFade;
    public bool bHasRuntimeVirtualTextureOutput;

    public FMaterialCompilationOutputLegacy(FMaterialResourceProxyReader Ar, string friendlyName)
    {
        UniformExpressionSet = new FUniformExpressionSetLegacy(Ar);

        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var tailPos = Ar.Position;
            var peek = Ar.ReadBytes((int) Math.Min(120, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: compilation output tail at {0} ({1}): {2}", tailPos, Ar.LegacyProfile, Convert.ToHexString(peek));
            Ar.Position = tailPos;
        }

        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // FMaterialCompilationOutput::Serialize (UE_4.19\...\MaterialShared.cpp:346) lists nine
            // separate 4-byte bools in source (bRequiresSceneColorCopy, bNeedsSceneTextures,
            // bUsesEyeAdaptation, bModifiesMeshPosition, bUsesWorldPositionOffset, bNeedsGBuffer,
            // bUsesGlobalDistanceField, bUsesPixelDepthOffset, bUsesSceneDepthLookup). The first few
            // read cleanly at a fixed offset, but real cooked data has a per-instance-variable number
            // of trailing bytes after that (observed 3 extra, unaccounted-for bytes before
            // DebugDescription in one sample) - the same kind of conditionally-present field already
            // seen in FMaterialUniformExpressionVectorParameter (see ResyncToNextExpressionAnchor).
            // Rather than guess the exact remaining field count, read the fields that reliably land at
            // a fixed offset, then anchor on DebugDescription's own "Compiling <name>: " prefix (the
            // same technique already used for the PreVirtualTexture profile below) to find the true
            // end of the tail regardless of what trails after bUsesGlobalDistanceField.
            Ar.Position += 2 * 4; // bRequiresSceneColorCopy, bNeedsSceneTextures - not tracked here
            bUsesEyeAdaptation = Ar.ReadBoolean();
            bModifiesMeshPosition = Ar.ReadBoolean();
            bUsesWorldPositionOffset = Ar.ReadBoolean();
            Ar.Position += 4; // bNeedsGBuffer - not tracked here
            bUsesGlobalDistanceField = Ar.ReadBoolean();
            SkipToDebugDescription(Ar, friendlyName);
            return;
        }

        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // Pre-VT branch tail: scene-texture/estimate/flag fields whose exact field map is
            // not public source, so they are skipped rather than misattributed. The size is NOT
            // constant: 62 bytes on most Fortnite Season X materials, but longer on some — e.g.
            // M_FN_Character_MASTER measures 78, exactly one 16-byte GUID more, consistent with
            // its material parameter collection reference. Instead of a fixed skip, anchor on
            // the DebugDescription FString that always follows the tail: int32 length +
            // "Compiling <FriendlyName>: ". The trailing bCooked flag still validates the
            // whole map parse.
            SkipToDebugDescription(Ar, friendlyName);
            return;
        }

        UsedSceneTextures = Ar.Read<uint>();
        // cooked (non-editor) estimates: 3x uint16 + 2x uint8
        Ar.Position += 3 * sizeof(ushort) + 2 * sizeof(byte);
        var packedFlags = Ar.Read<byte>();
        bUsesEyeAdaptation              = ((packedFlags >> 0) & 1) != 0;
        bModifiesMeshPosition           = ((packedFlags >> 1) & 1) != 0;
        bUsesWorldPositionOffset        = ((packedFlags >> 2) & 1) != 0;
        bUsesGlobalDistanceField        = ((packedFlags >> 3) & 1) != 0;
        bUsesPixelDepthOffset           = ((packedFlags >> 4) & 1) != 0;
        bUsesDistanceCullFade           = ((packedFlags >> 5) & 1) != 0;
        bHasRuntimeVirtualTextureOutput = ((packedFlags >> 6) & 1) != 0;
    }

    /// <summary>
    /// Positions the reader on the DebugDescription FString ("Compiling &lt;FriendlyName&gt;: …")
    /// that terminates the compilation-output tail. The match requires a plausible FString
    /// length immediately followed by the exact ASCII prefix, so a false hit inside the
    /// sub-1KB tail is practically impossible — and the map's trailing bCooked flag would
    /// still reject one. Throws when no anchor is found so the profile retry can react.
    /// </summary>
    private static void SkipToDebugDescription(FMaterialResourceProxyReader Ar, string friendlyName)
    {
        var start = Ar.Position;
        var prefix = System.Text.Encoding.ASCII.GetBytes($"Compiling {friendlyName}: ");
        var window = (int) Math.Min(1024 + prefix.Length + 4, Ar.Length - start);
        var bytes = Ar.ReadBytes(window);

        for (var offset = 0; offset + 4 + prefix.Length <= window; offset++)
        {
            // ANSI FString length incl. null terminator; instance shader maps can carry very
            // long DebugDescriptions (static permutation dumps, 10k+ chars), so the only upper
            // bound is that the string must fit in the remaining stream
            var length = BitConverter.ToInt32(bytes, offset);
            if (length <= prefix.Length || start + offset + 4 + length > Ar.Length) continue;

            var matches = true;
            for (var b = 0; b < prefix.Length; b++)
            {
                if (bytes[offset + 4 + b] == prefix[b]) continue;
                matches = false;
                break;
            }
            if (!matches) continue;

            Log.Verbose("LegacyShaderMap: compilation output tail is {0} bytes (DebugDescription anchor)", offset);
            Ar.Position = start + offset; // the caller reads the FString itself
            return;
        }

        throw new InvalidOperationException(
            $"DebugDescription anchor 'Compiling {friendlyName}: ' not found after the uniform expression set");
    }
}

/// <summary>
/// FUniformExpressionSet (&lt; 4.25): real serialized FMaterialUniformExpression trees
/// (polymorphic by registered type name), not preshader bytecode.
/// </summary>
public class FUniformExpressionSetLegacy
{
    public FMaterialUniformExpressionLegacy[] UniformVectorExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformScalarExpressions = [];
    public FMaterialUniformExpressionLegacy[] Uniform2DTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformCubeTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformVolumeTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformVirtualTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformExternalTextureExpressions = [];
    public FMaterialVirtualTextureStackLegacy[] VTStacks = [];
    public FGuid[] ParameterCollections = [];

    public FUniformExpressionSetLegacy(FMaterialResourceProxyReader Ar)
    {
        UniformVectorExpressions = ReadExpressionArray(Ar);
        UniformScalarExpressions = ReadExpressionArray(Ar);
        Uniform2DTextureExpressions = ReadExpressionArray(Ar);
        UniformCubeTextureExpressions = ReadExpressionArray(Ar);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // FUniformExpressionSet::Serialize (UE_4.19\...\MaterialUniformExpressions.cpp:102): no
            // volume-texture array and no reserved 2D-texture-array slot at all (both were added
            // later); ExternalTexture is immediately followed by ParameterCollections, then four
            // PerFrame/PerFramePrev arrays that still exist on disk here even though nothing in this
            // reader's output uses them (removed again by the time of the UE4_23/PreVirtualTexture
            // profiles above).
            UniformExternalTextureExpressions = ReadExpressionArray(Ar);
            ParameterCollections = Ar.ReadArray<FGuid>();
            ReadExpressionArray(Ar); // PerFrameUniformScalarExpressions - unused, still on disk
            ReadExpressionArray(Ar); // PerFrameUniformVectorExpressions - unused, still on disk
            ReadExpressionArray(Ar); // PerFramePrevUniformScalarExpressions - unused, still on disk
            ReadExpressionArray(Ar); // PerFramePrevUniformVectorExpressions - unused, still on disk
            return;
        }
        UniformVolumeTextureExpressions = ReadExpressionArray(Ar);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // pre-VT branch: no virtual texture arrays/stacks, but the reserved 2D array slot
            // IS already present here (FUniformExpressionSet::Serialize, MaterialUniformExpressions.cpp:112-113:
            // "Adding 2D texture array now to prevent bumping version when the feature gets added" -
            // serialized even though nothing populates it at this engine version). Skipping this
            // read previously misaligned ParameterCollections by reading this array's always-zero
            // count as ParameterCollections' own count, silently dropping any real collection GUIDs
            // into the unparsed compilation-output tail (see SkipToDebugDescription).
            UniformExternalTextureExpressions = ReadExpressionArray(Ar);
            ReadExpressionArray(Ar); // Uniform2DTextureArrayExpressions - reserved, always empty pre-VT
            ParameterCollections = Ar.ReadArray<FGuid>();
            return;
        }
        UniformVirtualTextureExpressions = ReadExpressionArray(Ar);
        UniformExternalTextureExpressions = ReadExpressionArray(Ar);
        VTStacks = Ar.ReadArray(() => new FMaterialVirtualTextureStackLegacy(Ar));
        ReadExpressionArray(Ar); // Uniform2DTextureArrayExpressions - reserved, always empty in 4.23
        ParameterCollections = Ar.ReadArray<FGuid>();
    }

    private static FMaterialUniformExpressionLegacy[] ReadExpressionArray(FMaterialResourceProxyReader Ar)
    {
        var num = Ar.Read<int>();
        var result = new FMaterialUniformExpressionLegacy[num];
        for (var i = 0; i < num; i++)
        {
            var elemStart = Ar.Position;
            result[i] = FMaterialUniformExpressionLegacy.Read(Ar);
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
                Log.Verbose("LegacyShaderMap: array[{0}] TypeName={1} ParamName={2} at {3}, ended at {4}", i, result[i].TypeName, result[i].ParameterName, elemStart, Ar.Position);
            if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19 && i < num - 1)
                ResyncToNextExpressionAnchor(Ar);
        }
        return result;
    }

    /// <summary>
    /// UE4_19 was reverse-engineered purely from byte-offset probing against real cooked data (no
    /// public source for this exact struct layout at this engine point) and turned out to have
    /// per-instance-variable trailing bytes after at least FMaterialUniformExpressionVectorParameter
    /// (observed 0 extra bytes for one instance, 5 for another, both otherwise-identical
    /// "SelectionColor" parameters) - likely a conditionally-present field this reader does not
    /// model. Rather than guess further, resync the same way SkipToDebugDescription/
    /// TryScanForShaderPlatformAndFriendlyName already do elsewhere in this file: if the current
    /// position isn't already a valid, registered FMaterialUniformExpressionType name, scan forward
    /// a small bounded window for the next one and land there. A no-op when already aligned.
    /// </summary>
    internal static void ResyncToNextExpressionAnchor(FMaterialResourceProxyReader Ar)
    {
        var nameMap = Ar.DebugGlobalNameMap;
        if (nameMap == null) return;

        bool LooksLikeExpressionTypeAt(long pos)
        {
            if (pos + 8 > Ar.Length) return false;
            var saved = Ar.Position;
            Ar.Position = pos;
            var bytes = Ar.ReadBytes(8);
            Ar.Position = saved;
            var idx = BitConverter.ToInt32(bytes, 0);
            var number = BitConverter.ToInt32(bytes, 4);
            if (number != 0 || idx < 0 || idx >= nameMap.Length) return false;
            return nameMap[idx].Name?.StartsWith("FMaterialUniformExpression", StringComparison.Ordinal) == true;
        }

        var start = Ar.Position;
        if (LooksLikeExpressionTypeAt(start)) return;

        for (var offset = 1; offset <= 64; offset++)
        {
            if (!LooksLikeExpressionTypeAt(start + offset)) continue;
            Log.Verbose("LegacyShaderMap: resynced expression array by {0} bytes at {1}", offset, start);
            Ar.Position = start + offset;
            return;
        }
        // No anchor found within the window - leave position as-is; the trailing bCooked
        // validation will reject the whole map parse if this was a real misalignment.
    }
}

/// <summary>FMaterialVirtualTextureStack (&lt; 4.25 layout: layer count + indices + preallocated index).</summary>
public class FMaterialVirtualTextureStackLegacy
{
    public uint NumLayers;
    public int[] LayerUniformExpressionIndices = [];
    public int PreallocatedStackTextureIndex;

    public FMaterialVirtualTextureStackLegacy(FArchive Ar)
    {
        NumLayers = Ar.Read<uint>();
        LayerUniformExpressionIndices = Ar.ReadArray<int>((int) NumLayers);
        PreallocatedStackTextureIndex = Ar.Read<int>();
    }
}

/// <summary>
/// A deserialized FMaterialUniformExpression tree node. TypeName is the engine class name
/// (e.g. FMaterialUniformExpressionScalarParameter); Operands are the nested expressions.
/// </summary>
public class FMaterialUniformExpressionLegacy
{
    public string TypeName;
    /// <summary>Nested sub-expressions with their slot names ("X", "A", "B", "Input", "Min", "Max", "Texture").</summary>
    public List<KeyValuePair<string, FMaterialUniformExpressionLegacy>> Operands = [];
    /// <summary>Scalar display values (op names, indices, defaults...) keyed by field name.</summary>
    public List<KeyValuePair<string, string>> Values = [];

    // Typed fields used by graph building (populated per type where applicable)
    public string? ParameterName;
    public int ParameterAssociation = -1;
    public int ParameterIndex;
    public FLinearColor? ConstantValue;
    public float? ScalarDefault;
    public FLinearColor? VectorDefault;
    public int TextureIndex = -1;
    public int TextureLayerIndex = -1;
    public int SamplerSource = -1;
    public bool bVirtualTexture;
    public string? OpName;

    private static readonly string[] FoldedMathOps = ["Add", "Sub", "Mul", "Div", "Dot", "Cross"];
    private static readonly string[] TrigMathOps = ["Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2"];

    public static FMaterialUniformExpressionLegacy Read(FMaterialResourceProxyReader Ar)
    {
        var typeNamePos = Ar.Position;
        var typeName = Ar.ReadFName().Text;
        var expr = new FMaterialUniformExpressionLegacy { TypeName = typeName };
        try
        {
            expr.Deserialize(Ar);
        }
        catch (NotSupportedException) when (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var resume = Ar.Position;
            Ar.Position = Math.Max(0, typeNamePos - 96);
            var context = Ar.ReadBytes((int) Math.Min(160, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: unknown expression type '{0}' at {1}, bytes from {2}: {3}",
                typeName, typeNamePos, Math.Max(0, typeNamePos - 96), Convert.ToHexString(context));
            Ar.Position = resume;
            throw;
        }
        return expr;
    }

    private void ReadParameterInfo(FMaterialResourceProxyReader Ar)
    {
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // This Fortnite branch's FMaterialParameterInfo predates the vanilla-4.19-onward layout
            // (FName Name(8B) + TEnumAsByte Association(1B) + int32 Index(4B), 13B total) - confirmed
            // by cross-checking against the 10.40 reference for this exact asset/parameter set (every
            // scalar/vector parameter there has ParameterAssociation=2/GlobalParameter, ParameterIndex=-1,
            // i.e. this era's materials never actually use per-layer indexing at all) and by finding
            // each parameter's OWN real default value (an exact bit-for-bit float match, e.g.
            // Speed_1=1.368582, DistanceFunction=80000) sitting exactly 8 bytes after the parameter's
            // own FName, not 13. The real layout here is just a plain FName (8B: name index + number,
            // e.g. "Speed_2" is stored as base name "Speed" with number=3 per FName's own "stored as
            // 1 more than actual" convention) - no separate Association or Index field at all; both
            // are hardcoded here to match what every real instance in this build actually has. Reading
            // the vanilla 13-byte layout was overshooting by 5 bytes into the copy of the very next
            // array element, which is what made every other element in a scalar/vector expression
            // array (and any leaf parameter nested as a compound expression's operand) silently vanish -
            // ReadExpressionArray's own resync recovered by skipping an entire extra element to find
            // the next valid type name, rather than 5 bytes to find the real very-next one.
            ParameterName = Ar.ReadFName().Text;
            ParameterAssociation = 2; // EMaterialParameterAssociation::GlobalParameter
            ParameterIndex = -1;
            Values.Add(new("Parameter", ParameterName));
            return;
        }

        // FMaterialParameterInfo: FName Name, TEnumAsByte Association, int32 Index
        ParameterName = Ar.ReadFName().Text;
        ParameterAssociation = Ar.Read<byte>();
        ParameterIndex = Ar.Read<int>();
        Values.Add(new("Parameter", ParameterName));
    }

    private void ReadTextureBase(FMaterialResourceProxyReader Ar)
    {
        if (Ar.LegacyProfile is ELegacyShaderMapProfile.PreVirtualTexture or ELegacyShaderMapProfile.UE4_19)
        {
            // pre-VT FMaterialUniformExpressionTexture::Serialize: int32 TextureIndex, int32 SamplerSource
            // (confirmed identical at UE_4.19\...\MaterialShared.h:242-277 - TextureIndex + SamplerSource only)
            TextureIndex = Ar.Read<int>();
            SamplerSource = Ar.Read<int>();
            Values.Add(new("Texture Index", TextureIndex.ToString()));
            return;
        }

        // FMaterialUniformExpressionTexture::Serialize: int32 TextureIndex, int32 LayerIndex, int32 SamplerSource, bool bVirtualTexture
        TextureIndex = Ar.Read<int>();
        TextureLayerIndex = Ar.Read<int>();
        SamplerSource = Ar.Read<int>();
        bVirtualTexture = Ar.ReadBoolean();
        Values.Add(new("Texture Index", TextureIndex.ToString()));
        if (bVirtualTexture) Values.Add(new("Virtual Texture", "true"));
    }

    private void ReadChild(FMaterialResourceProxyReader Ar, string slot)
        => Operands.Add(new(slot, Read(Ar)));

    /// <summary>
    /// Same per-instance trailing-bytes quirk ReadExpressionArray resyncs for between top-level array
    /// elements (see ResyncToNextExpressionAnchor's own summary) - a leaf ScalarParameter/
    /// VectorParameter operand nested inside a compound expression (FoldedMath, TrigMath, Min/Max,
    /// AppendVector, Fmod, Clamp) can carry the same conditionally-present extra bytes, and with
    /// nothing resyncing between sibling operand reads specifically, that drift accumulated silently
    /// until it desynced a top-level array boundary several elements downstream, well beyond
    /// ResyncToNextExpressionAnchor's own bounded scan window there. Only safe to call between two
    /// operand reads (where the next bytes are always another expression's own type name) - never
    /// after the last operand in a case, where non-operand fields (an Op byte, a swizzle, ValueType)
    /// legitimately follow instead.
    /// </summary>
    private static void ResyncBetweenOperands(FMaterialResourceProxyReader Ar)
    {
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
            FUniformExpressionSetLegacy.ResyncToNextExpressionAnchor(Ar);
    }

    private void Deserialize(FMaterialResourceProxyReader Ar)
    {
        switch (TypeName)
        {
            case "FMaterialUniformExpressionConstant":
            {
                ConstantValue = Ar.Read<FLinearColor>();
                var valueType = Ar.Read<byte>();
                Values.Add(new("Value", ConstantValue.Value.ToString()));
                Values.Add(new("Value Type", valueType.ToString()));
                break;
            }
            // FMaterialUniformExpressionTime/RealTime::Serialize (MaterialUniformExpressions.h:62-116)
            // are both true no-ops - zero fields, no operands, just a per-frame runtime value.
            case "FMaterialUniformExpressionTime":
                OpName = "Time";
                break;
            case "FMaterialUniformExpressionRealTime":
                OpName = "RealTime";
                break;
            case "FMaterialUniformExpressionVectorParameter":
                ReadParameterInfo(Ar);
                VectorDefault = Ar.Read<FLinearColor>();
                Values.Add(new("Default", VectorDefault.Value.ToString()));
                break;
            case "FMaterialUniformExpressionScalarParameter":
                ReadParameterInfo(Ar);
                ScalarDefault = Ar.Read<float>();
                Values.Add(new("Default", ScalarDefault.Value.ToString()));
                break;
            case "FMaterialUniformExpressionTexture":
            case "FMaterialUniformExpressionFlipBookTextureParameter": // no extra fields over the texture base
                ReadTextureBase(Ar);
                break;
            case "FMaterialUniformExpressionTextureParameter":
                ReadParameterInfo(Ar);
                ReadTextureBase(Ar);
                break;
            case "FMaterialUniformExpressionExternalTextureBase":
            case "FMaterialUniformExpressionExternalTexture":
            {
                TextureIndex = Ar.Read<int>(); // SourceTextureIndex
                var guid = Ar.Read<FGuid>();
                Values.Add(new("Source Texture Index", TextureIndex.ToString()));
                Values.Add(new("External Texture Guid", guid.ToString()));
                break;
            }
            case "FMaterialUniformExpressionExternalTextureParameter":
            {
                ParameterName = Ar.ReadFName().Text;
                Values.Add(new("Parameter", ParameterName));
                goto case "FMaterialUniformExpressionExternalTextureBase";
            }
            case "FMaterialUniformExpressionExternalTextureCoordinateScaleRotation":
            case "FMaterialUniformExpressionExternalTextureCoordinateOffset":
            {
                // TOptional<FName> parameter name: bool (uint32) + FName when set
                if (Ar.ReadBoolean())
                {
                    ParameterName = Ar.ReadFName().Text;
                    Values.Add(new("Parameter", ParameterName));
                }
                goto case "FMaterialUniformExpressionExternalTextureBase";
            }
            case "FMaterialUniformExpressionRuntimeVirtualTextureParameter":
            {
                TextureIndex = Ar.Read<int>();
                var paramIndex = Ar.Read<int>();
                Values.Add(new("Texture Index", TextureIndex.ToString()));
                Values.Add(new("Param Index", paramIndex.ToString()));
                break;
            }
            case "FMaterialUniformExpressionSine":
            {
                ReadChild(Ar, "X");
                var bIsCosine = Ar.ReadBoolean();
                OpName = bIsCosine ? "Cos" : "Sin";
                Values.Add(new("Op", OpName));
                break;
            }
            case "FMaterialUniformExpressionTrigMath":
            {
                ReadChild(Ar, "X");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "Y");
                var op = Ar.Read<byte>();
                OpName = op < TrigMathOps.Length ? TrigMathOps[op] : $"Trig{op}";
                Values.Add(new("Op", OpName));
                break;
            }
            case "FMaterialUniformExpressionSquareRoot":
                OpName = "Sqrt";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionLength":
            {
                OpName = "Length";
                ReadChild(Ar, "X");
                if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.TypeHandlingForMaterialSqrtNodes)
                    Ar.Position += 4; // uint32 ValueType
                break;
            }
            case "FMaterialUniformExpressionLogarithm2":
                OpName = "Log2";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionLogarithm10":
                OpName = "Log10";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFoldedMath":
            {
                ReadChild(Ar, "A");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "B");
                var op = Ar.Read<byte>();
                OpName = op < FoldedMathOps.Length ? FoldedMathOps[op] : $"Math{op}";
                Values.Add(new("Op", OpName));
                if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.TypeHandlingForMaterialSqrtNodes)
                    Ar.Position += 4; // uint32 ValueType
                break;
            }
            case "FMaterialUniformExpressionPeriodic":
                OpName = "Frac"; // periodic wraps its input into [0,1)
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionAppendVector":
            {
                OpName = "Append";
                ReadChild(Ar, "A");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "B");
                var numComponentsA = Ar.Read<uint>();
                Values.Add(new("NumComponentsA", numComponentsA.ToString()));
                break;
            }
            case "FMaterialUniformExpressionMin":
                OpName = "Min";
                ReadChild(Ar, "A");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionMax":
                OpName = "Max";
                ReadChild(Ar, "A");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionClamp":
                OpName = "Clamp";
                ReadChild(Ar, "Input");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "Min");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "Max");
                break;
            case "FMaterialUniformExpressionSaturate":
                OpName = "Saturate";
                ReadChild(Ar, "Input");
                break;
            case "FMaterialUniformExpressionComponentSwizzle":
            {
                OpName = "Swizzle";
                ReadChild(Ar, "X");
                var r = Ar.Read<sbyte>();
                var g = Ar.Read<sbyte>();
                var b = Ar.Read<sbyte>();
                var a = Ar.Read<sbyte>();
                Ar.Position += 1; // int8 NumElements (derived from the indices)
                Values.Add(new("Swizzle", FormatSwizzle(r, g, b, a)));
                break;
            }
            case "FMaterialUniformExpressionFloor":
                OpName = "Floor";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionCeil":
                OpName = "Ceil";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionRound":
                OpName = "Round";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionTruncate":
                OpName = "Truncate";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionSign":
                OpName = "Sign";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFrac":
                OpName = "Frac";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFmod":
                OpName = "Fmod";
                ReadChild(Ar, "A");
                ResyncBetweenOperands(Ar);
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionAbs":
                OpName = "Abs";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionTextureProperty":
            {
                OpName = "TextureProperty";
                ReadChild(Ar, "Texture");
                var property = Ar.Read<sbyte>(); // TMTM_TextureSize=0 / TMTM_TexelSize=1
                Values.Add(new("Property", property == 0 ? "TextureSize" : property == 1 ? "TexelSize" : property.ToString()));
                break;
            }
            default:
                // Unknown expression type: the stream cannot be advanced safely past it.
                throw new NotSupportedException($"Unknown FMaterialUniformExpression type '{TypeName}' in legacy shader map");
        }
    }

    private static string FormatSwizzle(sbyte r, sbyte g, sbyte b, sbyte a)
    {
        const string comps = "rgba";
        var result = "";
        Span<sbyte> idx = [r, g, b, a];
        foreach (var i in idx)
        {
            if (i is >= 0 and < 4) result += comps[i];
        }
        return result;
    }

    public override string ToString() => OpName ?? ParameterName ?? TypeName;
}

/// <summary>FShaderParameterMapInfo serialized with plain FArchive semantics (&lt; 4.25).</summary>
public class FShaderParameterMapInfoLegacy
{
    public FShaderParameterInfoLegacy[] UniformBuffers = [];
    public FShaderParameterInfoLegacy[] TextureSamplers = [];
    public FShaderParameterInfoLegacy[] SRVs = [];
    public FShaderLooseParameterBufferInfoLegacy[] LooseParameterBuffers = [];

    public FShaderParameterMapInfoLegacy(FArchive Ar)
    {
        UniformBuffers = Ar.ReadArray<FShaderParameterInfoLegacy>();
        TextureSamplers = Ar.ReadArray<FShaderParameterInfoLegacy>();
        SRVs = Ar.ReadArray<FShaderParameterInfoLegacy>();
        LooseParameterBuffers = Ar.ReadArray(() => new FShaderLooseParameterBufferInfoLegacy(Ar));
    }
}

public struct FShaderParameterInfoLegacy
{
    public ushort BaseIndex;
    public ushort Size;
}

public class FShaderLooseParameterBufferInfoLegacy
{
    public ushort BufferIndex;
    public ushort BufferSize;
    public FShaderParameterInfoLegacy[] Parameters;

    public FShaderLooseParameterBufferInfoLegacy(FArchive Ar)
    {
        BufferIndex = Ar.Read<ushort>();
        BufferSize = Ar.Read<ushort>();
        Parameters = Ar.ReadArray<FShaderParameterInfoLegacy>();
    }
}

/// <summary>FShaderUniformBufferParameter: uint16 BaseIndex + serialized bool bIsBound.</summary>
public struct FShaderUniformBufferParameterLegacy
{
    public ushort BaseIndex;
    public bool bIsBound;

    public FShaderUniformBufferParameterLegacy(FArchive Ar)
    {
        BaseIndex = Ar.Read<ushort>();
        bIsBound = Ar.ReadBoolean();
    }
}

/// <summary>FShaderResourceParameter (ShaderParameters.cpp): uint16 BaseIndex + uint16 NumResources.</summary>
public struct FShaderResourceParameterLegacy
{
    public ushort BaseIndex;
    public ushort NumResources;

    public FShaderResourceParameterLegacy(FArchive Ar)
    {
        BaseIndex = Ar.Read<ushort>();
        NumResources = Ar.Read<ushort>();
    }
}

/// <summary>FShaderParameter (ShaderParameters.cpp): uint16 BaseIndex + uint16 NumBytes + uint16 BufferIndex.</summary>
public struct FShaderParameterLegacy
{
    public ushort BaseIndex;
    public ushort NumBytes;
    public ushort BufferIndex;

    public FShaderParameterLegacy(FArchive Ar)
    {
        BaseIndex = Ar.Read<ushort>();
        NumBytes = Ar.Read<ushort>();
        BufferIndex = Ar.Read<ushort>();
    }
}

/// <summary>
/// The FMaterialShader/FMeshMaterialShader parameter block parsed out of the type-specific
/// (virtual Serialize) part of a base pass pixel shader. MaterialUniformBuffer.BaseIndex is
/// the material constant buffer slot.
/// </summary>
public class FMaterialShaderParametersLegacy
{
    public FShaderUniformBufferParameterLegacy SceneTexturesUniformBuffer;
    public FShaderUniformBufferParameterLegacy MobileSceneTexturesUniformBuffer;
    public FShaderUniformBufferParameterLegacy MaterialUniformBuffer;
    public FShaderUniformBufferParameterLegacy[] ParameterCollectionUniformBuffers = [];
    // FDebugUniformExpressionSet: expression counts recorded at cook time
    public int NumVectorExpressions;
    public int NumScalarExpressions;
    public int Num2DTextureExpressions;
    public int NumCubeTextureExpressions;
    public int NumVolumeTextureExpressions;
    public int NumVirtualTextureExpressions;
    public string DebugDescription = "";
    // FMeshMaterialShader
    public FShaderUniformBufferParameterLegacy PassUniformBuffer;
    public string VertexFactoryTypeName = "";
    // TBasePassPixelShaderPolicyParamType
    public FShaderUniformBufferParameterLegacy[] LightMapPolicyParameters = [];
    public FShaderUniformBufferParameterLegacy ReflectionCaptureBuffer;
}

/// <summary>
/// A single legacy FShader. The type-specific parameter block is decoded only for known layouts
/// (base pass pixel shaders). For every other type the FShader::SerializeBase tail — hashes,
/// target, uniform buffer parameter names and the FShaderResource with the compiled bytecode —
/// is recovered by a self-validating scan (see ReadUnknownTypeFromTail); only when that fails
/// is the shader skipped via the serialized end offset, like the engine skips unknown types.
/// </summary>
public class FShaderLegacy
{
    public string TypeName;
    /// <summary>Only set for parseable types (base pass pixel shaders).</summary>
    public FMaterialShaderParametersLegacy? MaterialParameters;
    public FSHAHash OutputHash;
    public FSHAHash MaterialShaderMapHash;
    public string ShaderPipelineName = "";
    public string VertexFactoryTypeName = "";
    public FShaderTargetLegacy Target;
    public int PermutationId;
    /// <summary>Uniform buffer struct names in slot order (name, BaseIndex) as serialized in the shader tail.</summary>
    public (string Name, FShaderUniformBufferParameterLegacy Parameter)[] UniformBufferParameters = [];
    public FShaderResourceLegacy? Resource;

    /// <summary>
    /// Light map policy name -> number of FShaderUniformBufferParameter entries its PixelParametersType serializes.
    /// From LightMapRendering.h: FUniformLightMapPolicyShaderParametersType = 3 (PrecomputedLightingBuffer,
    /// IndirectLightingCache, LightmapResourceCluster); FSelfShadowedTranslucencyPolicy = 1 (TranslucentSelfShadow);
    /// self-shadowed indirect policies = 3 + 1.
    /// </summary>
    private static readonly Dictionary<string, int> BasePassPixelPolicyParamCounts = new()
    {
        ["FNoLightMapPolicy"] = 3,
        ["FPrecomputedVolumetricLightmapLightingPolicy"] = 3,
        ["FCachedVolumeIndirectLightingPolicy"] = 3,
        ["FCachedPointIndirectLightingPolicy"] = 3,
        ["FSimpleNoLightmapLightingPolicy"] = 3,
        ["FSimpleLightmapOnlyLightingPolicy"] = 3,
        ["FSimpleDirectionalLightLightingPolicy"] = 3,
        ["FSimpleStationaryLightPrecomputedShadowsLightingPolicy"] = 3,
        ["FSimpleStationaryLightSingleSampleShadowsLightingPolicy"] = 3,
        ["FSimpleStationaryLightVolumetricLightmapShadowsLightingPolicy"] = 3,
        ["TLightMapPolicyLQ"] = 3,
        ["TLightMapPolicyHQ"] = 3,
        ["TDistanceFieldShadowsAndLightMapPolicyHQ"] = 3,
        ["FSelfShadowedTranslucencyPolicy"] = 1,
        ["FSelfShadowedCachedPointIndirectLightingPolicy"] = 4,
        ["FSelfShadowedVolumetricLightmapPolicy"] = 4,
    };

    /// <summary>
    /// Policy parameter counts at UE4_19: FUniformLightMapPolicyShaderParametersType::Serialize
    /// (LightMapRendering.h:802-805) is just "Ar &lt;&lt; BufferParameter;" - a single
    /// FShaderUniformBufferParameter - at this engine version, not the 3-field shape the 4.23-era
    /// dictionary below models (that shape was added later). TUniformLightMapPolicy&lt;Policy&gt;
    /// (for every LMP_* enum value) shares this one PixelParametersType, so every key below that
    /// resolves to a plain LMP_* policy becomes 1. The three FSelfShadowed* entries use a different,
    /// not-yet-independently-confirmed-for-4.19 PixelParametersType and are left unmapped here (falls
    /// back to null / unknown-type recovery) rather than guessing.
    /// </summary>
    private static readonly Dictionary<string, int> BasePassPixelPolicyParamCountsUE4_19 = new()
    {
        ["FNoLightMapPolicy"] = 1,
        ["FPrecomputedVolumetricLightmapLightingPolicy"] = 1,
        ["FCachedVolumeIndirectLightingPolicy"] = 1,
        ["FCachedPointIndirectLightingPolicy"] = 1,
        ["FSimpleNoLightmapLightingPolicy"] = 1,
        ["FSimpleLightmapOnlyLightingPolicy"] = 1,
        ["FSimpleDirectionalLightLightingPolicy"] = 1,
        ["FSimpleStationaryLightPrecomputedShadowsLightingPolicy"] = 1,
        ["FSimpleStationaryLightSingleSampleShadowsLightingPolicy"] = 1,
        ["FSimpleStationaryLightVolumetricLightmapShadowsLightingPolicy"] = 1,
        ["TLightMapPolicyLQ"] = 1,
        ["TLightMapPolicyHQ"] = 1,
        ["TDistanceFieldShadowsAndLightMapPolicyHQ"] = 1,
    };

    /// <summary>Returns the policy parameter count if the type is a parseable base pass pixel shader.</summary>
    private static int? GetBasePassPixelPolicyParamCount(string typeName, bool ue4_19 = false)
    {
        if (!typeName.StartsWith("TBasePassPS", StringComparison.Ordinal)) return null;
        var policy = typeName["TBasePassPS".Length..];
        if (policy.EndsWith("Skylight", StringComparison.Ordinal)) policy = policy[..^"Skylight".Length];
        if (ue4_19 && BasePassPixelPolicyParamCountsUE4_19.TryGetValue(policy, out var count19)) return count19;
        return BasePassPixelPolicyParamCounts.TryGetValue(policy, out var count) ? count : null;
    }

    /// <summary>
    /// Returns the policy parameter count for a parseable base pass VERTEX shader at UE4_19. Only the
    /// UE4_19 path is modeled (the 4.23-era vertex-side shape hasn't come up yet in this session's
    /// assets). TBasePassVertexShaderPolicyParamType&lt;VertexParametersType&gt; is templated on the same
    /// LightMapPolicyType::VertexParametersType (BasePassRendering.h:322-323); for FUniformLightMapPolicy
    /// (every LMP_* enum value covered by BasePassPixelPolicyParamCountsUE4_19)
    /// VertexParametersType == PixelParametersType == FUniformLightMapPolicyShaderParametersType
    /// (LightMapRendering.h:816-817), so the exact same param counts apply on the vertex side.
    /// </summary>
    private static int? GetBasePassVertexPolicyParamCount(string typeName, bool ue4_19 = false)
    {
        if (!ue4_19 || !typeName.StartsWith("TBasePassVS", StringComparison.Ordinal)) return null;
        var policy = typeName["TBasePassVS".Length..];
        if (policy.EndsWith("AtmosphericFog", StringComparison.Ordinal)) policy = policy[..^"AtmosphericFog".Length];
        return BasePassPixelPolicyParamCountsUE4_19.TryGetValue(policy, out var count19) ? count19 : null;
    }

    /// <summary>Reads one shader from TShaderMap::SerializeInline (type name + end-offset framed blob).</summary>
    public static FShaderLegacy? Read(FMaterialResourceProxyReader Ar)
    {
        var typeNamePosition = Ar.Position;
        var typeName = Ar.ReadFName().Text;

        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // UE_4.19's TShaderMap<FMaterialShaderType>::SerializeInline DOES write 4 bytes between
            // the type name and the shader's own fields - it was missed on first pass because the
            // wrapper at Shader.h:1841-1846 only shows "Ar << Type; Shader = SerializeShaderForLoad(...)";
            // the field is written one level down, inside SerializeShaderForLoad/ForSaving
            // (Shader.h:1713-1772): "int32 SkipOffset = Ar.Tell(); Ar << SkipOffset; ...
            // CurrentShader->SerializeBase(Ar, ...); int32 EndOffset = Ar.Tell(); Ar.Seek(SkipOffset);
            // Ar << EndOffset;" - the same "placeholder overwritten with Ar.Tell() at save time" pattern
            // as FVertexFactoryParameterRef's own skip offset. Missing this 4-byte field was why every
            // UE4_19 shader's own fields (e.g. FMaterialShader::Serialize's MaterialUniformBuffer, the
            // very first thing read) were being read 4 bytes early, landing on this offset's own bytes
            // instead - garbage BaseIndex values, or an outright "Invalid bool value" throw when the
            // misread bits didn't happen to decode as 0/1.
            //
            // The raw value read here does NOT reproduce a usable absolute or OffsetToFirstResource-
            // relative position for this cook (tried both; neither lands anywhere near the real end,
            // independently confirmed via the anchor-scan below finding the true end thousands of bytes
            // earlier) - unlike the shader-map-level FVertexFactoryParameterRef offset, whatever
            // coordinate space this was captured in at cook time does not survive into the final asset
            // in a form this reader can reconstruct. So the 4 bytes are consumed (fixing the alignment
            // bug) but not trusted for skipping; recovery for shader types this reader can't parse
            // directly still goes through the anchor scan below, exactly as before this fix.
            var unusedEndOffset19 = Ar.Read<int>();
            var policyParamCount19 = GetBasePassPixelPolicyParamCount(typeName, ue4_19: true);
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
                Log.Verbose("LegacyShaderMap: shader typeName='{0}' at {1}, policyParamCount={2}", typeName, typeNamePosition, policyParamCount19);
            if (policyParamCount19 != null)
            {
                var knownShader = new FShaderLegacy { TypeName = typeName };
                knownShader.Deserialize(Ar, policyParamCount19.Value);
                return knownShader;
            }

            var vertexPolicyParamCount19 = GetBasePassVertexPolicyParamCount(typeName, ue4_19: true);
            var vsFrontStart = Ar.Position;
            if (vertexPolicyParamCount19 != null)
            {
                try
                {
                    var knownVertexShader = new FShaderLegacy { TypeName = typeName };
                    knownVertexShader.DeserializeTBasePassVS_UE4_19(Ar, vertexPolicyParamCount19.Value);
                    return knownVertexShader;
                }
                catch (Exception exVs)
                {
                    Log.Verbose("LegacyShaderMap: TBasePassVS front-matter parse failed for '{0}': {1}", typeName, exVs.Message);
                    Ar.Position = vsFrontStart;
                }
            }

            // Not a TBasePassPS*/TBasePassVS* type, but not necessarily "plain FMaterialShader" either - most
            // other non-base-pass material shaders (TTranslucencyShadowDepthVS/PS, TLightMapDensityVS/PS,
            // FVelocityVS/PS, FHitProxyVS/PS, TDepthOnlyVS, FDebugViewModeVS, FConvertToUniformMeshVS/GS,
            // ...) still render actual mesh geometry, so they're FMeshMaterialShader-derived (need a
            // FVertexFactoryParameterRef, MeshMaterialShader.h:77-78) rather than extending FMaterialShader
            // directly - only truly "global" material shaders (e.g. TTranslucentLightingInjectPS) have no
            // per-type Serialize override at all. Try the cheaper plain-FMaterialShader shape first (see
            // DeserializeMaterialShaderFront_UE4_19), then the FMeshMaterialShader-extended shape (see
            // DeserializeMeshMaterialShaderFront_UE4_19), and only fall back to the anchor-scan recovery
            // if neither validates (e.g. a genuinely different/compute-based unknown type).
            var frontStart = Ar.Position;
            try
            {
                var plainShader = new FShaderLegacy { TypeName = typeName };
                plainShader.MaterialParameters = plainShader.DeserializeMaterialShaderFront_UE4_19(Ar);
                plainShader.DeserializeBaseTail(Ar);
                if (plainShader.Target.Frequency < 10 && string.Equals(plainShader.TypeName, typeName, StringComparison.Ordinal))
                    return plainShader;
            }
            catch (Exception exPlain)
            {
                Log.Verbose("LegacyShaderMap: plain FMaterialShader front-matter parse failed for '{0}': {1}", typeName, exPlain.Message);
            }

            Ar.Position = frontStart;
            try
            {
                var meshShader = new FShaderLegacy { TypeName = typeName };
                meshShader.DeserializeMeshMaterialShaderFront_UE4_19(Ar, typeName);
                return meshShader;
            }
            catch (Exception exMesh)
            {
                Log.Verbose("LegacyShaderMap: mesh FMeshMaterialShader front-matter parse failed for '{0}': {1}", typeName, exMesh.Message);
            }

            Ar.Position = typeNamePosition;
            var typeNameBytes19 = Ar.ReadBytes(8);
            Ar.Position += 4; // the same now-consumed-but-untrusted end-offset field, skipped again for the scan
            return ReadUnknownTypeUnbounded(Ar, typeName, typeNameBytes19);
        }

        var endOffset = Ar.Read<long>(); // relative to OffsetToFirstResource
        var endPosition = Ar.OffsetToFirstResource + endOffset;

        var policyParamCount = GetBasePassPixelPolicyParamCount(typeName);
        if (policyParamCount == null)
        {
            // Unknown type-specific parameter layout: the front of the frame cannot be parsed,
            // but the FShader::SerializeBase tail (hashes, target, uniform buffer names and the
            // FShaderResource with the bytecode) can still be recovered by anchoring on the
            // shader's own type FName — see ReadUnknownTypeFromTail. Failure skips the shader,
            // exactly like the engine skips shader types it does not know.
            var frameStart = Ar.Position;
            Ar.Position = typeNamePosition;
            var typeNameBytes = Ar.ReadBytes(8); // FName on disk: int32 name index + int32 number
            Ar.Position = frameStart;
            var recovered = ReadUnknownTypeFromTail(Ar, typeName, typeNameBytes, frameStart, endPosition);
            Ar.Position = endPosition;
            return recovered;
        }

        var startPosition = Ar.Position;
        try
        {
            var shader = new FShaderLegacy { TypeName = typeName };
            shader.Deserialize(Ar, policyParamCount.Value);
            if (Ar.Position != endPosition)
                throw new InvalidOperationException($"Legacy shader '{typeName}' parsed {Ar.Position - startPosition} bytes, expected {endPosition - startPosition}");
            return shader;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to parse legacy shader '{0}', skipping", typeName);
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            {
                var failPos = Ar.Position;
                Ar.Position = startPosition;
                var context = Ar.ReadBytes((int) Math.Min(Math.Min(480, endPosition - startPosition), Ar.Length - Ar.Position));
                Log.Verbose("LegacyShaderMap: shader '{0}' frame {1}..{2} (failed at {3}): {4}",
                    typeName, startPosition, endPosition, failPos, Convert.ToHexString(context));
            }
            Ar.Position = endPosition;
            return null;
        }
    }

    /// <summary>
    /// UE4_19 equivalent of ReadUnknownTypeFromTail for a format with no per-shader end offset at
    /// all (see the profile check in Read above) - scans forward for the shader's own type name
    /// reappearing 76 bytes before a tail that deserializes cleanly, bounded by a generous window
    /// (there is no authoritative end position to bound the scan or to detect ambiguity against, so
    /// the first candidate whose tail fully validates is accepted).
    /// </summary>
    private static FShaderLegacy? ReadUnknownTypeUnbounded(FMaterialResourceProxyReader Ar, string typeName, byte[] typeNameBytes)
    {
        const int tailBytesBeforeTypeName = 76;
        const int maxScanWindow = 131072;

        var scanStart = Ar.Position;
        var window = Ar.ReadBytes((int) Math.Min(maxScanWindow, Ar.Length - scanStart));
        Ar.Position = scanStart;

        var candidatesTried = 0;
        for (var offset = tailBytesBeforeTypeName; offset + 8 <= window.Length; offset++)
        {
            if (window[offset] != typeNameBytes[0]) continue;
            var matches = true;
            for (var b = 1; b < 8; b++)
            {
                if (window[offset + b] == typeNameBytes[b]) continue;
                matches = false;
                break;
            }
            if (!matches) continue;

            candidatesTried++;
            try
            {
                Ar.Position = scanStart + offset - tailBytesBeforeTypeName;
                var candidate = new FShaderLegacy { TypeName = typeName };
                candidate.DeserializeBaseTail(Ar);
                if (candidate.Target.Frequency >= 10) continue; // SF_NumFrequencies (RHIDefinitions.h)
                if (!string.Equals(candidate.TypeName, typeName, StringComparison.Ordinal)) continue;
                Log.Verbose("LegacyShaderMap: ReadUnknownTypeUnbounded '{0}' found at candidate offset {1} (of {2} tried), pos now {3}",
                    typeName, offset, candidatesTried, Ar.Position);
                return candidate;
            }
            catch (Exception ex)
            {
                Log.Verbose("LegacyShaderMap: ReadUnknownTypeUnbounded '{0}' candidate at offset {1} failed: {2}\n{3}", typeName, offset, ex.Message, ex.StackTrace);
            }
        }

        Log.Verbose("LegacyShaderMap: ReadUnknownTypeUnbounded '{0}' exhausted window ({1} candidates tried, window {2} bytes)", typeName, candidatesTried, window.Length);
        Ar.Position = scanStart;
        return null;
    }

    /// <summary>
    /// Recovers a shader whose type-specific parameter layout is unknown by locating the
    /// FShader::SerializeBase tail inside the end-offset frame. The tail contains the shader's
    /// own type FName — byte-identical to the FName the frame started with — at a fixed 76-byte
    /// distance past the tail start (OutputHash 20 + MaterialShaderMapHash 20 + ShaderPipelineName
    /// FName 8 + VertexFactoryTypeName FName 8 + VFSourceHash 20). The frame is scanned for those
    /// 8 bytes and a candidate is accepted only when the entire remainder (SerializeBase tail +
    /// inline FShaderResource + parameter bindings) deserializes cleanly and lands exactly on the
    /// serialized end offset. A misplaced anchor cannot survive that validation; ambiguity
    /// (more than one surviving candidate) rejects the shader instead of picking one.
    /// </summary>
    private static FShaderLegacy? ReadUnknownTypeFromTail(FMaterialResourceProxyReader Ar,
        string typeName, byte[] typeNameBytes, long frameStart, long endPosition)
    {
        const int tailBytesBeforeTypeName = 76;

        var frameLength = (int) (endPosition - frameStart);
        if (frameLength < tailBytesBeforeTypeName + 8 || frameStart + frameLength > Ar.Length)
            return null;
        Ar.Position = frameStart;
        var frame = Ar.ReadBytes(frameLength);

        FShaderLegacy? found = null;
        for (var offset = tailBytesBeforeTypeName; offset + 8 <= frameLength; offset++)
        {
            if (frame[offset] != typeNameBytes[0]) continue;
            var matches = true;
            for (var b = 1; b < 8; b++)
            {
                if (frame[offset + b] == typeNameBytes[b]) continue;
                matches = false;
                break;
            }
            if (!matches) continue;

            try
            {
                Ar.Position = frameStart + offset - tailBytesBeforeTypeName;
                var candidate = new FShaderLegacy { TypeName = typeName };
                candidate.DeserializeBaseTail(Ar);
                if (Ar.Position != endPosition) continue;
                if (candidate.Target.Frequency >= 10) continue; // SF_NumFrequencies (RHIDefinitions.h)
                if (!string.Equals(candidate.TypeName, typeName, StringComparison.Ordinal)) continue;
                if (found != null)
                {
                    Log.Verbose("LegacyShaderMap: shader '{0}' tail anchor is ambiguous, skipping", typeName);
                    return null;
                }
                found = candidate;
            }
            catch
            {
                // candidate did not validate — keep scanning
            }
        }
        return found;
    }

    private void Deserialize(FMaterialResourceProxyReader Ar, int policyParamCount)
    {
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            DeserializeTBasePassPS_UE4_19(Ar, policyParamCount);
            return;
        }

        // ---- virtual Serialize: FMaterialShader::Serialize (ShaderBaseClasses.cpp) ----
        var p = new FMaterialShaderParametersLegacy
        {
            SceneTexturesUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            MobileSceneTexturesUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            MaterialUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            ParameterCollectionUniformBuffers = Ar.ReadArray(() => new FShaderUniformBufferParameterLegacy(Ar)),
            NumVectorExpressions = Ar.Read<int>(),
            NumScalarExpressions = Ar.Read<int>(),
            Num2DTextureExpressions = Ar.Read<int>(),
            NumCubeTextureExpressions = Ar.Read<int>(),
            NumVolumeTextureExpressions = Ar.Read<int>()
        };
        if (Ar.LegacyProfile is not (ELegacyShaderMapProfile.PreVirtualTexture or ELegacyShaderMapProfile.UE4_19))
            p.NumVirtualTextureExpressions = Ar.Read<int>();
        Ar.ReadFName(); // DebugUniformExpressionUBLayout name
        Ar.Position += 4; // uint32 ConstantBufferSize
        Ar.ReadArray<ushort>(); // ResourceOffsets
        Ar.ReadArray<byte>(); // ResourceTypes
        p.DebugDescription = Ar.ReadFString(false);
        if (Ar.LegacyProfile is ELegacyShaderMapProfile.PreVirtualTexture or ELegacyShaderMapProfile.UE4_19)
        {
            // pre-VT branch: 20 bytes of parameters here instead of the 4-byte VTFeedbackBuffer
            // (measured against the VF reference + policy params + SerializeBase tail alignment
            // on Fortnite Season X cooks; the end-offset check catches any cook that differs)
            Ar.Position += 20;
        }
        else
        {
            Ar.Position += 2 * sizeof(ushort); // FShaderResourceParameter VTFeedbackBuffer (BaseIndex, NumResources)
        }

        // ---- FMeshMaterialShader::Serialize ----
        p.PassUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar);
        // FVertexFactoryParameterRef: VF type + freq + platform + hash + self-framed parameter blob
        p.VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 2; // uint8 ShaderFrequency, uint8 ShaderPlatform
        Ar.Position += 20; // FSHAHash VFHash
        var vfSkipOffset = Ar.Read<long>(); // relative to OffsetToFirstResource
        Ar.Position = Ar.OffsetToFirstResource + vfSkipOffset; // VF parameter layouts are per-VF-type: skip exactly

        // ---- TBasePassPixelShaderPolicyParamType::Serialize ----
        p.LightMapPolicyParameters = new FShaderUniformBufferParameterLegacy[policyParamCount];
        for (var i = 0; i < policyParamCount; i++)
            p.LightMapPolicyParameters[i] = new FShaderUniformBufferParameterLegacy(Ar);
        p.ReflectionCaptureBuffer = new FShaderUniformBufferParameterLegacy(Ar);
        MaterialParameters = p;

        DeserializeBaseTail(Ar);
    }

    /// <summary>
    /// FMaterialShader::Serialize (UE_4.19\...\ShaderBaseClasses.cpp:447-487) - the virtual "Serialize"
    /// front matter shared by every material shader that does NOT extend FMeshMaterialShader (i.e.
    /// isn't a TBasePassPS*-style mesh shader): FShader::Serialize is a no-op at this base, so this
    /// starts directly at FMaterialShader's own fields, in exact declaration/serialize order
    /// (MaterialShader.h:169-188, confirmed field-by-field against the operator&lt;&lt; bodies for
    /// FShaderParameter/FShaderResourceParameter/FDeferredPixelShaderParameters/
    /// FSceneTextureShaderParameters/FDebugUniformExpressionSet/FRHIUniformBufferLayout). None of these
    /// fields feed the decompiler's output, so only byte consumption matters here.
    /// </summary>
    private FMaterialShaderParametersLegacy DeserializeMaterialShaderFront_UE4_19(FMaterialResourceProxyReader Ar)
    {
        var diag = Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose);
        var p = new FMaterialShaderParametersLegacy
        {
            MaterialUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar)
        };
        if (diag) Log.Verbose("FMatFront: MaterialUniformBuffer BaseIndex={0} bIsBound={1}", p.MaterialUniformBuffer.BaseIndex, p.MaterialUniformBuffer.bIsBound);
        if (diag) Log.Verbose("FMatFront: ParameterCollectionUniformBuffers at {0}", Ar.Position);
        p.ParameterCollectionUniformBuffers = Ar.ReadArray(() => new FShaderUniformBufferParameterLegacy(Ar));
        if (diag) Log.Verbose("FMatFront: ParameterCollectionUniformBuffers count={0}, pos now {1}", p.ParameterCollectionUniformBuffers.Length, Ar.Position);
        // FDeferredPixelShaderParameters (SceneRenderTargetParameters.h/.cpp): FSceneTextureShaderParameters
        // (14 x FShaderResourceParameter = 56 bytes) + GBufferResources (FShaderUniformBufferParameter,
        // 6 bytes) + 21 x FShaderResourceParameter (84 bytes, DBufferATextureMS..CustomStencilTexture -
        // recounted from the operator<< body, SceneRenderTargets.cpp:3058-3086; the class's own field
        // list reads as 20 at a glance because CustomStencilTexture trails on its own line) = 146 bytes,
        // fixed size, no arrays inside.
        if (diag) Log.Verbose("FMatFront: DeferredParameters at {0}", Ar.Position);
        Ar.Position += 146;
        if (diag) Log.Verbose("FMatFront: SceneColorCopyTexture at {0}", Ar.Position);
        _ = new FShaderResourceParameterLegacy(Ar); // SceneColorCopyTexture
        _ = new FShaderResourceParameterLegacy(Ar); // SceneColorCopyTextureSampler
        // FDebugUniformExpressionSet (MaterialShader.h:90-99): field ORDER is NOT declaration order.
        if (diag) Log.Verbose("FMatFront: DebugUniformExpressionSet at {0}", Ar.Position);
        p.NumVectorExpressions = Ar.Read<int>();
        p.NumScalarExpressions = Ar.Read<int>();
        Ar.Position += 4; // NumPerFrameScalarExpressions - not tracked here
        Ar.Position += 4; // NumPerFrameVectorExpressions - not tracked here
        p.Num2DTextureExpressions = Ar.Read<int>();
        p.NumCubeTextureExpressions = Ar.Read<int>();
        if (diag) Log.Verbose("FMatFront: LayoutName at {0}", Ar.Position);
        Ar.ReadFName(); // DebugUniformExpressionUBLayout name
        Ar.Position += 4; // FRHIUniformBufferLayout::ConstantBufferSize (uint32)
        Ar.Position += 4; // FRHIUniformBufferLayout::ResourceOffset (uint32) - a single value at 4.19, not an array
        if (diag) Log.Verbose("FMatFront: Resources array at {0}", Ar.Position);
        Ar.ReadArray<byte>(); // FRHIUniformBufferLayout::Resources (TArray<uint8>)
        if (diag) Log.Verbose("FMatFront: DebugDescription at {0}", Ar.Position);
        p.DebugDescription = Ar.ReadFString(false);
        if (diag) Log.Verbose("FMatFront: EyeAdaptation at {0}, DebugDescription='{1}'", Ar.Position, p.DebugDescription);
        _ = new FShaderResourceParameterLegacy(Ar); // EyeAdaptation
        if (diag) Log.Verbose("FMatFront: PerFrame arrays at {0}", Ar.Position);
        var pf1 = Ar.ReadArray(() => new FShaderParameterLegacy(Ar)); // PerFrameScalarExpressions
        var pf2 = Ar.ReadArray(() => new FShaderParameterLegacy(Ar)); // PerFrameVectorExpressions
        var pf3 = Ar.ReadArray(() => new FShaderParameterLegacy(Ar)); // PerFramePrevScalarExpressions
        var pf4 = Ar.ReadArray(() => new FShaderParameterLegacy(Ar)); // PerFramePrevVectorExpressions
        if (diag) Log.Verbose("FMatFront: PerFrame counts={0},{1},{2},{3}, pos now {4}", pf1.Length, pf2.Length, pf3.Length, pf4.Length, Ar.Position);
        // InstanceCount/InstanceOffset/VertexOffset (18 bytes in vanilla 4.19) are genuinely absent
        // here - re-confirmed after fixing the missing per-shader end-offset (see Read() above) and
        // reverting the DebugUniformExpressionSet field back to its full 24 bytes: with both of those
        // corrected, restoring these three fields still overshoots VertexFactoryTypeName by exactly
        // their own 18 bytes (name-index read then throws on garbage), while omitting them lands
        // cleanly on a valid FName followed by a ShaderFrequencyByte of 3 (SF_Pixel). This Fortnite
        // branch's FMaterialShader::Serialize predates GPU instancing support for material shaders
        // (these three fields feed DrawIndexedInstanced args in FMeshMaterialShader::SetMesh), so they
        // were never added to this early build.
        if (diag) Log.Verbose("FMatFront: done at {0}", Ar.Position);
        return p;
    }

    /// <summary>
    /// Any non-TBasePassPS FMeshMaterialShader-derived shader at UE4_19 (TTranslucencyShadowDepthVS/PS,
    /// TLightMapDensityVS/PS, FVelocityVS/PS, FHitProxyVS/PS, TDepthOnlyVS, FDebugViewModeVS,
    /// FConvertToUniformMeshVS/GS, ...) - same FMeshMaterialShader::Serialize addition as TBasePassPS
    /// (FVertexFactoryParameterRef + NonInstancedDitherLODFactorParameter, MeshMaterialShader.h:77-78),
    /// reusing the exact same self-validating VF-parameter-shape/skip-offset candidates and
    /// DeserializeBaseTail resync window as DeserializeTBasePassPS_UE4_19 - but WITHOUT
    /// TBasePassPixelShaderPolicyParamType's own further additions (light-map-policy uniform buffers +
    /// the four base-pass-only parameter structs), which belong to that one pixel-shader-specific
    /// subclass, not to FMeshMaterialShader itself.
    /// </summary>
    private void DeserializeMeshMaterialShaderFront_UE4_19(FMaterialResourceProxyReader Ar, string expectedTypeName)
    {
        var diag = Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose);
        var p = DeserializeMaterialShaderFront_UE4_19(Ar);

        if (diag) Log.Verbose("MeshMatFront: VertexFactoryTypeName at {0}", Ar.Position);
        p.VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 1; // uint8 ShaderFrequencyByte
        Ar.Position += 20; // FSHAHash VFHash
        var vfSkipOffset = Ar.Read<int>();
        var afterVfSkipOffset = Ar.Position;
        if (diag) Log.Verbose("MeshMatFront: vfSkipOffset={0} at {1}, VertexFactoryTypeName='{2}'", vfSkipOffset, afterVfSkipOffset, p.VertexFactoryTypeName);

        // Same known-VF-type-first strategy as DeserializeTBasePassPS_UE4_19 (see its own comment for
        // why): a resolved VF type reads its own FVertexFactoryShaderParameters subclass in place and
        // never follows the stored skip offset at all.
        var candidates = new List<Action>();
        if (p.VertexFactoryTypeName == "FLocalVertexFactory")
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = Ar.ReadBoolean(); // bAnySpeedTreeParamIsBound
                _ = new FShaderParameterLegacy(Ar); // LODParameter
                _ = new FShaderParameterLegacy(Ar); // VertexFetch_VertexFetchParameters
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PositionBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_TexCoordBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PackedTangentsBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_ColorComponentsBufferParameter
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinVertexFactory", StringComparison.Ordinal)
                 || p.VertexFactoryTypeName.StartsWith("TGPUSkinMorphVertexFactory", StringComparison.Ordinal))
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinAPEXClothVertexFactory", StringComparison.Ordinal))
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // ClothSimulVertsPositionsNormalsParameter
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousClothSimulVertsPositionsNormalsParameter
                _ = new FShaderParameterLegacy(Ar); // ClothLocalToWorldParameter
                _ = new FShaderParameterLegacy(Ar); // ClothBlendWeightParameter
                _ = new FShaderResourceParameterLegacy(Ar); // GPUSkinApexClothParameter
                _ = new FShaderParameterLegacy(Ar); // GPUSkinApexClothStartIndexOffsetParameter
            });
        }
        candidates.Add(() => Ar.Position = vfSkipOffset);
        candidates.Add(() => Ar.Position = Ar.OffsetToFirstResource + vfSkipOffset);

        Exception? lastError = null;
        foreach (var seekToVertexFactoryParametersEnd in candidates)
        {
            Ar.Position = afterVfSkipOffset;
            try
            {
                seekToVertexFactoryParametersEnd();
                if (diag) Log.Verbose("MeshMatFront: NonInstancedDitherLODFactorParameter at {0}", Ar.Position);
                _ = new FShaderParameterLegacy(Ar); // NonInstancedDitherLODFactorParameter
                MaterialParameters = p;

                var tailStart = Ar.Position;
                Exception? tailError = null;
                var resynced = false;
                for (var delta = 0; !resynced && Math.Abs(delta) <= 64; delta = delta > 0 ? -delta : -delta + 1)
                {
                    Ar.Position = tailStart + delta;
                    try
                    {
                        DeserializeBaseTail(Ar);
                        if (Target.Frequency >= 10)
                            throw new InvalidOperationException($"implausible Target.Frequency {Target.Frequency}");
                        if (!string.Equals(TypeName, expectedTypeName, StringComparison.Ordinal))
                            throw new InvalidOperationException($"TypeName mismatch: expected '{expectedTypeName}', got '{TypeName}'");
                        resynced = true;
                    }
                    catch (Exception ex)
                    {
                        tailError = ex;
                    }
                }
                if (!resynced) throw tailError ?? new InvalidOperationException("DeserializeBaseTail resync exhausted");
                if (diag) Log.Verbose("MeshMatFront: validated, VF='{0}'", p.VertexFactoryTypeName);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (diag) Log.Verbose("MeshMatFront: candidate failed: {0}", ex.Message);
            }
        }
        throw lastError ?? new InvalidOperationException("DeserializeMeshMaterialShaderFront_UE4_19 exhausted VF candidates");
    }

    /// <summary>
    /// TBasePassPS* at UE4_19: FMeshMaterialShader::Serialize adds VertexFactoryParameters (a
    /// FVertexFactoryParameterRef whose "skip to the VF-type-specific parameter blob" offset is a
    /// 4-byte ABSOLUTE file position at this engine version - written via Ar.Tell()/Ar.Seek(),
    /// VertexFactory.cpp:336-393 - not the 8-byte "relative to OffsetToFirstResource" value used at
    /// 4.23) + NonInstancedDitherLODFactorParameter, and TBasePassPixelShaderPolicyParamType::
    /// Serialize adds the light-map-policy-specific params (same shape already modeled via
    /// policyParamCount) followed by four whole parameter-struct classes with no arrays inside them -
    /// FBasePassReflectionParameters (134 bytes: FPlanarReflectionParameters 74 + 2 FShaderResourceParameter
    /// 8 + 4 FShaderParameter 24 + FSkyLightReflectionParameters 28), FTranslucentLightingParameters
    /// (66 bytes), FHeightFogShaderParameters (70 bytes), FForwardLightingParameters (78 bytes) -
    /// BasePassRendering.h:53-232,628-719, FogRendering.h/.cpp, VolumetricFog.h, PlanarReflectionRendering.h.
    /// None of these fields feed the decompiler's output, so only byte consumption matters here.
    /// </summary>
    private void DeserializeTBasePassPS_UE4_19(FMaterialResourceProxyReader Ar, int policyParamCount)
    {
        var diag = Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose);
        var p = DeserializeMaterialShaderFront_UE4_19(Ar);

        // ---- FMeshMaterialShader::Serialize addition ----
        if (diag) Log.Verbose("TBasePassPS: VertexFactoryTypeName at {0}", Ar.Position);
        p.VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 1; // uint8 ShaderFrequencyByte
        Ar.Position += 20; // FSHAHash VFHash
        if (diag) Log.Verbose("TBasePassPS: vfSkipOffset at {0}, VertexFactoryTypeName='{1}'", Ar.Position, p.VertexFactoryTypeName);
        var vfSkipOffset = Ar.Read<int>();
        var afterVfSkipOffset = Ar.Position;
        var expectedTypeName = TypeName;
        if (diag) Log.Verbose("TBasePassPS: vfSkipOffset={0}, OffsetToFirstResource={1} (Ar.Length={2})", vfSkipOffset, Ar.OffsetToFirstResource, Ar.Length);

        // FVertexFactoryParameterRef::Serialize (VertexFactory.cpp:336-393) only falls back to
        // seeking past the stored skip offset when the vertex factory type failed to resolve by
        // name (Ref.Parameters == null); when it resolves - as FLocalVertexFactory (by far the most
        // common case for static-mesh-authored materials) always does - the engine instead reads
        // that VF type's own FVertexFactoryShaderParameters subclass in place, right after the skip
        // offset int32, and the recorded offset is never followed at all. So for a known VF type,
        // read its parameters directly; only fall back to the two skip-offset interpretations
        // (absolute Ar.Tell(), or OffsetToFirstResource-relative like the 4.23-era model) - each
        // validated against DeserializeBaseTail's own TypeName/Target anchor - for a type this
        // doesn't recognize yet.
        var candidates = new List<Action>();
        if (p.VertexFactoryTypeName == "FLocalVertexFactory")
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                // FLocalVertexFactoryShaderParameters::Serialize (LocalVertexFactory.cpp:44-53)
                _ = Ar.ReadBoolean(); // bAnySpeedTreeParamIsBound
                _ = new FShaderParameterLegacy(Ar); // LODParameter
                _ = new FShaderParameterLegacy(Ar); // VertexFetch_VertexFetchParameters
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PositionBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_TexCoordBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PackedTangentsBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_ColorComponentsBufferParameter
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinVertexFactory", StringComparison.Ordinal)
                 || p.VertexFactoryTypeName.StartsWith("TGPUSkinMorphVertexFactory", StringComparison.Ordinal))
        {
            // TGPUSkinMorphVertexFactory::ConstructShaderParameters (GPUSkinVertexFactory.cpp:744-748)
            // constructs the exact same FGPUSkinVertexFactoryShaderParameters as plain
            // TGPUSkinVertexFactory - no extra fields for the morph-target variant.
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                // FGPUSkinVertexFactoryShaderParameters::Serialize (GPUSkinVertexFactory.cpp:496-501)
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinAPEXClothVertexFactory", StringComparison.Ordinal))
        {
            // TGPUSkinAPEXClothVertexFactoryShaderParameters::Serialize (GPUSkinVertexFactory.cpp:780-789)
            // calls the base FGPUSkinVertexFactoryShaderParameters::Serialize first, then adds 6 more
            // cloth-simulation fields (field types confirmed from the class's own member declarations,
            // GPUSkinVertexFactory.cpp:854-859).
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // ClothSimulVertsPositionsNormalsParameter
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousClothSimulVertsPositionsNormalsParameter
                _ = new FShaderParameterLegacy(Ar); // ClothLocalToWorldParameter
                _ = new FShaderParameterLegacy(Ar); // ClothBlendWeightParameter
                _ = new FShaderResourceParameterLegacy(Ar); // GPUSkinApexClothParameter
                _ = new FShaderParameterLegacy(Ar); // GPUSkinApexClothStartIndexOffsetParameter
            });
        }
        candidates.Add(() => Ar.Position = vfSkipOffset);
        candidates.Add(() => Ar.Position = Ar.OffsetToFirstResource + vfSkipOffset);

        Exception? lastError = null;
        foreach (var seekToVertexFactoryParametersEnd in candidates)
        {
            Ar.Position = afterVfSkipOffset;
            try
            {
                seekToVertexFactoryParametersEnd();
                if (diag) Log.Verbose("TBasePassPS: NonInstancedDitherLODFactorParameter at {0}", Ar.Position);
                _ = new FShaderParameterLegacy(Ar); // NonInstancedDitherLODFactorParameter

                // ---- TBasePassPixelShaderPolicyParamType::Serialize addition ----
                if (diag) Log.Verbose("TBasePassPS: LightMapPolicyParameters ({0}) at {1}", policyParamCount, Ar.Position);
                p.LightMapPolicyParameters = new FShaderUniformBufferParameterLegacy[policyParamCount];
                for (var i = 0; i < policyParamCount; i++)
                    p.LightMapPolicyParameters[i] = new FShaderUniformBufferParameterLegacy(Ar);
                if (diag) Log.Verbose("TBasePassPS: 4 param structs at {0}", Ar.Position);
                Ar.Position += 134; // FBasePassReflectionParameters
                Ar.Position += 66; // FTranslucentLightingParameters
                Ar.Position += 70; // FHeightFogShaderParameters
                Ar.Position += 78; // FForwardLightingParameters (includes ReflectionCaptureBuffer at this version)
                if (diag) Log.Verbose("TBasePassPS: done, DeserializeBaseTail at {0}", Ar.Position);
                MaterialParameters = p;

                // The four param-struct byte counts (134/66/70/78) are derived from engine source
                // but not independently anchor-verified the way the rest of this chain is, so a
                // small residual miscount is plausible; resync onto DeserializeBaseTail's own
                // TypeName/Target anchor within a modest window rather than trusting the raw sum.
                var tailStart = Ar.Position;
                Exception? tailError = null;
                var resynced = false;
                var usedDelta = 0;
                for (var delta = 0; !resynced && Math.Abs(delta) <= 64; delta = delta > 0 ? -delta : -delta + 1)
                {
                    Ar.Position = tailStart + delta;
                    try
                    {
                        DeserializeBaseTail(Ar);
                        if (Target.Frequency >= 10)
                            throw new InvalidOperationException($"implausible Target.Frequency {Target.Frequency}");
                        if (!string.Equals(TypeName, expectedTypeName, StringComparison.Ordinal))
                            throw new InvalidOperationException($"TypeName mismatch: expected '{expectedTypeName}', got '{TypeName}'");
                        resynced = true;
                        usedDelta = delta;
                    }
                    catch (Exception ex)
                    {
                        tailError = ex;
                    }
                }
                if (!resynced) throw tailError ?? new InvalidOperationException("DeserializeBaseTail resync exhausted");
                if (diag) Log.Verbose("TBasePassPS: validated at tail delta {0} (Target.Frequency={1})", usedDelta, Target.Frequency);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (diag) Log.Verbose("TBasePassPS: candidate failed: {0}", ex.Message);
            }
        }

        throw lastError ?? new InvalidOperationException("TBasePassPS: no vertex-factory-parameters interpretation validated");
    }

    /// <summary>
    /// TBasePassVS* at UE4_19: the vertex-shader counterpart to DeserializeTBasePassPS_UE4_19, sharing
    /// the same FMeshMaterialShader front matter (VF-ref + NonInstancedDitherLODFactorParameter) but
    /// with TBasePassVertexShaderPolicyParamType::Serialize's own trailing fields instead of the pixel
    /// shader's (BasePassRendering.h:355-367): VertexParametersType::Serialize (same
    /// FUniformLightMapPolicyShaderParametersType as the pixel side for FUniformLightMapPolicy, hence
    /// reusing policyParamCount) + HeightFogParameters (70B, shared with the PS side) +
    /// TranslucentLightingVolumeParameters (8 x FShaderResourceParameter = 32B, BasePassRendering.h:292-314)
    /// + ForwardLightingParameters (78B, shared with the PS side) + four FShaderParameter fields
    /// (PreviousLocalToWorld/SkipOutputVelocity/InstancedEyeIndex/IsInstancedStereo, 6B each = 24B).
    /// </summary>
    private void DeserializeTBasePassVS_UE4_19(FMaterialResourceProxyReader Ar, int policyParamCount)
    {
        var diag = Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose);
        var p = DeserializeMaterialShaderFront_UE4_19(Ar);

        if (diag) Log.Verbose("TBasePassVS: VertexFactoryTypeName at {0}", Ar.Position);
        p.VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 1; // uint8 ShaderFrequencyByte
        Ar.Position += 20; // FSHAHash VFHash
        var vfSkipOffset = Ar.Read<int>();
        var afterVfSkipOffset = Ar.Position;
        var expectedTypeName = TypeName;

        var candidates = new List<Action>();
        if (p.VertexFactoryTypeName == "FLocalVertexFactory")
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = Ar.ReadBoolean(); // bAnySpeedTreeParamIsBound
                _ = new FShaderParameterLegacy(Ar); // LODParameter
                _ = new FShaderParameterLegacy(Ar); // VertexFetch_VertexFetchParameters
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PositionBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_TexCoordBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_PackedTangentsBufferParameter
                _ = new FShaderResourceParameterLegacy(Ar); // VertexFetch_ColorComponentsBufferParameter
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinVertexFactory", StringComparison.Ordinal)
                 || p.VertexFactoryTypeName.StartsWith("TGPUSkinMorphVertexFactory", StringComparison.Ordinal))
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
            });
        }
        else if (p.VertexFactoryTypeName.StartsWith("TGPUSkinAPEXClothVertexFactory", StringComparison.Ordinal))
        {
            candidates.Add(() =>
            {
                Ar.Position = afterVfSkipOffset;
                _ = new FShaderParameterLegacy(Ar); // PerBoneMotionBlur
                _ = new FShaderResourceParameterLegacy(Ar); // BoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousBoneMatrices
                _ = new FShaderResourceParameterLegacy(Ar); // ClothSimulVertsPositionsNormalsParameter
                _ = new FShaderResourceParameterLegacy(Ar); // PreviousClothSimulVertsPositionsNormalsParameter
                _ = new FShaderParameterLegacy(Ar); // ClothLocalToWorldParameter
                _ = new FShaderParameterLegacy(Ar); // ClothBlendWeightParameter
                _ = new FShaderResourceParameterLegacy(Ar); // GPUSkinApexClothParameter
                _ = new FShaderParameterLegacy(Ar); // GPUSkinApexClothStartIndexOffsetParameter
            });
        }
        candidates.Add(() => Ar.Position = vfSkipOffset);
        candidates.Add(() => Ar.Position = Ar.OffsetToFirstResource + vfSkipOffset);

        Exception? lastError = null;
        foreach (var seekToVertexFactoryParametersEnd in candidates)
        {
            Ar.Position = afterVfSkipOffset;
            try
            {
                seekToVertexFactoryParametersEnd();
                if (diag) Log.Verbose("TBasePassVS: NonInstancedDitherLODFactorParameter at {0}", Ar.Position);
                _ = new FShaderParameterLegacy(Ar); // NonInstancedDitherLODFactorParameter

                if (diag) Log.Verbose("TBasePassVS: VertexParametersType ({0}) at {1}", policyParamCount, Ar.Position);
                p.LightMapPolicyParameters = new FShaderUniformBufferParameterLegacy[policyParamCount];
                for (var i = 0; i < policyParamCount; i++)
                    p.LightMapPolicyParameters[i] = new FShaderUniformBufferParameterLegacy(Ar);

                if (diag) Log.Verbose("TBasePassVS: HeightFog/TranslucentLightingVolume/ForwardLighting at {0}", Ar.Position);
                Ar.Position += 70; // FHeightFogShaderParameters
                Ar.Position += 32; // FTranslucentLightingVolumeParameters (8 x FShaderResourceParameter)
                Ar.Position += 78; // FForwardLightingParameters
                _ = new FShaderParameterLegacy(Ar); // PreviousLocalToWorldParameter
                _ = new FShaderParameterLegacy(Ar); // SkipOutputVelocityParameter
                _ = new FShaderParameterLegacy(Ar); // InstancedEyeIndexParameter
                _ = new FShaderParameterLegacy(Ar); // IsInstancedStereoParameter
                if (diag) Log.Verbose("TBasePassVS: done, DeserializeBaseTail at {0}", Ar.Position);
                MaterialParameters = p;

                var tailStart = Ar.Position;
                Exception? tailError = null;
                var resynced = false;
                var usedDelta = 0;
                for (var delta = 0; !resynced && Math.Abs(delta) <= 64; delta = delta > 0 ? -delta : -delta + 1)
                {
                    Ar.Position = tailStart + delta;
                    try
                    {
                        DeserializeBaseTail(Ar);
                        if (Target.Frequency >= 10)
                            throw new InvalidOperationException($"implausible Target.Frequency {Target.Frequency}");
                        if (!string.Equals(TypeName, expectedTypeName, StringComparison.Ordinal))
                            throw new InvalidOperationException($"TypeName mismatch: expected '{expectedTypeName}', got '{TypeName}'");
                        resynced = true;
                        usedDelta = delta;
                    }
                    catch (Exception ex)
                    {
                        tailError = ex;
                    }
                }
                if (!resynced) throw tailError ?? new InvalidOperationException("DeserializeBaseTail resync exhausted");
                if (diag) Log.Verbose("TBasePassVS: validated at tail delta {0} (Target.Frequency={1})", usedDelta, Target.Frequency);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (diag) Log.Verbose("TBasePassVS: candidate failed: {0}", ex.Message);
            }
        }

        throw lastError ?? new InvalidOperationException("TBasePassVS: no vertex-factory-parameters interpretation validated");
    }

    /// <summary>
    /// FShader::SerializeBase tail (Shader.cpp) + inline FShaderResource + parameter bindings.
    /// This part is common to every shader type, so it is shared between the full parse of
    /// known layouts and the tail-anchored recovery of unknown ones.
    /// </summary>
    private void DeserializeBaseTail(FMaterialResourceProxyReader Ar)
    {
        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose)) Log.Verbose("FMatFront: DeserializeBaseTail start at {0}", Ar.Position);
        OutputHash = new FSHAHash(Ar);
        MaterialShaderMapHash = new FSHAHash(Ar);
        ShaderPipelineName = Ar.ReadFName().Text;
        VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 20; // VFSourceHash (default hash when cooked)
        TypeName = Ar.ReadFName().Text; // authoritative type name (matches the outer one)
        if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderPermutationId)
            PermutationId = Ar.Read<int>();
        Ar.Position += 20; // SourceHash (default hash when cooked)
        Target = Ar.Read<FShaderTargetLegacy>(); // 2x uint32 (frequency, platform)

        var numUniformParameters = Ar.Read<int>();
        if (numUniformParameters is < 0 or > 256) // largest observed counts are well below this
            throw new InvalidOperationException($"implausible uniform buffer parameter count {numUniformParameters}");
        UniformBufferParameters = new (string, FShaderUniformBufferParameterLegacy)[numUniformParameters];
        var useStructFName = FFortniteMainBranchObjectVersion.Get(Ar) >= FFortniteMainBranchObjectVersion.Type.MaterialInstanceSerializeOptimization_ShaderFName;
        for (var i = 0; i < numUniformParameters; i++)
        {
            var structName = useStructFName ? Ar.ReadFName().Text : Ar.ReadFString(false);
            UniformBufferParameters[i] = (structName, new FShaderUniformBufferParameterLegacy(Ar));
        }

        // inline FShaderResource (bShadersInline == true for cooked material shader maps)
        Resource = new FShaderResourceLegacy(Ar);

        if (Ar.LegacyProfile != ELegacyShaderMapProfile.UE4_19)
        {
            // FShaderParameterBindings (Shader.h): 9 arrays + uint16 RootParameterBufferIndex
            // (pre-VT branches have one array fewer between Parameters and ParameterReferences).
            // This whole reflection-based parameter-binding system does not exist yet at UE4_19 -
            // confirmed by FShader::SerializeBase (Shader.cpp:955-1041) ending immediately after the
            // inline FShaderResource::Serialize call, with nothing else read afterward.
            Ar.ReadArray<ulong>(); // Parameters (4x uint16)
            var bindingArrayCount = Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture ? 6 : 7;
            for (var i = 0; i < bindingArrayCount; i++) Ar.ReadArray<uint>(); // Textures..GraphUAVs (2x uint16 each)
            Ar.ReadArray<uint>(); // ParameterReferences (2x uint16)
            Ar.Position += 2; // RootParameterBufferIndex
        }
    }
}

/// <summary>FShaderTarget (&lt; 4.25 stream form): serialized as two uint32s (frequency, platform).</summary>
public struct FShaderTargetLegacy
{
    public uint Frequency;
    public uint Platform;
}

/// <summary>Inline FShaderResource (&lt; 4.25) carrying the compiled shader bytecode.</summary>
public class FShaderResourceLegacy
{
    public string SpecificTypeName = "";
    public int SpecificPermutationId;
    public FShaderTargetLegacy Target;
    public FSHAHash OutputHash;
    public uint NumInstructions;
    public FShaderParameterMapInfoLegacy ParameterMapInfo;
    /// <summary>True when the bytecode lives in a shared shader code library instead of the package.</summary>
    public bool bCodeInSharedLocation;
    /// <summary>Decompressed shader bytecode (FShaderCode layout: bytecode + optional data), empty if shared.</summary>
    [JsonIgnore] public byte[] Code = [];

    public FShaderResourceLegacy(FMaterialResourceProxyReader Ar)
    {
        var diag = Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose) && Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19;
        if (diag) Log.Verbose("FShaderResource: SpecificTypeName at {0}", Ar.Position);
        SpecificTypeName = Ar.ReadFName().Text;
        if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderPermutationId)
            SpecificPermutationId = Ar.Read<int>();
        if (diag) Log.Verbose("FShaderResource: Target at {0}", Ar.Position);
        Target = Ar.Read<FShaderTargetLegacy>();
        if (diag) Log.Verbose("FShaderResource: Code at {0}", Ar.Position);
        // NOTE: at UE4_19, GAME_UE4_19's FRenderingObjectVersion.Get fallback (VolumetricLightmaps,
        // needed elsewhere for FStaticParameterSet gating) sits ABOVE ShaderResourceCodeSharing, so a
        // blanket single substitute value can't correctly gate both checks - testing the "sharing
        // already enabled" branch here (skip inline Code, read bCodeInSharedLocation later instead).
        if (Ar.LegacyProfile != ELegacyShaderMapProfile.UE4_19 && FRenderingObjectVersion.Get(Ar) < FRenderingObjectVersion.Type.ShaderResourceCodeSharing)
        {
            Code = Ar.ReadArray<byte>();
        }
        if (diag) Log.Verbose("FShaderResource: Code.Length={0}, OutputHash at {1}", Code.Length, Ar.Position);
        OutputHash = new FSHAHash(Ar);
        NumInstructions = Ar.Read<uint>();
        if (diag) Log.Verbose("FShaderResource: NumInstructions={0}, NumTextureSamplers at {1}", NumInstructions, Ar.Position);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19)
        {
            // FShaderResource::Serialize at UE4_19 (Shader.cpp:480-499) reads a plain uint32
            // NumTextureSamplers here - there is no FShaderParameterMapInfo at all yet (that's a
            // later addition alongside the reflection-based parameter binding system, confirmed by
            // its total absence from this exact function at 4.19).
            Ar.Position += 4; // NumTextureSamplers - not tracked here
        }
        else
        {
            // NumTextureSamplers is editor-only and not cooked at this (4.23+) era
            ParameterMapInfo = new FShaderParameterMapInfoLegacy(Ar);
        }

        if (diag) Log.Verbose("FShaderResource: uncompressedCodeSize at {0}", Ar.Position);
        var uncompressedCodeSize = Ar.Read<int>(); // VER_UE4_COMPRESSED_SHADER_RESOURCES
        if (diag) Log.Verbose("FShaderResource: uncompressedCodeSize={0}, done at {1}", uncompressedCodeSize, Ar.Position);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.UE4_19 || FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderResourceCodeSharing)
        {
            if (diag) Log.Verbose("FShaderResource: bCodeShared at {0}", Ar.Position);
            bCodeInSharedLocation = Ar.ReadBoolean();
            if (diag) Log.Verbose("FShaderResource: bCodeShared={0}, Code (2nd) at {1}", bCodeInSharedLocation, Ar.Position);
            if (!bCodeInSharedLocation)
            {
                Code = Ar.ReadArray<byte>();
            }
            if (diag) Log.Verbose("FShaderResource: Code.Length={0}, done at {1}", Code.Length, Ar.Position);
        }

        // FShaderResource::UncompressCode: data is Zlib-compressed when the stored size differs
        if (Code.Length > 0 && uncompressedCodeSize > 0 && Code.Length != uncompressedCodeSize)
        {
            Code = Compression.Compression.Decompress(Code, uncompressedCodeSize, CompressionMethod.Zlib);
        }
    }
}
