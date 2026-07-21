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
const string assetPath = "FortniteGame/Content/Packages/Fortress_SharedMaterials/Base_Material/M_FN_Character_MASTER";

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
