using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Objects.RigVM;

/// <summary>
/// Which optional fields a RigVM serializes, for the UClass-based storage era (UE 4.25 - 5.0).
/// <para>
/// Every one of these is gated on a custom version, but cooked packages from this era are unversioned, so the
/// custom version has to be inferred from <see cref="EGame"/>. That inference is one value per engine version
/// and cannot distinguish an engine release from a game built off a mid-development snapshot of the same
/// version - e.g. Fortnite 14.40 (CL-14550713, ~Sept 2020) predates the 4.26 release by months and sits
/// between <see cref="FAnimObjectVersion.Type.NotifyAndSyncMarkerGuids"/> and
/// <see cref="FAnimObjectVersion.Type.SerializeRigVMRegisterDynamicState"/>, while 4.26 release is at
/// <see cref="FAnimObjectVersion.Type.GroomBindingSerialization"/>. <see cref="URigVM"/> therefore probes the
/// candidates from <see cref="GetCandidates"/> against the byte stream instead of trusting the inferred
/// version alone.
/// </para>
/// </summary>
public readonly struct FRigVMMemoryLayout(FAnimObjectVersion.Type animVersion, bool bSerializeOffsetSegmentPaths)
{
    public readonly FAnimObjectVersion.Type AnimVersion = animVersion;
    public readonly bool bSerializeOffsetSegmentPaths = bSerializeOffsetSegmentPaths;

    /// <summary>FRigVMRegister::Serialize - trailing bIsDynamic bool.</summary>
    public bool bSerializeRegisterDynamicState => AnimVersion >= FAnimObjectVersion.Type.SerializeRigVMRegisterDynamicState;

    /// <summary>FRigVMRegister::Serialize - trailing bIsArray bool.</summary>
    public bool bSerializeRegisterArrayState => AnimVersion >= FAnimObjectVersion.Type.SerializeRigVMRegisterArrayState;

    /// <summary>FRigVMByteCode::Serialize - trailing entry name table.</summary>
    public bool bSerializeEntries => AnimVersion >= FAnimObjectVersion.Type.SerializeRigVMEntries;

    /// <summary>
    /// Layouts to try, most likely first: the one the inferred custom versions call for, then the
    /// combinations a mid-development snapshot build could plausibly have shipped with.
    /// </summary>
    public static FRigVMMemoryLayout[] GetCandidates(FArchive Ar)
    {
        var animVersion = FAnimObjectVersion.Get(Ar);
        var bOffsetSegmentPaths = FReleaseObjectVersion.Get(Ar) >= FReleaseObjectVersion.Type.SerializeRigVMOffsetSegmentPaths;

        // The snapshot fallback pins FAnimObjectVersion just below the first RigVM change that a
        // pre-release build could be missing, which also implies no bytecode entry table.
        const FAnimObjectVersion.Type snapshot = FAnimObjectVersion.Type.NotifyAndSyncMarkerGuids;

        return
        [
            new FRigVMMemoryLayout(animVersion, bOffsetSegmentPaths),
            new FRigVMMemoryLayout(snapshot, false),
            new FRigVMMemoryLayout(snapshot, true),
            new FRigVMMemoryLayout(animVersion, !bOffsetSegmentPaths)
        ];
    }

    public override string ToString() => $"{nameof(AnimVersion)}: {AnimVersion}, {nameof(bSerializeOffsetSegmentPaths)}: {bSerializeOffsetSegmentPaths}";
}
