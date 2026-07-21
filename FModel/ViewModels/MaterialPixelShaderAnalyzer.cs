using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

namespace FModel.ViewModels;

public enum PixelValueKind
{
    VectorExpression,
    ScalarExpression,
    Texture,
    UniformExpression
}

/// <summary>
/// A serialized material value the compiled pixel shader reads: a uniform expression
/// (by preshader index) or a bound texture (by binding-table slot and index within it,
/// with the sampled channel, -1 meaning the texture as a whole).
/// </summary>
public readonly record struct PixelValueSource(PixelValueKind Kind, int Index, int TextureSlot, int Channel)
{
    public static PixelValueSource Vector(int index) => new(PixelValueKind.VectorExpression, index, -1, -1);
    public static PixelValueSource Scalar(int index) => new(PixelValueKind.ScalarExpression, index, -1, -1);
    public static PixelValueSource Texture(int slot, int index, int channel) => new(PixelValueKind.Texture, index, slot, channel);
    /// <summary>A UE5 preshader uniform expression (index into FUniformExpressionSet.UniformPreshaders).</summary>
    public static PixelValueSource Uniform(int index) => new(PixelValueKind.UniformExpression, index, -1, -1);
}

public class PixelShaderWiring
{
    /// <summary>Material output pin name → the serialized values the shader routes into it.</summary>
    public Dictionary<string, List<PixelValueSource>> PinSources { get; } = new();
    /// <summary>
    /// Material output pin name → annotated D3D SM5 assembly of the instructions whose values
    /// flow into that pin, sliced out of the compiled pixel shader.
    /// </summary>
    public Dictionary<string, string> PinDisassembly { get; } = new();
    /// <summary>
    /// Material output pin name → the recovered expression DAG of the compiled math feeding
    /// that pin. Every node is one decoded DXBC instruction (or leaf operand) — nothing is
    /// synthesized — so the graph can optionally show the combining math as real nodes
    /// instead of one opaque "Pixel Shader Math" box.
    /// </summary>
    public Dictionary<string, PixelExpressionNode> PinExpressions { get; } = new();
    /// <summary>
    /// Every OTHER compiled shader stage in this material's shader map (vertex, geometry,
    /// compute, hull/domain, shadow/depth pixel, and simplified-shading base-pass pixel
    /// permutations) — the shaders that are not the analyzed base-pass pixel shader. Each group
    /// becomes a node wired to the material output so the full shader map is visible, and where
    /// a stage provably routes material values into its own outputs those values are listed so
    /// the real uniform nodes can feed the stage node. Purely decoded, never guessed.
    /// </summary>
    public List<ShaderStageGroup> ShaderStages { get; } = [];
    public string ShaderTypeName { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public bool Success { get; set; }
}

/// <summary>
/// One node's worth of "other shader stage" information: either a single permutation that
/// routes material values to its outputs, or a category (all vertex shaders, all geometry
/// shaders, …) that carries no material expression. <see cref="OutputValues"/> is the set of
/// material values the taint analysis proves reach this stage's outputs (empty for stages that
/// read no material parameters, e.g. GPU-skinned vertex shaders).
/// </summary>
public sealed class ShaderStageGroup
{
    /// <summary>Output-node pin name and node title, e.g. "Vertex Shaders" or a permutation label.</summary>
    public string Label = string.Empty;
    /// <summary>D3D shader frequency (0 VS, 1 HS, 2 DS, 3 PS, 4 GS, 5 CS).</summary>
    public int Frequency;
    /// <summary>Distinct engine type names of the shaders folded into this group.</summary>
    public List<string> TypeNames { get; } = [];
    /// <summary>How many compiled shaders (deduped by output hash) this group represents.</summary>
    public int ShaderCount;
    /// <summary>True when at least one shader in the group binds the Material constant buffer.</summary>
    public bool BindsMaterial;
    /// <summary>Material values that provably reach this stage's outputs (may be empty).</summary>
    public List<PixelValueSource> OutputValues { get; } = [];
    /// <summary>
    /// The decoded per-output expression DAGs of a representative shader in this group, keyed by
    /// output semantic (SV_Position, TEXCOORD3, SV_Target0, …). Populated only for stages the
    /// linear decoder can model (vertex/geometry/pixel that write output registers); empty for
    /// compute shaders (which write UAVs). Lets the graph expand the stage into real instruction
    /// nodes under the "Expand Shader Math" option, exactly like the base-pass pixel shader.
    /// </summary>
    public Dictionary<string, PixelExpressionNode> OutputExpressions { get; } = new();
    /// <summary>Engine type name of the shader whose math <see cref="OutputExpressions"/> came from.</summary>
    public string RepresentativeType = string.Empty;
}

/// <summary>
/// One operand edge of a recovered pixel shader expression: the producing node plus the
/// swizzle and modifier bits taken straight from the instruction's operand token.
/// </summary>
public sealed class PixelExpressionArg
{
    public PixelExpressionNode Node;
    /// <summary>Component letters actually read (e.g. "xxy"), empty when the identity swizzle.</summary>
    public string Swizzle = string.Empty;
    public bool Negate;
    public bool Absolute;
    /// <summary>Semantic label for the input ("A", "B", "UVs", …), assigned per opcode.</summary>
    public string Name = string.Empty;
}

/// <summary>
/// A value-producing step recovered from the compiled pixel shader. Op is either a DXBC
/// mnemonic (one node per decoded instruction) or a leaf/structural kind: "imm" (immediate
/// constant), "input" (vertex interpolant), "cbrow" (constant-buffer value), "sample"
/// (texture read), "append" (a register whose components come from different producers),
/// "mask" (component selection), "phi" (value that differs between the branches of an if)
/// or "opaque" (dynamic flow the decoder does not follow — Detail says why).
/// </summary>
public sealed class PixelExpressionNode
{
    public string Op = string.Empty;
    public List<PixelExpressionArg> Args { get; } = [];
    /// <summary>The instruction's _sat modifier: result clamped to [0,1].</summary>
    public bool Saturate;
    /// <summary>Immediate values for "imm" leaves.</summary>
    public float[] Constants;
    /// <summary>Identity of a material cb value or material texture for "cbrow"/"sample" leaves.</summary>
    public PixelValueSource? Source;
    /// <summary>Foreign cb label, v# semantic name, opaque reason or other annotation.</summary>
    public string Detail = string.Empty;
    /// <summary>For "sample" nodes: node component → texture channel (the resource operand swizzle).</summary>
    public int[] ChannelMap;
}

/// <summary>
/// Recovers the material's real output-pin wiring from the compiled base-pass pixel shader
/// inlined in the cooked shader map. The D3D SM5 (DXBC) instruction stream is decoded and a
/// component-level dataflow (taint) pass tracks which Material constant-buffer values and
/// which Material-bound texture registers reach which render target. UE4's deferred GBuffer
/// layout is fixed per engine version, so render-target components map 1:1 to material pins
/// (GBufferA=Normal, GBufferB=Metallic/Specular/Roughness, GBufferC=BaseColor/AO, SceneColor
/// =Emissive, discard=OpacityMask). Everything used here is serialized data — no name
/// heuristics anywhere.
/// </summary>
public static class MaterialPixelShaderAnalyzer
{
    #region Public entry point

    public static PixelShaderWiring Analyze(FMaterialShaderMap shaderMap, FUniformExpressionSet expressionSet, bool usesGBuffer)
    {
        var wiring = new PixelShaderWiring();
        var code = shaderMap?.Code;
        if (code == null || code.ShaderEntries.Length == 0)
        {
            wiring.FailureReason = "compiled shader code is not inlined (stored in the shared shader library)";
            return wiring;
        }
        if (shaderMap.Content is not FMaterialShaderMapContent content)
        {
            wiring.FailureReason = "shader map has no material content";
            return wiring;
        }

        var candidates = CollectBasePassPixelShaders(content);
        if (candidates.Count == 0)
        {
            wiring.FailureReason = "no base-pass pixel shader found in the shader map";
            return wiring;
        }

        var lastError = "no base-pass pixel shader could be analyzed";
        foreach (var (shader, typeName) in candidates)
        {
            try
            {
                if (TryAnalyzeShader(shader, typeName, code, expressionSet, usesGBuffer, wiring, out var error))
                {
                    wiring.Success = true;
                    wiring.ShaderTypeName = typeName;
                    return wiring;
                }
                lastError = error;
            }
            catch (Exception e)
            {
                lastError = $"{typeName}: {e.Message}";
            }
        }

        wiring.FailureReason = lastError;
        return wiring;
    }

    #endregion

    #region UE5 SM6 (DXIL) analysis

    /// <summary>
    /// UE5 analysis path: the base-pass pixel shader is DXIL (SM6), retrieved from the IoStore
    /// shared shader library. The blob keeps the same UE D3D shader-resource-table prefix as SM5,
    /// so texture registers map through <see cref="BuildTextureRegisterMap"/> exactly as before;
    /// only the program (DXIL, not DXBC) and the constant-buffer layout (UE5 preshader buffer with
    /// explicit per-field offsets) differ. The Material cbuffer register is identified structurally
    /// — it is the constant buffer whose row loads land on serialized preshader fields — so nothing
    /// is guessed. Returns the same <see cref="PixelShaderWiring"/> the SM5 path produces.
    /// </summary>
    public static PixelShaderWiring AnalyzeDxil(byte[] blob, FUniformExpressionSet expressionSet, EGame game, bool usesGBuffer)
    {
        var wiring = new PixelShaderWiring();
        try
        {
            var module = DxilModule.Parse(blob);
            var fn = module.Functions.FirstOrDefault();
            if (fn == null) { wiring.FailureReason = "the DXIL module has no function body"; return wiring; }

            var resources = DxilTaint.BuildResources(module);
            var handles = DxilTaint.ResolveHandles(module, fn);

            var stores = fn.Instructions.Where(i => i.Callee != null && i.Callee.StartsWith("dx.op.storeOutput")).ToList();
            if (stores.Count == 0) { wiring.FailureReason = "the DXIL pixel shader writes no output registers"; return wiring; }

            var outLeaves = new Dictionary<(int Sig, int Col), List<DxLeaf>>();
            var storeValues = new List<(int Sig, int Col, int ValId)>();
            foreach (var st in stores)
            {
                var sig = (int) (DxilTaint.ConstOp(fn, st, 1) ?? -1);
                var col = (int) (DxilTaint.ConstOp(fn, st, 3) ?? -1);
                var valId = st.Operands.Count > 4 ? st.Operands[4] : -1;
                storeValues.Add((sig, col, valId));
                if (!outLeaves.TryGetValue((sig, col), out var list)) outLeaves[(sig, col)] = list = new List<DxLeaf>();
                list.AddRange(DxilTaint.Taint(fn, resources, handles, valId, col));
            }

            // float-offset in the preshader buffer -> uniform expression index (UE5 CreateBufferStruct)
            var offsetMap = BuildPreshaderOffsetMap(expressionSet);

            // Material cbuffer register = the cb whose loads land on preshader fields (validated, not guessed)
            var cbHit = new Dictionary<int, int>();
            foreach (var leaf in outLeaves.Values.SelectMany(v => v).Where(l => l.Kind == "cbuffer"))
            {
                var off = leaf.Row * 4 + Math.Clamp(leaf.Component, 0, 3);
                if (offsetMap.ContainsKey(off)) cbHit[leaf.Register] = cbHit.GetValueOrDefault(leaf.Register) + 1;
            }
            var materialReg = cbHit.Count > 0 ? cbHit.OrderByDescending(k => k.Value).First().Key : -1;

            // texture registers via the UE D3D shader resource table prefix (identical layout to SM5)
            var pos = 0;
            ReadU32(blob, ref pos);
            var srvMap = ReadResourceMap(blob, ref pos);
            ReadResourceMap(blob, ref pos); // sampler
            ReadResourceMap(blob, ref pos); // uav
            ReadResourceMap(blob, ref pos); // layout hashes
            var textureMap = ReadResourceMap(blob, ref pos);
            var textureByRegister = materialReg >= 0
                ? BuildTextureRegisterMap(expressionSet, materialReg, srvMap, textureMap)
                : new Dictionary<long, (int Slot, int Index)>();

            PixelValueSource? Resolve(DxLeaf leaf)
            {
                if (leaf.Kind == "cbuffer" && leaf.Register == materialReg)
                {
                    var off = leaf.Row * 4 + Math.Clamp(leaf.Component, 0, 3);
                    if (offsetMap.TryGetValue(off, out var uni)) return PixelValueSource.Uniform(uni);
                }
                else if (leaf.Kind == "texture" && textureByRegister.TryGetValue(leaf.Register, out var tex))
                {
                    return PixelValueSource.Texture(tex.Slot, tex.Index, leaf.Component is >= 0 and <= 3 ? leaf.Component : -1);
                }
                return null;
            }

            void Add(string pin, PixelValueSource source)
            {
                if (!wiring.PinSources.TryGetValue(pin, out var l)) wiring.PinSources[pin] = l = new List<PixelValueSource>();
                if (!l.Contains(source)) l.Add(source);
            }

            // UE deferred GBuffer layout (matches the SM5 MapSinksToPins mapping)
            string PinFor(int sig, int col) => usesGBuffer
                ? (sig, col) switch
                {
                    (1, _) => "Normal",
                    (2, 0) => "Metallic", (2, 1) => "Specular", (2, 2) => "Roughness",
                    (3, 3) => "Ambient Occlusion", (3, _) => "Base Color",
                    _ => null
                }
                : (sig, col) switch { (0, 3) => "Opacity", (0, _) => "Emissive Color", _ => null };

            foreach (var ((sig, col), leaves) in outLeaves)
            {
                var pin = PinFor(sig, col);
                if (pin == null) continue;
                foreach (var leaf in leaves)
                    if (Resolve(leaf) is { } src) Add(pin, src);
            }

            // scene color (RT0) receives base color/metallic back through the lighting math, so only
            // sources that reach no other pin are genuinely emissive
            if (usesGBuffer)
            {
                var elsewhere = new HashSet<PixelValueSource>(wiring.PinSources.Values.SelectMany(v => v));
                foreach (var ((sig, col), leaves) in outLeaves)
                {
                    if (sig != 0 || col > 2) continue;
                    foreach (var leaf in leaves)
                        if (Resolve(leaf) is { } src && !elsewhere.Contains(src)) Add("Emissive Color", src);
                }
            }

            // Expand Shader Math (UE5): recover each wired pin's expression DAG from the same stores,
            // so the graph can optionally show one node per decoded DXIL instruction instead of an
            // opaque combiner. Auxiliary — never fails the wiring; leaves reuse the same material sources.
            try
            {
                var pinComponents = new Dictionary<string, List<(int Col, PixelExpressionNode Expr)>>();
                foreach (var (sig, col, valId) in storeValues)
                {
                    var pin = PinFor(sig, col);
                    if (pin == null || valId < 0) continue;
                    var expr = DxilTaint.BuildExpression(fn, resources, handles, valId, col, Resolve);
                    if (!pinComponents.TryGetValue(pin, out var comps))
                        pinComponents[pin] = comps = new List<(int, PixelExpressionNode)>();
                    comps.Add((col, expr));
                }
                foreach (var (pin, comps) in pinComponents)
                {
                    if (!wiring.PinSources.ContainsKey(pin)) continue; // only expand pins that actually wired
                    var ordered = comps.OrderBy(c => c.Col).Select(c => c.Expr).ToList();
                    if (ordered.Count == 1) { wiring.PinExpressions[pin] = ordered[0]; continue; }
                    var append = new PixelExpressionNode { Op = "append" };
                    for (var i = 0; i < ordered.Count; i++)
                        append.Args.Add(new PixelExpressionArg { Node = ordered[i], Name = i < 4 ? "xyzw"[i].ToString() : $"c{i}" });
                    wiring.PinExpressions[pin] = append;
                }
            }
            catch { /* the opaque combiner remains the fallback */ }

            wiring.Success = wiring.PinSources.Count > 0;
            if (!wiring.Success) wiring.FailureReason = "no serialized material value reaches an output pin";
            wiring.ShaderTypeName = "SM6 DXIL";
        }
        catch (Exception e)
        {
            wiring.FailureReason = $"DXIL analysis failed: {e.Message}";
        }
        return wiring;
    }

