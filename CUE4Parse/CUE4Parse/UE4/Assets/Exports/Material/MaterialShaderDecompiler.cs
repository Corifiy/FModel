using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.Material;

/// <summary>
/// Reconstructs C++-style pseudocode from a cooked <see cref="UMaterialInterface"/>'s uniform
/// expression trees (<see cref="FMaterialUniformExpressionLegacy"/>, see LegacyShaderMap.cs).
/// Scope: only the pre-4.25 "legacy" shader map format is handled, because it serializes a real
/// symbolic FMaterialUniformExpression tree. UE 4.25+ replaced that with a stack-based
/// "preshader" bytecode (FMaterialPreshaderData) that isn't decoded into a tree today, so those
/// resources are reported as unsupported instead of guessed at.
///
/// The uniform expression tree only covers the CPU-folded part of a material (parameters and
/// constant math that don't depend on a texture sample or per-pixel/vertex input) — it has no
/// serialized link back to a named material property such as EmissiveColor or Opacity, so this
/// prints a flat, indexed list of every UniformVectorExpressions/UniformScalarExpressions entry
/// rather than inventing an attribution the cooked data doesn't actually contain.
/// </summary>
public static class MaterialShaderDecompiler
{
    /// <summary>
    /// A material instance's own parameter-value overrides (Vector/Scalar/TextureParameterValues),
    /// collected once per decompile and consulted whenever a VectorParameter/ScalarParameter/
    /// TextureParameter node is printed, so the output reflects what this specific instance actually
    /// renders with instead of only the base material's compile-time default. Mirrors
    /// UMaterialInstance::GetVectorParameterValue's own lookup order (MaterialInstance.cpp): the
    /// queried instance's own value wins, only falling through to Parent->GetVectorParameterValue(...)
    /// when it has none - so walking from the queried material up to its base and keeping the first
    /// (closest-to-leaf) value per parameter name reproduces that exactly, not just approximates it.
    /// </summary>
    public sealed class InstanceParameterOverrides
    {
        public static readonly InstanceParameterOverrides Empty = new()
        {
            Vectors = new Dictionary<string, FLinearColor>(),
            Scalars = new Dictionary<string, float>(),
            Textures = new Dictionary<string, UTexture?>(),
        };

        public required IReadOnlyDictionary<string, FLinearColor> Vectors { get; init; }
        public required IReadOnlyDictionary<string, float> Scalars { get; init; }
        public required IReadOnlyDictionary<string, UTexture?> Textures { get; init; }

        public static InstanceParameterOverrides Build(UMaterialInterface material)
        {
            var vectors = new Dictionary<string, FLinearColor>();
            var scalars = new Dictionary<string, float>();
            var textures = new Dictionary<string, UTexture?>();

            UMaterialInterface? current = material;
            var guard = 0;
            while (current is UMaterialInstance instance && ++guard < 16)
            {
                if (instance is UMaterialInstanceConstant constant)
                {
                    foreach (var v in constant.VectorParameterValues)
                        if (v.ParameterValue is { } value) vectors.TryAdd(v.Name, value);
                    foreach (var s in constant.ScalarParameterValues)
                        scalars.TryAdd(s.Name, s.ParameterValue);
                    foreach (var t in constant.TextureParameterValues)
                        textures.TryAdd(t.Name, t.ParameterValue.ResolvedObject?.Object?.Value as UTexture);
                }
                current = instance.Parent as UMaterialInterface;
            }

            if (vectors.Count == 0 && scalars.Count == 0 && textures.Count == 0) return Empty;
            return new InstanceParameterOverrides { Vectors = vectors, Scalars = scalars, Textures = textures };
        }
    }

    /// <summary>
    /// Public entry point for printing a single uniform expression node's evaluated form, reused
    /// by the pixel-shader disassembler (FModel/ViewModels/PixelShaderDecompiler.cs) to resolve a
    /// "Vector/Scalar Expression [N]" constant-buffer read back into the actual expression instead
    /// of a bare index. <paramref name="referencedTextures"/>, when supplied, resolves a hard
    /// (non-parameter) texture reference's TextureIndex into the actual asset name instead of a
    /// bare index - see <see cref="GetReferencedTextures"/>. <paramref name="overrides"/>, when
    /// supplied, annotates a VectorParameter/ScalarParameter/TextureParameter node with the actual
    /// value this specific material instance uses, if it differs from the base material's default.
    /// </summary>
    public static string PrintExpression(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures = null, InstanceParameterOverrides? overrides = null)
        => Print(expr, referencedTextures, overrides);

