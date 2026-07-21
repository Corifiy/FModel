using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;

namespace FModel.ViewModels;

/// <summary>
/// Reconstructs per-output-pin C++-style pseudocode ("Emissive Color = ...;") from the actual
/// compiled DXBC pixel shader of a pre-4.25 cooked material, using <see cref="MaterialPixelShaderAnalyzer"/>
/// (ported from a sibling project, see FModel/ViewModels/MaterialPixelShaderAnalyzer.cs and MaterialDxil.cs)
/// to decode the bytecode into a per-pin expression DAG, and <see cref="MaterialShaderDecompiler"/>
/// to resolve constant-buffer reads back into the actual uniform expression tree they carry.
///
/// This is the layer <see cref="MaterialShaderDecompiler.DecompileShaderToPseudo"/> cannot reach:
/// that one only covers the CPU-folded uniform expressions (parameters/constant math with no
/// texture or UV dependency). Everything else - the real per-pixel authored graph math (Abs, Add,
/// Clamp, DotProduct, texture coordinate arithmetic, ...) - only exists as compiled DXBC bytecode,
/// usually stored outside the material's own package in a shared shader code library
/// (.ushaderbytecode). This file locates that library, pulls the shader by its OutputHash, and
/// turns the decoded instruction DAG into pseudocode.
/// </summary>
public static class PixelShaderDecompiler
{
    // shared shader code libraries (.ushaderbytecode) parsed once per provider; loaded lazily
    // because only shared-library games (e.g. Fortnite-era cooks) need them, and kept weakly so
    // closing the archive releases the bytecode. Ported from MaterialGraphViewModel.cs.
    private static readonly ConditionalWeakTable<IFileProvider, List<FLegacyShaderCodeArchive>> LegacyShaderLibraries = new();

    /// <summary>
    /// Runs the same analysis as <see cref="DecompilePixelShaderToPseudo"/> but returns the raw
    /// <see cref="PixelShaderWiring"/> (and the expression set needed to resolve its cbrow leaves)
    /// instead of pretty-printed text, for tooling that wants to inspect the DAG directly
    /// (see ShaderDecompileTest) rather than trust the printer's output.
    /// </summary>
    public static (PixelShaderWiring Wiring, FUniformExpressionSetLegacy ExpressionSet)? AnalyzeForDiagnostics(UMaterialInterface material)
        => AnalyzeForDiagnostics(material, FindLegacyShaderMap(BuildInstanceChain(material), out _));

    /// <summary>
    /// Same as the single-argument overload but against an explicitly chosen shader map - a
    /// material can carry more than one <see cref="LoadedMaterialResources"/> entry (one per
    /// quality level), each with its OWN independently-indexed uniform expression arrays, and the
    /// single-argument overload only ever analyzes the first one found. Tooling that needs to
    /// cross-check a specific quality level (e.g. ShaderDecompileTest) should pass it explicitly.
    /// </summary>
    public static (PixelShaderWiring Wiring, FUniformExpressionSetLegacy ExpressionSet)? AnalyzeForDiagnostics(UMaterialInterface material, FMaterialShaderMapLegacy? shaderMap)
    {
        if (shaderMap?.MaterialCompilationOutput?.UniformExpressionSet is not { } expressionSet)
            return null;

        var chain = BuildInstanceChain(material);
        var parameters = new CMaterialParams2();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            try { chain[i].GetParams(parameters, EMaterialFormat.AllLayers); }
            catch { /* best-effort - only used to pick the GBuffer/forward pin layout */ }
        }
        var usesGBuffer = parameters.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked;

