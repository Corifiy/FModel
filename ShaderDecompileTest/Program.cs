using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Encryption.Aes;
using FModel.ViewModels;

const string paksPath = @"V:\.builds\10.40\FortniteGame\Content\Paks";
const string aesKey = "0x3FF229552FE0F0DC46A495F9E94766EB6B5106A136597C60E7132F413B7C016E";
const string assetPath = "FortniteGame/Content/Characters/Player/Female/Medium/Bodies/F_Med_Soldier_01/Skins/BR_Grave/Materials/F_MED_Body_Grave";

Console.WriteLine($"Mounting {paksPath} ...");
var provider = new DefaultFileProvider(paksPath, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE4_23), StringComparer.OrdinalIgnoreCase)
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
    }

    Console.WriteLine();
    Console.WriteLine("----- Instance parameter overrides (this object only, not inherited) -----");
    if (material is UMaterialInstanceConstant instanceConstant)
    {
        Console.WriteLine($"  VectorParameterValues: {instanceConstant.VectorParameterValues.Length}");
        foreach (var v in instanceConstant.VectorParameterValues)
            Console.WriteLine($"    {v.ParameterInfo.Name} = {v.ParameterValue}");
        Console.WriteLine($"  ScalarParameterValues: {instanceConstant.ScalarParameterValues.Length}");
        foreach (var s in instanceConstant.ScalarParameterValues)
            Console.WriteLine($"    {s.ParameterInfo.Name} = {s.ParameterValue}");
        Console.WriteLine($"  TextureParameterValues: {instanceConstant.TextureParameterValues.Length}");
        foreach (var t in instanceConstant.TextureParameterValues)
            Console.WriteLine($"    {t.ParameterInfo.Name} = {t.ParameterValue.Name}");
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
