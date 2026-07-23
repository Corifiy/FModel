using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Assets.Exports.Material;

[SkipObjectRegistration]
public class UMaterialInterface : UUnrealMaterial
{
    //I think those aren't used in UE4 but who knows
    //to delete
    public bool bUseMobileSpecular;
    public float MobileSpecularPower = 16.0f;
    public EMobileSpecularMask MobileSpecularMask = EMobileSpecularMask.MSM_Constant;
    public UTexture? FlattenedTexture;
    public UTexture? MobileBaseTexture;
    public UTexture? MobileNormalTexture;
    public UTexture? MobileMaskTexture;

    public FStructFallback? CachedExpressionData;
    public FMaterialTextureInfo[] TextureStreamingData = Array.Empty<FMaterialTextureInfo>();
    public List<FMaterialResource> LoadedMaterialResources = new();

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        if(Ar.Game == EGame.GAME_WorldofJadeDynasty) Ar.Position += 24;
        base.Deserialize(Ar, validPos);
        bUseMobileSpecular = GetOrDefault<bool>(nameof(bUseMobileSpecular));
        MobileSpecularPower = GetOrDefault<float>(nameof(MobileSpecularPower));
        MobileSpecularMask = GetOrDefault<EMobileSpecularMask>(nameof(MobileSpecularMask));
        FlattenedTexture = GetOrDefault<UTexture>(nameof(FlattenedTexture));
        MobileBaseTexture = GetOrDefault<UTexture>(nameof(MobileBaseTexture));
        MobileNormalTexture = GetOrDefault<UTexture>(nameof(MobileNormalTexture));
        MobileMaskTexture = GetOrDefault<UTexture>(nameof(MobileMaskTexture));
        TextureStreamingData = GetOrDefault(nameof(TextureStreamingData), Array.Empty<FMaterialTextureInfo>());

        var bSavedCachedExpressionData = FUE5ReleaseStreamObjectVersion.Get(Ar) >= FUE5ReleaseStreamObjectVersion.Type.MaterialInterfaceSavedCachedData && Ar.ReadBoolean();
        if (bSavedCachedExpressionData)
        {
            CachedExpressionData = new FStructFallback(Ar, "MaterialCachedExpressionData");
        }

