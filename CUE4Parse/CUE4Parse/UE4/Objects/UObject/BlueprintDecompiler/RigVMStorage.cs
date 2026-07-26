using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.Utils;

namespace CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;

/// <summary>
/// Resolves a <see cref="FRigVMOperand"/> to the name and, for literals, the authored value of the register it
/// points at. RigVM changed how that memory is stored partway through its life, so this hides the difference:
/// <list type="bullet">
/// <item>UE 4.25 - 4.27 keep named registers in <see cref="FRigVMMemoryContainer"/>s inline on the VM.</item>
/// <item>UE 5.0 moved them to generated classes (<see cref="URigVMMemoryStorageGeneratorClass"/>) whose
/// reflected properties are the registers, with authored values on the literal class's CDO.</item>
/// </list>
/// Either way a register is named after the graph pin it backs, which is what makes the bytecode readable.
/// </summary>
internal abstract class RigVMStorage
{
    /// <summary>Register name normalised to "Node.Pin", or null when the operand doesn't resolve.</summary>
    public abstract string? GetRegisterName(FRigVMOperand operand);

    public abstract string FormatLiteralValue(FRigVMOperand operand, string? pinName);

    protected abstract string? GetOffsetSuffix(FRigVMOperand operand);

    public string FormatOperand(FRigVMOperand operand)
    {
        var name = GetRegisterName(operand);
        if (name is null) return $"unresolved_{operand.MemoryType.ToString().ToLower()}_{operand.RegisterIndex}";
        return name + (operand.RegisterOffset != ushort.MaxValue ? GetOffsetSuffix(operand) ?? "" : "");
    }

    public static RigVMStorage? Resolve(UClass uClass, URigVM vm)
    {
        if (vm.WorkMemoryStorage is { } work && vm.LiteralMemoryStorageOld is { } literal)
            return new ContainerStorage(work, literal);

        return GeneratedClassStorage.TryCreate(uClass, vm);
    }

    /// <summary>
    /// Register names carry compiler markers that aren't part of the pin name: "ExecuteContext!" for the
    /// shared execution context and "Node.Pin::IO" for pins that are both an input and an output.
    /// </summary>
    protected static string StripMarkers(string name) => name.TrimEnd('!').Replace("::IO", "");

    private sealed class ContainerStorage(FRigVMMemoryContainer work, FRigVMMemoryContainer literal) : RigVMStorage
    {
        private FRigVMMemoryContainer? Container(FRigVMOperand operand) => operand.MemoryType switch
        {
            ERigVMMemoryType.Work => work,
            ERigVMMemoryType.Literal => literal,
            _ => null
        };

        private FRigVMRegister? Register(FRigVMOperand operand)
        {
            var container = Container(operand);
            return container is null || operand.RegisterIndex >= container.Registers.Length
                ? null
                : container.Registers[operand.RegisterIndex];
        }

        public override string? GetRegisterName(FRigVMOperand operand) =>
            Register(operand) is { } register ? StripMarkers(register.Name.Text) : null;

        public override string FormatLiteralValue(FRigVMOperand operand, string? pinName) =>
            Register(operand) is { } register ? FormatRegisterValue(register, pinName) : "<unresolved>";

        protected override string? GetOffsetSuffix(FRigVMOperand operand)
        {
            var container = Container(operand);
            return container is not null && operand.RegisterOffset < container.RegisterOffsets.Length
                ? FormatRegisterOffsetSuffix(container.RegisterOffsets[operand.RegisterOffset])
                : null;
        }

        private static string FormatRegisterValue(FRigVMRegister register, string? pinName)
        {
            switch (register.Type)
            {
                case ERigVMRegisterType.Name when register.View is FName[] { Length: > 0 } names:
                    return $"FName(\"{names[0].Text}\")";
                case ERigVMRegisterType.String or ERigVMRegisterType.Struct when register.View is string[] { Length: > 0 } strings:
                    return strings[0];
                case ERigVMRegisterType.Plain when register.View is byte[] bytes:
                    return FormatPlainBytes(bytes, pinName);
                default:
                    return $"<{register.Type}>";
            }
        }