    /// <summary>
    /// FMaterialUniformExpressionTexture::TextureIndex is documented in the engine (MaterialShared.h)
    /// as "Index into FMaterial::GetReferencedTextures". For UE4.25+ cooks that list is serialized
    /// directly (CachedExpressionData) and CUE4Parse already exposes it as UMaterial.ReferencedTextures.
    ///
    /// Pre-4.25 games (this project's target, GAME_UE4_23) never serialize that list at all -
    /// FMaterialResource::GetReferencedTextures (MaterialShared.cpp) returns Material-&gt;ExpressionTextureReferences,
    /// a transient runtime cache UMaterial::CacheExpressionTextureReferences rebuilds on load by
    /// calling UMaterial::AppendReferencedTextures (Material.cpp ~5470), which walks the material's
    /// own Expressions array. That walk is reproduced here exactly: it only appends a slot for
    /// UMaterialExpressionTextureBase-derived nodes (TextureSample, TextureSampleParameter, ...),
    /// and appends the slot even when the resolved texture is null - the engine's own comment is
    /// "Append even if null as textures can be stripped at cook without our knowledge so we want to
    /// maintain the indices" - which is exactly why this stays correct even though input wiring
    /// (which node feeds which) is stripped in this cook: the Texture property and node order
    /// survive independently of the wiring. Nested MaterialFunctionCall texture references are not
    /// walked into (a materially bigger addition); a material relying on one for its texture
    /// indices will have later slots misaligned - accepted as a known gap rather than guessed at,
    /// and never surfaces silently: DescribeHardTexture falls back to a bare index whenever
    /// resolution comes up empty instead of printing a wrong name.
    /// </summary>
    public static IReadOnlyList<UTexture?>? GetReferencedTextures(UMaterialInterface material)
    {
        UMaterialInterface current = material;
        var guard = 0;
        while (current is UMaterialInstance instance && instance.Parent is UMaterialInterface parent && ++guard < 16)
            current = parent;
        if (current is not UMaterial baseMaterial) return null;

        if (baseMaterial.ReferencedTextures.Count > 0)
            return baseMaterial.ReferencedTextures;

        var rebuilt = new List<UTexture?>();
        foreach (var index in baseMaterial.Expressions)
        {
            if (index?.ResolvedObject?.Object?.Value is not UMaterialExpressionTextureBase textureExpr) continue;
            rebuilt.Add(textureExpr.Texture);
        }
        return rebuilt;
    }

    public static string? DecompileShaderToPseudo(this UMaterialInterface material)
    {
        if (material.LoadedMaterialResources is not { Count: > 0 } resources)
            return null;

        var referencedTextures = GetReferencedTextures(material);
        var overrides = InstanceParameterOverrides.Build(material);
        var sb = new StringBuilder();
        var wroteAny = false;
        foreach (var resource in resources)
        {
            if (wroteAny) sb.AppendLine().AppendLine();

            if (resource.LoadedShaderMapLegacy is { } legacy)
            {
                AppendLegacyResource(sb, material.Name, legacy, referencedTextures, overrides);
                wroteAny = true;
            }
            else if (resource.LoadedShaderMap is { } modern)
            {
                AppendPreshaderResource(sb, material, modern, referencedTextures, overrides);
                wroteAny = true;
            }
        }

        return wroteAny ? sb.ToString() : null;
    }

    private static void AppendLegacyResource(StringBuilder sb, string materialName, FMaterialShaderMapLegacy shaderMap, IReadOnlyList<UTexture?>? referencedTextures, InstanceParameterOverrides overrides)
    {
        var id = shaderMap.ShaderMapId;
        sb.Append("// ").Append(materialName)
            .Append(" | ").Append(shaderMap.ShaderPlatform)
            .Append(" | Quality=").Append(id.QualityLevel)
            .Append(" FeatureLevel=").Append(id.FeatureLevel).AppendLine();
        sb.AppendLine();

        var expressionSet = shaderMap.MaterialCompilationOutput.UniformExpressionSet;
        var wroteVector = AppendExpressionArray(sb, "Uniform Vector Expressions", "float4", "UniformVector", expressionSet.UniformVectorExpressions, referencedTextures, overrides);
        var wroteScalar = AppendExpressionArray(sb, "Uniform Scalar Expressions", "float", "UniformScalar", expressionSet.UniformScalarExpressions, referencedTextures, overrides);

        if (!wroteVector && !wroteScalar)
        {
            sb.Append("// (no uniform expressions - this permutation has no CPU-evaluated parameters or math)");
        }
    }

