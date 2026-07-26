using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Objects.RigVM;

public class URigVM : Assets.Exports.UObject
{
    public uint CachedVMHash;
    public string? ExecuteContextPath;
    public FRigVMPropertyPathDescription[]? ExternalPropertyPathDescriptions;
    public FName[]? FunctionNamesStorage;
    public FRigVMByteCode? ByteCodeStorage;
    public FRigVMParameter[]? Parameters;
    public Dictionary<FRigVMOperand, FRigVMOperand[]>? OperandToDebugRegisters;
    public Dictionary<string, FSoftObjectPath>? UserDefinedStructGuidToPathName;
    public Dictionary<string, FSoftObjectPath>? UserDefinedEnumToPathName;
    public FRigVMMemoryContainer? WorkMemoryStorage;
    public FRigVMMemoryStorageStruct? LiteralMemoryStorage;
    public FRigVMMemoryStorageStruct? DefaultWorkMemoryStorage;
    public FRigVMMemoryStorageStruct? DefaultDebugMemoryStorage;
    public FRigVMMemoryContainer? LiteralMemoryStorageOld;
    public FRigVMMemoryContainer? DefaultWorkMemoryStorageOld;
    public FRigVMMemoryContainer? DefaultDebugMemoryStorageOld;
    public FRigVMRegistry_NoLock? LocalizedRegistry;

    /// <summary>
    /// Reads the UClass-based storage block (UE 4.25 - 5.0), probing the candidate layouts from
    /// <see cref="FRigVMMemoryLayout"/> until the whole block parses cleanly. The members here are all gated
    /// on custom versions that unversioned cooked packages don't carry, and the engine version alone can't
    /// tell a release apart from a mid-development snapshot of it, so the byte stream decides. Probing spans
    /// the entire block because a wrong guess in the memory containers only surfaces later, as a garbage
    /// FName once <see cref="FRigVMParameter"/> is reached.
    /// </summary>
    private void ReadUClassBasedStorage(FAssetArchive Ar)
    {
        var start = Ar.Position;
        var candidates = FRigVMMemoryLayout.GetCandidates(Ar);
        for (var i = 0; i < candidates.Length; i++)
        {
            Ar.Position = start;
            try
            {
                var workMemory = new FRigVMMemoryContainer(Ar, candidates[i]);
                var literalMemory = new FRigVMMemoryContainer(Ar, candidates[i]);
                if (!workMemory.LooksValid() || !literalMemory.LooksValid()) continue;

                var functionNames = Ar.ReadArray(Ar.ReadFName);
                var byteCode = new FRigVMByteCode(Ar, candidates[i]);
                var parameters = Ar.ReadArray(() => new FRigVMParameter(Ar));

                if (i > 0)
                    Log.Debug("RigVM did not match the layout implied by the package's engine version; using {Layout} instead", candidates[i]);

                WorkMemoryStorage = workMemory;
                LiteralMemoryStorageOld = literalMemory;
                FunctionNamesStorage = functionNames;
                ByteCodeStorage = byteCode;
                Parameters = parameters;
                return;
            }
            catch (Exception e) when (e is ParserException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // A wrong layout desyncs the stream, so a failure here just rules this candidate out.
            }
        }

        Ar.Position = start;
        throw new ParserException(Ar, "Could not read RigVM storage with any known serialization layout");
    }

