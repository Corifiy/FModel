using System;
using CUE4Parse.GameTypes.RocoKingdomWorld.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports.Material.Parameters;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects.Unversioned;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Assets.Exports.Material;

public class UMaterialInstanceDynamic : UMaterialInstance;
public class UMaterialInstanceTimeVarying : UMaterialInstance;

public class UMaterialInstance : UMaterialInterface
{
    private ResolvedObject? _parent;
    private bool bHasNonUPropertyStaticParameters = false;
    public UUnrealMaterial? Parent => _parent?.Load<UUnrealMaterial>();
    public bool bHasStaticPermutationResource;
    public FMaterialInstanceBasePropertyOverrides? BasePropertyOverrides;
    public FStaticParameterSet? StaticParameters;
    public FStructFallback? CachedData;

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        if (Ar.Game == EGame.GAME_WorldofJadeDynasty) Ar.Position += 24;
        base.Deserialize(Ar, validPos);
        _parent = GetOrDefault<ResolvedObject>(nameof(Parent));
        bHasStaticPermutationResource = GetOrDefault<bool>("bHasStaticPermutationResource");
        BasePropertyOverrides = GetOrDefault<FMaterialInstanceBasePropertyOverrides>(nameof(BasePropertyOverrides));
        StaticParameters = GetOrDefault(nameof(StaticParameters), GetOrDefault<FStaticParameterSet>("StaticParametersRuntime"));

        var bSavedCachedData = FUE5MainStreamObjectVersion.Get(Ar) >= FUE5MainStreamObjectVersion.Type.MaterialSavedCachedData && Ar.ReadBoolean();
        if (bSavedCachedData)
        {
            CachedData = new FStructFallback(Ar, "MaterialInstanceCachedData");
        }

        if (bHasStaticPermutationResource && Ar.Ver >= EUnrealEngineObjectUE4Version.PURGED_FMATERIAL_COMPILE_OUTPUTS)
        {
            if (FRenderingObjectVersion.Get(Ar) < FRenderingObjectVersion.Type.MaterialAttributeLayerParameters)
            {
                StaticParameters = new FStaticParameterSet(Ar);
                bHasNonUPropertyStaticParameters = true;
            }

            // See UMaterial.Deserialize for why this no longer additionally gates on Game>=GAME_UE4_23:
            // the Ar.Ver check above is the real, version-driven signal for the inline shader map
            // (FMaterialShaderMapLegacy) format, and at least one GAME_UE4_19-tagged title (Fortnite's
            // internal branch) already reports Ar.Ver past that threshold, meaning the extra Game gate
            // was silently skipping real, present, correctly-versioned data for it.
            if (Ar.Owner.Provider.ReadShaderMaps)
            {
                var saved = Ar.Position;
                try
                {
                    DeserializeInlineShaderMaps(Ar, LoadedMaterialResources);
                }
                catch (Exception e)
                {
                    // Retry once with the pre-proxy-reader (~4.19-era) format before giving up - see
                    // UMaterial.Deserialize / FMaterialResourceProxyReader.CreatePassthrough.
                    Log.Error(e, "Failed to deserialize inline shader maps (current format); retrying with the pre-4.22-era format.");
                    Ar.Position = saved;
                    LoadedMaterialResources.Clear();
                    try
                    {
                        // FMaterialResource::GetFriendlyName() (MaterialShared.cpp:1073) returns the
                        // root UMaterial's own name, not this instance's - walk the Parent chain to
                        // find it for the very-legacy anchor scan (MaterialResourceTypes.cs).
                        string? rootMaterialName = null;
                        try
                        {
                            var cur = Parent;
                            for (var guard = 0; cur is UMaterialInstance nextInstance && guard < 16; guard++)
                                cur = nextInstance.Parent;
                            rootMaterialName = cur?.Name;
                        }
                        catch (Exception eParent)
                        {
                            Log.Error(eParent, "Failed to resolve root material name for the pre-4.22-era friendly-name anchor scan.");
                        }
                        DeserializeInlineShaderMaps(Ar, LoadedMaterialResources, usePassthrough: true, friendlyNameOverride: rootMaterialName);
                    }
                    catch (Exception e2)
                    {
                        Log.Error(e2, "Failed to deserialize inline shader maps (pre-4.22-era format either).");
                        LoadedMaterialResources.Clear();
                        // Recover to validPos, not the mid-parse "saved" position - see UMaterial.Deserialize.
                        Ar.Position = validPos;
                    }
                }
            }
            else
            {
                Ar.Position = validPos;
            }
        }