        // Pre-SerializeRigVMOffsetSegmentPaths register offsets don't store their segment path as text, only
        // the target type plus accumulated byte offsets, so sub-pin names are recovered from the layout of the
        // common math types. Anything outside those keeps an explicit "<type>_at_<offset>" form rather than
        // guessing a name that might be wrong.
        private static string FormatRegisterOffsetSuffix(FRigVMRegisterOffset offset)
        {
            if (offset.CachedSegmentPath is { Length: > 0 } path) return $".{path}";

            var byteOffset = offset.Segments.Length > 0 ? offset.Segments[0] : 0;
            return offset.CPPType.Text switch
            {
                // FTransform: FQuat Rotation @0, FVector Translation @16, FVector Scale3D @28.
                "FQuat" when byteOffset == 0 => ".Rotation",
                "FVector" when byteOffset == 16 => ".Translation",
                "FVector" when byteOffset == 28 => ".Scale3D",
                // A float reached through an offset is a component of the enclosing vector/euler triple.
                "float" or "double" when byteOffset is 0 or 4 or 8 => "." + (char) ('X' + byteOffset / 4),
                { Length: > 0 } cppType => $".{cppType.TrimStart('F')}_at_{byteOffset}",
                _ => ""
            };
        }
    }

    /// <summary>
    /// UE 5.0: registers are reflected properties on a generated class per memory type, and the authored
    /// literal values live on that class's CDO.
    /// </summary>
    private sealed class GeneratedClassStorage : RigVMStorage
    {
        /// <summary>A register: the generated property name as stored, plus that name split into "Node.Pin".</summary>
        private readonly record struct Register(string RawName, string Name);

        private readonly Dictionary<ERigVMMemoryType, Register[]> _registers;
        private readonly Dictionary<string, FPropertyTag> _literalValues;
        private readonly FRigVMPropertyPathDescription[] _workPropertyPaths;

        private GeneratedClassStorage(Dictionary<ERigVMMemoryType, Register[]> registers, Dictionary<string, FPropertyTag> literalValues, FRigVMPropertyPathDescription[] workPropertyPaths)
        {
            _registers = registers;
            _literalValues = literalValues;
            _workPropertyPaths = workPropertyPaths;
        }

        private Register? RegisterAt(FRigVMOperand operand) =>
            _registers.TryGetValue(operand.MemoryType, out var registers) && operand.RegisterIndex < registers.Length
                ? registers[operand.RegisterIndex]
                : null;

        public static GeneratedClassStorage? TryCreate(UClass uClass, URigVM vm)
        {
            if (uClass.Owner is not { } package) return null;

            var generatorClasses = package.GetExports().OfType<URigVMMemoryStorageGeneratorClass>().ToArray();
            if (generatorClasses.Length == 0) return null;

            // The unit type names double as the node-name stems, which is what lets a flattened
            // "Node_Pin" property name be split back into its node and pin halves.
            var unitNames = (vm.FunctionNamesStorage ?? [])
                .Select(function => function.Text.SubstringBefore("::").TrimStart('F').SubstringAfter("RigUnit_"))
                .Where(name => name.Length > 0)
                .Distinct()
                .OrderByDescending(name => name.Length)
                .ToArray();

            var registers = new Dictionary<ERigVMMemoryType, Register[]>();
            var workPropertyPaths = Array.Empty<FRigVMPropertyPathDescription>();
            foreach (var generatorClass in generatorClasses)
            {
                registers[generatorClass.MemoryType] = (generatorClass.ChildProperties ?? [])
                    .Select(property => new Register(property.Name.Text, NormalizeName(property.Name.Text, unitNames)))
                    .ToArray();

                if (generatorClass.MemoryType == ERigVMMemoryType.Work)
                    workPropertyPaths = generatorClass.PropertyPathDescriptions ?? [];
            }
            if (registers.Count == 0) return null;

            // Only the literal CDO matters: work memory holds runtime state, not authored values.
            var literalValues = package.GetExports()
                .FirstOrDefault(export => export.Name.StartsWith("Default__RigVMMemory_Literal", StringComparison.OrdinalIgnoreCase))
                ?.Properties
                .GroupBy(property => property.Name.Text)
                .ToDictionary(group => group.Key, group => group.First()) ?? [];

            return new GeneratedClassStorage(registers, literalValues, workPropertyPaths);
        }