    public override void Deserialize(FAssetArchive Ar, long validPos)
    {
        if (FAnimObjectVersion.Get(Ar) < FAnimObjectVersion.Type.StoreMarkerNamesOnSkeleton) return;

        if (FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.BeforeCustomVersionWasAdded)
        {
            int RigVMUClassBasedStorageDefine = 1;
            if (FUE5MainStreamObjectVersion.Get(Ar) >= FUE5MainStreamObjectVersion.Type.RigVMMemoryStorageObject)
                RigVMUClassBasedStorageDefine = Ar.Read<int>();

            if (FUE5MainStreamObjectVersion.Get(Ar) < FUE5MainStreamObjectVersion.Type.RigVMExternalExecuteContextStruct
                && FUE5MainStreamObjectVersion.Get(Ar) >= FUE5MainStreamObjectVersion.Type.RigVMSerializeExecuteContextStruct)
            {
                // Context is now external to the VM, just serializing the string to keep compatibility
                ExecuteContextPath = Ar.ReadFString();
            }

            if (RigVMUClassBasedStorageDefine == 1)
            {
                ReadUClassBasedStorage(Ar);

                if (FUE5MainStreamObjectVersion.Get(Ar) < FUE5MainStreamObjectVersion.Type.RigVMCopyOpStoreNumBytes) return;

                if (FUE5ReleaseStreamObjectVersion.Get(Ar) >= FUE5ReleaseStreamObjectVersion.Type.RigVMSaveDebugMapInGraphFunctionData
                    || FFortniteMainBranchObjectVersion.Get(Ar) >= FFortniteMainBranchObjectVersion.Type.RigVMSaveDebugMapInGraphFunctionData)
                {
                    OperandToDebugRegisters = Ar.ReadMap(Ar.Read<FRigVMOperand>, () => Ar.ReadArray(Ar.Read<FRigVMOperand>));
                }

                if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMStoringUserDefinedStructMap
                    && FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.HostStoringUserDefinedData)
                {
                    UserDefinedStructGuidToPathName = Ar.ReadMap(Ar.ReadFString, () => new FSoftObjectPath(Ar));
                }

                if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMStoringUserDefinedEnumMap
                    && FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.HostStoringUserDefinedData)
                {
                    UserDefinedEnumToPathName = Ar.ReadMap(Ar.ReadFString, () => new FSoftObjectPath(Ar));
                }
            }