        if (Ar.Game is EGame.GAME_DeadByDaylight && Ar.Position < validPos && Ar is { Owner.Provider.ReadShaderMaps: true })
            CustomGameData = Ar.ReadArray(() => new FStructFallback(Ar, "BHVRVariantConfigurator", FRawHeader.FullRead, ReadType.RAW));
        if (Ar.Game == EGame.GAME_Valorant && !bHasStaticPermutationResource)
            Ar.Position += 8; // 0.0f and 1.0f, for all
        if (Ar.Game is EGame.GAME_RocoKingdomWorld && bHasStaticPermutationResource)
        {
            // Additional DynamicSwitchParameters
            CustomGameData = Ar.ReadArray(() => new FRKWStaticSwitchParameter(Ar));
            Ar.Position += 4;
        }
    }

    public override void GetParams(CMaterialParams2 parameters, EMaterialFormat format)
    {
        base.GetParams(parameters, format);

        if (StaticParameters != null)
            foreach (var switchParameter in StaticParameters.StaticSwitchParameters)
                parameters.Switches[switchParameter.Name] = switchParameter.Value;

        if (BasePropertyOverrides != null)
        {
            parameters.BlendMode = BasePropertyOverrides.BlendMode;
            parameters.ShadingModel = BasePropertyOverrides.ShadingModel;
        }
    }

    protected internal override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        if (CachedData != null)
        {
            writer.WritePropertyName("CachedData");
            serializer.Serialize(writer, CachedData);
        }

        //fix StaticParameters not showing in the json on versions such as 4.16
        if (StaticParameters != null && bHasNonUPropertyStaticParameters)
        {
            writer.WritePropertyName("StaticParameters");
            serializer.Serialize(writer, StaticParameters);
        }
    }
}

[StructFallback]
public class FStaticParameterSet
{
    public FStaticSwitchParameter[] StaticSwitchParameters;
    public FStaticComponentMaskParameter[] StaticComponentMaskParameters;
    public FStaticTerrainLayerWeightParameter[] TerrainLayerWeightParameters;
    public FStaticMaterialLayersParameter[]? MaterialLayersParameters;

    public FStaticParameterSet(FArchive Ar)
    {
        if (Ar.Game < EGame.GAME_UE4_0)
        {
            Ar.Read<FGuid>(); // BaseMaterialId
        }

        StaticSwitchParameters = Ar.ReadArray(() => new FStaticSwitchParameter(Ar));
        StaticComponentMaskParameters = Ar.ReadArray(() => new FStaticComponentMaskParameter(Ar));
        if (Ar.Ver >= EUnrealEngineObjectUE3Version.ADD_TERRAINLAYERWEIGHT_PARAMETERS)
        {
            TerrainLayerWeightParameters = Ar.ReadArray(() => new FStaticTerrainLayerWeightParameter(Ar));
        }

        if (FReleaseObjectVersion.Get(Ar) >= FReleaseObjectVersion.Type.MaterialLayersParameterSerializationRefactor)
        {
            MaterialLayersParameters = Ar.ReadArray(() => new FStaticMaterialLayersParameter(Ar));
        }
    }

    public FStaticParameterSet(FStructFallback fallback)
    {
        StaticSwitchParameters = fallback.GetOrDefault(nameof(StaticSwitchParameters), Array.Empty<FStaticSwitchParameter>());
        StaticComponentMaskParameters = fallback.GetOrDefault(nameof(StaticComponentMaskParameters), Array.Empty<FStaticComponentMaskParameter>());
        TerrainLayerWeightParameters = fallback.GetOrDefault(nameof(TerrainLayerWeightParameters), Array.Empty<FStaticTerrainLayerWeightParameter>());
        MaterialLayersParameters = fallback.GetOrDefault(nameof(MaterialLayersParameters), Array.Empty<FStaticMaterialLayersParameter>());
    }
}
