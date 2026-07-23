using System.Collections.Generic;

namespace CUE4Parse.UE4.Assets.Exports.Material;

/// <summary>
/// Row/component -> field name tables for the two fixed, engine-wide uniform buffers every base-pass
/// pixel shader binds (View, Primitive), used to resolve a foreign "cbrow" read
/// (e.g. "FViewUniformShaderParameters cb0[66]") that PixelShaderDecompiler.cs would otherwise print
/// as an opaque placeholder.
///
/// Unlike a material's own uniform expressions or a Material Parameter Collection (both instance
/// data with no fixed layout), these two structs are literally hardcoded C++ types
/// (Engine/Public/SceneView.h VIEW_UNIFORM_BUFFER_MEMBER_TABLE, Engine/Public/PrimitiveUniformShaderParameters.h),
/// declared with SHADER_PARAMETER(_EX/_ARRAY) macros - the same for every shader that binds them. The
/// row (and, for scalars sharing a row, the component) of every field is fully determined by walking
/// the struct's own field declaration order and applying its real, engine-defined alignment rules -
/// not measured from any one compiled shader, so it applies to every material.
///
/// Confirmed alignment rule per type - RenderCore/Public/ShaderParameterMacros.h,
/// TShaderParameterTypeInfo&lt;T&gt;::Alignment specializations (not assumed): float/int32/uint32/bool=4,
/// FVector2D=8, FVector/FVector4/FLinearColor/FIntVector/FIntVector4/FIntRect/FMatrix=16. A field starts
/// at the next offset that is a multiple of its own alignment (standard C struct layout - enforced at
/// compile time by a static_assert in the same header); FMatrix always occupies exactly 4 full rows
/// (each row 16-byte aligned per RenderCore/Private/ShaderParameters.cpp
/// CreateHLSLUniformBufferStructMembersDeclaration); an array's elements are each 16-byte aligned per
/// the same function, so an array element never shares a row with anything else.
///
/// This is why a lone scalar can end up sharing a row with an unrelated field two declarations later
/// (e.g. View row 137 packs DeltaTime/MaterialTextureMipBias/MaterialTextureDerivativeMultiply/Random
/// into .x/.y/.z/.w) while a 16-byte-aligned FVector/FVector4 always claims a row (or, for FVector's
/// true 12-byte size, up to 3 of its components) by itself.
///
/// Cross-checked against real compiled output (not just derived in the abstract) on
/// M_FN_Character_MASTER's actual decompiled pixel shader before being trusted: row 44-47 is read as a
/// 4-row matrix multiplied against SV_Position and divided by .w - exactly the SVPositionToTranslatedWorld
/// reconstruction idiom; row 66 is subtracted from a world-space vertex interpolator - exactly what
/// PreViewTranslation is for; rows 130/131/132 each blend into diffuse/specular/normal right where
/// Diffuse/Specular/NormalOverrideParameter would; row 139 gates the entire lit-emissive expression -
/// exactly UnlitViewmodeMask's role; row 137's second component feeds a biased texture sample's LOD-bias
/// argument - exactly MaterialTextureMipBias. Every one of these matched the computed table with zero
/// mismatches before this file was written.
/// </summary>
public static class EngineUniformBufferLayout
{
    public static readonly Dictionary<int, string?[]> ViewRows = new()
    {
        [0] = new string?[] { "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip" },
        [1] = new string?[] { "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip" },
        [2] = new string?[] { "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip" },
        [3] = new string?[] { "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip", "TranslatedWorldToClip" },
        [4] = new string?[] { "WorldToClip", "WorldToClip", "WorldToClip", "WorldToClip" },
        [5] = new string?[] { "WorldToClip", "WorldToClip", "WorldToClip", "WorldToClip" },
        [6] = new string?[] { "WorldToClip", "WorldToClip", "WorldToClip", "WorldToClip" },
        [7] = new string?[] { "WorldToClip", "WorldToClip", "WorldToClip", "WorldToClip" },
        [8] = new string?[] { "ClipToWorld", "ClipToWorld", "ClipToWorld", "ClipToWorld" },
        [9] = new string?[] { "ClipToWorld", "ClipToWorld", "ClipToWorld", "ClipToWorld" },
        [10] = new string?[] { "ClipToWorld", "ClipToWorld", "ClipToWorld", "ClipToWorld" },
        [11] = new string?[] { "ClipToWorld", "ClipToWorld", "ClipToWorld", "ClipToWorld" },
        [12] = new string?[] { "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView" },
        [13] = new string?[] { "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView" },
        [14] = new string?[] { "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView" },
        [15] = new string?[] { "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView", "TranslatedWorldToView" },
        [16] = new string?[] { "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld" },
        [17] = new string?[] { "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld" },
        [18] = new string?[] { "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld" },
        [19] = new string?[] { "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld", "ViewToTranslatedWorld" },
        [20] = new string?[] { "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView" },
        [21] = new string?[] { "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView" },
        [22] = new string?[] { "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView" },
        [23] = new string?[] { "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView", "TranslatedWorldToCameraView" },
        [24] = new string?[] { "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld" },
        [25] = new string?[] { "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld" },
        [26] = new string?[] { "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld" },
        [27] = new string?[] { "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld", "CameraViewToTranslatedWorld" },
        [28] = new string?[] { "ViewToClip", "ViewToClip", "ViewToClip", "ViewToClip" },
        [29] = new string?[] { "ViewToClip", "ViewToClip", "ViewToClip", "ViewToClip" },
        [30] = new string?[] { "ViewToClip", "ViewToClip", "ViewToClip", "ViewToClip" },
        [31] = new string?[] { "ViewToClip", "ViewToClip", "ViewToClip", "ViewToClip" },
        [32] = new string?[] { "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA" },
        [33] = new string?[] { "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA" },
        [34] = new string?[] { "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA" },
        [35] = new string?[] { "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA", "ViewToClipNoAA" },
        [36] = new string?[] { "ClipToView", "ClipToView", "ClipToView", "ClipToView" },
        [37] = new string?[] { "ClipToView", "ClipToView", "ClipToView", "ClipToView" },
        [38] = new string?[] { "ClipToView", "ClipToView", "ClipToView", "ClipToView" },
        [39] = new string?[] { "ClipToView", "ClipToView", "ClipToView", "ClipToView" },
        [40] = new string?[] { "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld" },
        [41] = new string?[] { "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld" },
        [42] = new string?[] { "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld" },
        [43] = new string?[] { "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld", "ClipToTranslatedWorld" },
        [44] = new string?[] { "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld" },
        [45] = new string?[] { "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld" },
        [46] = new string?[] { "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld" },
        [47] = new string?[] { "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld", "SVPositionToTranslatedWorld" },
        [48] = new string?[] { "ScreenToWorld", "ScreenToWorld", "ScreenToWorld", "ScreenToWorld" },
        [49] = new string?[] { "ScreenToWorld", "ScreenToWorld", "ScreenToWorld", "ScreenToWorld" },
        [50] = new string?[] { "ScreenToWorld", "ScreenToWorld", "ScreenToWorld", "ScreenToWorld" },
        [51] = new string?[] { "ScreenToWorld", "ScreenToWorld", "ScreenToWorld", "ScreenToWorld" },
        [52] = new string?[] { "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld" },
        [53] = new string?[] { "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld" },
        [54] = new string?[] { "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld" },
        [55] = new string?[] { "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld", "ScreenToTranslatedWorld" },
        [56] = new string?[] { "ViewForward", "ViewForward", "ViewForward", null },
        [57] = new string?[] { "ViewUp", "ViewUp", "ViewUp", null },
        [58] = new string?[] { "ViewRight", "ViewRight", "ViewRight", null },
        [59] = new string?[] { "HMDViewNoRollUp", "HMDViewNoRollUp", "HMDViewNoRollUp", null },
        [60] = new string?[] { "HMDViewNoRollRight", "HMDViewNoRollRight", "HMDViewNoRollRight", null },
        [61] = new string?[] { "InvDeviceZToWorldZTransform", "InvDeviceZToWorldZTransform", "InvDeviceZToWorldZTransform", "InvDeviceZToWorldZTransform" },
        [62] = new string?[] { "ScreenPositionScaleBias", "ScreenPositionScaleBias", "ScreenPositionScaleBias", "ScreenPositionScaleBias" },
        [63] = new string?[] { "WorldCameraOrigin", "WorldCameraOrigin", "WorldCameraOrigin", null },
        [64] = new string?[] { "TranslatedWorldCameraOrigin", "TranslatedWorldCameraOrigin", "TranslatedWorldCameraOrigin", null },
        [65] = new string?[] { "WorldViewOrigin", "WorldViewOrigin", "WorldViewOrigin", null },
        [66] = new string?[] { "PreViewTranslation", "PreViewTranslation", "PreViewTranslation", null },
        [67] = new string?[] { "PrevProjection", "PrevProjection", "PrevProjection", "PrevProjection" },
        [68] = new string?[] { "PrevProjection", "PrevProjection", "PrevProjection", "PrevProjection" },
        [69] = new string?[] { "PrevProjection", "PrevProjection", "PrevProjection", "PrevProjection" },
        [70] = new string?[] { "PrevProjection", "PrevProjection", "PrevProjection", "PrevProjection" },
        [71] = new string?[] { "PrevViewProj", "PrevViewProj", "PrevViewProj", "PrevViewProj" },
        [72] = new string?[] { "PrevViewProj", "PrevViewProj", "PrevViewProj", "PrevViewProj" },
        [73] = new string?[] { "PrevViewProj", "PrevViewProj", "PrevViewProj", "PrevViewProj" },
        [74] = new string?[] { "PrevViewProj", "PrevViewProj", "PrevViewProj", "PrevViewProj" },
        [75] = new string?[] { "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj" },
        [76] = new string?[] { "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj" },
        [77] = new string?[] { "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj" },
        [78] = new string?[] { "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj", "PrevViewRotationProj" },
        [79] = new string?[] { "PrevViewToClip", "PrevViewToClip", "PrevViewToClip", "PrevViewToClip" },
        [80] = new string?[] { "PrevViewToClip", "PrevViewToClip", "PrevViewToClip", "PrevViewToClip" },
        [81] = new string?[] { "PrevViewToClip", "PrevViewToClip", "PrevViewToClip", "PrevViewToClip" },
        [82] = new string?[] { "PrevViewToClip", "PrevViewToClip", "PrevViewToClip", "PrevViewToClip" },
        [83] = new string?[] { "PrevClipToView", "PrevClipToView", "PrevClipToView", "PrevClipToView" },
        [84] = new string?[] { "PrevClipToView", "PrevClipToView", "PrevClipToView", "PrevClipToView" },
        [85] = new string?[] { "PrevClipToView", "PrevClipToView", "PrevClipToView", "PrevClipToView" },
        [86] = new string?[] { "PrevClipToView", "PrevClipToView", "PrevClipToView", "PrevClipToView" },
        [87] = new string?[] { "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip" },
        [88] = new string?[] { "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip" },
        [89] = new string?[] { "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip" },
        [90] = new string?[] { "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip", "PrevTranslatedWorldToClip" },
        [91] = new string?[] { "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView" },
        [92] = new string?[] { "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView" },
        [93] = new string?[] { "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView" },
        [94] = new string?[] { "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView", "PrevTranslatedWorldToView" },
        [95] = new string?[] { "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld" },
        [96] = new string?[] { "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld" },
        [97] = new string?[] { "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld" },
        [98] = new string?[] { "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld", "PrevViewToTranslatedWorld" },
        [99] = new string?[] { "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView" },
        [100] = new string?[] { "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView" },
        [101] = new string?[] { "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView" },
        [102] = new string?[] { "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView", "PrevTranslatedWorldToCameraView" },
        [103] = new string?[] { "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld" },
        [104] = new string?[] { "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld" },
        [105] = new string?[] { "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld" },
        [106] = new string?[] { "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld", "PrevCameraViewToTranslatedWorld" },
        [107] = new string?[] { "PrevWorldCameraOrigin", "PrevWorldCameraOrigin", "PrevWorldCameraOrigin", null },
        [108] = new string?[] { "PrevWorldViewOrigin", "PrevWorldViewOrigin", "PrevWorldViewOrigin", null },
        [109] = new string?[] { "PrevPreViewTranslation", "PrevPreViewTranslation", "PrevPreViewTranslation", null },
        [110] = new string?[] { "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj" },
        [111] = new string?[] { "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj" },
        [112] = new string?[] { "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj" },
        [113] = new string?[] { "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj", "PrevInvViewProj" },
        [114] = new string?[] { "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld" },
        [115] = new string?[] { "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld" },
        [116] = new string?[] { "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld" },
        [117] = new string?[] { "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld", "PrevScreenToTranslatedWorld" },
        [118] = new string?[] { "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip" },
        [119] = new string?[] { "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip" },
        [120] = new string?[] { "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip" },
        [121] = new string?[] { "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip", "ClipToPrevClip" },
        [122] = new string?[] { "TemporalAAJitter", "TemporalAAJitter", "TemporalAAJitter", "TemporalAAJitter" },
        [123] = new string?[] { "GlobalClippingPlane", "GlobalClippingPlane", "GlobalClippingPlane", "GlobalClippingPlane" },
        [124] = new string?[] { "FieldOfViewWideAngles", "FieldOfViewWideAngles", "PrevFieldOfViewWideAngles", "PrevFieldOfViewWideAngles" },
        [125] = new string?[] { "ViewRectMin", "ViewRectMin", "ViewRectMin", "ViewRectMin" },
        [126] = new string?[] { "ViewSizeAndInvSize", "ViewSizeAndInvSize", "ViewSizeAndInvSize", "ViewSizeAndInvSize" },
        [127] = new string?[] { "BufferSizeAndInvSize", "BufferSizeAndInvSize", "BufferSizeAndInvSize", "BufferSizeAndInvSize" },
        [128] = new string?[] { "BufferBilinearUVMinMax", "BufferBilinearUVMinMax", "BufferBilinearUVMinMax", "BufferBilinearUVMinMax" },
        [129] = new string?[] { "NumSceneColorMSAASamples", "PreExposure", "OneOverPreExposure", null },
        [130] = new string?[] { "DiffuseOverrideParameter", "DiffuseOverrideParameter", "DiffuseOverrideParameter", "DiffuseOverrideParameter" },
        [131] = new string?[] { "SpecularOverrideParameter", "SpecularOverrideParameter", "SpecularOverrideParameter", "SpecularOverrideParameter" },
        [132] = new string?[] { "NormalOverrideParameter", "NormalOverrideParameter", "NormalOverrideParameter", "NormalOverrideParameter" },
        [133] = new string?[] { "RoughnessOverrideParameter", "RoughnessOverrideParameter", "PrevFrameGameTime", "PrevFrameRealTime" },
        [134] = new string?[] { "OutOfBoundsMask", null, null, null },
        [135] = new string?[] { "WorldCameraMovementSinceLastFrame", "WorldCameraMovementSinceLastFrame", "WorldCameraMovementSinceLastFrame", "CullingSign" },
        [136] = new string?[] { "NearPlane", "AdaptiveTessellationFactor", "GameTime", "RealTime" },
        [137] = new string?[] { "DeltaTime", "MaterialTextureMipBias", "MaterialTextureDerivativeMultiply", "Random" },
        [138] = new string?[] { "FrameNumber", "StateFrameIndexMod8", "StateFrameIndex", "CameraCut" },
        [139] = new string?[] { "UnlitViewmodeMask", null, null, null },
        [140] = new string?[] { "DirectionalLightColor", "DirectionalLightColor", "DirectionalLightColor", "DirectionalLightColor" },
        [141] = new string?[] { "DirectionalLightDirection", "DirectionalLightDirection", "DirectionalLightDirection", null },
        [142] = new string?[] { "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin" },
        [143] = new string?[] { "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin", "TranslucencyLightingVolumeMin" },
        [144] = new string?[] { "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize" },
        [145] = new string?[] { "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize", "TranslucencyLightingVolumeInvSize" },
        [146] = new string?[] { "TemporalAAParams", "TemporalAAParams", "TemporalAAParams", "TemporalAAParams" },
        [147] = new string?[] { "CircleDOFParams", "CircleDOFParams", "CircleDOFParams", "CircleDOFParams" },
        [148] = new string?[] { "DepthOfFieldSensorWidth", "DepthOfFieldFocalDistance", "DepthOfFieldScale", "DepthOfFieldFocalLength" },
        [149] = new string?[] { "DepthOfFieldFocalRegion", "DepthOfFieldNearTransitionRegion", "DepthOfFieldFarTransitionRegion", "MotionBlurNormalizedToPixel" },
        [150] = new string?[] { "bSubsurfacePostprocessEnabled", "GeneralPurposeTweak", "DemosaicVposOffset", null },
        [151] = new string?[] { "IndirectLightingColorScale", "IndirectLightingColorScale", "IndirectLightingColorScale", "HDR32bppEncodingMode" },
        [152] = new string?[] { "AtmosphericFogSunDirection", "AtmosphericFogSunDirection", "AtmosphericFogSunDirection", "AtmosphericFogSunPower" },
        [153] = new string?[] { "AtmosphericFogPower", "AtmosphericFogDensityScale", "AtmosphericFogDensityOffset", "AtmosphericFogGroundOffset" },
        [154] = new string?[] { "AtmosphericFogDistanceScale", "AtmosphericFogAltitudeScale", "AtmosphericFogHeightScaleRayleigh", "AtmosphericFogStartDistance" },
        [155] = new string?[] { "AtmosphericFogDistanceOffset", "AtmosphericFogSunDiscScale", "AtmosphericFogRenderMask", "AtmosphericFogInscatterAltitudeSampleNum" },
        [156] = new string?[] { "AtmosphericFogSunColor", "AtmosphericFogSunColor", "AtmosphericFogSunColor", "AtmosphericFogSunColor" },
        [157] = new string?[] { "NormalCurvatureToRoughnessScaleBias", "NormalCurvatureToRoughnessScaleBias", "NormalCurvatureToRoughnessScaleBias", "RenderingReflectionCaptureMask" },
        [158] = new string?[] { "AmbientCubemapTint", "AmbientCubemapTint", "AmbientCubemapTint", "AmbientCubemapTint" },
        [159] = new string?[] { "AmbientCubemapIntensity", "SkyLightParameters", null, null },
        [160] = new string?[] { "SkyLightColor", "SkyLightColor", "SkyLightColor", "SkyLightColor" },
        [161] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [162] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [163] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [164] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [165] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [166] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [167] = new string?[] { "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap", "SkyIrradianceEnvironmentMap" },
        [168] = new string?[] { "MobilePreviewMode", "HMDEyePaddingOffset", "ReflectionCubemapMaxMip", "ShowDecalsMask" },
        [169] = new string?[] { "DistanceFieldAOSpecularOcclusionMode", "IndirectCapsuleSelfShadowingIntensity", null, null },
        [170] = new string?[] { "ReflectionEnvironmentRoughnessMixingScaleBiasAndLargestWeight", "ReflectionEnvironmentRoughnessMixingScaleBiasAndLargestWeight", "ReflectionEnvironmentRoughnessMixingScaleBiasAndLargestWeight", "StereoPassIndex" },
    };

