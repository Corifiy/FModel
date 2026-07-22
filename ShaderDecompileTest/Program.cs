using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Encryption.Aes;
using FModel.ViewModels;
using Serilog;

Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateLogger();

const string paksPath = @"V:\.builds\1.10\FortniteGame\Content\Paks";
const string aesKey = "0x79323938716A53623131354E71513341676164333044576E3251597254493843";
const string assetPath = "FortniteGame/Content/Athena/Prototype/Terrain/M_Athena_Fortress_Skybox_LF_Spinning_2";

Console.WriteLine($"Mounting {paksPath} ...");
var provider = new DefaultFileProvider(paksPath, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE4_19), StringComparer.OrdinalIgnoreCase)
{
    ReadShaderMaps = true
};
provider.Initialize();
provider.SubmitKeys(new Dictionary<FGuid, FAesKey> { [new FGuid()] = new FAesKey(aesKey) });
provider.PostMount();
Console.WriteLine($"Mounted: {provider.MountedVfs.Count} archives, {provider.Files.Count} files.");

Console.WriteLine($"Loading {assetPath} ...");
var pkg = provider.LoadPackage(assetPath);

var found = false;
for (var i = 0; i < pkg.ExportMapLength; i++)
{
    var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
    if (pointer?.Object?.Value is not UMaterialInterface material)
        continue;

    found = true;
    Console.WriteLine();
    Console.WriteLine($"===== {material.GetPathName()} ({material.ExportType}) =====");

    Console.WriteLine();
    Console.WriteLine("----- Pixel shader (DXBC) reconstruction -----");
    string? pixelShader = null;
    try
    {
        pixelShader = PixelShaderDecompiler.DecompilePixelShaderToPseudo(material);
    }
    catch (Exception e)
    {
        Console.WriteLine($"EXCEPTION: {e}");
    }
    Console.WriteLine(pixelShader ?? "(null - no legacy shader map / no pixel shader analysis)");

    Console.WriteLine();
    Console.WriteLine($"----- All LoadedMaterialResources ({material.LoadedMaterialResources.Count}) -----");
    foreach (var r in material.LoadedMaterialResources)
    {
        if (r.LoadedShaderMapLegacy is { } sm)
            Console.WriteLine($"  Quality={sm.ShaderMapId.QualityLevel} FeatureLevel={sm.ShaderMapId.FeatureLevel} Platform={sm.ShaderPlatform} (LoadedShaderMapLegacy present)");
        else
            Console.WriteLine($"  LoadedShaderMapLegacy=null LoadedShaderMap={(r.LoadedShaderMap != null ? "present" : "null")}");
    }

    Console.WriteLine();
    Console.WriteLine("----- Vertex factory of every TBasePassPS* shader (top-level vs. per-VF) -----");
    foreach (var r in material.LoadedMaterialResources)
    {
        if (r.LoadedShaderMapLegacy is not { } sm) continue;
        Console.WriteLine($"  Quality={sm.ShaderMapId.QualityLevel}:");
        foreach (var s in sm.Shaders)
            if (s.TypeName.StartsWith("TBasePassPS", StringComparison.Ordinal))
                Console.WriteLine($"    top-level (no VF): {s.TypeName}");
        foreach (var meshMap in sm.MeshShaderMaps)
            foreach (var s in meshMap.Shaders)
                if (s.TypeName.StartsWith("TBasePassPS", StringComparison.Ordinal))
                    Console.WriteLine($"    VF={meshMap.VertexFactoryTypeName}: {s.TypeName}");
    }

    Console.WriteLine();
    Console.WriteLine("----- Sample node ChannelMap diagnostics -----");
    if (PixelShaderDecompiler.AnalyzeForDiagnostics(material) is { } diag && diag.Wiring.Success)
    {
        var seen = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
        void Walk(PixelExpressionNode node)
        {
            if (!seen.Add(node)) return;
            if (node.Op == "sample")
            {
                Console.WriteLine($"  Detail='{node.Detail}' Source={node.Source} ChannelMap=[{string.Join(",", node.ChannelMap ?? [])}] Saturate={node.Saturate}");
            }
            foreach (var arg in node.Args) Walk(arg.Node);
        }
        foreach (var (pin, root) in diag.Wiring.PinExpressions)
        {
            Console.WriteLine($" pin={pin}");
            Walk(root);
        }
    }
    else
    {
        Console.WriteLine("  (no wiring)");
    }

    Console.WriteLine();
    Console.WriteLine("----- M-channel provenance for Emissive Color, per quality level -----");
    foreach (var resource in material.LoadedMaterialResources)
    {
        if (resource.LoadedShaderMapLegacy is not { } shaderMapForM) continue;
        var qualityLabel = $"Quality={shaderMapForM.ShaderMapId.QualityLevel} FeatureLevel={shaderMapForM.ShaderMapId.FeatureLevel}";
        if (PixelShaderDecompiler.AnalyzeForDiagnostics(material, shaderMapForM) is not { } mDiag || !mDiag.Wiring.Success)
        {
            Console.WriteLine($"  {qualityLabel}: analysis failed");
            continue;
        }

        var referencedTextures = MaterialShaderDecompiler.GetReferencedTextures(material);
        string? ResolveSampleName(PixelExpressionNode n)
        {
            if (n.Source is not { Kind: PixelValueKind.Texture } src) return null;
            var array = src.TextureSlot switch
            {
                0 => mDiag.ExpressionSet.Uniform2DTextureExpressions,
                1 => mDiag.ExpressionSet.UniformCubeTextureExpressions,
                3 => mDiag.ExpressionSet.UniformVolumeTextureExpressions,
                4 => mDiag.ExpressionSet.UniformVirtualTextureExpressions,
                _ => null,
            };
            if (array == null || src.Index < 0 || src.Index >= array.Length) return null;
            return MaterialShaderDecompiler.TryResolveTextureIdentifier(array[src.Index], referencedTextures);
        }

        PixelExpressionNode? mNode = null;
        var seen1 = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
        void FindM(PixelExpressionNode node)
        {
            if (!seen1.Add(node)) return;
            if (node.Op == "sample" && ResolveSampleName(node) == "M") mNode = node;
            foreach (var arg in node.Args) FindM(arg.Node);
        }
        foreach (var root in mDiag.Wiring.PinExpressions.Values) FindM(root);

        if (mNode == null)
        {
            Console.WriteLine($"  {qualityLabel}: 'M' not sampled in this resource's compiled shader.");
            continue;
        }

        // Provenance resolver: for a given node + a specific single output component (0=x..3=w),
        // determines which M-texture channel(s) actually determine that component's value, by
        // propagating through component-wise ops (mul/add/mad/min/max/...) where output.i is a pure
        // function of each arg's OWN component i (translated through that arg's swizzle) - never
        // guessed, only followed through ops that are provably component-wise (dot/cross products,
        // which MIX components together, are deliberately NOT descended into - provenance through
        // those would require actual numeric weighting, not a clean channel identity).
        HashSet<char> ResolveMSources(PixelExpressionNode node, int component, HashSet<PixelExpressionNode> guard)
        {
            if (ReferenceEquals(node, mNode)) return ["xyzw"[component]];
            if (!guard.Add(node)) return [];
            var result = new HashSet<char>();
            IEnumerable<PixelExpressionArg> argsToFollow = node.Op switch
            {
                "mul" or "add" or "sub" or "mad" or "min" or "max" or "div" => node.Args,
                // phi = [Condition, Then, Else] (MergeBranches always emits exactly this order);
                // movc = the raw SM5 conditional-move instruction, same [Condition, Then, Else] arg
                // shape (PrintInstruction's own "movc" case prints it identically to phi: "cond ? A
                // : B"). Neither's Condition is a data source, but either Then/Else could be live at
                // runtime, so both are valid provenance sources to report.
                "phi" or "movc" when node.Args.Count == 3 => [node.Args[1], node.Args[2]],
                _ => [],
            };
            foreach (var arg in argsToFollow)
            {
                var argComponent = arg.Swizzle.Length switch
                {
                    0 => component,
                    1 => "xyzw".IndexOf(arg.Swizzle[0]),
                    _ => component < arg.Swizzle.Length ? "xyzw".IndexOf(arg.Swizzle[component]) : -1,
                };
                if (argComponent < 0) continue;
                foreach (var c in ResolveMSources(arg.Node, argComponent, guard)) result.Add(c);
            }
            guard.Remove(node);
            return result;
        }

        Console.WriteLine($"  {qualityLabel}: available pin keys = [{string.Join(", ", mDiag.Wiring.PinExpressions.Keys)}]");
        if (!mDiag.Wiring.PinExpressions.TryGetValue("Emissive Color", out var emissiveRoot))
        {
            Console.WriteLine($"  {qualityLabel}: no Emissive Color pin");
            continue;
        }
        Console.WriteLine($"  {qualityLabel}: Emissive Color root op={emissiveRoot.Op} argCount={emissiveRoot.Args.Count} argOps=[{string.Join(", ", emissiveRoot.Args.Select(a => $"{a.Node.Op}(swz={a.Swizzle})"))}]");
        var r = ResolveMSources(emissiveRoot, 0, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
        var g = ResolveMSources(emissiveRoot, 1, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
        var b = ResolveMSources(emissiveRoot, 2, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
        Console.WriteLine($"  {qualityLabel}: Emissive.r <- M[{string.Join(",", r.OrderBy(c => c))}]  Emissive.g <- M[{string.Join(",", g.OrderBy(c => c))}]  Emissive.b <- M[{string.Join(",", b.OrderBy(c => c))}]");

        // Isolate specifically what's multiplied by Rim_Intensity, rather than the whole Emissive
        // Color pin (which also mixes in Skin/SSS, DeRez, and OutOfBoundsMask branches that touch M
        // for entirely unrelated reasons).
        var rimIntensityIdx = Array.FindIndex(mDiag.ExpressionSet.UniformScalarExpressions, e => e.ParameterName == "Rim Intensity");
        Console.WriteLine($"  {qualityLabel}: Rim_Intensity scalar index = {rimIntensityIdx}");
        if (rimIntensityIdx >= 0)
        {
            // Find EVERY node anywhere that directly consumes the Rim_Intensity cbrow as an arg -
            // regardless of parent op shape (mul, mad-as-multiplier, mad-as-addend, anything) - so a
            // fused mad instruction isn't silently missed the way a mul-only search would miss it.
            var consumers = new List<(PixelExpressionNode Parent, int ArgIndex)>();
            var seen3 = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
            void FindConsumers(PixelExpressionNode node)
            {
                if (!seen3.Add(node)) return;
                for (var i = 0; i < node.Args.Count; i++)
                {
                    var a = node.Args[i];
                    if (a.Node.Op == "cbrow" && a.Node.Source is { Kind: PixelValueKind.ScalarExpression, Index: var idx } && idx == rimIntensityIdx)
                        consumers.Add((node, i));
                }
                foreach (var arg in node.Args) FindConsumers(arg.Node);
            }
            foreach (var root in mDiag.Wiring.PinExpressions.Values) FindConsumers(root);

            Console.WriteLine($"  {qualityLabel}: found {consumers.Count} direct consumer(s) of the Rim_Intensity cbrow");
            foreach (var (parent, argIndex) in consumers)
            {
                Console.WriteLine($"    parent op={parent.Op} argCount={parent.Args.Count} rimIntensityIsArg#{argIndex} otherArgs=[{string.Join(", ", parent.Args.Select((a, argI) => argI == argIndex ? "<RimIntensity>" : $"#{argI}:{a.Node.Op}(swz={a.Swizzle})"))}]");
                for (var otherIdx = 0; otherIdx < parent.Args.Count; otherIdx++)
                {
                    if (otherIdx == argIndex) continue;
                    var operand = parent.Args[otherIdx].Node;
                    var rr = ResolveMSources(operand, 0, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
                    var rg = ResolveMSources(operand, 1, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
                    var rb = ResolveMSources(operand, 2, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
                    var rw = ResolveMSources(operand, 3, new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance));
                    Console.WriteLine($"      arg#{otherIdx} op={operand.Op}: .x<-M[{string.Join(",", rr.OrderBy(c => c))}] .y<-M[{string.Join(",", rg.OrderBy(c => c))}] .z<-M[{string.Join(",", rb.OrderBy(c => c))}] .w<-M[{string.Join(",", rw.OrderBy(c => c))}]");
                }
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Raw DAG dump for the Normal pin (structure of the TBN reconstruction) -----");
    if (PixelShaderDecompiler.AnalyzeForDiagnostics(material) is { } diagRaw && diagRaw.Wiring.Success
        && diagRaw.Wiring.PinExpressions.TryGetValue("Normal", out var normalRoot))
    {
        var ids = new Dictionary<PixelExpressionNode, int>(ReferenceEqualityComparer.Instance);
        var nextId = 0;
        int IdOf(PixelExpressionNode n) => ids.TryGetValue(n, out var i) ? i : ids[n] = nextId++;
        var printed = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
        void Dump(PixelExpressionNode node, int depth)
        {
            var id = IdOf(node);
            var argsDesc = string.Join(", ", node.Args.Select(a => $"[{IdOf(a.Node)}]{(a.Negate ? " neg" : "")}{(a.Absolute ? " abs" : "")} swz='{a.Swizzle}'"));
            Console.WriteLine($"{new string(' ', depth * 2)}#{id} op={node.Op} Detail='{node.Detail}' Source={node.Source} args=[{argsDesc}]");
            if (!printed.Add(node)) { Console.WriteLine($"{new string(' ', (depth + 1) * 2)}(already printed above)"); return; }
            if (depth > 12) return;
            foreach (var arg in node.Args) Dump(arg.Node, depth + 1);
        }
        Dump(normalRoot, 0);
    }

    Console.WriteLine();
    Console.WriteLine("----- PinSources (coarse: which values reach which pin, always populated) -----");
    if (PixelShaderDecompiler.AnalyzeForDiagnostics(material) is { } diagSrc && diagSrc.Wiring.Success)
    {
        foreach (var (pin, sources) in diagSrc.Wiring.PinSources)
        {
            var scalars = sources.Where(s => s.Kind == PixelValueKind.ScalarExpression).Select(s => s.Index).ToList();
            var vectors = sources.Where(s => s.Kind == PixelValueKind.VectorExpression).Select(s => s.Index).ToList();
            Console.WriteLine($"  {pin}: scalarIdx=[{string.Join(",", scalars)}] vectorIdx=[{string.Join(",", vectors)}]");
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Per-resource (quality level), per-pin: does EQ_MaxIntensityTreble / Rim_Light_Overall_Boost's index show up? -----");
    foreach (var resource in material.LoadedMaterialResources)
    {
        if (resource.LoadedShaderMapLegacy is not { } shaderMap) continue;
        var expr = shaderMap.MaterialCompilationOutput.UniformExpressionSet;
        int FindScalarIndex(string paramName)
        {
            for (var idx = 0; idx < expr.UniformScalarExpressions.Length; idx++)
                if (expr.UniformScalarExpressions[idx].ParameterName == paramName) return idx;
            return -1;
        }
        var eqTrebleIdx = FindScalarIndex("EQ_MaxIntensityTreble");
        var rimBoostIdx = FindScalarIndex("Rim Light Overall Boost");
        Console.WriteLine($" Quality={shaderMap.ShaderMapId.QualityLevel} FeatureLevel={shaderMap.ShaderMapId.FeatureLevel}: " +
                           $"EQ_MaxIntensityTreble is scalar[{eqTrebleIdx}], Rim_Light_Overall_Boost is scalar[{rimBoostIdx}]");

        if (PixelShaderDecompiler.AnalyzeForDiagnostics(material, shaderMap) is not { } diagRes) { Console.WriteLine("   (analysis unavailable)"); continue; }
        if (!diagRes.Wiring.Success) { Console.WriteLine($"   (analysis failed: {diagRes.Wiring.FailureReason})"); continue; }

        foreach (var (pin, pinRoot) in diagRes.Wiring.PinExpressions)
        {
            var scalarIdx = new SortedSet<int>();
            var seenIdx = new HashSet<PixelExpressionNode>(ReferenceEqualityComparer.Instance);
            void Walk(PixelExpressionNode node)
            {
                if (!seenIdx.Add(node)) return;
                if (node.Source is { Kind: PixelValueKind.ScalarExpression } src) scalarIdx.Add(src.Index);
                foreach (var arg in node.Args) Walk(arg.Node);
            }
            Walk(pinRoot);
            var hasTreble = eqTrebleIdx >= 0 && scalarIdx.Contains(eqTrebleIdx);
            var hasRimBoost = rimBoostIdx >= 0 && scalarIdx.Contains(rimBoostIdx);
            if (hasTreble || hasRimBoost)
                Console.WriteLine($"   {pin}: EQ_MaxIntensityTreble={hasTreble} Rim_Light_Overall_Boost={hasRimBoost}");
        }

        Console.WriteLine($"   Other shader stages found: {diagRes.Wiring.ShaderStages.Count}");
        foreach (var stage in diagRes.Wiring.ShaderStages)
        {
            var hasTreble = eqTrebleIdx >= 0 && stage.OutputValues.Any(v => v.Kind == PixelValueKind.ScalarExpression && v.Index == eqTrebleIdx);
            var hasRimBoost = rimBoostIdx >= 0 && stage.OutputValues.Any(v => v.Kind == PixelValueKind.ScalarExpression && v.Index == rimBoostIdx);
            Console.WriteLine($"     stage='{stage.Label}' freq={stage.Frequency} types=[{string.Join(",", stage.TypeNames)}] bindsMaterial={stage.BindsMaterial} outputValues={stage.OutputValues.Count} EQ_MaxIntensityTreble={hasTreble} Rim_Light_Overall_Boost={hasRimBoost}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Material Parameter Collection diagnostics -----");
    foreach (var resource in material.LoadedMaterialResources)
    {
        if (resource.LoadedShaderMapLegacy is not { } shaderMap) continue;
        var expr = shaderMap.MaterialCompilationOutput.UniformExpressionSet;
        Console.WriteLine($" Quality={shaderMap.ShaderMapId.QualityLevel}: ParameterCollections.Length={expr.ParameterCollections.Length}");
        foreach (var guid in expr.ParameterCollections)
            Console.WriteLine($"   collection GUID: {guid}");

        var allShaders = shaderMap.Shaders
            .Concat(shaderMap.MeshShaderMaps.SelectMany(m => m.Shaders))
            .ToList();
        var boundBufferNames = allShaders
            .SelectMany(s => s.UniformBufferParameters.Select(p => p.Name))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"   distinct bound uniform buffer names across {allShaders.Count} shaders: {string.Join(", ", boundBufferNames)}");

        foreach (var s in allShaders)
        {
            var collectionBuffers = s.UniformBufferParameters.Where(p => p.Name.StartsWith("MaterialCollection", StringComparison.Ordinal)).ToList();
            if (collectionBuffers.Count > 0)
                Console.WriteLine($"   shader '{s.TypeName}' binds: {string.Join(", ", collectionBuffers.Select(p => $"{p.Name}@baseIndex{p.Parameter.BaseIndex}(bound={p.Parameter.bIsBound})"))}");
        }

        var basePassShader = allShaders.FirstOrDefault(s => s.TypeName == "TBasePassPSFNoLightMapPolicy");
        if (basePassShader != null)
        {
            Console.WriteLine($"   TBasePassPSFNoLightMapPolicy (Quality={shaderMap.ShaderMapId.QualityLevel}) MaterialUniformBuffer.BaseIndex={basePassShader.MaterialParameters?.MaterialUniformBuffer.BaseIndex} bound={basePassShader.MaterialParameters?.MaterialUniformBuffer.bIsBound}");
            Console.WriteLine($"   all UniformBufferParameters: {string.Join(", ", basePassShader.UniformBufferParameters.Select(p => $"{(string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name)}@{p.Parameter.BaseIndex}(bound={p.Parameter.bIsBound})"))}");
        }

        foreach (var s in allShaders.Where(s => s.TypeName.StartsWith("TBasePassPS", StringComparison.Ordinal)))
        {
            Console.WriteLine($"   DIAG '{s.TypeName}' MaterialUniformBuffer.BaseIndex={s.MaterialParameters?.MaterialUniformBuffer.BaseIndex} bound={s.MaterialParameters?.MaterialUniformBuffer.bIsBound}");
            Console.WriteLine($"     NumVectorExpressions={s.MaterialParameters?.NumVectorExpressions} NumScalarExpressions={s.MaterialParameters?.NumScalarExpressions} Num2DTextureExpressions={s.MaterialParameters?.Num2DTextureExpressions} NumCubeTextureExpressions={s.MaterialParameters?.NumCubeTextureExpressions}");
            Console.WriteLine($"     all UniformBufferParameters: {string.Join(", ", s.UniformBufferParameters.Select(p => $"{(string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name)}@{p.Parameter.BaseIndex}(bound={p.Parameter.bIsBound})"))}");
            Console.WriteLine($"     Resource.OutputHash={s.Resource?.OutputHash} NumInstructions={s.Resource?.NumInstructions} Code.Length={s.Resource?.Code?.Length}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Static switch parameters (whole instance chain) -----");
    {
        UMaterialInterface? current = material;
        var guard = 0;
        while (current is UMaterialInstance instance && ++guard < 16)
        {
            var sp = instance.StaticParameters;
            if (sp?.StaticSwitchParameters is { Length: > 0 } switches)
            {
                Console.WriteLine($"  {instance.Name}:");
                foreach (var sw in switches)
                    Console.WriteLine($"    {sw.Name} = {sw.Value}");
            }
            current = instance.Parent as UMaterialInterface;
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Base material's own declared StaticSwitchParameter nodes (defaults) -----");
    {
        UMaterialInterface? current = material;
        var guard = 0;
        while (current is UMaterialInstance instance && ++guard < 16)
            current = instance.Parent as UMaterialInterface;
        if (current is UMaterial baseMaterial)
        {
            foreach (var index in baseMaterial.Expressions)
            {
                if (index?.ResolvedObject?.Object?.Value is not { } export) continue;
                if (!export.ExportType.Contains("StaticSwitchParameter", StringComparison.Ordinal)) continue;
                var name = export.GetOrDefault<FName>("ParameterName").Text;
                var def = export.GetOrDefault<bool>("DefaultValue");
                Console.WriteLine($"  {name} default={def}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Instance parameter overrides (this object only, not inherited) -----");
    if (material is UMaterialInstanceConstant instanceConstant)
    {
        Console.WriteLine($"  VectorParameterValues: {instanceConstant.VectorParameterValues.Length}");
        foreach (var v in instanceConstant.VectorParameterValues)
            Console.WriteLine($"    {v.Name} = {v.ParameterValue} (ParameterInfo null={v.ParameterInfo == null})");
        Console.WriteLine($"  ScalarParameterValues: {instanceConstant.ScalarParameterValues.Length}");
        foreach (var s in instanceConstant.ScalarParameterValues)
            Console.WriteLine($"    {s.Name} = {s.ParameterValue} (ParameterInfo null={s.ParameterInfo == null})");
        Console.WriteLine($"  TextureParameterValues: {instanceConstant.TextureParameterValues.Length}");
        foreach (var t in instanceConstant.TextureParameterValues)
            Console.WriteLine($"    {t.Name} = {t.ParameterValue.Name} (ParameterInfo null={t.ParameterInfo == null})");
    }
    else
    {
        Console.WriteLine($"  (not a UMaterialInstanceConstant - actual type: {material.GetType().Name})");
    }

    Console.WriteLine();
    Console.WriteLine("----- Material Parameter Collection resolution -----");
    try
    {
        var collections = MaterialParameterCollectionResolver.FindReferencedCollections(material);
        Console.WriteLine($"  Found {collections.Count} collection(s)");
        foreach (var c in collections)
        {
            Console.WriteLine($"  Collection '{c.Name}' StateId={c.StateId} slotCount={c.Slots.Count}");
            foreach (var (row, slot) in c.Slots.OrderBy(kv => kv.Key))
            {
                if (slot.VectorName != null) Console.WriteLine($"    row {row}: vector {slot.VectorName}");
                else Console.WriteLine($"    row {row}: scalars [{string.Join(", ", slot.ScalarNames.Select((n, i) => $"{"xyzw"[i]}={n ?? "-"}"))}]");
            }
        }

        foreach (var resource in material.LoadedMaterialResources)
        {
            if (resource.LoadedShaderMapLegacy is not { } sm) continue;
            var pcs = sm.MaterialCompilationOutput.UniformExpressionSet.ParameterCollections;
            Console.WriteLine($"  Quality={sm.ShaderMapId.QualityLevel}: shader map ParameterCollections=[{string.Join(", ", pcs.Select(g => g.ToString()))}]");
            foreach (var g in pcs)
            {
                var resolved = MaterialParameterCollectionResolver.Resolve(material, g);
                Console.WriteLine($"    {g} => {(resolved != null ? resolved.Name : "UNRESOLVED")}");
            }
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"EXCEPTION: {e}");
    }

    Console.WriteLine();
    Console.WriteLine("----- Raw DXBC disassembly per pin -----");
    if (PixelShaderDecompiler.AnalyzeForDiagnostics(material) is { } diag2 && diag2.Wiring.Success)
    {
        foreach (var (pin, asm) in diag2.Wiring.PinDisassembly)
        {
            Console.WriteLine($"=== {pin} ===");
            Console.WriteLine(asm);
        }
    }

    Console.WriteLine();
    Console.WriteLine("----- Uniform expression tree -----");
    string? uniforms = null;
    try
    {
        uniforms = material.DecompileShaderToPseudo();
    }
    catch (Exception e)
    {
        Console.WriteLine($"EXCEPTION: {e}");
    }
    Console.WriteLine(uniforms ?? "(null)");
}

if (!found)
{
    Console.WriteLine("No UMaterialInterface export found in this package.");
}

Console.WriteLine();
Console.WriteLine("----- Locating texture files by name -----");
foreach (var needle in new[] { "linear_gradient", "Pattern-HeavyArrows" })
{
    var matches = provider.Files.Keys.Where(k => k.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"'{needle}': {matches.Count} match(es)");
    foreach (var m in matches) Console.WriteLine($"  {m}");
}

Console.WriteLine();
Console.WriteLine("----- Material Parameter Collection asset dump -----");
try
{
    var mpcPkg = provider.LoadPackage("FortniteGame/Content/Packages/Fortress_SharedMaterials/GlobalMaterialParameters/FortniteMaterialParameters");
    for (var i = 0; i < mpcPkg.ExportMapLength; i++)
    {
        var p = new FPackageIndex(mpcPkg, i + 1).ResolvedObject;
        if (p?.Object?.Value is not { } mpcObj) continue;
        Console.WriteLine($"export[{i}] type={mpcObj.GetType().Name}");
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(mpcObj, Newtonsoft.Json.Formatting.Indented));
    }
}
catch (Exception e)
{
    Console.WriteLine($"EXCEPTION: {e}");
}

Console.WriteLine();
Console.WriteLine("----- FN_Char_RimColor_v3 material function (cooked) raw dump -----");
try
{
    var fnPkg = provider.LoadPackage("FortniteGame/Content/Packages/Fortress_SharedMaterials/Base_Material_Functions/FN_Char_RimColor_v3");
    for (var i = 0; i < fnPkg.ExportMapLength; i++)
    {
        var p = new FPackageIndex(fnPkg, i + 1).ResolvedObject;
        if (p?.Object?.Value is not { } obj) continue;
        if (!obj.ExportType.Contains("CollectionParameter", StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"export[{i}] type={obj.ExportType} name={obj.Name}");
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented));
    }
}
catch (Exception e)
{
    Console.WriteLine($"EXCEPTION: {e}");
}

Console.WriteLine();
Console.WriteLine("----- Texture formats -----");
foreach (var needle in new[] { "linear_gradient", "Pattern-HeavyArrows" })
{
    var path = provider.Files.Keys.FirstOrDefault(k => k.Contains(needle, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
    if (path is null) { Console.WriteLine($"{needle}: not found"); continue; }
    try
    {
        var texPkg = provider.LoadPackage(path);
        for (var i = 0; i < texPkg.ExportMapLength; i++)
        {
            var p = new FPackageIndex(texPkg, i + 1).ResolvedObject;
            if (p?.Object?.Value is not UTexture2D tex) continue;
            Console.WriteLine($"{tex.Name}: PixelFormat={tex.Format} CompressionSettings={tex.CompressionSettings} SRGB={tex.SRGB}");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"{path}: EXCEPTION: {e.Message}");
    }
}