    /// <summary>
    /// The 4.25+ equivalent of <see cref="AppendLegacyResource"/>: the CPU-folded values are preshader
    /// opcode ranges rather than an expression tree (see <see cref="MaterialPreshaderDecompiler"/>),
    /// and the constant buffer is laid out by FUniformExpressionSet::CreateBufferStruct as the vector
    /// preshaders first, then the scalar ones packed four per float4 - so each printed row is named by
    /// the register it actually occupies, which is what the pixel-shader layer's cb reads refer to.
    /// </summary>
    private static void AppendPreshaderResource(StringBuilder sb, UMaterialInterface material, FMaterialShaderMap shaderMap, IReadOnlyList<UTexture?>? referencedTextures, InstanceParameterOverrides overrides)
    {
        var id = shaderMap.ShaderMapId;
        sb.Append("// ").Append(material.Name)
            .Append(" | ").Append(shaderMap.ShaderPlatform)
            .Append(" | Quality=").Append(id.QualityLevel)
            .Append(" FeatureLevel=").Append(id.FeatureLevel).AppendLine();
        sb.AppendLine();

        if (shaderMap.Content is not FMaterialShaderMapContent { MaterialCompilationOutput.UniformExpressionSet: { } expressionSet })
        {
            sb.Append("// (no uniform expression set in this shader map)");
            return;
        }

        var game = material.Owner?.Provider?.Versions.Game ?? EGame.GAME_UE4_LATEST;
        if (!MaterialPreshaderDecompiler.IsSupported(game))
        {
            sb.Append("// ").Append(material.Name)
                .Append(": this engine version's preshader encoding (")
                .Append(game)
                .Append(") is not decoded - only UE 4.26/4.27 preshaders are.");
            return;
        }

        var wroteAny = false;
        wroteAny |= AppendPreshaderArray(sb, "Uniform Vector Expressions", "float4", "UniformVector",
            expressionSet.UniformVectorPreshaders, expressionSet, game, referencedTextures, overrides);
        wroteAny |= AppendPreshaderArray(sb, "Uniform Scalar Expressions", "float", "UniformScalar",
            expressionSet.UniformScalarPreshaders, expressionSet, game, referencedTextures, overrides);
        wroteAny |= AppendTextureParameters(sb, expressionSet, referencedTextures, overrides);

        if (!wroteAny)
            sb.Append("// (no uniform expressions - this permutation has no CPU-evaluated parameters or math)");
    }

    private static bool AppendPreshaderArray(StringBuilder sb, string title, string type, string prefix,
        FMaterialUniformPreshaderHeader[]? preshaders, FUniformExpressionSet expressionSet, EGame game,
        IReadOnlyList<UTexture?>? referencedTextures, InstanceParameterOverrides overrides)
    {
        if (preshaders is not { Length: > 0 }) return false;

        sb.Append("// ").AppendLine(title);
        for (var i = 0; i < preshaders.Length; i++)
        {
            var expression = MaterialPreshaderDecompiler.Decompile(expressionSet, preshaders[i], game, referencedTextures, overrides);
            sb.Append(type).Append(' ').Append(prefix).Append(i).Append(" = ")
                .Append(expression ?? "/* preshader opcodes could not be decoded */ 0").AppendLine(";");
        }
        sb.AppendLine();
        return true;
    }