    public static readonly Dictionary<int, string?[]> PrimitiveRows = new()
    {
        [0] = new string?[] { "LocalToWorld", "LocalToWorld", "LocalToWorld", "LocalToWorld" },
        [1] = new string?[] { "LocalToWorld", "LocalToWorld", "LocalToWorld", "LocalToWorld" },
        [2] = new string?[] { "LocalToWorld", "LocalToWorld", "LocalToWorld", "LocalToWorld" },
        [3] = new string?[] { "LocalToWorld", "LocalToWorld", "LocalToWorld", "LocalToWorld" },
        [4] = new string?[] { "InvNonUniformScaleAndDeterminantSign", "InvNonUniformScaleAndDeterminantSign", "InvNonUniformScaleAndDeterminantSign", "InvNonUniformScaleAndDeterminantSign" },
        [5] = new string?[] { "ObjectWorldPositionAndRadius", "ObjectWorldPositionAndRadius", "ObjectWorldPositionAndRadius", "ObjectWorldPositionAndRadius" },
        [6] = new string?[] { "WorldToLocal", "WorldToLocal", "WorldToLocal", "WorldToLocal" },
        [7] = new string?[] { "WorldToLocal", "WorldToLocal", "WorldToLocal", "WorldToLocal" },
        [8] = new string?[] { "WorldToLocal", "WorldToLocal", "WorldToLocal", "WorldToLocal" },
        [9] = new string?[] { "WorldToLocal", "WorldToLocal", "WorldToLocal", "WorldToLocal" },
        [10] = new string?[] { "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld" },
        [11] = new string?[] { "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld" },
        [12] = new string?[] { "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld" },
        [13] = new string?[] { "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld", "PreviousLocalToWorld" },
        [14] = new string?[] { "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal" },
        [15] = new string?[] { "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal" },
        [16] = new string?[] { "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal" },
        [17] = new string?[] { "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal", "PreviousWorldToLocal" },
        [18] = new string?[] { "ActorWorldPosition", "ActorWorldPosition", "ActorWorldPosition", "UseSingleSampleShadowFromStationaryLights" },
        [19] = new string?[] { "ObjectBounds", "ObjectBounds", "ObjectBounds", "LpvBiasMultiplier" },
        [20] = new string?[] { "DecalReceiverMask", "PerObjectGBufferData", "UseVolumetricLightmapShadowFromStationaryLights", "UseEditorDepthTest" },
        [21] = new string?[] { "ObjectOrientation", "ObjectOrientation", "ObjectOrientation", "ObjectOrientation" },
        [22] = new string?[] { "NonUniformScale", "NonUniformScale", "NonUniformScale", "NonUniformScale" },
        [23] = new string?[] { "LocalObjectBoundsMin", "LocalObjectBoundsMin", "LocalObjectBoundsMin", null },
        [24] = new string?[] { "LocalObjectBoundsMax", "LocalObjectBoundsMax", "LocalObjectBoundsMax", "LightingChannelMask" },
        [25] = new string?[] { "LightmapDataIndex", "SingleCaptureIndex", null, null },
    };

    public static string? Resolve(string bufferName, int row, int component) => bufferName switch
    {
        "FViewUniformShaderParameters" => ViewRows.TryGetValue(row, out var v) ? v[component] : null,
        "FPrimitiveUniformShaderParameters" => PrimitiveRows.TryGetValue(row, out var p) ? p[component] : null,
        _ => null,
    };
}