    /// <summary>
    /// Maps each float slot of the UE5 material preshader constant buffer to the uniform expression
    /// that writes it. Layout is FUniformExpressionSet::CreateBufferStruct / HLSLMaterialTranslator:
    /// every UniformPreshader has one or more fields, each at an explicit BufferOffset (in floats)
    /// spanning its component count.
    /// </summary>
    private static Dictionary<int, int> BuildPreshaderOffsetMap(FUniformExpressionSet es)
    {
        var map = new Dictionary<int, int>();
        var fields = es.UniformPreshaderFields ?? [];
        var preshaders = es.UniformPreshaders ?? [];

        static int NumComponents(EShaderValueType t) => t switch
        {
            EShaderValueType.Float1 or EShaderValueType.Double1 or EShaderValueType.Int1 => 1,
            EShaderValueType.Float2 or EShaderValueType.Double2 or EShaderValueType.Int2 => 2,
            EShaderValueType.Float3 or EShaderValueType.Double3 or EShaderValueType.Int3 => 3,
            EShaderValueType.Float4 or EShaderValueType.Double4 or EShaderValueType.Int4 => 4,
            _ => 1
        };

        void Mark(int offset, int components, int uniformIndex)
        {
            for (var c = 0; c < components; c++) map[offset + c] = uniformIndex;
        }

        for (var i = 0; i < preshaders.Length; i++)
        {
            switch (preshaders[i])
            {
                case FMaterialUniformPreshaderHeader_5_1 h51:
                    for (var f = 0u; f < h51.NumFields; f++)
                    {
                        var idx = (int) (h51.FieldIndex + f);
                        if (idx >= fields.Length) break;
                        Mark((int) fields[idx].BufferOffset, NumComponents(fields[idx].Type), i);
                    }
                    break;
                case FMaterialUniformPreshaderHeader_5_0 h50:
                    Mark((int) h50.BufferOffset, (int) h50.NumComponents, i);
                    break;
                case FMaterialUniformPreshaderHeader_5_8 h58:
                    Mark((int) h58.BufferOffset, NumComponents(h58.Type), i);
                    break;
            }
        }
        return map;
    }

    #endregion

    #region Base-pass shader lookup

    // UE 4.25-4.27 base-pass pixel shader types are TBasePassPS{Policy}[Skylight]
    // (BasePassRendering.cpp IMPLEMENT_BASEPASS_PIXELSHADER_TYPE), stored in the map
    // keyed by CityHash64 of the upper-cased type name
    private static readonly string[] BasePassLightMapPolicies =
    [
        "FNoLightMapPolicy",
        "FPrecomputedVolumetricLightmapLightingPolicy",
        "FCachedVolumeIndirectLightingPolicy",
        "FCachedPointIndirectLightingPolicy",
        "FSelfShadowedTranslucencyPolicy",
        "FSelfShadowedCachedPointIndirectLightingPolicy",
        "FSelfShadowedVolumetricLightmapPolicy",
        "FSimpleNoLightmapLightingPolicy",
        "FSimpleLightmapOnlyLightingPolicy",
        "FSimpleDirectionalLightLightingPolicy",
        "FSimpleStationaryLightPrecomputedShadowsLightingPolicy",
        "FSimpleStationaryLightSingleSampleShadowsLightingPolicy",
        "FSimpleStationaryLightVolumetricLightmapShadowsLightingPolicy",
        "TLightMapPolicyLQ",
        "TLightMapPolicyHQ",
        "TDistanceFieldShadowsAndLightMapPolicyHQ"
    ];

    private static readonly Lazy<Dictionary<ulong, string>> BasePassNameHashes = new(() =>
    {
        var map = new Dictionary<ulong, string>();
        void Add(string name) => map[CityHash.CityHash64WithSeed(Encoding.UTF8.GetBytes(name.ToUpperInvariant()), 0)] = name;
        foreach (var policy in BasePassLightMapPolicies)
        {
            Add($"TBasePassPS{policy}");
            Add($"TBasePassPS{policy}Skylight");
        }
        Add("F128BitRTBasePassPS");
        return map;
    });

    private static string ResolveTypeName(ulong hash)
    {
        if (hash == 0) return null;
        if (HashedNamesProvider.TryGetEntry(hash, out var name) && !string.IsNullOrEmpty(name)) return name;
        return BasePassNameHashes.Value.TryGetValue(hash, out var known) ? known : null;
    }

