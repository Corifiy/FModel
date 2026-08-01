using System;
using System.IO;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace CUE4Parse.Example
{
    public static class Program
    {
        static void Run(string label, string paks, string usmap, EGame game, string aes, string asset)
        {
            var p = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, true, new VersionContainer(game));
            p.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);
            p.Initialize();
            p.SubmitKey(new FGuid(), new FAesKey(aes));
            foreach (var e in p.LoadPackage(asset).GetExports())
            {
                if (e is not UClass c || !c.Name.EndsWith("_C")) continue;
                var text = c.DecompileBlueprintToPseudo(p.MappingsContainer!.MappingsForGame);
                File.WriteAllText(Path.Combine(@"C:\Users\admin\AppData\Local\Temp\claude\D--build-FModel-FModel\b9876ade-e914-4275-b620-532a082d768c\scratchpad", label + ".txt"), text);
                Console.WriteLine($"RESULT {label,-8} chars={text.Length,-7} unresolved={System.Text.RegularExpressions.Regex.Matches(text, "unresolved_").Count}");
            }
        }

        public static void Main()
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
            const string M = @"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\";
            const string P = @"V:\.builds\31.40\FortniteGame\Content\Paks";
            const string U = M + @"31.40\++Fortnite+Release-31.40-CL-36874825-Windows_oo.usmap";
            const string K = "0x6B80868E9345C839D8B10CE00179763E15E5FDA976E499D6CFBEDB41AC0FAD36";
            Run("Deform", P, U, EGame.GAME_UE5_5, K, "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/F_MED_DeformRig_Lilac_ControlRig");
            Run("BeltB", P, U, EGame.GAME_UE5_5, K, "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/F_MED_Lilac_BeltB_CtrlRig");
        }
    }
}
