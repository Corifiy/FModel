using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Objects.RigVM;

public class FRigVMRegister
{
    public readonly ERigVMRegisterType Type;
    public readonly uint ByteIndex;
    public readonly ushort ElementSize;
    public readonly ushort ElementCount;
    public readonly ushort SliceIndex;
    public readonly ushort SliceCount;
    public readonly byte AlignmentBytes;
    public readonly ushort TrailingBytes;
    public readonly FName Name;
    public readonly int ScriptStructIndex;
    public readonly bool bIsArray;
    public readonly bool bIsDynamic;
    public object? View;

    public FRigVMRegister(FArchive Ar, FRigVMMemoryLayout layout)
    {
        Type = Ar.Read<ERigVMRegisterType>();
        ByteIndex = Ar.Read<uint>();
        ElementSize = Ar.Read<ushort>();
        ElementCount = Ar.Read<ushort>();
        SliceIndex = Ar.Read<ushort>();
        SliceCount = Ar.Read<ushort>();
        AlignmentBytes = Ar.Read<byte>();
        TrailingBytes = Ar.Read<ushort>();
        Name = Ar.ReadFName();
        ScriptStructIndex = Ar.Read<int>();
        // Both bools are version-gated (FRigVMRegister::Serialize, RigVMMemory.cpp): builds cooked from
        // pre-release 4.26 snapshots (e.g. Fortnite 14.x) have bIsArray but not yet bIsDynamic.
        bIsArray = layout.bSerializeRegisterArrayState && Ar.ReadBoolean();
        bIsDynamic = layout.bSerializeRegisterDynamicState && Ar.ReadBoolean();
    }

    public bool IsDynamic() => bIsDynamic;
    public bool IsNestedDynamic() => bIsDynamic && bIsArray;
    public ulong GetWorkByteIndex(int sliceIndex = 0) => (ulong) (ByteIndex + sliceIndex * ElementCount * ElementSize);
}