    private static List<(FShader Shader, string TypeName)> CollectBasePassPixelShaders(FMaterialShaderMapContent content)
    {
        var result = new List<(FShader, string)>();
        var seenEntries = new HashSet<int>();

        void Consider(FShader shader, ulong typeHash)
        {
            if (shader == null || shader.Target.Frequency != EShaderFrequency.SF_Pixel) return;
            var name = ResolveTypeName(typeHash);
            if (name == null ||
                name.IndexOf("BasePassPS", StringComparison.OrdinalIgnoreCase) < 0 ||
                name.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0)
                return;
            if (!seenEntries.Add(shader.ResourceIndex)) return; // same bytecode, analyze once
            result.Add((shader, name));
        }

        void Visit(FShaderMapContent map)
        {
            if (map == null) return;
            for (var i = 0; i < (map.Shaders?.Length ?? 0); i++)
            {
                var shader = map.Shaders[i];
                if (shader == null) continue;
                var hash = shader.Type.Hash != 0
                    ? shader.Type.Hash
                    : map.ShaderTypes != null && i < map.ShaderTypes.Length ? map.ShaderTypes[i].Hash : 0;
                Consider(shader, hash);
            }
            foreach (var pipeline in map.ShaderPipelines ?? [])
            foreach (var shader in pipeline?.Shaders ?? [])
                Consider(shader, shader?.Type.Hash ?? 0);
        }

        Visit(content);
        foreach (var meshMap in content.OrderedMeshShaderMaps ?? [])
            Visit(meshMap);

        // no-skylight/no-lightmap permutations carry the least non-material clutter
        return result
            .OrderBy(c => c.Item2.IndexOf("Skylight", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
            .ThenBy(c => c.Item2.Contains("FNoLightMapPolicy") ? 0 : 1)
            .ToList();
    }

    #endregion

    #region Per-shader analysis

    private static bool TryAnalyzeShader(FShader shader, string typeName, FShaderMapResourceCode code,
        FUniformExpressionSet expressionSet, bool usesGBuffer, PixelShaderWiring wiring, out string error)
    {
        if (shader.ResourceIndex < 0 || shader.ResourceIndex >= code.ShaderEntries.Length)
        {
            error = $"{typeName}: shader entry {shader.ResourceIndex} out of range";
            return false;
        }
        var entry = code.ShaderEntries[shader.ResourceIndex];
        if (entry.Code == null || entry.Code.Length == 0)
        {
            error = $"{typeName}: shader entry is empty";
            return false;
        }

        // entries are LZ4 compressed unless they came out the same size (ShaderResource.cpp)
        var blob = entry.Code.Length == entry.UncompressedSize
            ? entry.Code
            : Compression.Decompress(entry.Code, entry.UncompressedSize, CompressionMethod.LZ4);

        // blob layout on PC D3D: [FD3D11ShaderResourceTable][DXBC container][optional data]
        // (D3D11ShaderResources.h: ResourceTableBits, then the five resource maps)
        var pos = 0;
        ReadU32(blob, ref pos); // ResourceTableBits
        var srvMap = ReadResourceMap(blob, ref pos);
        ReadResourceMap(blob, ref pos); // SamplerMap
        ReadResourceMap(blob, ref pos); // UnorderedAccessViewMap
        ReadResourceMap(blob, ref pos); // ResourceTableLayoutHashes
        var textureMap = ReadResourceMap(blob, ref pos);
        if (!HasDxbcMagic(blob, pos))
        {
            error = $"{typeName}: shader bytecode is not D3D SM5 DXBC (platform not supported)";
            return false;
        }
        var dxbcStart = pos;

        if (!TryParseDxbcContainer(blob, dxbcStart, out var outputRegToTarget, out var program, out var inputSemantics, out var containerError))
        {
            error = $"{typeName}: {containerError}";
            return false;
        }
        if (program[0] >> 16 != 0)
        {
            error = $"{typeName}: DXBC program is not a pixel shader";
            return false;
        }

        var instructions = DecodeProgram(program, out var declaredCbSizes, out var resourceDimensions);

        // constant-buffer layout of the data-driven "Material" uniform buffer
        // (FUniformExpressionSet::CreateBufferStruct): VT page-table constants, VT packed
        // constants, one float4 per vector expression, scalars packed 4 per float4
        var vtStackCount = expressionSet.VTStacks?.Length ?? 0;
        var virtualCount = CountTextureSlot(expressionSet, 4);
        var vecCount = expressionSet.UniformVectorPreshaders?.Length ?? 0;
        var scalarCount = expressionSet.UniformScalarPreshaders?.Length ?? 0;
        var vecBase = vtStackCount * 32 + virtualCount * 16;
        var scalarBase = vecBase + vecCount * 16;

        // data-driven uniform buffers are not auto-bound (ShaderParameterMetadata GetStructList
        // only registers static-slot structs), so "Material" is the parameter-map slot that is
        // NOT among the auto-bound UniformBufferParameters
        var autoBound = new HashSet<int>((shader.UniformBufferParameters ?? []).Select(p => (int) p.BaseIndex));
        var unnamed = (shader.ParameterMapInfo?.UniformBuffers ?? [])
            .Select(p => (int) p.BaseIndex)
            .Distinct()
            .Where(s => !autoBound.Contains(s))
            .ToList();
        int materialSlot;
        switch (unnamed.Count)
        {
            case 0:
                materialSlot = -1; // no material constants or textures bound in this shader
                break;
            case 1:
                materialSlot = unnamed[0];
                break;
            default:
                var expectedVec4s = vtStackCount * 2 + virtualCount + vecCount + (scalarCount + 3) / 4;
                var matches = unnamed.Where(s => declaredCbSizes.TryGetValue(s, out var size) && size == expectedVec4s).ToList();
                if (matches.Count != 1)
                {
                    error = $"{typeName}: could not identify the Material constant buffer slot";
                    return false;
                }
                materialSlot = matches[0];
                break;
        }

        var textureByRegister = BuildTextureRegisterMap(expressionSet, materialSlot, srvMap, textureMap);

        var context = new AnalysisContext
        {
            MaterialSlot = materialSlot,
            VecBase = vecBase,
            ScalarBase = scalarBase,
            VecCount = vecCount,
            ScalarCount = scalarCount,
            TextureByRegister = textureByRegister
        };
        var (state, discardSink) = RunTaintAnalysis(instructions, context);

        MapSinksToPins(state, discardSink, outputRegToTarget, usesGBuffer, wiring);
        try
        {
            BuildPinDisassemblies(instructions, context, outputRegToTarget, usesGBuffer, typeName, wiring);
        }
        catch
        {
            // the disassembly view is auxiliary — never fail the recovered wiring over it
        }
        try
        {
            // 4.25+ auto-bound uniform buffer parameters carry no names here, so foreign
            // cb leaves keep their raw cb#[row] labels
            BuildPinExpressions(instructions, context, outputRegToTarget, usesGBuffer,
                null, inputSemantics, resourceDimensions, wiring);
        }
        catch
        {
            // expression recovery is auxiliary too — the opaque combiner remains the fallback
        }
        error = string.Empty;
        return true;
    }

    private static int CountTextureSlot(FUniformExpressionSet expressionSet, int slot) =>
        expressionSet.UniformTextureParameters != null && slot < expressionSet.UniformTextureParameters.Length
            ? expressionSet.UniformTextureParameters[slot]?.Length ?? 0
            : 0;

    /// <summary>
    /// Maps t# registers to material texture bindings. The shader resource table stores, per
    /// uniform buffer, tokens of [UniformBufferIndex:8|ResourceIndex:16|BindIndex:8] where
    /// ResourceIndex counts positions in the Material buffer's resource array. That array's
    /// order is fixed by FUniformExpressionSet::CreateBufferStruct: texture+sampler pairs for
    /// Standard2D/Cube/Array2D/Volume/External bindings, VT page tables per stack, physical
    /// SRV+sampler per virtual texture, then the two shared world-group samplers.
    /// </summary>
    private static Dictionary<long, (int Slot, int Index)> BuildTextureRegisterMap(
        FUniformExpressionSet expressionSet, int materialSlot, uint[] srvMap, uint[] textureMap)
    {
        var registers = new Dictionary<long, (int, int)>();
        if (materialSlot < 0) return registers;

        var positions = new List<(int Slot, int Index)?>();
        for (var slot = 0; slot <= 3; slot++)
        {
            for (var i = 0; i < CountTextureSlot(expressionSet, slot); i++)
            {
                positions.Add((slot, i));
                positions.Add(null); // sampler
            }
        }
        for (var i = 0; i < (expressionSet.UniformExternalTextureParameters?.Length ?? 0); i++)
        {
            positions.Add(null); // external texture
            positions.Add(null); // sampler
        }
        foreach (var stack in expressionSet.VTStacks ?? [])
        {
            positions.Add(null); // PageTable0
            if (stack.NumLayers > 4) positions.Add(null); // PageTable1
            positions.Add(null); // PageTableIndirection
        }
        for (var i = 0; i < CountTextureSlot(expressionSet, 4); i++)
        {
            positions.Add((4, i)); // physical texture SRV
            positions.Add(null); // sampler
        }
        positions.Add(null); // Wrap_WorldGroupSettings sampler
        positions.Add(null); // Clamp_WorldGroupSettings sampler

        void MapEntries(uint[] map)
        {
            if (map == null || materialSlot >= map.Length) return;
            var offset = map[materialSlot];
            if (offset == 0 || offset >= map.Length) return;
            for (var i = (long) offset; i < map.Length; i++)
            {
                var token = map[i];
                if (token == 0xFFFFFFFFu) break;
                if ((int) (token >> 24) != materialSlot) break;
                var resourceIndex = (int) ((token >> 8) & 0xFFFF);
                var bindIndex = (int) (token & 0xFF);
                if (resourceIndex < positions.Count && positions[resourceIndex] is { } texture)
                    registers[bindIndex] = texture;
            }
        }

        MapEntries(textureMap);
        MapEntries(srvMap); // virtual texture physical textures bind as SRVs

        return registers;
    }

    #endregion

    #region UE 4.23/4.24 legacy shader map analysis

    /// <summary>
    /// Same analysis for the legacy (pre-FMemoryImage, &lt; 4.25) shader map format. The legacy
    /// format is friendlier: shader type names are plain FNames (no hashing), the Material
    /// constant buffer slot is serialized directly in FMaterialShader::Serialize, and the
    /// uniform expression set stores real expression trees whose array indices are the
    /// constant buffer rows. Blob layout and DXBC decoding are identical to 4.25+
    /// (D3DShaderCompiler.cpp writes the same FD3D11ShaderResourceTable + DXBC stream).
    /// </summary>
    public static PixelShaderWiring AnalyzeLegacy(FMaterialShaderMapLegacy shaderMap, bool usesGBuffer,
        Func<CUE4Parse.UE4.Objects.Core.Misc.FSHAHash, byte[]> sharedCodeResolver = null)
    {
        var wiring = new PixelShaderWiring();
        var expressionSet = shaderMap?.MaterialCompilationOutput?.UniformExpressionSet;
        if (expressionSet == null)
        {
            wiring.FailureReason = "legacy shader map has no uniform expression set";
            return wiring;
        }

        // FShaderLegacy only decodes base-pass pixel shader layouts, so every shader with
        // MaterialParameters here is a TBasePassPS permutation (SF_Pixel == 3)
        var candidates = new List<FShaderLegacy>();
        var seenBytecode = new HashSet<string>();
        void Visit(FShaderLegacy[] shaders)
        {
            foreach (var shader in shaders ?? [])
            {
                if (shader?.MaterialParameters == null || shader.Target.Frequency != 3) continue;
                if (shader.TypeName.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!seenBytecode.Add(shader.Resource?.OutputHash.ToString() ?? shader.TypeName)) continue;
                candidates.Add(shader);
            }
        }
        Visit(shaderMap.Shaders);
        foreach (var meshMap in shaderMap.MeshShaderMaps ?? [])
            Visit(meshMap.Shaders);

        if (candidates.Count == 0)
        {
            wiring.FailureReason = "no base-pass pixel shader found in the shader map";
            return wiring;
        }

        // no-skylight/no-lightmap permutations carry the least non-material clutter
        candidates = candidates
            .OrderBy(c => c.TypeName.IndexOf("Skylight", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
            .ThenBy(c => c.TypeName.Contains("FNoLightMapPolicy") ? 0 : 1)
            .ToList();

        var lastError = "no base-pass pixel shader could be analyzed";
        foreach (var shader in candidates)
        {
            if (shader.Resource == null)
            {
                lastError = $"{shader.TypeName}: shader has no resource";
                continue;
            }
            // inline code first; shared-library games resolve the bytecode by output hash
            var blob = shader.Resource.Code is { Length: > 0 } inline
                ? inline
                : sharedCodeResolver?.Invoke(shader.Resource.OutputHash);
            if (blob == null || blob.Length == 0)
            {
                lastError = sharedCodeResolver == null
                    ? $"{shader.TypeName}: compiled shader code is not inlined (stored in the shared shader library)"
                    : $"{shader.TypeName}: bytecode not found in the shared shader library";
                continue;
            }
            try
            {
                if (TryAnalyzeShaderLegacy(shader, blob, expressionSet, usesGBuffer, wiring, out var error))
                {
                    wiring.Success = true;
                    wiring.ShaderTypeName = shader.TypeName;
                    try
                    {
                        CollectShaderStages(shaderMap, expressionSet, shader, wiring, sharedCodeResolver);
                    }
                    catch
                    {
                        // auxiliary: the shader-stage overview must never break the primary result
                    }
                    return wiring;
                }
                lastError = error;
            }
            catch (Exception e)
            {
                lastError = $"{shader.TypeName}: {e.Message}";
            }
        }

        wiring.FailureReason = lastError;
        return wiring;
    }

    /// <summary>
    /// Fills <see cref="PixelShaderWiring.ShaderStages"/>: a node-per-stage overview of every
    /// OTHER compiled shader in the map, so the graph shows the whole shader map, not just the
    /// analyzed base-pass pixel shader. A permutation that provably routes fresh material values
    /// into its outputs becomes its own labeled group (with those values); every remaining shader
    /// is folded into a per-category group (all vertex shaders, all geometry shaders, …) that
    /// records its type names and whether it reads any material parameter at all.
    /// </summary>
    private static void CollectShaderStages(FMaterialShaderMapLegacy shaderMap, FUniformExpressionSetLegacy expressionSet,
        FShaderLegacy analyzedShader, PixelShaderWiring wiring,
        Func<CUE4Parse.UE4.Objects.Core.Misc.FSHAHash, byte[]> sharedCodeResolver)
    {
        // everything the primary shader already accounts for: taint pin sources plus every
        // material leaf of the recovered expression DAGs (covers sample-UV-only values)
        var consumed = new HashSet<(PixelValueKind Kind, int Index, int Slot)>(
            wiring.PinSources.Values.SelectMany(v => v).Select(s => (s.Kind, s.Index, s.TextureSlot)));
        var visited = new HashSet<PixelExpressionNode>();
        void WalkLeaves(PixelExpressionNode node)
        {
            if (!visited.Add(node)) return;
            if (node.Source is { } source) consumed.Add((source.Kind, source.Index, source.TextureSlot));
            foreach (var arg in node.Args) WalkLeaves(arg.Node);
        }
        foreach (var root in wiring.PinExpressions.Values) WalkLeaves(root);

        var seen = new HashSet<string> { analyzedShader.Resource?.OutputHash.ToString() ?? string.Empty };
        var stages = new List<FShaderLegacy>();
        void Visit(FShaderLegacy[] shaders)
        {
            foreach (var candidate in shaders ?? [])
            {
                if (candidate?.Resource == null || ReferenceEquals(candidate, analyzedShader)) continue;
                if (!seen.Add(candidate.Resource.OutputHash.ToString())) continue;
                stages.Add(candidate);
            }
        }
        Visit(shaderMap.Shaders);
        foreach (var meshMap in shaderMap.MeshShaderMaps ?? [])
            Visit(meshMap.Shaders);

        // base-pass pixel permutations first so they claim shared values before other stages
        stages = stages
            .OrderBy(s => s.MaterialParameters != null ? 0 : s.Target.Frequency == 3 ? 1 : 2)
            .ThenBy(s => s.TypeName, StringComparer.Ordinal)
            .ToList();

        // per-category accumulators for the shaders that carry no fresh material output flow
        var categories = new Dictionary<string, ShaderStageGroup>();
        // one representative shader per category to decode when the graph expands the stage math;
        // a base-pass shader is preferred (its math is the most material-relevant)
        var representatives = new Dictionary<string, (FShaderLegacy Shader, byte[] Blob)>();
        ShaderStageGroup Category(string label, int frequency)
        {
            if (!categories.TryGetValue(label, out var group))
                categories[label] = group = new ShaderStageGroup { Label = label, Frequency = frequency };
            return group;
        }
        void RememberRepresentative(string label, FShaderLegacy stage, byte[] stageBlob)
        {
            if (stageBlob is not { Length: > 0 }) return;
            var isBasePass = stage.TypeName.Contains("BasePass", StringComparison.OrdinalIgnoreCase);
            if (!representatives.TryGetValue(label, out var current))
            {
                representatives[label] = (stage, stageBlob);
                return;
            }
            // upgrade to a base-pass representative if the current one is not
            if (isBasePass && !current.Shader.TypeName.Contains("BasePass", StringComparison.OrdinalIgnoreCase))
                representatives[label] = (stage, stageBlob);
        }

        foreach (var stage in stages)
        {
            var blob = stage.Resource.Code is { Length: > 0 } inline
                ? inline
                : sharedCodeResolver?.Invoke(stage.Resource.OutputHash);

            HashSet<PixelValueSource> flows = null;
            var bindsMaterial = false;
            if (blob is { Length: > 0 })
            {
                try
                {
                    if (TryGetMaterialUse(stage, blob, expressionSet, out flows, out _, out _))
                        bindsMaterial = stage.MaterialParameters?.MaterialUniformBuffer is { bIsBound: true }
                            || (stage.UniformBufferParameters ?? []).Any(p => p.Name == "Material" && p.Parameter.bIsBound);
                }
                catch
                {
                    flows = null;
                }
            }

            var fresh = flows?.Where(s => !consumed.Contains((s.Kind, s.Index, s.TextureSlot))).ToList() ?? [];
            if (fresh.Count > 0)
            {
                // a real material→output flow: its own node, wired from the actual uniform values
                foreach (var source in fresh)
                    consumed.Add((source.Kind, source.Index, source.TextureSlot));
                var group = new ShaderStageGroup
                {
                    Label = StageLabel(stage),
                    Frequency = (int) stage.Target.Frequency,
                    ShaderCount = 1,
                    BindsMaterial = true
                };
                group.TypeNames.Add(stage.TypeName);
                group.OutputValues.AddRange(fresh);
                PopulateStageExpressions(group, stage, blob, expressionSet);
                wiring.ShaderStages.Add(group);
            }
            else
            {
                // no fresh material output flow: fold into a per-category presence node
                var category = Category(CategoryLabel(stage), (int) stage.Target.Frequency);
                category.ShaderCount++;
                category.BindsMaterial |= bindsMaterial;
                if (!category.TypeNames.Contains(stage.TypeName))
                    category.TypeNames.Add(stage.TypeName);
                RememberRepresentative(category.Label, stage, blob);
            }
        }

        // category nodes after the precise value-flow nodes, ordered by frequency then label
        foreach (var group in categories.Values.OrderBy(g => g.Frequency).ThenBy(g => g.Label, StringComparer.Ordinal))
        {
            if (representatives.TryGetValue(group.Label, out var rep))
                PopulateStageExpressions(group, rep.Shader, rep.Blob, expressionSet);
            wiring.ShaderStages.Add(group);
        }
    }

    /// <summary>Decodes a representative shader's per-output math into the group (best-effort).</summary>
    private static void PopulateStageExpressions(ShaderStageGroup group, FShaderLegacy shader, byte[] blob,
        FUniformExpressionSetLegacy expressionSet)
    {
        if (group.OutputExpressions.Count > 0) return;
        try
        {
            var expressions = BuildStageExpressions(shader, blob, expressionSet);
            if (expressions.Count == 0) return;
            foreach (var (semantic, root) in expressions)
                group.OutputExpressions[semantic] = root;
            group.RepresentativeType = shader.TypeName;
        }
        catch
        {
            // expansion is optional; a stage that fails to decode simply stays an opaque node
        }
    }

    /// <summary>Coarse stage category used to group shaders that carry no material expression.</summary>
    private static string CategoryLabel(FShaderLegacy shader) => shader.Target.Frequency switch
    {
        0 => "Vertex Shaders",
        1 => "Hull Shaders",
        2 => "Domain Shaders",
        3 => shader.TypeName.Contains("BasePass", StringComparison.OrdinalIgnoreCase)
            ? "Base-Pass Pixel Permutations"
            : "Shadow / Depth Pixel Shaders",
        4 => "Geometry Shaders",
        5 => "Compute Shaders",
        _ => $"Frequency {shader.Target.Frequency} Shaders"
    };

    /// <summary>
    /// Short human label for a shader permutation, derived from its real type name plus the
    /// vertex factory that distinguishes same-policy permutations (e.g. local mesh vs spline
    /// mesh vs GPU-skinned), so two base-pass pixel shaders never collapse to the same pin name.
    /// </summary>
    private static string StageLabel(FShaderLegacy shader)
    {
        var name = shader.TypeName;
        var stage = shader.Target.Frequency switch
        {
            0 => "VS", 1 => "HS", 2 => "DS", 3 => "PS", 4 => "GS", 5 => "CS",
            _ => $"f{shader.Target.Frequency}"
        };
        if (name.StartsWith("TBasePassPS", StringComparison.Ordinal))
        {
            stage = "Base Pass PS";
            name = name["TBasePassPS".Length..];
        }
        else if (name.StartsWith("TBasePassVS", StringComparison.Ordinal))
        {
            stage = "Base Pass VS";
            name = name["TBasePassVS".Length..];
        }
        name = name.Replace("LightingPolicy", " ").Replace("LightMapPolicy", " LightMap").Trim();
        if (name.Length > 1 && name[0] == 'F' && char.IsUpper(name[1])) name = name[1..];

        var vf = ShortVertexFactory(shader.VertexFactoryTypeName);
        var label = name.Length > 0 ? $"{stage}: {name}" : stage;
        return vf.Length > 0 ? $"{label} · {vf}" : label;
    }

    /// <summary>Compact vertex-factory name: strips the F/T prefix, the VertexFactory suffix and trailing bool flags.</summary>
    private static string ShortVertexFactory(string vertexFactoryTypeName)
    {
        if (string.IsNullOrEmpty(vertexFactoryTypeName)) return string.Empty;
        var name = vertexFactoryTypeName;
        while (name.EndsWith("true", StringComparison.Ordinal)) name = name[..^4];
        while (name.EndsWith("false", StringComparison.Ordinal)) name = name[..^5];
        const string suffix = "VertexFactory";
        var cut = name.IndexOf(suffix, StringComparison.Ordinal);
        if (cut > 0) name = name[..cut];
        if (name.Length > 1 && name[0] is 'F' or 'T' && char.IsUpper(name[1])) name = name[1..];
        return name.Trim();
    }

    /// <summary>
    /// Diagnostic companion to <see cref="AnalyzeLegacy"/>: runs the same taint analysis over ANY
    /// compiled shader stage in the map (vertex, shadow depth, velocity, other pixel permutations)
    /// and reports which material uniform expression values flow into that stage's outputs
    /// (<paramref name="flowsToOutputs"/>), plus the coarser set of material constant-buffer rows the
    /// stage's code reads at all (<paramref name="rowsRead"/>, row granularity — a row holds one
    /// vector expression or up to four packed scalars). Read-only; never used to draw connections.
    /// </summary>
    public static bool TryGetMaterialUse(FShaderLegacy shader, byte[] blob, FUniformExpressionSetLegacy expressionSet,
        out HashSet<PixelValueSource> flowsToOutputs, out HashSet<int> rowsRead, out string error)
    {
        flowsToOutputs = [];
        rowsRead = [];
        error = string.Empty;

        // same blob layout as TryAnalyzeShaderLegacy: [FD3D11ShaderResourceTable][DXBC container]
        var pos = 0;
        ReadU32(blob, ref pos); // ResourceTableBits
        var srvMap = ReadResourceMap(blob, ref pos);
        ReadResourceMap(blob, ref pos); // SamplerMap
        ReadResourceMap(blob, ref pos); // UnorderedAccessViewMap
        ReadResourceMap(blob, ref pos); // ResourceTableLayoutHashes
        var textureMap = ReadResourceMap(blob, ref pos);
        if (!HasDxbcMagic(blob, pos))
        {
            error = "shader bytecode is not D3D SM5 DXBC";
            return false;
        }
        if (!TryParseDxbcContainer(blob, pos, out _, out var program, out error, requireOutputSignature: false))
            return false;

        // the Material buffer slot: base-pass shaders carry it in MaterialParameters, every other
        // stage names it in the serialized UniformBufferParameters list (FShader::SerializeBase)
        var materialSlot = -1;
        if (shader.MaterialParameters?.MaterialUniformBuffer is { bIsBound: true } materialBuffer)
            materialSlot = materialBuffer.BaseIndex;
        else
            foreach (var (name, parameter) in shader.UniformBufferParameters ?? [])
                if (name == "Material" && parameter.bIsBound)
                {
                    materialSlot = parameter.BaseIndex;
                    break;
                }
        if (materialSlot < 0) return true; // this stage binds no material constants at all

        var vtStackCount = expressionSet.VTStacks?.Length ?? 0;
        var virtualCount = expressionSet.UniformVirtualTextureExpressions?.Length ?? 0;
        var vecCount = expressionSet.UniformVectorExpressions?.Length ?? 0;
        var scalarCount = expressionSet.UniformScalarExpressions?.Length ?? 0;
        var vecBase = vtStackCount * 32 + virtualCount * 16;

        var instructions = DecodeProgram(program, out _, out _);
        var context = new AnalysisContext
        {
            MaterialSlot = materialSlot,
            VecBase = vecBase,
            ScalarBase = vecBase + vecCount * 16,
            VecCount = vecCount,
            ScalarCount = scalarCount,
            TextureByRegister = BuildLegacyTextureRegisterMap(expressionSet, materialSlot, srvMap, textureMap)
        };

        // coarse pass: every static material cb row the code references (post-optimizer DXBC has
        // no dead reads, so a referenced row is a consumed row — including sample-UV-only math)
        foreach (var instruction in instructions)
            foreach (var operand in instruction.Operands)
                if (operand is { Type: 8, Dynamic0: false, Dynamic1: false, Index1: >= 0 } &&
                    operand.Index0 == materialSlot)
                    rowsRead.Add((int) operand.Index1);

        // precise pass: values that provably flow into this stage's outputs (for a vertex shader
        // that is exactly the customized-UV/WPO math handed to the interpolators)
        var (state, discardSink) = RunTaintAnalysis(instructions, context);
        foreach (var (key, components) in state.Registers)
        {
            if (key.Kind != 'o') continue;
            foreach (var component in components)
                flowsToOutputs.UnionWith(component);
        }
        flowsToOutputs.UnionWith(discardSink);
        return true;
    }

    /// <summary>
    /// Recovers the per-output expression DAGs of any compiled shader stage (vertex, geometry,
    /// pixel, …) by running the same symbolic decoder used for the base-pass pixel shader, but
    /// rooting every written output register by its signature semantic instead of the material
    /// GBuffer seeds. Returns an empty map for stages the linear decode cannot model (e.g. a
    /// compute shader that only writes UAVs). Every node is one decoded DXBC instruction — nothing
    /// is synthesized — so the graph can expand the stage into real nodes.
    /// </summary>
    public static Dictionary<string, PixelExpressionNode> BuildStageExpressions(
        FShaderLegacy shader, byte[] blob, FUniformExpressionSetLegacy expressionSet)
    {
        var result = new Dictionary<string, PixelExpressionNode>();
        if (blob is not { Length: > 0 }) return result;

        // [FD3D11ShaderResourceTable][DXBC container] prefix, as in TryAnalyzeShaderLegacy
        var pos = 0;
        ReadU32(blob, ref pos); // ResourceTableBits
        var srvMap = ReadResourceMap(blob, ref pos);
        ReadResourceMap(blob, ref pos); // SamplerMap
        ReadResourceMap(blob, ref pos); // UnorderedAccessViewMap
        ReadResourceMap(blob, ref pos); // ResourceTableLayoutHashes
        var textureMap = ReadResourceMap(blob, ref pos);
        if (!HasDxbcMagic(blob, pos)) return result;

        if (!TryParseDxbcContainer(blob, pos, out _, out var program, out var inputSemantics,
                out var outputSemantics, out _, requireOutputSignature: false))
            return result;
        if (program == null || program.Length < 2 || outputSemantics.Count == 0)
            return result; // no output registers to root (compute writes UAVs, etc.)

        var instructions = DecodeProgram(program, out _, out var resourceDimensions);

        var materialSlot = -1;
        if (shader.MaterialParameters?.MaterialUniformBuffer is { bIsBound: true } materialBuffer)
            materialSlot = materialBuffer.BaseIndex;
        else
            foreach (var (name, parameter) in shader.UniformBufferParameters ?? [])
                if (name == "Material" && parameter.bIsBound)
                {
                    materialSlot = parameter.BaseIndex;
                    break;
                }

        var vtStackCount = expressionSet.VTStacks?.Length ?? 0;
        var virtualCount = expressionSet.UniformVirtualTextureExpressions?.Length ?? 0;
        var vecCount = expressionSet.UniformVectorExpressions?.Length ?? 0;
        var scalarCount = expressionSet.UniformScalarExpressions?.Length ?? 0;
        var vecBase = vtStackCount * 32 + virtualCount * 16;

        var context = new AnalysisContext
        {
            MaterialSlot = materialSlot,
            VecBase = vecBase,
            ScalarBase = vecBase + vecCount * 16,
            VecCount = vecCount,
            ScalarCount = scalarCount,
            TextureByRegister = materialSlot >= 0
                ? BuildLegacyTextureRegisterMap(expressionSet, materialSlot, srvMap, textureMap)
                : new Dictionary<long, (int Slot, int Index)>()
        };

        // serialized uniform buffer names so foreign cb leaves read "View cb0[135]" etc.
        var cbNames = new Dictionary<int, string>();
        foreach (var (name, parameter) in shader.UniformBufferParameters ?? [])
            if (parameter.bIsBound)
                cbNames.TryAdd(parameter.BaseIndex, name);

        var scratch = new PixelShaderWiring();
        BuildPinExpressions(instructions, context, new Dictionary<long, int>(), usesGBuffer: false,
            cbNames, inputSemantics, resourceDimensions, scratch,
            resultOverride: result, outputSemantics: outputSemantics);
        return result;
    }

    private static bool TryAnalyzeShaderLegacy(FShaderLegacy shader, byte[] blob, FUniformExpressionSetLegacy expressionSet,
        bool usesGBuffer, PixelShaderWiring wiring, out string error)
    {
        var typeName = shader.TypeName; // blob is already Zlib-decompressed (inline via CUE4Parse, shared via the library lookup)

        // blob layout on PC D3D: [FD3D11ShaderResourceTable][DXBC container][optional data]
        // (D3D11ShaderResources.h, identical member order in 4.23 and 4.25+)
        var pos = 0;
        ReadU32(blob, ref pos); // ResourceTableBits
        var srvMap = ReadResourceMap(blob, ref pos);
        ReadResourceMap(blob, ref pos); // SamplerMap
        ReadResourceMap(blob, ref pos); // UnorderedAccessViewMap
        ReadResourceMap(blob, ref pos); // ResourceTableLayoutHashes
        var textureMap = ReadResourceMap(blob, ref pos);
        if (!HasDxbcMagic(blob, pos))
        {
            error = $"{typeName}: shader bytecode is not D3D SM5 DXBC (platform not supported)";
            return false;
        }

        if (!TryParseDxbcContainer(blob, pos, out var outputRegToTarget, out var program, out var inputSemantics, out var containerError))
        {
            error = $"{typeName}: {containerError}";
            return false;
        }
        if (program[0] >> 16 != 0)
        {
            error = $"{typeName}: DXBC program is not a pixel shader";
            return false;
        }

        var instructions = DecodeProgram(program, out _, out var resourceDimensions);

        // constant-buffer layout (4.23 FUniformExpressionSet::CreateBufferStruct): 2 uint4
        // rows per VT stack, 1 uint4 per virtual texture, one float4 per vector expression,
        // scalars packed 4 per float4 — the array index IS the expression identity
        var vtStackCount = expressionSet.VTStacks?.Length ?? 0;
        var virtualCount = expressionSet.UniformVirtualTextureExpressions?.Length ?? 0;
        var vecCount = expressionSet.UniformVectorExpressions?.Length ?? 0;
        var scalarCount = expressionSet.UniformScalarExpressions?.Length ?? 0;
        var vecBase = vtStackCount * 32 + virtualCount * 16;
        var scalarBase = vecBase + vecCount * 16;

        // the Material constant buffer slot is serialized directly (FMaterialShader::Serialize)
        var materialSlot = shader.MaterialParameters.MaterialUniformBuffer.bIsBound
            ? shader.MaterialParameters.MaterialUniformBuffer.BaseIndex
            : -1;

        var textureByRegister = BuildLegacyTextureRegisterMap(expressionSet, materialSlot, srvMap, textureMap);

        var context = new AnalysisContext
        {
            MaterialSlot = materialSlot,
            VecBase = vecBase,
            ScalarBase = scalarBase,
            VecCount = vecCount,
            ScalarCount = scalarCount,
            TextureByRegister = textureByRegister
        };
        var (state, discardSink) = RunTaintAnalysis(instructions, context);

        MapSinksToPins(state, discardSink, outputRegToTarget, usesGBuffer, wiring);
        try
        {
            BuildPinDisassemblies(instructions, context, outputRegToTarget, usesGBuffer, typeName, wiring);
        }
        catch
        {
            // the disassembly view is auxiliary — never fail the recovered wiring over it
        }
        try
        {
            // cb slot → uniform buffer struct name, straight from the serialized shader tail
            var cbNames = new Dictionary<int, string>();
            foreach (var (name, parameter) in shader.UniformBufferParameters ?? [])
                if (parameter.bIsBound)
                    cbNames.TryAdd(parameter.BaseIndex, name);
            BuildPinExpressions(instructions, context, outputRegToTarget, usesGBuffer,
                cbNames, inputSemantics, resourceDimensions, wiring);
        }
        catch
        {
            // expression recovery is auxiliary too — the opaque combiner remains the fallback
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Maps t# registers to legacy material texture bindings. Resource member order per
    /// 4.23 FUniformExpressionSet::CreateBufferStruct: texture+sampler pairs for 2D, Cube,
    /// Volume and External expressions, page table textures per VT stack (second one only
    /// when the stack has more than 4 layers — 4.23 has no page table indirection texture),
    /// physical SRV+sampler per virtual texture, then the two shared world-group samplers.
    /// Slot ids follow the analyzer's convention: 0=2D, 1=Cube, 3=Volume, 4=Virtual.
    /// </summary>
    private static Dictionary<long, (int Slot, int Index)> BuildLegacyTextureRegisterMap(
        FUniformExpressionSetLegacy expressionSet, int materialSlot, uint[] srvMap, uint[] textureMap)
    {
        var registers = new Dictionary<long, (int, int)>();
        if (materialSlot < 0) return registers;

        var positions = new List<(int Slot, int Index)?>();
        void AddPairs(int count, int slot)
        {
            for (var i = 0; i < count; i++)
            {
                positions.Add(slot >= 0 ? (slot, i) : null);
                positions.Add(null); // sampler
            }
        }
        AddPairs(expressionSet.Uniform2DTextureExpressions?.Length ?? 0, 0);
        AddPairs(expressionSet.UniformCubeTextureExpressions?.Length ?? 0, 1);
        AddPairs(expressionSet.UniformVolumeTextureExpressions?.Length ?? 0, 3);
        AddPairs(expressionSet.UniformExternalTextureExpressions?.Length ?? 0, -1);
        foreach (var stack in expressionSet.VTStacks ?? [])
        {
            positions.Add(null); // PageTable0
            if (stack.NumLayers > 4) positions.Add(null); // PageTable1
        }
        for (var i = 0; i < (expressionSet.UniformVirtualTextureExpressions?.Length ?? 0); i++)
        {
            positions.Add((4, i)); // physical texture SRV
            positions.Add(null); // sampler
        }
        positions.Add(null); // Wrap_WorldGroupSettings sampler
        positions.Add(null); // Clamp_WorldGroupSettings sampler

        void MapEntries(uint[] map)
        {
            if (map == null || materialSlot >= map.Length) return;
            var offset = map[materialSlot];
            if (offset == 0 || offset >= map.Length) return;
            for (var i = (long) offset; i < map.Length; i++)
            {
                var token = map[i];
                if (token == 0xFFFFFFFFu) break;
                if ((int) (token >> 24) != materialSlot) break;
                var resourceIndex = (int) ((token >> 8) & 0xFFFF);
                var bindIndex = (int) (token & 0xFF);
                if (resourceIndex < positions.Count && positions[resourceIndex] is { } texture)
                    registers[bindIndex] = texture;
            }
        }

        MapEntries(textureMap);
        MapEntries(srvMap); // virtual texture physical textures bind as SRVs

        return registers;
    }

    #endregion

    #region Full shader disassembly

    /// <summary>
    /// Disassembles an entire legacy shader program (any frequency), unlike the per-pin slices
    /// of the wiring analysis. The blob is the decompressed FShaderCode payload:
    /// [FD3D11ShaderResourceTable][DXBC container][optional data]. Material cbuffer and texture
    /// annotations are added when the shader binds the Material uniform buffer. Never throws —
    /// problems come back as a "// …" comment so the UI can always show something.
    /// </summary>
    public static string DisassembleLegacyShader(FShaderLegacy shader, byte[] blob, FUniformExpressionSetLegacy expressionSet)
    {
        if (blob == null || blob.Length == 0)
            return "// no shader bytecode available";
        try
        {
            return DisassembleLegacyShaderInner(shader, blob, expressionSet);
        }
        catch (Exception e)
        {
            return $"// disassembly failed: {e.Message}";
        }
    }

    private static string DisassembleLegacyShaderInner(FShaderLegacy shader, byte[] blob, FUniformExpressionSetLegacy expressionSet)
    {
        // the D3D11 shader compiler prepends the serialized FD3D11ShaderResourceTable for every
        // frequency; when the header does not parse, fall back to scanning for the DXBC magic
        // (the container parse re-validates the hit against its own total size)
        uint[] srvMap = null, textureMap = null;
        var pos = 0;
        try
        {
            var headerPos = 0;
            ReadU32(blob, ref headerPos); // ResourceTableBits
            var srv = ReadResourceMap(blob, ref headerPos);
            ReadResourceMap(blob, ref headerPos); // SamplerMap
            ReadResourceMap(blob, ref headerPos); // UnorderedAccessViewMap
            ReadResourceMap(blob, ref headerPos); // ResourceTableLayoutHashes
            var textures = ReadResourceMap(blob, ref headerPos);
            if (HasDxbcMagic(blob, headerPos))
            {
                srvMap = srv;
                textureMap = textures;
                pos = headerPos;
            }
        }
        catch (InvalidOperationException)
        {
            // not the expected resource-table layout; the scan below still finds the container
        }
        if (!HasDxbcMagic(blob, pos))
        {
            pos = -1;
            for (var i = 0; i + 4 <= blob.Length; i++)
            {
                if (!HasDxbcMagic(blob, i)) continue;
                pos = i;
                break;
            }
            if (pos < 0)
                return "// shader bytecode is not D3D SM5 DXBC (platform not supported by the disassembler)";
        }

        if (!TryParseDxbcContainer(blob, pos, out var outputRegToTarget, out var program, out var containerError,
                requireOutputSignature: false))
            return $"// {containerError}";

        var programType = (int) (program[0] >> 16); // D3D10_SB_TOKENIZED_PROGRAM_TYPE
        var versionMajor = (program[0] >> 4) & 0xF;
        var versionMinor = program[0] & 0xF;
        var instructions = DecodeProgram(program, out var declaredCbSizes);

        // material annotations only make sense when this shader binds the Material cbuffer
        var materialSlot = shader?.MaterialParameters?.MaterialUniformBuffer is { bIsBound: true } materialBuffer
            ? materialBuffer.BaseIndex
            : -1;
        var vtStackCount = expressionSet?.VTStacks?.Length ?? 0;
        var virtualCount = expressionSet?.UniformVirtualTextureExpressions?.Length ?? 0;
        var vecCount = expressionSet?.UniformVectorExpressions?.Length ?? 0;
        var scalarCount = expressionSet?.UniformScalarExpressions?.Length ?? 0;
        var vecBase = vtStackCount * 32 + virtualCount * 16;
        var context = new AnalysisContext
        {
            MaterialSlot = materialSlot,
            VecBase = vecBase,
            ScalarBase = vecBase + vecCount * 16,
            VecCount = vecCount,
            ScalarCount = scalarCount,
            TextureByRegister = expressionSet != null && materialSlot >= 0 && srvMap != null
                ? BuildLegacyTextureRegisterMap(expressionSet, materialSlot, srvMap, textureMap)
                : new Dictionary<long, (int Slot, int Index)>()
        };

        var builder = new StringBuilder();
        builder.Append("// ").Append(shader?.TypeName ?? "shader");
        if (!string.IsNullOrEmpty(shader?.VertexFactoryTypeName))
            builder.Append(" — vertex factory ").Append(shader.VertexFactoryTypeName);
        builder.Append('\n');
        builder.Append("// ").Append(ProgramTypeName(programType)).Append(" shader, model ")
            .Append(versionMajor).Append('.').Append(versionMinor)
            .Append(", ").Append(instructions.Count).Append(" instructions (resource declarations not shown)\n");

        // cb slot → uniform buffer struct name, straight from the serialized shader tail
        foreach (var (name, parameter) in shader?.UniformBufferParameters ?? [])
        {
            if (!parameter.bIsBound) continue;
            builder.Append("// cb").Append(parameter.BaseIndex).Append(" = ").Append(name);
            if (declaredCbSizes.TryGetValue(parameter.BaseIndex, out var rows))
                builder.Append(" (").Append(rows).Append(" float4 rows)");
            builder.Append('\n');
        }
        foreach (var (register, texture) in context.TextureByRegister.OrderBy(t => t.Key))
            builder.Append("// t").Append(register).Append(" = material ").Append(TextureSlotName(texture.Slot))
                .Append(" texture #").Append(texture.Index).Append('\n');
        foreach (var (register, target) in outputRegToTarget.OrderBy(t => t.Key))
            builder.Append("// o").Append(register).Append(" = SV_Target").Append(target).Append('\n');

        var depth = ComputeControlFlowDepth(instructions, out _);
        const int maxLines = 20000;
        for (var i = 0; i < instructions.Count && i < maxLines; i++)
            builder.Append(new string(' ', Math.Min(depth[i], 8) * 2))
                .Append(FormatInstruction(instructions[i], context, outputRegToTarget)).Append('\n');
        if (instructions.Count > maxLines)
            builder.Append("// … ").Append(instructions.Count - maxLines).Append(" more instructions omitted");
        return builder.ToString().TrimEnd('\n');
    }

    private static string ProgramTypeName(int programType) => programType switch
    {
        0 => "pixel", 1 => "vertex", 2 => "geometry", 3 => "hull", 4 => "domain", 5 => "compute",
        _ => $"type-{programType}"
    };

    #endregion

    #region Blob / DXBC container parsing

    private static uint ReadU32(byte[] blob, ref int pos)
    {
        if (pos + 4 > blob.Length) throw new InvalidOperationException("shader blob truncated");
        var value = BitConverter.ToUInt32(blob, pos);
        pos += 4;
        return value;
    }

    private static uint[] ReadResourceMap(byte[] blob, ref int pos)
    {
        var count = (int) ReadU32(blob, ref pos);
        if (count < 0 || count > 0xFFFF || pos + count * 4 > blob.Length)
            throw new InvalidOperationException("unexpected shader resource table layout");
        var values = new uint[count];
        for (var i = 0; i < count; i++)
            values[i] = ReadU32(blob, ref pos);
        return values;
    }

    private static bool HasDxbcMagic(byte[] blob, int pos) =>
        pos >= 0 && pos + 4 <= blob.Length &&
        blob[pos] == (byte) 'D' && blob[pos + 1] == (byte) 'X' && blob[pos + 2] == (byte) 'B' && blob[pos + 3] == (byte) 'C';

    private static bool TryParseDxbcContainer(byte[] blob, int start,
        out Dictionary<long, int> outputRegToTarget, out uint[] program, out string error,
        bool requireOutputSignature = true)
        => TryParseDxbcContainer(blob, start, out outputRegToTarget, out program, out _, out error, requireOutputSignature);

    private static bool TryParseDxbcContainer(byte[] blob, int start,
        out Dictionary<long, int> outputRegToTarget, out uint[] program,
        out Dictionary<long, string> inputSemantics, out string error,
        bool requireOutputSignature = true)
        => TryParseDxbcContainer(blob, start, out outputRegToTarget, out program, out inputSemantics, out _, out error, requireOutputSignature);

    private static bool TryParseDxbcContainer(byte[] blob, int start,
        out Dictionary<long, int> outputRegToTarget, out uint[] program,
        out Dictionary<long, string> inputSemantics, out Dictionary<long, string> outputSemantics,
        out string error, bool requireOutputSignature = true)
    {
        outputRegToTarget = new Dictionary<long, int>();
        inputSemantics = new Dictionary<long, string>();
        outputSemantics = new Dictionary<long, string>();
        program = null;
        error = string.Empty;

        var pos = start + 20; // magic + 16-byte digest
        ReadU32(blob, ref pos); // version (u16 major + u16 minor)
        var totalSize = ReadU32(blob, ref pos);
        var chunkCount = ReadU32(blob, ref pos);
        if (totalSize > blob.Length - start || chunkCount > 64)
        {
            error = "malformed DXBC container";
            return false;
        }

        var foundOutputs = false;
        for (var i = 0; i < chunkCount; i++)
        {
            var offsetPos = start + 32 + i * 4;
            var chunkStart = start + (int) BitConverter.ToUInt32(blob, offsetPos);
            if (chunkStart + 8 > blob.Length) continue;
            var fourCc = Encoding.ASCII.GetString(blob, chunkStart, 4);
            var chunkSize = (int) BitConverter.ToUInt32(blob, chunkStart + 4);
            var dataStart = chunkStart + 8;
            if (dataStart + chunkSize > blob.Length) continue;

            switch (fourCc)
            {
                case "OSGN":
                    ParseOutputSignature(blob, dataStart, outputRegToTarget);
                    ParseOutputSemantics(blob, dataStart, outputSemantics);
                    foundOutputs = true;
                    break;
                case "ISGN":
                    ParseInputSignature(blob, dataStart, inputSemantics);
                    break;
                case "SHEX" or "SHDR":
                    var dwordCount = chunkSize / 4;
                    program = new uint[dwordCount];
                    Buffer.BlockCopy(blob, dataStart, program, 0, dwordCount * 4);
                    break;
            }
        }

        if (!foundOutputs && requireOutputSignature)
        {
            error = "DXBC output signature (OSGN) chunk missing";
            return false;
        }
        if (program == null || program.Length < 2)
        {
            error = "DXBC shader program (SHEX/SHDR) chunk missing";
            return false;
        }
        return true;
    }

    private static void ParseOutputSignature(byte[] blob, int dataStart, Dictionary<long, int> outputRegToTarget)
    {
        var count = BitConverter.ToUInt32(blob, dataStart);
        for (var i = 0; i < count; i++)
        {
            var element = dataStart + 8 + i * 24;
            var nameOffset = (int) BitConverter.ToUInt32(blob, element);
            var semanticIndex = (int) BitConverter.ToUInt32(blob, element + 4);
            var register = BitConverter.ToUInt32(blob, element + 16);
            if (register == 0xFFFFFFFFu) continue; // system values (oDepth, coverage)

            var nameStart = dataStart + nameOffset;
            var nameEnd = nameStart;
            while (nameEnd < blob.Length && blob[nameEnd] != 0) nameEnd++;
            var name = Encoding.ASCII.GetString(blob, nameStart, nameEnd - nameStart);
            if (name.Equals("SV_Target", StringComparison.OrdinalIgnoreCase))
                outputRegToTarget[register] = semanticIndex;
        }
    }

    /// <summary>
    /// Every output register's full semantic name (SV_Position, TEXCOORD3, SV_Target1, …) so a
    /// non-pixel stage's decoded outputs can be labeled when the graph expands its math. A
    /// register that packs several semantics keeps the first (they share the register anyway).
    /// </summary>
    private static void ParseOutputSemantics(byte[] blob, int dataStart, Dictionary<long, string> outputSemantics)
    {
        var count = BitConverter.ToUInt32(blob, dataStart);
        for (var i = 0; i < count; i++)
        {
            var element = dataStart + 8 + i * 24;
            var nameOffset = (int) BitConverter.ToUInt32(blob, element);
            var semanticIndex = (int) BitConverter.ToUInt32(blob, element + 4);
            var register = BitConverter.ToUInt32(blob, element + 16);
            if (register == 0xFFFFFFFFu) continue; // system-value-only rows (oDepth, coverage)

            var nameStart = dataStart + nameOffset;
            var nameEnd = nameStart;
            while (nameEnd < blob.Length && blob[nameEnd] != 0) nameEnd++;
            var name = Encoding.ASCII.GetString(blob, nameStart, nameEnd - nameStart);
            var label = semanticIndex > 0 ? $"{name}{semanticIndex}" : name;
            if (!outputSemantics.ContainsKey(register))
                outputSemantics[register] = label;
        }
    }

    /// <summary>
    /// ISGN elements share the OSGN layout; v# registers get their semantic name so the
    /// expression recovery can label interpolant leaves (a register packing several semantics
    /// keeps them all, joined).
    /// </summary>
    private static void ParseInputSignature(byte[] blob, int dataStart, Dictionary<long, string> inputSemantics)
    {
        var count = BitConverter.ToUInt32(blob, dataStart);
        if (count > 64) return;
        for (var i = 0; i < count; i++)
        {
            var element = dataStart + 8 + i * 24;
            if (element + 24 > blob.Length) return;
            var nameOffset = (int) BitConverter.ToUInt32(blob, element);
            var semanticIndex = BitConverter.ToUInt32(blob, element + 4);
            var register = BitConverter.ToUInt32(blob, element + 16);
            if (register == 0xFFFFFFFFu) continue;

            var nameStart = dataStart + nameOffset;
            var nameEnd = nameStart;
            while (nameEnd < blob.Length && blob[nameEnd] != 0) nameEnd++;
            if (nameEnd <= nameStart) continue;
            var name = Encoding.ASCII.GetString(blob, nameStart, nameEnd - nameStart) + semanticIndex;
            inputSemantics[register] = inputSemantics.TryGetValue(register, out var existing)
                ? $"{existing} / {name}"
                : name;
        }
    }

    #endregion

    #region Instruction decoding

    private const int OpcodeDiscard = 13;
    private const int OpcodeDclConstantBuffer = 89;
    private const int OpcodeCustomData = 53;

    private sealed class Operand
    {
        public int Type;
        public int[] Swizzle = [0, 1, 2, 3];
        public int Mask = 0xF;
        public long Index0 = -1, Index1 = -1;
        public bool Dynamic0, Dynamic1;
        public int IndexDimension;
        public int SelMode = -1; // 0 = write mask, 1 = swizzle, 2 = select-one, -1 = scalar/none
        public bool Neg, Abs;
        public uint[] Immediates;
    }

    private sealed class Instruction
    {
        public int Opcode;
        public List<Operand> Operands;
        public bool Saturate;
        public bool TestNonZero;
    }

    private static bool IsDeclarationOpcode(int opcode) =>
        opcode is >= 88 and <= 106 or >= 113 and <= 116 or >= 143 and <= 162;

    private static List<Instruction> DecodeProgram(uint[] program, out Dictionary<int, int> declaredCbSizes)
        => DecodeProgram(program, out declaredCbSizes, out _);

    private static List<Instruction> DecodeProgram(uint[] program, out Dictionary<int, int> declaredCbSizes,
        out Dictionary<long, int> resourceDimensions)
    {
        var instructions = new List<Instruction>();
        declaredCbSizes = new Dictionary<int, int>();
        resourceDimensions = new Dictionary<long, int>();

        var pos = 2; // version + length tokens
        var length = Math.Min((int) program[1], program.Length);
        while (pos < length)
        {
            var token = program[pos];
            var opcode = (int) (token & 0x7FF);

            if (opcode == OpcodeCustomData)
            {
                if (pos + 1 >= length) break;
                var customLength = (int) program[pos + 1];
                if (customLength < 2) break;
                pos += customLength;
                continue;
            }

            var instructionLength = (int) ((token >> 24) & 0x7F);
            if (instructionLength == 0 || pos + instructionLength > length) break;
            var end = pos + instructionLength;

            if (IsDeclarationOpcode(opcode) && opcode != OpcodeDclConstantBuffer)
            {
                if (opcode == 88) // dcl_resource: dimension in token bits [15:11], operand is the t#
                {
                    var declPos = pos + 1;
                    var resourceOperand = ParseOperand(program, ref declPos, end);
                    if (resourceOperand is { Type: 7, Dynamic0: false, Index0: >= 0 })
                        resourceDimensions[resourceOperand.Index0] = (int) ((token >> 11) & 0x1F);
                }
                pos = end; // declarations otherwise carry literals that are not operand-encoded
                continue;
            }

            // skip chained extended opcode tokens (sample controls etc.)
            var p = pos + 1;
            var current = token;
            while ((current & 0x80000000u) != 0 && p < end)
            {
                current = program[p];
                p++;
            }

            var operands = new List<Operand>();
            var malformed = false;
            while (p < end)
            {
                var operand = ParseOperand(program, ref p, end);
                if (operand == null)
                {
                    malformed = true;
                    break;
                }
                operands.Add(operand);
            }

            if (!malformed)
            {
                if (opcode == OpcodeDclConstantBuffer)
                {
                    if (operands.Count > 0 && operands[0].Type == 8 && operands[0].IndexDimension >= 2 &&
                        !operands[0].Dynamic0 && !operands[0].Dynamic1)
                        declaredCbSizes[(int) operands[0].Index0] = (int) operands[0].Index1;
                }
                else
                {
                    instructions.Add(new Instruction
                    {
                        Opcode = opcode,
                        Operands = operands,
                        Saturate = (token & (1u << 13)) != 0,
                        TestNonZero = (token & (1u << 18)) != 0
                    });
                }
            }

            pos = end;
        }

        return instructions;
    }

    private static Operand ParseOperand(uint[] program, ref int pos, int end)
    {
        if (pos >= end) return null;
        var token = program[pos];
        pos++;

        var operand = new Operand();
        var componentField = (int) (token & 3);
        var componentCount = componentField switch { 0 => 0, 1 => 1, 2 => 4, _ => -1 };
        if (componentCount < 0) return null; // N-component operands never appear in SM5 material shaders

        if (componentCount == 4)
        {
            operand.SelMode = (int) ((token >> 2) & 3);
            switch (operand.SelMode)
            {
                case 0: // mask
                    operand.Mask = (int) ((token >> 4) & 0xF);
                    break;
                case 1: // swizzle
                    operand.Swizzle =
                    [
                        (int) ((token >> 4) & 3), (int) ((token >> 6) & 3),
                        (int) ((token >> 8) & 3), (int) ((token >> 10) & 3)
                    ];
                    break;
                case 2: // select one component
                    var component = (int) ((token >> 4) & 3);
                    operand.Swizzle = [component, component, component, component];
                    operand.Mask = 1 << component;
                    break;
            }
        }
        else if (componentCount == 1)
        {
            operand.Swizzle = [0, 0, 0, 0];
            operand.Mask = 1;
        }

        operand.Type = (int) ((token >> 12) & 0xFF);
        operand.IndexDimension = (int) ((token >> 20) & 3);

        // extended operand tokens (modifiers) chain on bit 31
        var extended = (token & 0x80000000u) != 0;
        while (extended)
        {
            if (pos >= end) return null;
            var extendedToken = program[pos];
            if ((extendedToken & 0x3F) == 1) // operand modifier: 1 = neg, 2 = abs, 3 = -abs
            {
                var modifier = (int) ((extendedToken >> 6) & 0xFF);
                operand.Neg = modifier is 1 or 3;
                operand.Abs = modifier is 2 or 3;
            }
            extended = (extendedToken & 0x80000000u) != 0;
            pos++;
        }

        if (operand.Type == 4) // 32-bit immediate: 1 or 4 values
        {
            var valueCount = componentCount == 4 ? 4 : 1;
            if (pos + valueCount > end) return null;
            operand.Immediates = new uint[valueCount];
            for (var i = 0; i < valueCount; i++)
                operand.Immediates[i] = program[pos + i];
            pos += valueCount;
            return operand;
        }
        if (operand.Type == 5) // 64-bit immediate
        {
            pos += (componentCount == 4 ? 4 : 1) * 2;
            return pos <= end ? operand : null;
        }

        for (var i = 0; i < operand.IndexDimension; i++)
        {
            var representation = (int) ((token >> (22 + 3 * i)) & 7);
            long index = -1;
            var dynamic = false;
            switch (representation)
            {
                case 0: // imm32
                    if (pos >= end) return null;
                    index = program[pos];
                    pos++;
                    break;
                case 1: // imm64
                    if (pos + 2 > end) return null;
                    index = program[pos];
                    pos += 2;
                    break;
                case 2: // relative operand only
                    if (ParseOperand(program, ref pos, end) == null) return null;
                    dynamic = true;
                    break;
                case 3: // imm32 + relative
                    if (pos >= end) return null;
                    index = program[pos];
                    pos++;
                    if (ParseOperand(program, ref pos, end) == null) return null;
                    dynamic = true;
                    break;
                case 4: // imm64 + relative
                    if (pos + 2 > end) return null;
                    index = program[pos];
                    pos += 2;
                    if (ParseOperand(program, ref pos, end) == null) return null;
                    dynamic = true;
                    break;
                default:
                    return null;
            }
            if (i == 0)
            {
                operand.Index0 = index;
                operand.Dynamic0 = dynamic;
            }
            else if (i == 1)
            {
                operand.Index1 = index;
                operand.Dynamic1 = dynamic;
            }
        }

        return operand;
    }

    #endregion

    #region Taint analysis

    private sealed class AnalysisContext
    {
        public int MaterialSlot;
        public int VecBase, ScalarBase, VecCount, ScalarCount;
        public Dictionary<long, (int Slot, int Index)> TextureByRegister;
    }

    private sealed class TaintState
    {
        public readonly Dictionary<(char Kind, long Register), HashSet<PixelValueSource>[]> Registers = new();

        public HashSet<PixelValueSource>[] GetOrAdd(char kind, long register)
        {
            var key = (kind, register);
            if (!Registers.TryGetValue(key, out var components))
            {
                components = [new(), new(), new(), new()];
                Registers[key] = components;
            }
            return components;
        }

        public TaintState Clone()
        {
            var clone = new TaintState();
            foreach (var (key, components) in Registers)
                clone.Registers[key] = [new(components[0]), new(components[1]), new(components[2]), new(components[3])];
            return clone;
        }

        public void MergeWith(TaintState other)
        {
            foreach (var (key, components) in other.Registers)
            {
                var target = GetOrAdd(key.Kind, key.Register);
                for (var c = 0; c < 4; c++)
                    target[c].UnionWith(components[c]);
            }
        }
    }

    // opcodes that neither produce a register value nor need special handling: flow control,
    // ret, emit/cut, sync and the UAV store/atomic family (base-pass UAV traffic is virtual
    // texture feedback, which never feeds a render target)
    private static readonly HashSet<int> PassiveOpcodes =
        [2, 3, 4, 5, 6, 7, 8, 9, 10, 19, 44, 58, 62, 63, 117, 118, 119, 120, 190, 207, 208];

    private static readonly HashSet<int> TwoDestOpcodes = [38, 77, 78, 81, 142]; // imul, sincos, udiv, umul, swapc

    private static (TaintState State, HashSet<PixelValueSource> DiscardSink) RunTaintAnalysis(
        List<Instruction> instructions, AnalysisContext context)
    {
        var state = new TaintState();
        var discardSink = new HashSet<PixelValueSource>();
        string previousSignature = null;

        // up to three passes so labels carried backwards by loops stabilize
        for (var pass = 0; pass < 3; pass++)
        {
            var controlFlow = new Stack<(TaintState Entry, TaintState ThenExit)>();
            foreach (var instruction in instructions)
            {
                switch (instruction.Opcode)
                {
                    case 31 or 48 or 76: // if / loop / switch
                        controlFlow.Push((state.Clone(), null));
                        break;
                    case 18: // else: keep the then-branch exit, restart from the entry state
                        if (controlFlow.Count > 0)
                        {
                            var frame = controlFlow.Pop();
                            controlFlow.Push((frame.Entry, state));
                            state = frame.Entry.Clone();
                        }
                        break;
                    case 21 or 22 or 23: // endif / endloop / endswitch: both paths may have run
                        if (controlFlow.Count > 0)
                        {
                            var frame = controlFlow.Pop();
                            state.MergeWith(frame.ThenExit ?? frame.Entry);
                        }
                        break;
                    case OpcodeDiscard: // the discard condition is the Opacity Mask input
                        if (instruction.Operands.Count > 0)
                            for (var c = 0; c < 4; c++)
                                discardSink.UnionWith(SourceLabels(state, instruction.Operands[0], c, context));
                        break;
                    default:
                        ApplyInstruction(state, instruction, context);
                        break;
                }
            }

            var signature = BuildSinkSignature(state, discardSink);
            if (signature == previousSignature) break;
            previousSignature = signature;
        }

        return (state, discardSink);
    }

    private static string BuildSinkSignature(TaintState state, HashSet<PixelValueSource> discardSink)
    {
        var builder = new StringBuilder();
        foreach (var (key, components) in state.Registers.Where(r => r.Key.Kind == 'o').OrderBy(r => r.Key.Register))
        {
            builder.Append(key.Register).Append(':');
            for (var c = 0; c < 4; c++)
            {
                foreach (var source in components[c].OrderBy(s => s.ToString()))
                    builder.Append(source).Append(',');
                builder.Append(';');
            }
        }
        builder.Append('|');
        foreach (var source in discardSink.OrderBy(s => s.ToString()))
            builder.Append(source).Append(',');
        return builder.ToString();
    }

    private static void ApplyInstruction(TaintState state, Instruction instruction, AnalysisContext context)
    {
        var opcode = instruction.Opcode;
        if (PassiveOpcodes.Contains(opcode) || opcode is >= 164 and <= 189) return;

        var destCount = TwoDestOpcodes.Contains(opcode) ? 2 : 1;
        if (instruction.Operands.Count <= destCount) return;

        var labels = new HashSet<PixelValueSource>[4];

        var resourceOperandIndex = GetResourceOperandIndex(opcode);
        if (resourceOperandIndex >= 0 && resourceOperandIndex < instruction.Operands.Count)
        {
            // texture read: the dest holds texture data; which channel comes from the
            // resource operand's swizzle. Coordinate taint is deliberately not propagated —
            // UVs feeding a sample are not the sampled value.
            var resource = instruction.Operands[resourceOperandIndex];
            (int Slot, int Index)? texture = null;
            if (resource.Type == 7 && !resource.Dynamic0 && resource.Index0 >= 0)
                texture = context.TextureByRegister.TryGetValue(resource.Index0, out var mapped) ? mapped : null;

            for (var c = 0; c < 4; c++)
            {
                labels[c] = new HashSet<PixelValueSource>();
                if (texture is { } tex)
                {
                    var channel = GetSampleChannel(opcode, resource, c);
                    labels[c].Add(PixelValueSource.Texture(tex.Slot, tex.Index, channel));
                }
            }
        }
        else if (opcode is 15 or 16 or 17) // dp2/dp3/dp4 collapse components horizontally
        {
            var componentCount = opcode - 13;
            var combined = new HashSet<PixelValueSource>();
            for (var c = 0; c < componentCount; c++)
            for (var s = destCount; s < instruction.Operands.Count; s++)
                combined.UnionWith(SourceLabels(state, instruction.Operands[s], c, context));
            for (var c = 0; c < 4; c++)
                labels[c] = combined;
        }
        else
        {
            for (var c = 0; c < 4; c++)
            {
                labels[c] = new HashSet<PixelValueSource>();
                for (var s = destCount; s < instruction.Operands.Count; s++)
                    labels[c].UnionWith(SourceLabels(state, instruction.Operands[s], c, context));
            }
        }

        for (var d = 0; d < destCount; d++)
            WriteDestination(state, instruction.Operands[d], labels);
    }

    private static int GetResourceOperandIndex(int opcode) => opcode switch
    {
        45 or 46 => 2, // ld, ld_ms
        61 => 2, // resinfo
        >= 69 and <= 74 => 2, // sample family
        108 => 2, // lod
        109 or 110 => 2, // gather4, sample_pos
        111 => 1, // sample_info
        121 => 1, // bufinfo
        126 => 2, // gather4_c
        127 or 128 => 3, // gather4_po, gather4_po_c
        _ => -1
    };

    private static int GetSampleChannel(int opcode, Operand resource, int destComponent) => opcode switch
    {
        61 or 108 or 110 or 111 or 121 => -1, // metadata queries touch the texture as a whole
        109 or 126 or 127 or 128 => resource.Swizzle[0], // gather4 reads one channel of 4 texels
        _ => resource.Swizzle[destComponent]
    };

    private static readonly HashSet<PixelValueSource> EmptyLabels = [];

    private static HashSet<PixelValueSource> SourceLabels(TaintState state, Operand operand, int component, AnalysisContext context)
    {
        switch (operand.Type)
        {
            case 0: // r# temp
                return state.Registers.TryGetValue(('r', operand.Index0), out var temp)
                    ? temp[operand.Swizzle[component]]
                    : EmptyLabels;
            case 3: // x# indexable temp — tracked as a whole-register blob
            {
                var union = new HashSet<PixelValueSource>();
                foreach (var (key, components) in state.Registers)
                {
                    if (key.Kind != 'x' || (!operand.Dynamic0 && key.Register != operand.Index0)) continue;
                    for (var c = 0; c < 4; c++)
                        union.UnionWith(components[c]);
                }
                return union;
            }
            case 8: // cb#[row]
            {
                if (operand.Index0 != context.MaterialSlot || operand.Dynamic0 ||
                    operand.Dynamic1 || operand.Index1 < 0)
                    return EmptyLabels;
                var byteOffset = operand.Index1 * 16 + operand.Swizzle[component] * 4;
                if (byteOffset >= context.VecBase && byteOffset < context.ScalarBase)
                {
                    var index = (int) ((byteOffset - context.VecBase) / 16);
                    if (index < context.VecCount) return [PixelValueSource.Vector(index)];
                }
                else if (byteOffset >= context.ScalarBase && byteOffset < context.ScalarBase + context.ScalarCount * 4L)
                {
                    return [PixelValueSource.Scalar((int) ((byteOffset - context.ScalarBase) / 4))];
                }
                return EmptyLabels;
            }
            default: // inputs, immediates, samplers, resources: no material value flows through
                return EmptyLabels;
        }
    }

    private static void WriteDestination(TaintState state, Operand destination, HashSet<PixelValueSource>[] labels)
    {
        switch (destination.Type)
        {
            case 0 or 2: // r# / o# — strong update of the masked components
            {
                var kind = destination.Type == 0 ? 'r' : 'o';
                var components = state.GetOrAdd(kind, destination.Index0);
                for (var c = 0; c < 4; c++)
                {
                    if ((destination.Mask & (1 << c)) == 0) continue;
                    components[c] = new HashSet<PixelValueSource>(labels[c]);
                }
                break;
            }
            case 3: // x# — writes may be dynamically indexed, so union into the blob
            {
                var components = state.GetOrAdd('x', destination.Dynamic0 ? -1 : destination.Index0);
                var union = new HashSet<PixelValueSource>();
                for (var c = 0; c < 4; c++)
                    if ((destination.Mask & (1 << c)) != 0)
                        union.UnionWith(labels[c]);
                for (var c = 0; c < 4; c++)
                    components[c].UnionWith(union);
                break;
            }
            // null / depth outputs: nothing to track
        }
    }

    #endregion

    #region Symbolic expression recovery

    private readonly record struct ExprRef(PixelExpressionNode Node, int Component);

    /// <summary>Per register component, the expression node (and its lane) that last wrote it.</summary>
    private sealed class ExprState
    {
        public readonly Dictionary<(char Kind, long Register), ExprRef?[]> Registers = new();

        public ExprRef?[] GetOrAdd(char kind, long register)
        {
            var key = (kind, register);
            if (!Registers.TryGetValue(key, out var components))
                Registers[key] = components = new ExprRef?[4];
            return components;
        }

        public ExprState Clone()
        {
            var clone = new ExprState();
            foreach (var (key, components) in Registers)
                clone.Registers[key] = (ExprRef?[]) components.Clone();
            return clone;
        }
    }

    /// <summary>
    /// Second decoding pass over the same instruction stream as the taint analysis: rebuilds,
    /// per material output pin, the expression DAG of the compiled math feeding it. Where the
    /// taint pass only answers "which serialized values reach this pin", this pass keeps the
    /// operations between them — every DAG node is exactly one decoded instruction, every edge
    /// carries the operand token's swizzle and modifier bits. Anything the linear pass cannot
    /// follow honestly (loops, switches, indexable temps, dynamically indexed constants)
    /// degrades to "opaque" leaves instead of guessed values.
    /// </summary>
    private static void BuildPinExpressions(List<Instruction> instructions, AnalysisContext context,
        Dictionary<long, int> outputRegToTarget, bool usesGBuffer,
        Dictionary<int, string> cbNames, Dictionary<long, string> inputSemantics,
        Dictionary<long, int> resourceDimensions, PixelShaderWiring wiring,
        Dictionary<string, PixelExpressionNode> resultOverride = null,
        Dictionary<long, string> outputSemantics = null)
    {
        var state = new ExprState();
        var discardConditions = new List<ExprRef>();

        // hash-consed leaves so every consumer of the same value shares one node
        var immLeaves = new Dictionary<string, PixelExpressionNode>();
        var sourceLeaves = new Dictionary<PixelValueSource, PixelExpressionNode>();
        var foreignCbLeaves = new Dictionary<(long Slot, long Row), PixelExpressionNode>();
        var inputLeaves = new Dictionary<long, PixelExpressionNode>();
        var opaqueLeaves = new Dictionary<string, PixelExpressionNode>();

        PixelExpressionNode Opaque(string reason)
        {
            if (!opaqueLeaves.TryGetValue(reason, out var node))
                opaqueLeaves[reason] = node = new PixelExpressionNode { Op = "opaque", Detail = reason };
            return node;
        }

        // "xyzw" lane letters ("rgba" through a sample node's channel map); identity prefixes
        // (.x, .xy, .xyz, .xyzw) collapse to nothing like an authored graph's implicit casts
        static string BuildSwizzle(PixelExpressionNode node, int[] components)
        {
            if (node.Op == "cbrow" && node.Source is { Kind: PixelValueKind.ScalarExpression }) return string.Empty;
            if (node.Op == "imm" && (node.Constants?.Length ?? 0) <= 1) return string.Empty;
            if (node.Op is "dp2" or "dp3" or "dp4" or "phi" or "mask") return string.Empty;
            var letters = node.ChannelMap != null
                ? string.Concat(components.Select(c => "rgba"[Math.Clamp(node.ChannelMap[Math.Clamp(c, 0, 3)], 0, 3)]))
                : string.Concat(components.Select(c => "xyzw"[Math.Clamp(c, 0, 3)]));
            return "xyzw".StartsWith(letters, StringComparison.Ordinal) || "rgba".StartsWith(letters, StringComparison.Ordinal)
                ? string.Empty
                : letters;
        }

        // resolves one post-swizzle component of a source operand to the node that produces it
        ExprRef ResolveComponent(Operand op, int k)
        {
            var raw = op.Swizzle[Math.Clamp(k, 0, 3)];
            switch (op.Type)
            {
                case 0: // r# temp
                    if (state.Registers.TryGetValue(('r', op.Index0), out var written) && written[raw] is { } producer)
                        return producer;
                    return new ExprRef(Opaque("value of an unwritten register"), 0);
                case 1: // v# interpolant
                {
                    if (!inputLeaves.TryGetValue(op.Index0, out var leaf))
                    {
                        var detail = inputSemantics != null && inputSemantics.TryGetValue(op.Index0, out var semantic)
                            ? $"{semantic} (v{op.Index0})"
                            : $"v{op.Index0}";
                        inputLeaves[op.Index0] = leaf = new PixelExpressionNode { Op = "input", Detail = detail };
                    }
                    return new ExprRef(leaf, raw);
                }
                case 3: // x# indexable temp
                    return new ExprRef(Opaque("indexable temp array (dynamic addressing)"), 0);
                case 4: // immediate
                {
                    var bits = op.Immediates ?? [];
                    var key = string.Join(",", bits);
                    if (!immLeaves.TryGetValue(key, out var leaf))
                        immLeaves[key] = leaf = new PixelExpressionNode
                        {
                            Op = "imm",
                            Constants = bits.Select(b => BitConverter.Int32BitsToSingle((int) b)).ToArray()
                        };
                    return new ExprRef(leaf, bits.Length == 1 ? 0 : raw);
                }
                case 8: // cb#[row]
                {
                    if (op.Dynamic0 || op.Dynamic1 || op.Index1 < 0)
                        return new ExprRef(Opaque("dynamically indexed constant buffer"), 0);
                    if (op.Index0 == context.MaterialSlot)
                    {
                        var byteOffset = op.Index1 * 16 + raw * 4;
                        if (byteOffset >= context.VecBase && byteOffset < context.ScalarBase)
                        {
                            var index = (int) ((byteOffset - context.VecBase) / 16);
                            if (index < context.VecCount)
                            {
                                var source = PixelValueSource.Vector(index);
                                if (!sourceLeaves.TryGetValue(source, out var leaf))
                                    sourceLeaves[source] = leaf = new PixelExpressionNode { Op = "cbrow", Source = source };
                                return new ExprRef(leaf, raw);
                            }
                        }
                        else if (byteOffset >= context.ScalarBase && byteOffset < context.ScalarBase + context.ScalarCount * 4L)
                        {
                            var source = PixelValueSource.Scalar((int) ((byteOffset - context.ScalarBase) / 4));
                            if (!sourceLeaves.TryGetValue(source, out var leaf))
                                sourceLeaves[source] = leaf = new PixelExpressionNode { Op = "cbrow", Source = source };
                            return new ExprRef(leaf, 0);
                        }
                        // material rows outside the expression arrays (VT constants) label below
                    }
                    var cbKey = (op.Index0, op.Index1);
                    if (!foreignCbLeaves.TryGetValue(cbKey, out var cbLeaf))
                    {
                        var label = cbNames != null && cbNames.TryGetValue((int) op.Index0, out var bufferName)
                            ? $"{bufferName} cb{op.Index0}[{op.Index1}]"
                            : op.Index0 == context.MaterialSlot
                                ? $"Material cb{op.Index0}[{op.Index1}] (virtual-texture constants)"
                                : $"cb{op.Index0}[{op.Index1}]";
                        foreignCbLeaves[cbKey] = cbLeaf = new PixelExpressionNode { Op = "cbrow", Detail = label };
                    }
                    return new ExprRef(cbLeaf, raw);
                }
                default:
                    return new ExprRef(Opaque($"operand type {op.Type}"), 0);
            }
        }

        // one operand → one edge; a register whose lanes come from different producers keeps
        // them behind a real "append" node instead of merging them
        PixelExpressionArg MakeArg(Operand op, int[] components, string name)
        {
            var refs = components.Select(k => ResolveComponent(op, k)).ToArray();
            PixelExpressionNode node;
            string swizzle;
            if (refs.All(r => ReferenceEquals(r.Node, refs[0].Node)))
            {
                node = refs[0].Node;
                swizzle = BuildSwizzle(node, refs.Select(r => r.Component).ToArray());
            }
            else
            {
                node = new PixelExpressionNode { Op = "append" };
                for (var i = 0; i < refs.Length; i++)
                    node.Args.Add(new PixelExpressionArg
                    {
                        Node = refs[i].Node,
                        Swizzle = BuildSwizzle(refs[i].Node, [refs[i].Component]),
                        Name = i < 4 ? new string("XYZW"[i], 1) : $"In {i}"
                    });
                swizzle = string.Empty;
            }
            return new PixelExpressionArg { Node = node, Swizzle = swizzle, Negate = op.Neg, Absolute = op.Abs, Name = name };
        }

        // strong per-lane update; all lanes resolve before any is written (mov r0.xy, r0.yx)
        void WriteDest(Operand dest, Func<int, ExprRef?> value)
        {
            if (dest.Type is not (0 or 2)) return;
            var resolved = new ExprRef?[4];
            for (var c = 0; c < 4; c++)
                if ((dest.Mask & (1 << c)) != 0)
                    resolved[c] = value(c);
            var components = state.GetOrAdd(dest.Type == 0 ? 'r' : 'o', dest.Index0);
            for (var c = 0; c < 4; c++)
                if ((dest.Mask & (1 << c)) != 0)
                    components[c] = resolved[c];
        }

        int[] MaskedComponents(Operand dest)
        {
            var list = new List<int>(4);
            for (var c = 0; c < 4; c++)
                if ((dest.Mask & (1 << c)) != 0)
                    list.Add(c);
            return list.ToArray();
        }

        // values that survive an if with different producers per branch become explicit
        // per-lane "phi" nodes carrying the branch condition — the select the GPU performs
        ExprState MergeBranches(ExprState thenState, ExprState elseState, ExprRef condition, bool testNonZero)
        {
            var merged = new ExprState();
            var phiCache = new Dictionary<(PixelExpressionNode, int, PixelExpressionNode, int), PixelExpressionNode>();
            foreach (var key in thenState.Registers.Keys.Union(elseState.Registers.Keys).ToList())
            {
                thenState.Registers.TryGetValue(key, out var thenComps);
                elseState.Registers.TryGetValue(key, out var elseComps);
                var target = merged.GetOrAdd(key.Kind, key.Register);
                for (var c = 0; c < 4; c++)
                {
                    var thenRef = thenComps?[c];
                    var elseRef = elseComps?[c];
                    if (thenRef == null) { target[c] = elseRef; continue; }
                    if (elseRef == null || thenRef.Value == elseRef.Value) { target[c] = thenRef; continue; }
                    var cacheKey = (thenRef.Value.Node, thenRef.Value.Component, elseRef.Value.Node, elseRef.Value.Component);
                    if (!phiCache.TryGetValue(cacheKey, out var phi))
                    {
                        phi = new PixelExpressionNode { Op = "phi", Detail = testNonZero ? "if_nz" : "if_z" };
                        phi.Args.Add(new PixelExpressionArg
                        {
                            Node = condition.Node,
                            Swizzle = BuildSwizzle(condition.Node, [condition.Component]),
                            Name = testNonZero ? "Condition (≠0 → Then)" : "Condition (=0 → Then)"
                        });
                        phi.Args.Add(new PixelExpressionArg { Node = thenRef.Value.Node, Swizzle = BuildSwizzle(thenRef.Value.Node, [thenRef.Value.Component]), Name = "Then" });
                        phi.Args.Add(new PixelExpressionArg { Node = elseRef.Value.Node, Swizzle = BuildSwizzle(elseRef.Value.Node, [elseRef.Value.Component]), Name = "Else" });
                        phiCache[cacheKey] = phi;
                    }
                    target[c] = new ExprRef(phi, 0);
                }
            }
            return merged;
        }

        // loop/switch bodies run a data-dependent number of times: every value they changed
        // is only known at runtime, so it degrades to an opaque leaf
        void OpaqueChangedValues(ExprState entry, string reason)
        {
            foreach (var (key, components) in state.Registers)
            {
                entry.Registers.TryGetValue(key, out var entryComps);
                for (var c = 0; c < 4; c++)
                {
                    if (components[c] == null) continue;
                    if (entryComps?[c] is { } before && before == components[c].Value) continue;
                    components[c] = new ExprRef(Opaque(reason), 0);
                }
            }
        }

        void ApplySymbolic(Instruction instruction)
        {
            var opcode = instruction.Opcode;
            var mnemonic = OpcodeName(opcode);
            var destCount = TwoDestOpcodes.Contains(opcode) ? 2 : 1;
            if (instruction.Operands.Count <= destCount) return;

            var resourceIndex = GetResourceOperandIndex(opcode);
            if (resourceIndex >= 0 && resourceIndex < instruction.Operands.Count)
            {
                var resource = instruction.Operands[resourceIndex];
                var node = new PixelExpressionNode { Op = "sample", Detail = mnemonic, Saturate = instruction.Saturate };
                if (resource.Type == 7 && !resource.Dynamic0 && resource.Index0 >= 0 &&
                    context.TextureByRegister.TryGetValue(resource.Index0, out var texture))
                    node.Source = PixelValueSource.Texture(texture.Slot, texture.Index, -1);
                else
                    node.Detail = $"{mnemonic} — t{(resource.Type == 7 ? resource.Index0.ToString() : "?")} (engine resource)";
                node.ChannelMap = opcode is 109 or 126 or 127 or 128 // gather4 reads one channel of 4 texels
                    ? [resource.Swizzle[0], resource.Swizzle[0], resource.Swizzle[0], resource.Swizzle[0]]
                    : (int[]) resource.Swizzle.Clone();

                // coordinate width from the dcl_resource dimension; 2D when undeclared
                var coordinateCount = resource.Type == 7 && !resource.Dynamic0 &&
                                      resourceDimensions != null && resourceDimensions.TryGetValue(resource.Index0, out var dimension)
                    ? CoordinateCountForDimension(dimension)
                    : 2;
                var extraIndex = 0;
                for (var s = destCount; s < instruction.Operands.Count; s++)
                {
                    if (s == resourceIndex || instruction.Operands[s].Type == 6) continue; // sampler carries no value
                    var isCoordinate = extraIndex == 0;
                    var name = isCoordinate ? "UVs" : opcode switch
                    {
                        72 => "Level", 74 => "Bias", 70 or 71 or 126 or 128 => "Compare",
                        73 => extraIndex == 1 ? "Gradient X" : "Gradient Y",
                        _ => $"In {extraIndex}"
                    };
                    node.Args.Add(MakeArg(instruction.Operands[s],
                        Enumerable.Range(0, isCoordinate ? coordinateCount : 1).ToArray(), name));
                    extraIndex++;
                }
                WriteDest(instruction.Operands[0], c => new ExprRef(node, c));
                return;
            }

            if (opcode is 15 or 16 or 17) // dp2/dp3/dp4 collapse to one scalar
            {
                var lanes = Enumerable.Range(0, opcode - 13).ToArray();
                var node = new PixelExpressionNode { Op = mnemonic, Saturate = instruction.Saturate };
                node.Args.Add(MakeArg(instruction.Operands[destCount], lanes, "A"));
                if (instruction.Operands.Count > destCount + 1)
                    node.Args.Add(MakeArg(instruction.Operands[destCount + 1], lanes, "B"));
                WriteDest(instruction.Operands[0], _ => new ExprRef(node, 0));
                return;
            }

            if (opcode == 54 && !instruction.Saturate && !instruction.Operands[1].Neg && !instruction.Operands[1].Abs)
            {
                // a plain mov computes nothing: alias the source lanes
                var source = instruction.Operands[1];
                WriteDest(instruction.Operands[0], c => ResolveComponent(source, c));
                return;
            }

            if (destCount == 2)
            {
                var (firstOp, secondOp) = opcode switch
                {
                    77 => ("sin", "cos"),
                    38 => ("imul_hi", "imul"),
                    81 => ("umul_hi", "umul"),
                    78 => ("udiv", "udiv_rem"),
                    _ => (mnemonic, mnemonic + "_2")
                };
                for (var d = 0; d < 2; d++)
                {
                    var destination = instruction.Operands[d];
                    if (destination.Type == 13) continue; // null dest
                    var node = new PixelExpressionNode { Op = d == 0 ? firstOp : secondOp, Saturate = instruction.Saturate };
                    var lanes = MaskedComponents(destination);
                    if (lanes.Length == 0) continue;
                    var argNames = ArgNamesFor(node.Op, instruction.Operands.Count - 2);
                    for (var s = 2; s < instruction.Operands.Count; s++)
                        node.Args.Add(MakeArg(instruction.Operands[s], lanes, argNames[s - 2]));
                    WriteDest(destination, c => new ExprRef(node, c));
                }
                return;
            }

            {
                var destination = instruction.Operands[0];
                var lanes = MaskedComponents(destination);
                if (lanes.Length == 0) return;
                var node = new PixelExpressionNode { Op = mnemonic, Saturate = instruction.Saturate };
                var argNames = ArgNamesFor(mnemonic, instruction.Operands.Count - 1);
                for (var s = 1; s < instruction.Operands.Count; s++)
                    node.Args.Add(MakeArg(instruction.Operands[s], lanes, argNames[s - 1]));
                WriteDest(destination, c => new ExprRef(node, c));
            }
        }

        // ---- linear walk with an explicit control-flow stack, mirroring the taint pass ----
        var frames = new Stack<(bool IsIf, ExprState Entry, ExprState ThenExit, ExprRef Condition, bool TestNonZero)>();
        foreach (var instruction in instructions)
        {
            switch (instruction.Opcode)
            {
                case 31: // if
                {
                    var condition = instruction.Operands.Count > 0
                        ? ResolveComponent(instruction.Operands[0], 0)
                        : new ExprRef(Opaque("missing branch condition"), 0);
                    frames.Push((true, state.Clone(), null, condition, instruction.TestNonZero));
                    continue;
                }
                case 48 or 76: // loop / switch
                    frames.Push((false, state.Clone(), null, default, false));
                    continue;
                case 18: // else: keep the then-branch exit, restart from the entry state
                    if (frames.Count > 0 && frames.Peek().IsIf)
                    {
                        var frame = frames.Pop();
                        frames.Push((true, frame.Entry, state, frame.Condition, frame.TestNonZero));
                        state = frame.Entry.Clone();
                    }
                    continue;
                case 21: // endif
                    if (frames.Count > 0 && frames.Peek().IsIf)
                    {
                        var frame = frames.Pop();
                        var thenState = frame.ThenExit ?? state;
                        var elseState = frame.ThenExit != null ? state : frame.Entry;
                        state = MergeBranches(thenState, elseState, frame.Condition, frame.TestNonZero);
                    }
                    continue;
                case 22 or 23: // endloop / endswitch
                    if (frames.Count > 0 && !frames.Peek().IsIf)
                        OpaqueChangedValues(frames.Pop().Entry, "computed inside a loop/switch (data-dependent iteration)");
                    continue;
                case OpcodeDiscard:
                    if (instruction.Operands.Count > 0)
                        discardConditions.Add(ResolveComponent(instruction.Operands[0], 0));
                    continue;
            }
            if (instruction.Opcode is 165 or 167 && instruction.Operands.Count > 1)
            {
                // raw/structured buffer loads read GPU runtime data (light grids, GPUScene
                // primitive data, instance data — engine plumbing, not a material expression),
                // so the value cannot be a material node; label it with the SRV register it reads
                var resource = instruction.Operands[^1];
                var register = resource is { Type: 7, Dynamic0: false, Index0: >= 0 } ? $" t{resource.Index0}" : string.Empty;
                var bufferKind = instruction.Opcode == 167 ? "structured" : "raw";
                var loaded = Opaque($"value read from {bufferKind} buffer{register} (GPU runtime data — engine buffer, not a material value)");
                WriteDest(instruction.Operands[0], _ => new ExprRef(loaded, 0));
                continue;
            }
            if (PassiveOpcodes.Contains(instruction.Opcode) || instruction.Opcode is >= 164 and <= 189)
                continue;
            ApplySymbolic(instruction);
        }

        var results = resultOverride ?? wiring.PinExpressions;

        // stage mode: this is not the base-pass pixel shader, so root EVERY written output
        // register (keyed by its signature semantic) instead of the GBuffer/forward material
        // seeds — shows the full decoded math of a vertex/geometry/other stage
        if (outputSemantics != null)
        {
            foreach (var (key, components) in state.Registers.Where(r => r.Key.Kind == 'o').OrderBy(r => r.Key.Register))
            {
                var refs = new List<ExprRef>(4);
                for (var c = 0; c < 4; c++)
                    if (components[c] is { } written) refs.Add(written);
                if (refs.Count == 0) continue;
                var name = outputSemantics.TryGetValue(key.Register, out var semantic) ? semantic : $"o{key.Register}";
                var unique = name;
                var n = 2;
                while (results.ContainsKey(unique)) unique = $"{name} ({n++})";
                results[unique] = WrapRefs(refs.ToArray());
            }
            return;
        }

        // ---- pin roots over the same GBuffer seeds as the taint mapping ----
        var targetToReg = new Dictionary<int, long>();
        foreach (var (register, target) in outputRegToTarget)
            targetToReg[target] = register;

        PixelExpressionNode WrapRefs(ExprRef[] refs)
        {
            if (refs.All(r => ReferenceEquals(r.Node, refs[0].Node)))
            {
                var letters = BuildSwizzle(refs[0].Node, refs.Select(r => r.Component).ToArray());
                if (letters.Length == 0) return refs[0].Node;
                var mask = new PixelExpressionNode { Op = "mask", Detail = letters };
                mask.Args.Add(new PixelExpressionArg { Node = refs[0].Node, Swizzle = letters, Name = "In" });
                return mask;
            }
            var append = new PixelExpressionNode { Op = "append" };
            for (var i = 0; i < refs.Length; i++)
                append.Args.Add(new PixelExpressionArg
                {
                    Node = refs[i].Node,
                    Swizzle = BuildSwizzle(refs[i].Node, [refs[i].Component]),
                    Name = i < 4 ? new string("XYZW"[i], 1) : $"In {i}"
                });
            return append;
        }

        void Root(string pin, int target, int mask)
        {
            if (!targetToReg.TryGetValue(target, out var register)) return;
            if (!state.Registers.TryGetValue(('o', register), out var components)) return;
            var refs = new List<ExprRef>(4);
            for (var c = 0; c < 4; c++)
                if ((mask & (1 << c)) != 0 && components[c] is { } written)
                    refs.Add(written);
            if (refs.Count == 0) return;
            results[pin] = WrapRefs(refs.ToArray());
        }

        if (usesGBuffer)
        {
            Root("Normal", 1, 0b0111);
            Root("Metallic", 2, 0b0001);
            Root("Specular", 2, 0b0010);
            Root("Roughness", 2, 0b0100);
            Root("Base Color", 3, 0b0111);
            Root("Ambient Occlusion", 3, 0b1000);
            Root("Emissive Color", 0, 0b0111);
        }
        else
        {
            Root("Emissive Color", 0, 0b0111);
            Root("Opacity", 0, 0b1000);
        }

        if (discardConditions.Count == 1)
        {
            results["Opacity Mask"] = WrapRefs([discardConditions[0]]);
        }
        else if (discardConditions.Count > 1)
        {
            var any = new PixelExpressionNode { Op = "discard", Detail = "any condition discards the pixel" };
            for (var i = 0; i < discardConditions.Count; i++)
                any.Args.Add(new PixelExpressionArg
                {
                    Node = discardConditions[i].Node,
                    Swizzle = BuildSwizzle(discardConditions[i].Node, [discardConditions[i].Component]),
                    Name = $"In {i}"
                });
            results["Opacity Mask"] = any;
        }
    }

    private static string[] ArgNamesFor(string mnemonic, int count) => mnemonic switch
    {
        "movc" => ["Condition", "A (non-zero)", "B (zero)"],
        "mad" or "imad" or "umad" => ["A", "B", "C"],
        _ when count == 1 => ["X"],
        _ when count == 2 => ["A", "B"],
        _ => Enumerable.Range(0, Math.Max(count, 0)).Select(i => $"In {i}").ToArray()
    };

    /// <summary>D3D10_SB_RESOURCE_DIMENSION → number of coordinate components a read consumes.</summary>
    private static int CoordinateCountForDimension(int dimension) => dimension switch
    {
        1 => 1, // buffer
        2 => 1, // texture1d
        3 or 4 => 2, // texture2d / texture2dms
        5 or 6 => 3, // texture3d / texturecube
        7 => 2, // texture1darray
        8 or 9 => 3, // texture2darray / texture2dmsarray
        10 => 4, // texturecubearray
        _ => 2
    };

    #endregion

    #region Render target → material pin mapping

    /// <summary>
    /// UE 4.2x deferred GBuffer layout (DeferredShadingCommon.ush EncodeGBuffer +
    /// SceneRenderTargets MRT order): MRT0 scene color, MRT1 GBufferA.rgb = world normal,
    /// MRT2 GBufferB = metallic/specular/roughness, MRT3 GBufferC.rgb = base color with
    /// .a = ambient occlusion. Values reaching only scene color are emissive. Translucent
    /// materials render forward: MRT0.rgb = emissive+lit color, .a = opacity.
    /// </summary>
    private static void MapSinksToPins(TaintState state, HashSet<PixelValueSource> discardSink,
        Dictionary<long, int> outputRegToTarget, bool usesGBuffer, PixelShaderWiring wiring)
    {
        var byTarget = new Dictionary<int, HashSet<PixelValueSource>[]>();
        foreach (var (key, components) in state.Registers)
        {
            if (key.Kind != 'o' || !outputRegToTarget.TryGetValue(key.Register, out var target)) continue;
            byTarget[target] = components;
        }

        void Add(string pin, IEnumerable<PixelValueSource> sources)
        {
            if (!wiring.PinSources.TryGetValue(pin, out var list))
                wiring.PinSources[pin] = list = [];
            foreach (var source in sources)
                if (!list.Contains(source))
                    list.Add(source);
        }

        void AddComponents(int target, int firstComponent, int lastComponent, string pin)
        {
            if (!byTarget.TryGetValue(target, out var components)) return;
            for (var c = firstComponent; c <= lastComponent; c++)
                Add(pin, components[c]);
        }

        if (usesGBuffer)
        {
            AddComponents(1, 0, 2, "Normal");
            AddComponents(2, 0, 0, "Metallic");
            AddComponents(2, 1, 1, "Specular");
            AddComponents(2, 2, 2, "Roughness");
            AddComponents(3, 0, 2, "Base Color");
            AddComponents(3, 3, 3, "Ambient Occlusion");

            // scene color receives base color and metallic again through the lighting math,
            // so only values reaching no other pin are genuinely emissive
            if (byTarget.TryGetValue(0, out var sceneColor))
            {
                var elsewhere = new HashSet<PixelValueSource>(wiring.PinSources.Values.SelectMany(v => v));
                var emissive = new HashSet<PixelValueSource>();
                for (var c = 0; c <= 2; c++)
                    emissive.UnionWith(sceneColor[c]);
                emissive.ExceptWith(elsewhere);
                if (emissive.Count > 0) Add("Emissive Color", emissive);
            }
        }
        else if (byTarget.TryGetValue(0, out var forwardColor))
        {
            for (var c = 0; c <= 2; c++)
                Add("Emissive Color", forwardColor[c]);
            Add("Opacity", forwardColor[3]);
        }

        if (discardSink.Count > 0)
            Add("Opacity Mask", discardSink);

        // drop pins that ended up with nothing so callers can treat presence as signal
        foreach (var pin in wiring.PinSources.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            wiring.PinSources.Remove(pin);
    }

    #endregion

    #region Per-pin disassembly

    /// <summary>
    /// For every wired pin, slices out of the decoded instruction stream the instructions
    /// whose values flow into that pin's render-target components (backward dependence over
    /// the same component-level semantics the taint pass uses) and formats them as annotated
    /// D3D SM5 assembly, so the graph can show the real compiled math behind each connection.
    /// </summary>
    private static void BuildPinDisassemblies(List<Instruction> instructions, AnalysisContext context,
        Dictionary<long, int> outputRegToTarget, bool usesGBuffer, string typeName, PixelShaderWiring wiring)
    {
        if (wiring.PinSources.Count == 0 || instructions.Count == 0) return;

        // control-flow nesting depth per instruction plus matched if/loop/switch regions,
        // so slices can keep the enclosing structure for readability
        var depth = ComputeControlFlowDepth(instructions, out var regions);

        var targetToReg = new Dictionary<int, long>();
        foreach (var (register, target) in outputRegToTarget)
            targetToReg[target] = register;

        var pinSeeds = new Dictionary<string, List<(long Reg, int Mask)>>();
        void Seed(string pin, int target, int mask)
        {
            if (!targetToReg.TryGetValue(target, out var register)) return;
            if (!pinSeeds.TryGetValue(pin, out var list)) pinSeeds[pin] = list = [];
            list.Add((register, mask));
        }
        if (usesGBuffer)
        {
            Seed("Normal", 1, 0b0111);
            Seed("Metallic", 2, 0b0001);
            Seed("Specular", 2, 0b0010);
            Seed("Roughness", 2, 0b0100);
            Seed("Base Color", 3, 0b0111);
            Seed("Ambient Occlusion", 3, 0b1000);
            Seed("Emissive Color", 0, 0b0111);
        }
        else
        {
            Seed("Emissive Color", 0, 0b0111);
            Seed("Opacity", 0, 0b1000);
        }

        foreach (var pin in wiring.PinSources.Keys)
        {
            var isDiscardPin = pin == "Opacity Mask";
            if (!isDiscardPin && !pinSeeds.ContainsKey(pin)) continue;
            var included = ComputeSlice(instructions, depth, isDiscardPin ? null : pinSeeds[pin], isDiscardPin);
            if (included.Count == 0) continue;
            wiring.PinDisassembly[pin] = FormatSlice(instructions, included, depth, regions, context, outputRegToTarget, pin, typeName);
        }
    }

    /// <summary>Control-flow nesting depth per instruction, plus matched if/loop/switch regions.</summary>
    private static int[] ComputeControlFlowDepth(List<Instruction> instructions, out List<(int Start, int Else, int End)> regions)
    {
        var depth = new int[instructions.Count];
        regions = [];
        var frames = new Stack<(int Start, int Else)>();
        for (var i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i].Opcode)
            {
                case 31 or 48 or 76: // if / loop / switch
                    depth[i] = frames.Count;
                    frames.Push((i, -1));
                    break;
                case 18: // else sits at its header's level
                    depth[i] = Math.Max(0, frames.Count - 1);
                    if (frames.Count > 0)
                    {
                        var frame = frames.Pop();
                        frames.Push((frame.Start, i));
                    }
                    break;
                case 21 or 22 or 23: // endif / endloop / endswitch
                    if (frames.Count > 0)
                    {
                        var frame = frames.Pop();
                        regions.Add((frame.Start, frame.Else, i));
                    }
                    depth[i] = frames.Count;
                    break;
                default:
                    depth[i] = frames.Count;
                    break;
            }
        }
        return depth;
    }

    /// <summary>
    /// Backward dependence closure from the pin's render-target components (or from the
    /// discard conditions). Component-precise like the taint pass; writes at top level kill
    /// the need above them, writes inside branches/loops stay conservative, and repeated
    /// passes pick up loop-carried dependencies. Coordinate/UV chains feeding texture reads
    /// are deliberately not chased — the slice shows value flow, matching the wiring.
    /// </summary>
    private static HashSet<int> ComputeSlice(List<Instruction> instructions, int[] depth,
        List<(long Reg, int Mask)> seeds, bool discardSlice)
    {
        var included = new HashSet<int>();
        var everNeeded = new Dictionary<(char Kind, long Register), int>();
        if (seeds != null)
        {
            foreach (var (register, mask) in seeds)
            {
                everNeeded.TryGetValue(('o', register), out var current);
                everNeeded[('o', register)] = current | mask;
            }
        }

        for (var pass = 0; pass < 4; pass++)
        {
            var changed = false;
            var active = new Dictionary<(char Kind, long Register), int>(everNeeded);

            for (var i = instructions.Count - 1; i >= 0; i--)
            {
                var instruction = instructions[i];
                var opcode = instruction.Opcode;

                if (opcode == OpcodeDiscard)
                {
                    if (!discardSlice) continue;
                    if (included.Add(i)) changed = true;
                    if (instruction.Operands.Count > 0)
                        for (var c = 0; c < 4; c++)
                            AddSourceNeed(active, everNeeded, instruction.Operands[0], c, ref changed);
                    continue;
                }
                if (PassiveOpcodes.Contains(opcode) || opcode is >= 164 and <= 189 ||
                    opcode is 18 or 21 or 22 or 23 or 31 or 48 or 76)
                    continue;

                var destCount = TwoDestOpcodes.Contains(opcode) ? 2 : 1;
                if (instruction.Operands.Count <= destCount) continue;

                var hitMask = 0;
                for (var d = 0; d < destCount; d++)
                {
                    var dest = instruction.Operands[d];
                    switch (dest.Type)
                    {
                        case 0 or 2:
                        {
                            var key = (dest.Type == 0 ? 'r' : 'o', dest.Index0);
                            if (active.TryGetValue(key, out var need))
                            {
                                var hit = need & dest.Mask;
                                if (hit != 0)
                                {
                                    hitMask |= hit;
                                    if (depth[i] == 0) // unconditional: earlier defs are dead
                                        active[key] = need & ~dest.Mask;
                                }
                            }
                            break;
                        }
                        case 3: // indexable temps are one blob; comp mapping is unknown
                            if (active.TryGetValue(('x', 0L), out var blobNeed) && blobNeed != 0)
                                hitMask |= dest.Mask;
                            break;
                    }
                }
                if (hitMask == 0) continue;
                if (included.Add(i)) changed = true;

                if (GetResourceOperandIndex(opcode) >= 0) continue; // texture data originates here
                if (opcode is 15 or 16 or 17) // dp2/3/4 consume components horizontally
                {
                    var componentCount = opcode - 13;
                    for (var s = destCount; s < instruction.Operands.Count; s++)
                    for (var c = 0; c < componentCount; c++)
                        AddSourceNeed(active, everNeeded, instruction.Operands[s], c, ref changed);
                }
                else
                {
                    for (var s = destCount; s < instruction.Operands.Count; s++)
                    for (var c = 0; c < 4; c++)
                        if ((hitMask & (1 << c)) != 0)
                            AddSourceNeed(active, everNeeded, instruction.Operands[s], c, ref changed);
                }
            }

            if (!changed) break;
        }

        return included;
    }

    private static void AddSourceNeed(Dictionary<(char Kind, long Register), int> active,
        Dictionary<(char Kind, long Register), int> everNeeded, Operand source, int component, ref bool changed)
    {
        (char, long) key;
        int mask;
        switch (source.Type)
        {
            case 0: // r# temp
                key = ('r', source.Index0);
                mask = 1 << source.Swizzle[component];
                break;
            case 3: // x# indexable temp blob
                key = ('x', 0L);
                mask = 0xF;
                break;
            default: // cb / inputs / immediates terminate the chase
                return;
        }
        active.TryGetValue(key, out var activeMask);
        active[key] = activeMask | mask;
        everNeeded.TryGetValue(key, out var everMask);
        if ((everMask | mask) != everMask) changed = true;
        everNeeded[key] = everMask | mask;
    }

    private static string FormatSlice(List<Instruction> instructions, HashSet<int> included, int[] depth,
        List<(int Start, int Else, int End)> regions, AnalysisContext context,
        Dictionary<long, int> outputRegToTarget, string pinName, string typeName)
    {
        // keep the if/else/endif and loop skeleton around whatever made it into the slice
        var structural = new HashSet<int>();
        foreach (var (start, elseIndex, end) in regions)
        {
            if (!included.Any(i => i > start && i < end)) continue;
            structural.Add(start);
            if (elseIndex >= 0) structural.Add(elseIndex);
            structural.Add(end);
        }

        var lines = new List<string>();
        for (var i = 0; i < instructions.Count; i++)
        {
            if (!included.Contains(i) && !structural.Contains(i)) continue;
            lines.Add(new string(' ', Math.Min(depth[i], 8) * 2) + FormatInstruction(instructions[i], context, outputRegToTarget));
        }

        const int maxLines = 220;
        var builder = new StringBuilder();
        builder.Append("// ").Append(pinName).Append(" — instructions whose values reach this pin\n");
        builder.Append("// sliced from ").Append(typeName).Append(" (D3D SM5); UV/coordinate math not shown\n");
        for (var i = 0; i < lines.Count && i < maxLines; i++)
            builder.Append(lines[i]).Append('\n');
        if (lines.Count > maxLines)
            builder.Append("// … ").Append(lines.Count - maxLines).Append(" more instructions omitted");
        return builder.ToString().TrimEnd('\n');
    }

    private static string FormatInstruction(Instruction instruction, AnalysisContext context,
        Dictionary<long, int> outputRegToTarget)
    {
        var name = OpcodeName(instruction.Opcode);
        if (instruction.Opcode is 3 or 5 or 8 or 13 or 31 or 63) // conditional forms
            name += instruction.TestNonZero ? "_nz" : "_z";
        if (instruction.Saturate) name += "_sat";

        var comments = new List<string>();
        var parts = instruction.Operands.Select(op => FormatOperand(op, context, outputRegToTarget, comments)).ToList();
        var text = parts.Count > 0 ? $"{name} {string.Join(", ", parts)}" : name;
        var unique = comments.Distinct().ToList();
        return unique.Count > 0 ? $"{text}  ; {string.Join(", ", unique)}" : text;
    }

    private static string FormatOperand(Operand op, AnalysisContext context,
        Dictionary<long, int> outputRegToTarget, List<string> comments)
    {
        string body;
        switch (op.Type)
        {
            case 0:
                body = $"r{op.Index0}";
                break;
            case 1:
                body = $"v{op.Index0}";
                break;
            case 2:
                body = $"o{op.Index0}";
                if (outputRegToTarget.TryGetValue(op.Index0, out var target))
                    comments.Add($"o{op.Index0} = SV_Target{target}");
                break;
            case 3:
                var element = op.Dynamic1 ? op.Index1 > 0 ? $"{op.Index1}+*" : "*" : op.Index1.ToString();
                body = $"x{op.Index0}[{element}]";
                break;
            case 4:
                return ApplyModifiers($"l({string.Join(", ", (op.Immediates ?? []).Select(FormatImmediate))})", op);
            case 5:
                return ApplyModifiers("l64(…)", op);
            case 6:
                return ApplyModifiers($"s{op.Index0}", op);
            case 7:
                body = $"t{op.Index0}";
                if (!op.Dynamic0 && context.TextureByRegister.TryGetValue(op.Index0, out var texture))
                    comments.Add($"t{op.Index0} = material {TextureSlotName(texture.Slot)} texture #{texture.Index}");
                break;
            case 8:
                var row = op.Dynamic1 ? op.Index1 > 0 ? $"{op.Index1}+*" : "*" : op.Index1.ToString();
                body = $"cb{op.Index0}[{row}]";
                if (!op.Dynamic0 && !op.Dynamic1 && op.Index0 == context.MaterialSlot && op.Index1 >= 0)
                    AnnotateMaterialConstant(op, context, comments);
                break;
            case 12:
                return ApplyModifiers("oDepth", op);
            case 13:
                return ApplyModifiers("null", op);
            default:
                body = $"op{op.Type}_{op.Index0}";
                break;
        }
        return ApplyModifiers(body + SwizzleSuffix(op), op);
    }

    /// <summary>
    /// Names the Material cbuffer rows an operand touches, using the CreateBufferStruct
    /// layout already computed for the taint pass, so the assembly reads like the graph.
    /// </summary>
    private static void AnnotateMaterialConstant(Operand op, AnalysisContext context, List<string> comments)
    {
        var row = op.Index1;
        var vectorFirstRow = context.VecBase / 16;
        var scalarFirstRow = context.ScalarBase / 16;
        if (row < vectorFirstRow)
        {
            comments.Add($"cb{op.Index0}[{row}] = virtual-texture constants");
        }
        else if (row < vectorFirstRow + context.VecCount)
        {
            comments.Add($"cb{op.Index0}[{row}] = Vector Expression [{row - vectorFirstRow}]");
        }
        else if ((row - scalarFirstRow) * 4 < context.ScalarCount)
        {
            var usedComponents = new SortedSet<int>();
            switch (op.SelMode)
            {
                case 1:
                    foreach (var component in op.Swizzle) usedComponents.Add(component);
                    break;
                case 2:
                    usedComponents.Add(op.Swizzle[0]);
                    break;
                default:
                    for (var c = 0; c < 4; c++) usedComponents.Add(c);
                    break;
            }
            foreach (var component in usedComponents)
            {
                var index = (int) (row - scalarFirstRow) * 4 + component;
                if (index < context.ScalarCount)
                    comments.Add($"cb{op.Index0}[{row}].{"xyzw"[component]} = Scalar Expression [{index}]");
            }
        }
    }

    private static string TextureSlotName(int slot) => slot switch
    {
        0 => "2D",
        1 => "cube",
        2 => "2D-array",
        3 => "volume",
        4 => "virtual (physical)",
        _ => $"slot {slot}"
    };

    private static string SwizzleSuffix(Operand op)
    {
        const string components = "xyzw";
        switch (op.SelMode)
        {
            case 0:
            {
                var suffix = string.Empty;
                for (var c = 0; c < 4; c++)
                    if ((op.Mask & (1 << c)) != 0)
                        suffix += components[c];
                return suffix.Length == 0 ? string.Empty : "." + suffix;
            }
            case 1:
                var swizzle = op.Swizzle;
                return swizzle[0] == swizzle[1] && swizzle[1] == swizzle[2] && swizzle[2] == swizzle[3]
                    ? "." + components[swizzle[0]]
                    : $".{components[swizzle[0]]}{components[swizzle[1]]}{components[swizzle[2]]}{components[swizzle[3]]}";
            case 2:
                return "." + components[op.Swizzle[0]];
            default:
                return string.Empty;
        }
    }

    private static string ApplyModifiers(string text, Operand op)
    {
        if (op.Abs) text = $"|{text}|";
        if (op.Neg) text = "-" + text;
        return text;
    }

    private static string FormatImmediate(uint bits)
    {
        var value = BitConverter.Int32BitsToSingle((int) bits);
        if (float.IsFinite(value) && (value == 0f || (MathF.Abs(value) >= 1e-6f && MathF.Abs(value) < 1e7f)))
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        return bits <= 0xFFFF ? bits.ToString(CultureInfo.InvariantCulture) : $"0x{bits:X8}";
    }

    private static string OpcodeName(int opcode) => opcode switch
    {
        0 => "add", 1 => "and", 2 => "break", 3 => "breakc", 4 => "call", 5 => "callc",
        6 => "case", 7 => "continue", 8 => "continuec", 9 => "cut", 10 => "default",
        11 => "deriv_rtx", 12 => "deriv_rty", 13 => "discard", 14 => "div", 15 => "dp2",
        16 => "dp3", 17 => "dp4", 18 => "else", 19 => "emit", 20 => "emitthencut",
        21 => "endif", 22 => "endloop", 23 => "endswitch", 24 => "eq", 25 => "exp",
        26 => "frc", 27 => "ftoi", 28 => "ftou", 29 => "ge", 30 => "iadd", 31 => "if",
        32 => "ieq", 33 => "ige", 34 => "ilt", 35 => "imad", 36 => "imax", 37 => "imin",
        38 => "imul", 39 => "ine", 40 => "ineg", 41 => "ishl", 42 => "ishr", 43 => "itof",
        44 => "label", 45 => "ld", 46 => "ld_ms", 47 => "log", 48 => "loop", 49 => "lt",
        50 => "mad", 51 => "min", 52 => "max", 54 => "mov", 55 => "movc", 56 => "mul",
        57 => "ne", 58 => "nop", 59 => "not", 60 => "or", 61 => "resinfo", 62 => "ret",
        63 => "retc", 64 => "round_ne", 65 => "round_ni", 66 => "round_pi", 67 => "round_z",
        68 => "rsq", 69 => "sample", 70 => "sample_c", 71 => "sample_c_lz", 72 => "sample_l",
        73 => "sample_d", 74 => "sample_b", 75 => "sqrt", 76 => "switch", 77 => "sincos",
        78 => "udiv", 79 => "ult", 80 => "uge", 81 => "umul", 82 => "umad", 83 => "umax",
        84 => "umin", 85 => "ushr", 86 => "utof", 87 => "xor",
        108 => "lod", 109 => "gather4", 110 => "sample_pos", 111 => "sample_info",
        121 => "bufinfo", 122 => "deriv_rtx_coarse", 123 => "deriv_rtx_fine",
        124 => "deriv_rty_coarse", 125 => "deriv_rty_fine", 126 => "gather4_c",
        127 => "gather4_po", 128 => "gather4_po_c", 129 => "rcp", 130 => "f32tof16",
        131 => "f16tof32", 132 => "uaddc", 133 => "usubb", 134 => "countbits",
        135 => "firstbit_hi", 136 => "firstbit_lo", 137 => "firstbit_shi", 138 => "ubfe",
        139 => "ibfe", 140 => "bfi", 141 => "bfrev", 142 => "swapc",
        163 => "ld_uav_typed", 165 => "ld_raw", 167 => "ld_structured",
        _ => $"op{opcode}"
    };

    #endregion
}