        if (Ar.Game == EGame.GAME_HogwartsLegacy) CustomGameData = new FSHAHash(Ar);
    }

    protected internal override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        if (LoadedMaterialResources is not null)
        {
            writer.WritePropertyName("LoadedMaterialResources");
            serializer.Serialize(writer, LoadedMaterialResources);
        }

        if (CachedExpressionData is not null)
        {
            writer.WritePropertyName("CachedExpressionData");
            serializer.Serialize(writer, CachedExpressionData);
        }

    }

    public override void GetParams(CMaterialParams parameters)
    {
        if (FlattenedTexture != null) parameters.Diffuse = FlattenedTexture;
        if (MobileBaseTexture != null) parameters.Diffuse = MobileBaseTexture;
        if (MobileNormalTexture != null) parameters.Normal = MobileNormalTexture;
        if (MobileMaskTexture != null) parameters.Opacity = MobileMaskTexture;
        parameters.UseMobileSpecular = bUseMobileSpecular;
        parameters.MobileSpecularPower = MobileSpecularPower;
        parameters.MobileSpecularMask = MobileSpecularMask;
    }

    public override void GetParams(CMaterialParams2 parameters, EMaterialFormat format)
    {
        for (int i = 0; i < TextureStreamingData.Length; i++)
        {
            var name = TextureStreamingData[i].TextureName.Text;
            if (!parameters.TryGetTexture2d(out var texture, name))
                continue;

            parameters.VerifyTexture(name, texture, false);
        }

        // *****************************************
        // CachedExpressionData ONLY AFTER THIS LINE
        // *****************************************

        if (CachedExpressionData == null) return;
        if (CachedExpressionData.TryGetValue(out FStructFallback materialParameters, "Parameters"))
        {
            ParseCachedDataLegacy(parameters, materialParameters);
        }
        else
        {
            ParseCachedData(parameters, CachedExpressionData);
        }
    }

    private void ParseCachedDataLegacy(CMaterialParams2 parameters, FStructFallback materialParameters)
    {
        if (!materialParameters.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries"))
            return;

        if (materialParameters.TryGetValue(out float[] scalarValues, "ScalarValues") &&
            runtimeEntries.Length > 0 &&
            runtimeEntries[0].TryGetValue(out FMaterialParameterInfo[] scalarParameterInfos, "ParameterInfos"))
            for (int i = 0; i < scalarParameterInfos.Length; i++)
                parameters.Scalars[scalarParameterInfos[i].Name.Text] = scalarValues[i];

        if (materialParameters.TryGetValue(out FLinearColor[] vectorValues, "VectorValues") &&
            runtimeEntries.Length > 1 &&
            runtimeEntries[1].TryGetValue(out FMaterialParameterInfo[] vectorParameterInfos, "ParameterInfos"))
            for (int i = 0; i < vectorParameterInfos.Length; i++)
                parameters.Colors[vectorParameterInfos[i].Name.Text] = vectorValues[i];

        if (materialParameters.TryGetValue(out FPackageIndex[] textureValues, "TextureValues") &&
            runtimeEntries.Length > 2 &&
            runtimeEntries[2].TryGetValue(out FMaterialParameterInfo[] textureParameterInfos, "ParameterInfos"))
        {
            for (int i = 0; i < textureParameterInfos.Length; i++)
            {
                var name = textureParameterInfos[i].Name.Text;
                if (!textureValues[i].TryLoad(out UTexture texture)) continue;

                parameters.VerifyTexture(name, texture);
            }
        }
    }

    private void ParseCachedData(CMaterialParams2 parameters, FStructFallback materialParameters)
    {
        if (!materialParameters.TryGetAllValues(out FStructFallback[] runtimeEntries, "RuntimeEntries"))
            return;

        if (materialParameters.TryGetValue(out float[] scalarValues, "ScalarValues") &&
            runtimeEntries.Length > 0 &&
            runtimeEntries[0].TryGetValue(out FMaterialParameterInfo[] scalarParameterInfos, "ParameterInfoSet"))
            for (int i = 0; i < scalarParameterInfos.Length; i++)
                parameters.Scalars[scalarParameterInfos[i].Name.Text] = scalarValues[i];

        if (materialParameters.TryGetValue(out FLinearColor[] vectorValues, "VectorValues") &&
            runtimeEntries.Length > 1 &&
            runtimeEntries[1].TryGetValue(out FMaterialParameterInfo[] vectorParameterInfos, "ParameterInfoSet"))
            for (int i = 0; i < vectorParameterInfos.Length; i++)
                parameters.Colors[vectorParameterInfos[i].Name.Text] = vectorValues[i];

        if (materialParameters.TryGetValue(out FSoftObjectPath[] textureValues, "TextureValues") &&
            runtimeEntries.Length > 3 &&
            runtimeEntries[3].TryGetValue(out FMaterialParameterInfo[] textureParameterInfos, "ParameterInfoSet"))
        {
            for (int i = 0; i < textureParameterInfos.Length; i++)
            {
                var name = textureParameterInfos[i].Name.Text;
                if (!textureValues[i].TryLoad(out UTexture texture)) continue;

                parameters.VerifyTexture(name, texture);
            }
        }
    }

    /// <summary>
    /// <paramref name="usePassthrough"/> selects the pre-proxy-reader (~4.19-era) format - see
    /// FMaterialResourceProxyReader.CreatePassthrough. The NumLoadedResources preamble read below is
    /// identical in both eras (confirmed against UE_4.19's own SerializeInlineShaderMaps, Material.cpp
    /// ~line 576), so only the per-resource reader construction needs to differ.
    /// <paramref name="friendlyNameOverride"/>, when given, is used instead of this object's own Name
    /// as the expected FriendlyName for the very-legacy anchor scan (see FMaterialResource::
    /// GetFriendlyName, MaterialShared.cpp:1073: "return *GetNameSafe(Material);" - for a
    /// UMaterialInstance's own static-permutation resource this is the root UMaterial's name, not
    /// the instance's own name; UMaterialInstance.Deserialize resolves and passes that root name).
    /// </summary>
    public void DeserializeInlineShaderMaps(FAssetArchive Ar, ICollection<FMaterialResource> loadedResources, bool usePassthrough = false, string? friendlyNameOverride = null)
    {
        var numLoadedResources = Ar.Read<int>();
        if (numLoadedResources > 0)
        {
            FMaterialResourceProxyReader resourceAr;
            if (Ar.Game != EGame.GAME_Stalker2)
            {
                resourceAr = usePassthrough ? FMaterialResourceProxyReader.CreatePassthrough(Ar) : new FMaterialResourceProxyReader(Ar);
            }
            else
            {
                var ShaderMaps = new FByteBulkData(Ar);
                using var ShaderMapsAr = new FByteArchive("ShaderMaps", ShaderMaps.Data, Ar.Versions);
                resourceAr = usePassthrough ? FMaterialResourceProxyReader.CreatePassthrough(ShaderMapsAr) : new FMaterialResourceProxyReader(ShaderMapsAr);
            }

            // Every resource belonging to the same UMaterial writes the same BaseMaterialId (only
            // populated by the very-legacy/UE4_19 anchor-scan path - see
            // FMaterialShaderMapIdLegacy.BaseMaterialIdFromVeryLegacyScan) - once the first resource
            // has parsed successfully, this becomes a reliable resync anchor: if a later resource in
            // this same array fails partway through (this reader's UE4_19 per-shader byte layout still
            // isn't fully verified in every code path), scan forward for that exact 16-byte GUID
            // reappearing and walk back 8 bytes (bCooked+bValid) to land on the next resource's own
            // start, rather than giving up on every resource from that point on.
            FGuid? baseMaterialIdAnchor = null;
            for (var resourceIndex = 0; resourceIndex < numLoadedResources; ++resourceIndex)
            {
                var loadedResource = new FMaterialResource();
                if (usePassthrough)
                {
                    try
                    {
                        loadedResource.DeserializeInlineShaderMap(resourceAr, friendlyNameOverride ?? Name);
                    }
                    catch (Exception e)
                    {
                        if (baseMaterialIdAnchor is { } anchor && TryResyncViaBaseMaterialId(resourceAr, anchor))
                        {
                            loadedResource = new FMaterialResource();
                            try
                            {
                                loadedResource.DeserializeInlineShaderMap(resourceAr, friendlyNameOverride ?? Name);
                                Log.Warning(e, "DeserializeInlineShaderMaps (passthrough): resource {0}/{1} failed to parse; resynced to the next resource via its shared BaseMaterialId.",
                                    resourceIndex + 1, numLoadedResources);
                            }
                            catch (Exception e2)
                            {
                                Log.Warning(e2, "DeserializeInlineShaderMaps (passthrough): resource {0}/{1} failed to parse, and the resynced resource after it also failed; keeping the {2} resource(s) already loaded.",
                                    resourceIndex + 1, numLoadedResources, loadedResources.Count);
                                break;
                            }
                        }
                        else
                        {
                            Log.Warning(e, "DeserializeInlineShaderMaps (passthrough): resource {0}/{1} failed to parse; keeping the {2} resource(s) already loaded.",
                                resourceIndex + 1, numLoadedResources, loadedResources.Count);
                            break;
                        }
                    }
                }
                else
                {
                    loadedResource.DeserializeInlineShaderMap(resourceAr, friendlyNameOverride ?? Name);
                }
                loadedResources.Add(loadedResource);
                if (baseMaterialIdAnchor == null && loadedResource.LoadedShaderMapLegacy?.ShaderMapId.BaseMaterialIdFromVeryLegacyScan is { } capturedGuid)
                    baseMaterialIdAnchor = capturedGuid;
            }
        }
    }

    /// <summary>
    /// Scans forward from the current position for <paramref name="anchor"/>'s exact 16-byte
    /// representation reappearing (every resource of the same UMaterial shares the same
    /// BaseMaterialId), then seeks back 12 bytes - bCooked(4) + bValid(4) + Usage(4), the three fixed
    /// fields FMaterial::SerializeInlineShaderMap/FMaterialShaderMapIdLegacy.DeserializeVeryLegacy
    /// write immediately before BaseMaterialId - to land on the start of the resource that owns it.
    /// Returns false (leaving the position unchanged) if no match is found before the end of the
    /// archive.
    /// </summary>
    private static bool TryResyncViaBaseMaterialId(FMaterialResourceProxyReader Ar, FGuid anchor)
    {
        var needle = anchor.AsByteSpan().ToArray();
        var start = Ar.Position;
        var remaining = (int) Math.Min(int.MaxValue, Ar.Length - start);
        if (remaining < needle.Length) return false;

        var buf = Ar.ReadBytes(remaining);
        Ar.Position = start;
        for (var i = 0; i <= buf.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
                if (buf[i + j] != needle[j]) match = false;
            if (!match) continue;

            var candidateStart = start + i - 12;
            if (candidateStart < start) continue;
            Ar.Position = candidateStart;
            return true;
        }
        return false;
    }
}
