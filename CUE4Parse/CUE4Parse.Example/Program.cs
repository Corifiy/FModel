using System;
using System.IO;
using System.Linq;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace CUE4Parse.Example
{
    public static class Program
    {
        public static void Main()
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
            var p = new DefaultFileProvider(@"V:\.builds\31.40\FortniteGame\Content\Paks", SearchOption.TopDirectoryOnly, true, new VersionContainer(EGame.GAME_UE5_5));
            p.MappingsContainer = new FileUsmapTypeMappingsProvider(@"C:\Users\admin\AppData\Roaming\Core\.installation\.mappings\.github\31.40\++Fortnite+Release-31.40-CL-36874825-Windows_oo.usmap");
            p.Initialize();
            p.SubmitKey(new FGuid(), new FAesKey("0x6B80868E9345C839D8B10CE00179763E15E5FDA976E499D6CFBEDB41AC0FAD36"));

            const string path = "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Characters/Player/Female/Medium/Bodies/F_MED_Lilac/Meshes/F_MED_DeformRig_Lilac_ControlRig";
            foreach (var e in p.LoadPackage(path).GetExports())
            {
                if (e is not URigVM vm || vm.ByteCodeStorage is not { } bc) continue;
                Console.WriteLine($"instructions={bc.Instructions.Count}  branchInfos={bc.BranchInfos.Length}");
                foreach (var b in bc.BranchInfos.Take(14))
                    Console.WriteLine($"BI[{b.Index,3}] Label={b.Label.Text,-12} InstructionIndex={b.InstructionIndex,4} ArgIndex={b.ArgumentIndex,2} First={b.FirstInstruction,4} Last={b.LastInstruction,4}");
                for (var i = 0; i < bc.Instructions.Count; i++)
                    if (bc.Instructions[i] is FRigVMJumpToBranchOp j)
                        Console.WriteLine($"JUMPTOBRANCH at instruction {i}: FirstBranchInfoIndex={j.FirstBranchInfoIndex}");
            }
        }
    }
}