    /// <summary>
    /// Texture parameters are not preshaders - they are serialized directly, with their real name and
    /// their index into the material's referenced-texture list - so they are listed as-is rather than
    /// decoded. Slot order is EMaterialTextureParameterType (Standard2D, Cube, Array2D, Volume,
    /// Virtual), matching FUniformExpressionSet::CreateBufferStruct's own binding order.
    /// </summary>
    private static bool AppendTextureParameters(StringBuilder sb, FUniformExpressionSet expressionSet, IReadOnlyList<UTexture?>? referencedTextures)
    {
        string[] slotNames = ["Texture2D", "TextureCube", "Texture2DArray", "VolumeTexture", "VirtualTexture"];
        var wroteAny = false;
        for (var slot = 0; slot < (expressionSet.UniformTextureParameters?.Length ?? 0); slot++)
        {
            var parameters = expressionSet.UniformTextureParameters[slot];
            if (parameters is not { Length: > 0 }) continue;

            if (!wroteAny) sb.AppendLine("// Uniform Texture Parameters");
            wroteAny = true;
            var slotName = slot < slotNames.Length ? slotNames[slot] : $"TextureSlot{slot}";
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var name = MaterialPreshaderDecompiler.GetParameterName(parameter);
                var asset = referencedTextures is { } textures && parameter.TextureIndex >= 0 && parameter.TextureIndex < textures.Count
                    ? textures[parameter.TextureIndex]?.Name
                    : null;
                sb.Append(slotName).Append(' ').Append(slotName).Append(i).Append(" = ");
                if (name != null) sb.Append("TextureParameter'").Append(name).Append('\'');
                else if (asset != null) sb.Append("Texture'").Append(asset).Append('\'');
                else sb.Append("Texture[").Append(parameter.TextureIndex).Append(']');
                if (name != null && asset != null) sb.Append(" /* ").Append(asset).Append(" */");
                sb.Append("; // ").Append(parameter.SamplerSource).AppendLine();
            }
        }
        if (wroteAny) sb.AppendLine();
        return wroteAny;
    }

    private static bool AppendExpressionArray(StringBuilder sb, string title, string type, string prefix, FMaterialUniformExpressionLegacy[] expressions, IReadOnlyList<UTexture?>? referencedTextures, InstanceParameterOverrides overrides)
    {
        if (expressions.Length == 0) return false;

        sb.Append("// ").AppendLine(title);
        for (var i = 0; i < expressions.Length; i++)
        {
            var expr = expressions[i];
            sb.Append(type).Append(' ').Append(prefix).Append(i)
                .Append(" = ").Append(Print(expr, referencedTextures, overrides)).Append("; // ").AppendLine(RoleComment(expr));
        }
        sb.AppendLine();
        return true;
    }

    private static string RoleComment(FMaterialUniformExpressionLegacy expr) => expr.TypeName switch
    {
        "FMaterialUniformExpressionConstant" => "Constant",
        "FMaterialUniformExpressionVectorParameter" => $"VectorParameter '{expr.ParameterName}', default {GetValue(expr, "Default")}",
        "FMaterialUniformExpressionScalarParameter" => $"ScalarParameter '{expr.ParameterName}', default {GetValue(expr, "Default")}",
        _ => expr.OpName ?? expr.TypeName.Replace("FMaterialUniformExpression", ""),
    };

    /// <summary>
    /// Prints one node's evaluated expression, matching the exact call/operand order each
    /// FMaterialUniformExpression subclass uses in its own GetNumberValue (MaterialUniformExpressions.h),
    /// not a reinterpretation of it. Only the outermost declaration (see AppendExpressionArray) is
    /// aware of whether the surrounding array is Vector- or Scalar-typed; nested operands are typed
    /// entirely by their own node kind (a VectorParameter is always a vector, a Dot is always a
    /// scalar result, etc.), so no type is guessed here.
    /// </summary>
    private static string Print(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures, InstanceParameterOverrides? overrides = null)
    {
        string Operand(string slot)
        {
            foreach (var (key, value) in expr.Operands)
            {
                if (key == slot) return Print(value, referencedTextures, overrides);
            }
            return "/* missing operand */ 0";
        }

        switch (expr.TypeName)
        {
            case "FMaterialUniformExpressionConstant":
                return FormatColor(expr.ConstantValue);
            case "FMaterialUniformExpressionTime":
                return "Time";
            case "FMaterialUniformExpressionRealTime":
                return "RealTime";
            case "FMaterialUniformExpressionVectorParameter":
            {
                var name = SanitizeIdentifier(expr.ParameterName);
                if (!string.IsNullOrEmpty(expr.ParameterName) && overrides != null && overrides.Vectors.TryGetValue(expr.ParameterName, out var v))
                    return $"{name} [instance: {FormatColor(v)}]";
                return name;
            }
            case "FMaterialUniformExpressionScalarParameter":
            {
                var name = SanitizeIdentifier(expr.ParameterName);
                if (!string.IsNullOrEmpty(expr.ParameterName) && overrides != null && overrides.Scalars.TryGetValue(expr.ParameterName, out var s))
                    return $"{name} [instance: {Fmt(s)}]";
                return name;
            }
            case "FMaterialUniformExpressionTexture":
            case "FMaterialUniformExpressionFlipBookTextureParameter":
                return DescribeHardTexture(expr, referencedTextures);
            case "FMaterialUniformExpressionTextureParameter":
            {
                if (string.IsNullOrEmpty(expr.ParameterName)) return DescribeHardTexture(expr, referencedTextures);
                var name = SanitizeIdentifier(expr.ParameterName);
                if (overrides != null && overrides.Textures.TryGetValue(expr.ParameterName, out var tex) && tex != null)
                    return $"{name} [instance: {SanitizeIdentifier(tex.Name)}]";
                return name;
            }
            case "FMaterialUniformExpressionExternalTextureBase":
            case "FMaterialUniformExpressionExternalTexture":
                return ExternalTextureRef(expr);
            case "FMaterialUniformExpressionExternalTextureParameter":
                return ExternalTextureRef(expr);
            case "FMaterialUniformExpressionExternalTextureCoordinateScaleRotation":
                return $"{ExternalTextureRef(expr)}.CoordinateScaleRotation";
            case "FMaterialUniformExpressionExternalTextureCoordinateOffset":
                return $"{ExternalTextureRef(expr)}.CoordinateOffset";
            case "FMaterialUniformExpressionRuntimeVirtualTextureParameter":
                return $"VirtualTexture[{expr.TextureIndex}]";

            case "FMaterialUniformExpressionSine":
                return expr.OpName == "Cos" ? $"cos({Operand("X")})" : $"sin({Operand("X")})";
            case "FMaterialUniformExpressionTrigMath":
                return expr.OpName switch
                {
                    "Sin" => $"sin({Operand("X")})",
                    "Cos" => $"cos({Operand("X")})",
                    "Tan" => $"tan({Operand("X")})",
                    "Asin" => $"asin({Operand("X")})",
                    "Acos" => $"acos({Operand("X")})",
                    "Atan" => $"atan({Operand("X")})",
                    // Matches the engine's own FMath::Atan2(ValueX, ValueY) call order exactly (see
                    // MaterialUniformExpressions.h TMO_Atan2) - printed as evaluated, not "corrected".
                    "Atan2" => $"atan2({Operand("X")}, {Operand("Y")})",
                    _ => $"/* {expr.OpName} */({Operand("X")})",
                };
            case "FMaterialUniformExpressionSquareRoot":
                return $"sqrt({Operand("X")})";
            case "FMaterialUniformExpressionLength":
                return $"length({Operand("X")})";
            case "FMaterialUniformExpressionLogarithm2":
                return $"log2({Operand("X")})";
            case "FMaterialUniformExpressionLogarithm10":
                return $"log10({Operand("X")})";

            case "FMaterialUniformExpressionFoldedMath":
                return expr.OpName switch
                {
                    "Add" => $"({Operand("A")} + {Operand("B")})",
                    "Sub" => $"({Operand("A")} - {Operand("B")})",
                    "Mul" => $"({Operand("A")} * {Operand("B")})",
                    "Div" => $"({Operand("A")} / {Operand("B")})",
                    "Dot" => $"dot({Operand("A")}, {Operand("B")})",
                    "Cross" => $"cross({Operand("A")}, {Operand("B")})",
                    _ => $"/* {expr.OpName} */({Operand("A")}, {Operand("B")})",
                };
            // FMath::Fractional() is truncation-based (Value - TruncToFloat(Value)), which differs
            // from HLSL's floor-based frac() for negative inputs - deliberately not named "frac"
            // here so it isn't mistaken for FMaterialUniformExpressionFrac below.
            case "FMaterialUniformExpressionPeriodic":
                return $"fractional({Operand("X")})";
            case "FMaterialUniformExpressionAppendVector":
                return $"append({Operand("A")}, {Operand("B")}) /* firstN={GetValue(expr, "NumComponentsA")} */";
            case "FMaterialUniformExpressionMin":
                return $"min({Operand("A")}, {Operand("B")})";
            case "FMaterialUniformExpressionMax":
                return $"max({Operand("A")}, {Operand("B")})";
            case "FMaterialUniformExpressionClamp":
                return $"clamp({Operand("Input")}, {Operand("Min")}, {Operand("Max")})";
            case "FMaterialUniformExpressionSaturate":
                return $"saturate({Operand("Input")})";
            case "FMaterialUniformExpressionComponentSwizzle":
                return $"{Operand("X")}.{GetValue(expr, "Swizzle")}";
            case "FMaterialUniformExpressionFloor":
                return $"floor({Operand("X")})";
            case "FMaterialUniformExpressionCeil":
                return $"ceil({Operand("X")})";
            case "FMaterialUniformExpressionRound":
                return $"round({Operand("X")})";
            case "FMaterialUniformExpressionTruncate":
                return $"trunc({Operand("X")})";
            case "FMaterialUniformExpressionSign":
                return $"sign({Operand("X")})";
            // Floor-based (Value - FloorToInt(Value)) - matches HLSL frac() semantics.
            case "FMaterialUniformExpressionFrac":
                return $"frac({Operand("X")})";
            case "FMaterialUniformExpressionFmod":
                return $"fmod({Operand("A")}, {Operand("B")})";
            case "FMaterialUniformExpressionAbs":
                return $"abs({Operand("X")})";
            case "FMaterialUniformExpressionTextureProperty":
                return GetValue(expr, "Property") == "TexelSize"
                    ? $"texel_size({Operand("Texture")})"
                    : $"texture_size({Operand("Texture")})";

            default:
                // Unrecognized node type: flagged visibly instead of throwing or guessing, so one
                // unknown node doesn't blank out the rest of the decompile.
                return $"/* unsupported: {expr.TypeName} */ 0";
        }
    }

    private static string ExternalTextureRef(FMaterialUniformExpressionLegacy expr)
        => !string.IsNullOrEmpty(expr.ParameterName) ? SanitizeIdentifier(expr.ParameterName) : $"ExternalTexture[{expr.TextureIndex}]";

    /// <summary>
    /// Same resolution <see cref="DescribeHardTexture"/> and the texture-parameter case of
    /// <see cref="Print"/> use, but returning just the bare identifier (parameter name, or the
    /// resolved texture asset's name) with none of the "[Texture N]" display styling - for callers
    /// that want to build their own identifier from it, e.g. naming a hoisted pseudocode variable
    /// after the texture it samples (see PixelShaderDecompiler.cs). Returns null rather than a
    /// fallback placeholder when nothing resolves, so the caller can fall back to its own naming.
    /// </summary>
    public static string? TryResolveTextureIdentifier(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures)
    {
        if (!string.IsNullOrEmpty(expr.ParameterName)) return SanitizeIdentifier(expr.ParameterName);
        if (referencedTextures != null && expr.TextureIndex >= 0 && expr.TextureIndex < referencedTextures.Count
            && referencedTextures[expr.TextureIndex] is { } texture)
            return SanitizeIdentifier(texture.Name);
        return null;
    }

    /// <summary>
    /// A "hard" (non-parameter) texture reference only carries a TextureIndex - "Index into
    /// FMaterial::GetReferencedTextures" per the engine (MaterialShared.h). When the caller
    /// supplied that list (see GetReferencedTextures), resolves to the actual asset name with the
    /// raw index kept alongside for verification; falls back to a bare index otherwise rather than
    /// guessing.
    /// </summary>
    private static string DescribeHardTexture(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures)
    {
        if (referencedTextures != null && expr.TextureIndex >= 0 && expr.TextureIndex < referencedTextures.Count
            && referencedTextures[expr.TextureIndex] is { } texture)
        {
            // Square brackets, not a /* */ comment: callers (e.g. MaterialPixelShaderAnalyzer's
            // "sample" node printer in PixelShaderDecompiler.cs) wrap this whole result in its own
            // comment, and nested /* */ isn't valid C - this keeps the raw index visible for
            // verification without producing "/* Name /* Texture[0] */ */".
            return $"{SanitizeIdentifier(texture.Name)} [Texture {expr.TextureIndex}]";
        }
        return $"Texture[{expr.TextureIndex}]";
    }

    private static string? GetValue(FMaterialUniformExpressionLegacy expr, string key)
    {
        foreach (var (k, v) in expr.Values)
        {
            if (k == key) return v;
        }
        return null;
    }

    private static string FormatColor(FLinearColor? color)
    {
        if (color is not { } c) return "Color(0, 0, 0, 0)";
        return $"Color({Fmt(c.R)}, {Fmt(c.G)}, {Fmt(c.B)}, {Fmt(c.A)})";
    }

    private static string Fmt(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "Param";

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}