        /// <summary>
        /// Turns a generated property name back into "Node.Pin". The generator flattens the pin path into the
        /// property name ("GetInitialBoneTransform_0_Space__Const"), so the node half is recovered by matching
        /// a known unit type name plus its instance suffix.
        /// </summary>
        private static string NormalizeName(string propertyName, string[] unitNames)
        {
            var name = propertyName;
            if (name.EndsWith("__Const", StringComparison.Ordinal)) name = name[..^"__Const".Length];
            name = name.TrimEnd('_');

            foreach (var unitName in unitNames)
            {
                if (!name.StartsWith(unitName, StringComparison.Ordinal)) continue;

                // Consume the instance suffix the compiler appends to disambiguate nodes ("_0", "_1_2", ...).
                var index = unitName.Length;
                while (index < name.Length && name[index] == '_' && index + 1 < name.Length && char.IsDigit(name[index + 1]))
                {
                    index++;
                    while (index < name.Length && char.IsDigit(name[index])) index++;
                }

                if (index < name.Length && name[index] == '_')
                    return $"{name[..index]}.{name[(index + 1)..]}";
            }

            return name;
        }

        public override string? GetRegisterName(FRigVMOperand operand) => RegisterAt(operand)?.Name;

        public override string FormatLiteralValue(FRigVMOperand operand, string? pinName)
        {
            if (RegisterAt(operand) is not { } register) return "<unresolved>";

            // The CDO is keyed by the raw generated property names.
            if (_literalValues.TryGetValue(register.RawName, out var property))
                return FormatPropertyValue(property, pinName);

            // A literal left at its type default isn't written to the CDO at all.
            return "default";
        }

        protected override string? GetOffsetSuffix(FRigVMOperand operand)
        {
            if (operand.MemoryType != ERigVMMemoryType.Work || operand.RegisterOffset >= _workPropertyPaths.Length)
                return null;

            // UE 5.0 stores the segment path as text, so the sub-pin name needs no reconstruction.
            var segmentPath = _workPropertyPaths[operand.RegisterOffset].SegmentPath;
            return string.IsNullOrEmpty(segmentPath) ? null : "." + segmentPath.Replace('/', '.');
        }

        private static string FormatPropertyValue(FPropertyTag property, string? pinName)
        {
            var value = property.Tag?.GenericValue;
            return value switch
            {
                // Byte-backed enums surface as a qualified FName ("EBoneGetterSetterMode::GlobalSpace"),
                // which is already valid C++ - only genuine name pins want the FName(...) wrapper.
                FName enumValue when enumValue.Text.Contains("::") => enumValue.Text,
                FName name => $"FName(\"{name.Text}\")",
                string text => $"\"{text}\"",
                bool flag => flag ? "true" : "false",
                float or double => Convert.ToString(value, CultureInfo.InvariantCulture) + "f",
                byte or sbyte or short or ushort or int or uint or long or ulong => value.ToString()!,
                _ => property.Tag?.ToString() ?? "default"
            };
        }
    }

    protected static string FormatPlainBytes(byte[] bytes, string? pinName)
    {
        switch (bytes.Length)
        {
            case 1:
                // A single byte is either a bool or a byte-backed enum; the bool-prefix naming convention on
                // the pin is the only signal available in cooked data.
                return pinName?.StartsWith('b') == true
                    ? bytes[0] switch { 0 => "false", _ => "true" }
                    : bytes[0].ToString();
            case 4:
            {
                var asInt = BitConverter.ToInt32(bytes);
                if (Math.Abs(asInt) <= 1_000_000) return asInt.ToString();
                return $"{BitConverter.ToSingle(bytes).ToString(CultureInfo.InvariantCulture)}f";
            }
            default:
                return $"<{bytes.Length} bytes>";
        }
    }
}
