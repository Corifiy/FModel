using System;
using System.IO;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CUE4Parse.Example
{
    public static class Program
    {
        public static void Main()
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console(theme: AnsiConsoleTheme.Literate).CreateLogger();
            var provider = new DefaultFileProvider(@"V:\.builds\19.01\FortniteGame\Content\Paks", SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE5_0));
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(@"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\19.01\++Fortnite+Release-19.01-CL-18489740-Windows_oo.usmap");
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey("0xDAE1418B289573D4148C72F3C76ABC7E2DB9CAA618A3EAF2D8580EB3A1BB7A63"));

            var pkg = provider.LoadPackage("FortniteGame/Content/Characters/Player/Male/Medium/Bodies/M_MED_Werewolf_01/Meshes/M_MED_Werewolf_ControlRig");

            // Exactly what FModel does: decompile every UClass export, in export order.
            foreach (var export in pkg.GetExports())
            {
                if (export is not UClass cls) continue;
                Console.WriteLine($"--- {cls.Name} (C#={cls.GetType().Name}) ---");
                if (cls is URigVMBlueprintGeneratedClass rigClass)
                    Console.WriteLine($"    VM null? {rigClass.VM == null}; instr={rigClass.VM?.ByteCodeStorage?.Instructions.Count ?? -1}; funcs={rigClass.VM?.FunctionNamesStorage?.Length ?? -1}");
                Console.WriteLine($"    Owner null? {cls.Owner == null}");
                var text = cls.DecompileBlueprintToPseudo(pkg.Mappings);
                Console.WriteLine($"    decompiled {text.Length} chars, graph={text.Contains("Decompiled ControlRig graph")}");
            }
        }
    }
}