        var wiring = MaterialPixelShaderAnalyzer.AnalyzeLegacy(shaderMap, usesGBuffer, CreateLegacyShaderCodeResolver(chain));
        return (wiring, expressionSet);
    }

    /// <summary>
    /// A material can carry more than one <see cref="UMaterialInterface.LoadedMaterialResources"/>
    /// entry, one per quality level, and different quality levels can compile meaningfully
    /// different HLSL (whole branches - e.g. a rim-light refinement - can be statically stripped
    /// at lower quality). Each is independently indexed, so all of them are decompiled separately
    /// rather than only the first one found.
    /// </summary>
    public static string? DecompilePixelShaderToPseudo(UMaterialInterface material)
    {
        var chain = BuildInstanceChain(material);
        var resources = chain.SelectMany(m => m.LoadedMaterialResources ?? [])
            .Where(r => r.LoadedShaderMapLegacy != null)
            .ToList();
        if (resources.Count == 0) return null;

        var sections = resources
            .Select(r => DecompileOneResource(material, r.LoadedShaderMapLegacy!))
            .Where(s => !string.IsNullOrEmpty(s));
        var combined = string.Join("\n\n", sections);
        return string.IsNullOrEmpty(combined) ? null : combined;
    }

    private static string? DecompileOneResource(UMaterialInterface material, FMaterialShaderMapLegacy shaderMap)
    {
        if (AnalyzeForDiagnostics(material, shaderMap) is not { } diagnostics)
            return null;
        var (wiring, expressionSet) = diagnostics;

        var id = shaderMap.ShaderMapId;
        var header = $"// {material.Name} | {shaderMap.ShaderPlatform} | Quality={id.QualityLevel} FeatureLevel={id.FeatureLevel}";

        if (!wiring.Success)
            return $"{header}\n// pixel shader analysis failed - {wiring.FailureReason}";

        var sb = new StringBuilder();
        sb.Append(header).Append(" | ").Append(wiring.ShaderTypeName)
            .Append(" | reconstructed from the compiled DXBC pixel shader").AppendLine();
        sb.AppendLine();

        var orderedPins = wiring.PinSources.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();

        // The expression DAG hash-conses repeated subtrees (the same constant-buffer read or the
        // same sub-computation can be reached from many places, e.g. a shared UV computation feeding
        // several output pins). Printing every reference in full would blow up combinatorially, so
        // every node reached more than once across the whole shader is hoisted into a named
        // declaration up front, in dependency order, and every other reference to it becomes just
        // that name - this is a straightforward CSE pass over the DAG, not a rewrite of it.
        var ctx = new PrintCtx(material, expressionSet, MaterialShaderDecompiler.GetReferencedTextures(material));
        var recursed = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
        foreach (var pin in orderedPins)
            if (wiring.PinExpressions.TryGetValue(pin, out var root))
                CountRefs(root, ctx, recursed);

        var named = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
        foreach (var pin in orderedPins)
            if (wiring.PinExpressions.TryGetValue(pin, out var root))
                AssignNames(root, ctx, named);

        if (ctx.Declarations.Count > 0)
        {
            sb.Append("// Shared subexpressions (referenced more than once below)").AppendLine();
            foreach (var decl in ctx.Declarations)
                sb.AppendLine(decl);
            sb.AppendLine();
        }

        foreach (var pin in orderedPins)
        {
            var identifier = SanitizeIdentifier(pin);
            if (wiring.PinExpressions.TryGetValue(pin, out var node))
            {
                sb.Append(identifier).Append(" = ").Append(Ref(node, ctx)).Append(';').AppendLine();
            }
            else if (wiring.PinDisassembly.TryGetValue(pin, out var asm))
            {
                sb.Append("// ").Append(identifier).Append(" (expression DAG unavailable, raw disassembly below)").AppendLine();
                foreach (var line in asm.Split('\n'))
                    sb.Append("//   ").AppendLine(line.TrimEnd('\r'));
            }
            else
            {
                sb.Append("// ").Append(identifier).Append(" reads: ")
                    .Append(string.Join(", ", wiring.PinSources[pin].Select(s => DescribeSource(s, ctx))))
                    .AppendLine();
            }
        }

        return sb.ToString();
    }

    private static List<UMaterialInterface> BuildInstanceChain(UMaterialInterface material)
    {
        var chain = new List<UMaterialInterface> { material };
        var guard = 0;
        while (chain[^1] is UMaterialInstance instance && instance.Parent is UMaterialInterface parent && ++guard < 16)
            chain.Add(parent);
        return chain;
    }

    private static FMaterialShaderMapLegacy? FindLegacyShaderMap(List<UMaterialInterface> chain, out UMaterialInterface? owner)
    {
        foreach (var material in chain)
        {
            var map = material.LoadedMaterialResources?.FirstOrDefault(r => r.LoadedShaderMapLegacy != null)?.LoadedShaderMapLegacy;
            if (map == null) continue;
            owner = material;
            return map;
        }
        owner = null;
        return null;
    }

    /// <summary>
    /// Builds a bytecode lookup (shader output hash -> code) over the game's pak-cooked shared
    /// shader libraries, or null when the provider has none. The libraries are only actually read
    /// on the first lookup. Ported from MaterialGraphViewModel.cs.
    /// </summary>
    private static Func<FSHAHash, byte[]?>? CreateLegacyShaderCodeResolver(List<UMaterialInterface> chain)
    {
        if (chain[0].Owner?.Provider is not { } provider) return null;
        if (!provider.Files.Keys.Any(k => k.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)))
            return null;

        return hash =>
        {
            List<FLegacyShaderCodeArchive> libraries;
            lock (LegacyShaderLibraries)
            {
                libraries = LegacyShaderLibraries.GetValue(provider, static p =>
                {
                    var result = new List<FLegacyShaderCodeArchive>();
                    foreach (var (path, file) in p.Files)
                    {
                        if (!path.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var archive = new FShaderCodeArchive(new FByteArchive(path, file.Read(), p.Versions));
                            if (archive.SerializedShaders is FLegacyShaderCodeArchive legacy)
                                result.Add(legacy);
                        }
                        catch
                        {
                            // an unreadable library only disables pixel-shader decompilation
                        }
                    }
                    return result;
                });
            }

            foreach (var library in libraries)
            {
                if (library.TryGetCode(hash) is { } code)
                    return code;
            }
            return null;
        };
    }

    #region Expression DAG -> pseudocode

    /// <summary>
    /// Shared state for one shader's worth of printing: a CSE pass over the (possibly heavily
    /// shared) expression DAG so a subtree reached from many places is declared once and referenced
    /// by name everywhere else, instead of being fully re-expanded at every occurrence.
    /// </summary>
    private sealed class PrintCtx(UMaterialInterface material, FUniformExpressionSetLegacy expressionSet, IReadOnlyList<UTexture?>? referencedTextures)
    {
        public readonly UMaterialInterface Material = material;
        public readonly FUniformExpressionSetLegacy ExpressionSet = expressionSet;
        public readonly IReadOnlyList<UTexture?>? ReferencedTextures = referencedTextures;
        public readonly Dictionary<int, MaterialParameterCollectionResolver.ResolvedCollection?> CollectionCache = new();
        public readonly Dictionary<int, int> LeftoverCbRegisterToCollectionIndex = new();
        public readonly Dictionary<PixelExpressionNode, int> RefCounts = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<PixelExpressionNode, string> Names = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<string> UsedNames = new(StringComparer.Ordinal);
        public readonly List<string> Declarations = [];
        public int NextId;
    }

    /// <summary>Counts how many edges point at each node across the whole DAG (root calls accumulate into the same counters).</summary>
    private static void CountRefs(PixelExpressionNode node, PrintCtx ctx, HashSet<PixelExpressionNode> recursed)
    {
        ctx.RefCounts[node] = ctx.RefCounts.GetValueOrDefault(node) + 1;
        if (!recursed.Add(node)) return; // children already counted once from an earlier reference
        foreach (var arg in node.Args) CountRefs(arg.Node, ctx, recursed);
    }

    /// <summary>
    /// Post-order: declares every node with more than one incoming reference, children before
    /// parents, PLUS every texture "sample" node unconditionally - even one used only once is worth
    /// naming after the texture it reads (e.g. "Pattern_HeavyArrows") rather than leaving readers to
    /// spot the identity buried in a trailing comment at its point of use.
    /// </summary>
    private static void AssignNames(PixelExpressionNode node, PrintCtx ctx, HashSet<PixelExpressionNode> visited)
    {
        if (!visited.Add(node)) return;
        foreach (var arg in node.Args) AssignNames(arg.Node, ctx, visited);
        if (ctx.Names.ContainsKey(node)) return;

        var forceHoist = node.Op == "sample";
        if (!forceHoist)
        {
            if (ctx.RefCounts.GetValueOrDefault(node) <= 1) return;
            // A bare numeric literal is exactly as clear inlined as it is named ("0" vs "_3") -
            // naming it only adds an indirection to look through, so leave immediates inlined.
            if (node.Op == "imm") return;
        }

        // "_N", never "tN"/"vN"/"rN"/etc: those single-letter prefixes are the actual DXBC register
        // classes (t# texture/SRV, v# input, r# temp, o# output - see PrintNodeInner/PrintArg), and
        // some of them legitimately appear inside Detail strings (e.g. "sample_l - t1 (engine
        // resource)" names real register t1). An "_N" name can never collide with those.
        var name = forceHoist && TryGetSampleTextureName(node, ctx, out var textureName)
            ? MakeUniqueName(textureName, ctx)
            : $"_{ctx.NextId++}";
        ctx.UsedNames.Add(name);
        ctx.Names[node] = name; // set before printing the body in case a node ever referenced itself
        ctx.Declarations.Add($"var {name} = {PrintNodeBody(node, ctx)};");
    }

    /// <summary>Resolves the same (Slot, Index) -> array lookup DescribeTexture uses, but returns just the bare identifier for naming.</summary>
    private static bool TryGetSampleTextureName(PixelExpressionNode node, PrintCtx ctx, out string name)
    {
        name = "";
        if (node.Source is not { Kind: PixelValueKind.Texture } source) return false;
        var array = source.TextureSlot switch
        {
            0 => ctx.ExpressionSet.Uniform2DTextureExpressions,
            1 => ctx.ExpressionSet.UniformCubeTextureExpressions,
            3 => ctx.ExpressionSet.UniformVolumeTextureExpressions,
            4 => ctx.ExpressionSet.UniformVirtualTextureExpressions,
            _ => null,
        };
        if (array == null || source.Index < 0 || source.Index >= array.Length) return false;
        var resolved = MaterialShaderDecompiler.TryResolveTextureIdentifier(array[source.Index], ctx.ReferencedTextures);
        if (resolved == null) return false;
        name = resolved;
        return true;
    }

    /// <summary>Disambiguates a candidate name against every name already handed out this shader (e.g. the same texture sampled twice at different UVs).</summary>
    private static string MakeUniqueName(string baseName, PrintCtx ctx)
    {
        if (!ctx.UsedNames.Contains(baseName)) return baseName;
        var suffix = 1;
        string candidate;
        do { candidate = $"{baseName}_{suffix++}"; } while (ctx.UsedNames.Contains(candidate));
        return candidate;
    }

    /// <summary>Prints a reference to a node: its assigned name if it was hoisted, otherwise its full body inline.</summary>
    private static string Ref(PixelExpressionNode node, PrintCtx ctx)
        => ctx.Names.TryGetValue(node, out var name) ? name : PrintNodeBody(node, ctx);

    private static string DescribeSource(PixelValueSource source, PrintCtx ctx) => source.Kind switch
    {
        PixelValueKind.VectorExpression => ResolveUniform(ctx.ExpressionSet.UniformVectorExpressions, source.Index, "UniformVector", ctx),
        PixelValueKind.ScalarExpression => ResolveUniform(ctx.ExpressionSet.UniformScalarExpressions, source.Index, "UniformScalar", ctx),
        PixelValueKind.Texture => DescribeTexture(source, ctx),
        _ => $"Uniform[{source.Index}]",
    };

    private static string ResolveUniform(FMaterialUniformExpressionLegacy[] expressions, int index, string fallbackPrefix, PrintCtx ctx)
        => index >= 0 && index < expressions.Length
            ? MaterialShaderDecompiler.PrintExpression(expressions[index], ctx.ReferencedTextures)
            : $"{fallbackPrefix}{index} /* out of range */";

    /// <summary>
    /// A sampled texture's (Slot, Index) pair comes straight out of the analyzer's own
    /// BuildLegacyTextureRegisterMap (MaterialPixelShaderAnalyzer.cs ~line 1256), which builds its
    /// register table by walking these exact arrays in this exact order: Slot 0 =
    /// Uniform2DTextureExpressions[Index], Slot 1 = UniformCubeTextureExpressions[Index], Slot 3 =
    /// UniformVolumeTextureExpressions[Index], Slot 4 = UniformVirtualTextureExpressions[Index] (a
    /// legacy quirk of that map: Slot 2 - Texture2DArray - and external textures are never entered
    /// into it, so those always fall through to the raw index below). Reusing the array the
    /// analyzer itself indexed from means this is a verified lookup, not a parallel guess.
    /// </summary>
    private static string DescribeTexture(PixelValueSource source, PrintCtx ctx)
    {
        var array = source.TextureSlot switch
        {
            0 => ctx.ExpressionSet.Uniform2DTextureExpressions,
            1 => ctx.ExpressionSet.UniformCubeTextureExpressions,
            3 => ctx.ExpressionSet.UniformVolumeTextureExpressions,
            4 => ctx.ExpressionSet.UniformVirtualTextureExpressions,
            _ => null,
        };

        var name = array != null && source.Index >= 0 && source.Index < array.Length
            ? MaterialShaderDecompiler.PrintExpression(array[source.Index], ctx.ReferencedTextures)
            : $"Texture[slot={source.TextureSlot}, index={source.Index}]";
        return source.Channel >= 0 ? $"{name}.{"rgba"[source.Channel]}" : name;
    }

    /// <summary>
    /// A foreign cbrow's Detail is built by MaterialPixelShaderAnalyzer.cs (~line 2286) as the exact
    /// literal "{bufferName} cb{Index0}[{Index1}]" whenever the bound buffer's own name is known -
    /// this matches that format specifically for the "MaterialCollectionN" buffer name the engine
    /// binds a referenced Parameter Collection under (HLSLMaterialTranslator.h AccessCollectionParameter),
    /// to resolve N -> the shader map's own ParameterCollections[N] GUID -> the actual collection
    /// asset (MaterialParameterCollectionResolver, verified against real data) -> the row's
    /// parameter name(s). Falls through untouched for every other buffer name.
    /// </summary>
    private static readonly Regex MaterialCollectionCbPattern = new(@"^MaterialCollection(?<n>\d+) cb\d+\[(?<row>\d+)\]$", RegexOptions.Compiled);

    /// <summary>
    /// MaterialCollectionN buffers are NEVER named in a shader's own reflected UniformBufferParameters
    /// list, for any shader - confirmed against engine source: ModifyCompilationEnvironment
    /// (HLSLMaterialTranslator.h:1082-1090) only declares the raw HLSL cbuffer/resource-table entry;
    /// the buffer is bound at draw time through a separate runtime path, never through a serialized
    /// FShaderUniformBufferParameter the way View/Primitive/Material are (verified: M_FN_Character_MASTER's
    /// Quality=High TBasePassPSFNoLightMapPolicy reads register cb2 at rows 22/27/29 - exactly the rows
    /// MaterialParameterCollectionResolver computed for SunLightColor/FogDirectionalInscatteringColor/
    /// SunAndMoonModelDirectionalVector - while its own MaterialUniformBuffer.BaseIndex is 3, and
    /// UniformBufferParameters lists only View@0/Primitive@1; Quality=Low, whose compiled bytecode never
    /// takes that branch, has no such register and Material sits at cb2 instead). So any foreign cbrow
    /// that reaches the plain "cb{N}[{row}]" fallback (no name resolved, N isn't the Material buffer's
    /// own register) is, by elimination, one of the shader map's own ParameterCollections entries.
    /// Registers are matched to ParameterCollections in order of first appearance while printing, which
    /// is exact for the single-collection case (the only one verified against real data); for a
    /// hypothetical material referencing more than one collection this ordering is a best-effort
    /// heuristic, not a proven mapping.
    /// </summary>
    private static readonly Regex ForeignCbPattern = new(@"^cb(?<n>\d+)\[(?<row>\d+)\]$", RegexOptions.Compiled);

    private static bool TryDescribeParameterCollectionRead(string? detail, PrintCtx ctx, out string result)
    {
        result = "";
        if (string.IsNullOrEmpty(detail)) return false;

        var namedMatch = MaterialCollectionCbPattern.Match(detail);
        if (namedMatch.Success)
        {
            var n = int.Parse(namedMatch.Groups["n"].Value);
            var row = int.Parse(namedMatch.Groups["row"].Value);
            return TryDescribeCollectionRow(n, row, ctx, out result);
        }

        var foreignMatch = ForeignCbPattern.Match(detail);
        if (foreignMatch.Success && ctx.ExpressionSet.ParameterCollections.Length > 0)
        {
            var register = int.Parse(foreignMatch.Groups["n"].Value);
            var row = int.Parse(foreignMatch.Groups["row"].Value);
            if (!ctx.LeftoverCbRegisterToCollectionIndex.TryGetValue(register, out var n))
            {
                if (ctx.LeftoverCbRegisterToCollectionIndex.Count >= ctx.ExpressionSet.ParameterCollections.Length) return false;
                n = ctx.LeftoverCbRegisterToCollectionIndex.Count;
                ctx.LeftoverCbRegisterToCollectionIndex[register] = n;
            }
            return TryDescribeCollectionRow(n, row, ctx, out result);
        }

        return false;
    }

    private static bool TryDescribeCollectionRow(int n, int row, PrintCtx ctx, out string result)
    {
        result = "";
        if (n < 0 || n >= ctx.ExpressionSet.ParameterCollections.Length) return false;

        if (!ctx.CollectionCache.TryGetValue(n, out var collection))
            ctx.CollectionCache[n] = collection = MaterialParameterCollectionResolver.Resolve(ctx.Material, ctx.ExpressionSet.ParameterCollections[n]);
        if (collection == null || !collection.Slots.TryGetValue(row, out var slot)) return false;

        if (slot.VectorName != null)
        {
            result = $"{SanitizeIdentifier(slot.VectorName)} /* {collection.Name}[{row}] */";
            return true;
        }
        // A scalar-packed row holds up to 4 unrelated parameters, one per component - which
        // specific one this particular read means is decided by the swizzle PrintArg appends
        // around this value, not by anything visible here, so all 4 are shown rather than guessing.
        var names = string.Join(", ", "xyzw".Select((c, i) => $"{c}={slot.ScalarNames[i] ?? "?"}"));
        result = $"{collection.Name}[{row}] /* {names} */";
        return true;
    }

    private static string PrintNodeBody(PixelExpressionNode node, PrintCtx ctx)
    {
        var expr = PrintNodeInner(node, ctx);
        return node.Saturate ? $"saturate({expr})" : expr;
    }

    private static string PrintNodeInner(PixelExpressionNode node, PrintCtx ctx)
    {
        switch (node.Op)
        {
            case "imm":
                return FormatConstants(node.Constants);
            case "input":
                return string.IsNullOrEmpty(node.Detail) ? "Input" : node.Detail;
            case "cbrow":
                // Source is only set for reads from the Material constant buffer; reads from any
                // other bound buffer (View, Primitive, MaterialCollectionN, ...) are non-material
                // engine state and carry a plain label in Detail instead (see
                // MaterialPixelShaderAnalyzer.cs ~line 2286).
                if (node.Source is { } cbSource) return DescribeSource(cbSource, ctx);
                if (TryDescribeParameterCollectionRead(node.Detail, ctx, out var mpcRead)) return mpcRead;
                return string.IsNullOrEmpty(node.Detail) ? "/* unresolved constant buffer read */ 0" : $"/* {node.Detail} */ 0";
            case "sample":
                return $"{node.Detail}({string.Join(", ", node.Args.Select(a => PrintArg(a, ctx)))})" +
                       (node.Source is { } texSource ? $" /* {DescribeSource(texSource, ctx)} */" : "");
            case "append":
                // append(cond?A.x:B.x, cond?A.y:B.y, ...) is exactly (cond?A:B).xy... when every
                // component shares the same condition and the same two source vectors - a proof,
                // not a guess: TryCollapseUniformSelect only fires when that's checked exactly.
                return TryCollapseUniformSelect(node, ctx, out var collapsed)
                    ? collapsed
                    : $"append({string.Join(", ", node.Args.Select(a => PrintArg(a, ctx)))})";
            case "mask":
                return node.Args.Count > 0 ? PrintArg(node.Args[0], ctx) : "0";
            case "phi":
                // MergeBranches (MaterialPixelShaderAnalyzer.cs ~line 2370) always emits exactly
                // 3 args in this order: [Condition, Then, Else].
                return node.Args.Count == 3
                    ? $"({PrintArg(node.Args[0], ctx)} ? {PrintArg(node.Args[1], ctx)} : {PrintArg(node.Args[2], ctx)})"
                    : $"phi({string.Join(", ", node.Args.Select(a => $"{a.Name}: {PrintArg(a, ctx)}"))})";
            case "opaque":
                return $"/* opaque: {node.Detail} */ 0";
            default:
                return PrintInstruction(node, ctx);
        }
    }

    private static string PrintInstruction(PixelExpressionNode node, PrintCtx ctx)
    {
        string Arg(int i) => i < node.Args.Count ? PrintArg(node.Args[i], ctx) : "0";

        return node.Op switch
        {
            "add" or "iadd" => $"({Arg(0)} + {Arg(1)})",
            "mul" or "imul" or "umul" => $"({Arg(0)} * {Arg(1)})",
            "div" or "udiv" => $"({Arg(0)} / {Arg(1)})",
            "mad" or "imad" or "umad" => $"({Arg(0)} * {Arg(1)} + {Arg(2)})",
            "dp2" => $"dot2({Arg(0)}, {Arg(1)})",
            "dp3" => $"dot3({Arg(0)}, {Arg(1)})",
            "dp4" => $"dot4({Arg(0)}, {Arg(1)})",
            "min" or "imin" or "umin" => $"min({Arg(0)}, {Arg(1)})",
            "max" or "imax" or "umax" => $"max({Arg(0)}, {Arg(1)})",
            "mov" => Arg(0),
            "frc" => $"frac({Arg(0)})",
            "rsq" => $"rsqrt({Arg(0)})",
            "sqrt" => $"sqrt({Arg(0)})",
            "rcp" => $"(1 / {Arg(0)})",
            "log" => $"log2({Arg(0)})",
            "exp" => $"exp2({Arg(0)})",
            "lt" or "ilt" or "ult" => $"({Arg(0)} < {Arg(1)})",
            "ge" or "ige" or "uge" => $"({Arg(0)} >= {Arg(1)})",
            "eq" or "ieq" => $"({Arg(0)} == {Arg(1)})",
            "ne" or "ine" => $"({Arg(0)} != {Arg(1)})",
            "and" => $"({Arg(0)} & {Arg(1)})",
            "or" => $"({Arg(0)} | {Arg(1)})",
            "xor" => $"({Arg(0)} ^ {Arg(1)})",
            "not" => $"~{Arg(0)}",
            "movc" => $"({Arg(0)} ? {Arg(1)} : {Arg(2)})",
            "ftoi" => $"(int) {Arg(0)}",
            "ftou" => $"(uint) {Arg(0)}",
            "itof" or "utof" => $"(float) {Arg(0)}",
            "discard" => $"discard({Arg(0)})",
            _ => node.Args.Count == 0
                ? node.Op
                : $"{node.Op}({string.Join(", ", node.Args.Select(a => PrintArg(a, ctx)))})",
        };
    }

    /// <summary>
    /// An "append" built from 2-4 "phi" args collapses to one vector-level ternary,
    /// (Condition ? Then : Else).xyz.., only when every single component matches exactly:
    /// - the append-arg edge itself carries no swizzle/negate/abs (append.Args[i] wraps the phi
    ///   node directly - MergeBranches never negates/swizzles the phi itself, only its Then/Else);
    /// - every component's Condition is the identical edge (same node, same swizzle/negate/abs) -
    ///   MergeBranches always reuses the branch's own condition reference, so this holds whenever
    ///   the components really did all come from the same if;
    /// - every component's Then resolves to the SAME underlying node, and its swizzle is exactly
    ///   the expected identity component for that position ("" or "x" at position 0, "y" at 1, ...)
    ///   with no negate/abs - i.e. component i really is just (that one shared vector).charAt(i);
    /// - same check for Else.
    /// Any single mismatch aborts the whole collapse and the caller falls back to the fully
    /// explicit append(...) form - nothing here is inferred, only confirmed.
    /// </summary>
    private static bool TryCollapseUniformSelect(PixelExpressionNode append, PrintCtx ctx, out string result)
    {
        result = "";
        if (append.Args.Count is < 2 or > 4) return false;

        PixelExpressionArg? condition = null;
        PixelExpressionNode? thenNode = null;
        PixelExpressionNode? elseNode = null;

        for (var i = 0; i < append.Args.Count; i++)
        {
            var outer = append.Args[i];
            if (outer.Negate || outer.Absolute || !string.IsNullOrEmpty(outer.Swizzle)) return false;
            if (outer.Node.Op != "phi" || outer.Node.Args.Count != 3) return false;

            var cond = outer.Node.Args[0];
            var then = outer.Node.Args[1];
            var els = outer.Node.Args[2];

            if (condition is null) condition = cond;
            else if (!SameEdge(condition, cond)) return false;

            if (!IsIdentityComponent(then, i) || !IsIdentityComponent(els, i)) return false;

            if (thenNode is null) thenNode = then.Node;
            else if (!ReferenceEquals(thenNode, then.Node)) return false;
            if (elseNode is null) elseNode = els.Node;
            else if (!ReferenceEquals(elseNode, els.Node)) return false;
        }

        if (condition is null || thenNode is null || elseNode is null) return false;

        var swizzle = "xyzw"[..append.Args.Count];
        result = $"({PrintArg(condition, ctx)} ? {Ref(thenNode, ctx)}.{swizzle} : {Ref(elseNode, ctx)}.{swizzle})";
        return true;
    }

    private static bool IsIdentityComponent(PixelExpressionArg edge, int position)
    {
        if (edge.Negate || edge.Absolute) return false;
        var expected = "xyzw"[position].ToString();
        return edge.Swizzle == expected || (edge.Swizzle.Length == 0 && position == 0);
    }

    private static bool SameEdge(PixelExpressionArg a, PixelExpressionArg b)
        => ReferenceEquals(a.Node, b.Node) && a.Swizzle == b.Swizzle && a.Negate == b.Negate && a.Absolute == b.Absolute;

    private static string PrintArg(PixelExpressionArg arg, PrintCtx ctx)
    {
        var value = Ref(arg.Node, ctx);
        if (arg.Absolute) value = $"abs({value})";
        if (arg.Negate) value = $"-{value}";
        if (!string.IsNullOrEmpty(arg.Swizzle)) value = $"{value}.{arg.Swizzle}";
        return value;
    }

    private static string FormatConstants(float[]? constants)
    {
        if (constants is not { Length: > 0 }) return "0";
        return constants.Length == 1
            ? constants[0].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : $"Const({string.Join(", ", constants.Select(c => c.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))})";
    }

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.Length == 0 ? "Pin" : sb.ToString();
    }

    #endregion
}