            if (RigVMUClassBasedStorageDefine != 0) return;
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.AddedVMHashChecks)
        {
            CachedVMHash = Ar.Read<uint>();
        }

        ExternalPropertyPathDescriptions = Ar.ReadArray(() => new FRigVMPropertyPathDescription(Ar));
        FunctionNamesStorage = Ar.ReadArray(Ar.ReadFName);
        ByteCodeStorage = new FRigVMByteCode(Ar);
        Parameters = Ar.ReadArray(() => new FRigVMParameter(Ar));

        if (FUE5ReleaseStreamObjectVersion.Get(Ar) >= FUE5ReleaseStreamObjectVersion.Type.RigVMSaveDebugMapInGraphFunctionData ||
            FFortniteMainBranchObjectVersion.Get(Ar) >= FFortniteMainBranchObjectVersion.Type.RigVMSaveDebugMapInGraphFunctionData)
        {
            if (FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.DebugOperandMappingSimplified)
                OperandToDebugRegisters = Ar.ReadMap(Ar.Read<FRigVMOperand>, () => Ar.ReadArray(Ar.Read<FRigVMOperand>));
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMStoringUserDefinedStructMap &&
            FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.HostStoringUserDefinedData)
        {
            UserDefinedStructGuidToPathName = Ar.ReadMap(Ar.ReadFString, () => new FSoftObjectPath(Ar));
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMMemoryStorageStructSerialized &&
            FRigVMObjectVersion.Get(Ar) < FRigVMObjectVersion.Type.HostStoringUserDefinedData)
        {
            UserDefinedEnumToPathName = Ar.ReadMap(Ar.ReadFString, () => new FSoftObjectPath(Ar));
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMMemoryStorageStructSerialized)
        {
            LiteralMemoryStorage = new FRigVMMemoryStorageStruct(Ar);
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.VMMemoryStorageDefaultsGeneratedAtVM)
        {
            DefaultWorkMemoryStorage = new FRigVMMemoryStorageStruct(Ar);
            DefaultDebugMemoryStorage = new FRigVMMemoryStorageStruct(Ar);
        }

        if (FRigVMObjectVersion.Get(Ar) >= FRigVMObjectVersion.Type.LocalizedRegistry)
        {
            var bStoredLocalizedRegistry = Ar.ReadBoolean();

            if (!bStoredLocalizedRegistry) return;
            var ArchivePosAfterSerializedRegistry = Ar.Read<long>();
            LocalizedRegistry = new FRigVMRegistry_NoLock(Ar);
        }
    }

    protected internal override void WriteJson(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteJson(writer, serializer);

        writer.WritePropertyName(nameof(CachedVMHash));
        writer.WriteValue(CachedVMHash);

        if (ExecuteContextPath != null)
        {
            writer.WritePropertyName(nameof(ExecuteContextPath));
            writer.WriteValue(ExecuteContextPath);
        }

        if (ExternalPropertyPathDescriptions != null)
        {
            writer.WritePropertyName(nameof(ExternalPropertyPathDescriptions));
            serializer.Serialize(writer, ExternalPropertyPathDescriptions);
        }

        if (FunctionNamesStorage != null)
        {
            writer.WritePropertyName(nameof(FunctionNamesStorage));
            serializer.Serialize(writer, FunctionNamesStorage);
        }

        if (ByteCodeStorage != null)
        {
            writer.WritePropertyName(nameof(ByteCodeStorage));
            serializer.Serialize(writer, ByteCodeStorage);
        }

        if (Parameters != null)
        {
            writer.WritePropertyName(nameof(Parameters));
            serializer.Serialize(writer, Parameters);
        }

        if (OperandToDebugRegisters != null)
        {
            writer.WritePropertyName(nameof(OperandToDebugRegisters));
            serializer.Serialize(writer, OperandToDebugRegisters);
        }

        if (UserDefinedStructGuidToPathName != null)
        {
            writer.WritePropertyName(nameof(UserDefinedStructGuidToPathName));
            serializer.Serialize(writer, UserDefinedStructGuidToPathName);
        }

        if (UserDefinedEnumToPathName != null)
        {
            writer.WritePropertyName(nameof(UserDefinedEnumToPathName));
            serializer.Serialize(writer, UserDefinedEnumToPathName);
        }

        if (WorkMemoryStorage != null)
        {
            writer.WritePropertyName(nameof(WorkMemoryStorage));
            serializer.Serialize(writer, WorkMemoryStorage);
        }

        if (LiteralMemoryStorage != null)
        {
            writer.WritePropertyName(nameof(LiteralMemoryStorage));
            serializer.Serialize(writer, LiteralMemoryStorage);
        }

        if (DefaultWorkMemoryStorage != null)
        {
            writer.WritePropertyName(nameof(DefaultWorkMemoryStorage));
            serializer.Serialize(writer, DefaultWorkMemoryStorage);
        }

        if (DefaultDebugMemoryStorage != null)
        {
            writer.WritePropertyName(nameof(DefaultDebugMemoryStorage));
            serializer.Serialize(writer, DefaultDebugMemoryStorage);
        }

        if (LiteralMemoryStorageOld != null)
        {
            writer.WritePropertyName(nameof(LiteralMemoryStorageOld));
            serializer.Serialize(writer, LiteralMemoryStorageOld);
        }

        if (DefaultWorkMemoryStorageOld != null)
        {
            writer.WritePropertyName(nameof(DefaultWorkMemoryStorageOld));
            serializer.Serialize(writer, DefaultWorkMemoryStorageOld);
        }

        if (DefaultDebugMemoryStorageOld != null)
        {
            writer.WritePropertyName(nameof(DefaultDebugMemoryStorageOld));
            serializer.Serialize(writer, DefaultDebugMemoryStorageOld);
        }

        if (LocalizedRegistry != null)
        {
            writer.WritePropertyName(nameof(LocalizedRegistry));
            serializer.Serialize(writer, LocalizedRegistry);
        }
    }
}
