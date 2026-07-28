using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Serilog;

const string Build = @"V:\.builds\31.40\FortniteGame\Content\Paks";
const string Mappings = @"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\31.40\++Fortnite+Release-31.40-CL-36874825-Windows_oo.usmap";
const string Key = "0x6B80868E9345C839D8B10CE00179763E15E5FDA976E499D6CFBEDB41AC0FAD36";

Log.Logger = new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();

var provider = new DefaultFileProvider(Build, SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE5_5));
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(Mappings);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(Key));

var outDir = args.Length > 1 ? args[1] : ".";
foreach (var path in args.Length > 0 && args[0].Length > 0 ? [args[0]] : new[]
         {
             "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/F_MED_Lilac_AnimBP",
             "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/Parts/F_MED_Lilac_FaceAcc_AnimBP"
         })
{
    var pkg = provider.LoadPackage(path);
    foreach (var export in pkg.GetExports())
    {
        if (export is not UClass cls) continue;
        var text = cls.DecompileBlueprintToPseudo(pkg.Mappings);
        var file = Path.Combine(outDir, cls.Name + ".cpp");
        File.WriteAllText(file, text);
        Console.WriteLine($"{file}: {text.Length} chars");

        if (Environment.GetEnvironmentVariable("DUMP_JSON") == "1")
            File.WriteAllText(Path.Combine(outDir, cls.Name + ".json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(pkg.GetExports(), Newtonsoft.Json.Formatting.Indented));
    }
}
