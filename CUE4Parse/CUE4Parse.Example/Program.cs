using System;
using System.IO;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CUE4Parse.Example
{
    public static class Program
    {
        public static void Main()
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console(theme: AnsiConsoleTheme.Literate).CreateLogger();
            var provider = new DefaultFileProvider(@"V:\.builds\31.40\FortniteGame\Content\Paks", SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE5_5));
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(@"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\31.40\++Fortnite+Release-31.40-CL-36874825-Windows_oo.usmap");
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey("0x6B80868E9345C839D8B10CE00179763E15E5FDA976E499D6CFBEDB41AC0FAD36"));

            const string path = "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/Parts/F_MED_Lilac_FaceAcc_AnimBP";
            var pkg = provider.LoadPackage(path);

            var outDir = @"C:\Users\admin\AppData\Local\Temp\claude\D--build-FModel-FModel\86b7bc31-1601-4f28-8cc6-82f597621d75\scratchpad";
            Directory.CreateDirectory(outDir);

            using var sw = new StreamWriter(Path.Combine(outDir, "animbp_dump.txt"));
            foreach (var export in pkg.GetExports())
            {
                sw.WriteLine($"===== EXPORT {export.Name} : {export.ExportType} (C#={export.GetType().Name}) =====");
                if (export is UStruct st)
                {
                    sw.WriteLine($"  Super={st.SuperStruct?.Name}");
                    sw.WriteLine($"  ChildProperties: {st.ChildProperties?.Length ?? 0}");
                    foreach (var cp in st.ChildProperties ?? [])
                        sw.WriteLine($"    - {cp.Name} : {cp.GetType().Name}");
                }
                if (export is UClass cls)
                {
                    sw.WriteLine($"  FuncMap: {cls.FuncMap.Count}");
                    foreach (var kv in cls.FuncMap) sw.WriteLine($"    fn {kv.Key} -> {kv.Value.Name}");
                }
            }

            sw.WriteLine();
            sw.WriteLine("################ JSON ################");
            sw.WriteLine(JsonConvert.SerializeObject(pkg.GetExports(), Formatting.Indented));
            sw.Flush();
            Console.WriteLine("done");
        }
    }
}
