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
        public static void Main()
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
            var p = new DefaultFileProvider(@"V:\.builds\40.10\Fortnite\FortniteGame\Content\Paks", SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE6_0));
            p.MappingsContainer = new FileUsmapTypeMappingsProvider(@"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\40.10\++Fortnite+Release-40.10-CL-52157884_zs.usmap");
            p.Initialize();
            p.SubmitKey(new FGuid(), new FAesKey("0x03C8AAEDE702DB50231125AF91F24EF9171723274AC73DFBE06C95FF9AE911D6"));

            var outDir = @"C:\Users\admin\AppData\Local\Temp\claude\D--build-FModel-FModel\b9876ade-e914-4275-b620-532a082d768c\scratchpad";
            var pkg = p.LoadPackage("FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_JadeTowelGloss/Meshes/F_MED_JadeTowelGloss_CtrlRig");
            foreach (var e in pkg.GetExports())
            {
                if (e is not UClass c || !c.Name.EndsWith("_C")) continue;
                var text = c.DecompileBlueprintToPseudo(p.MappingsContainer!.MappingsForGame);
                File.WriteAllText(Path.Combine(outDir, "jade.txt"), text);
                Console.WriteLine($"chars={text.Length} defaults={System.Text.RegularExpressions.Regex.Matches(text, "default").Count}");
            }
        }
    }
}
