using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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

    /// <summary>The path into a register an operand addresses, or null when it addresses the whole thing.</summary>
    protected virtual string? GetSegmentPath(FRigVMOperand operand) => null;

    /// <summary>
    /// An operand can address one member of a literal rather than the whole of it - the compiler copies a
    /// struct constant a field at a time - so the value worth printing is that member's, not the struct it was
    /// cut from. Falls back to the whole value for a shape this cannot walk, which loses nothing.
    /// </summary>
    protected FPropertyTag ResolveSegment(FPropertyTag property, FRigVMOperand operand)
    {
        var segmentPath = GetSegmentPath(operand);
        if (string.IsNullOrEmpty(segmentPath)) return property;

        var current = property;
        foreach (var segment in segmentPath.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if ((current.Tag?.GenericValue as FScriptStruct)?.StructType is not FStructFallback members) return property;

            var member = members.Properties.FirstOrDefault(candidate => candidate.Name.Text == segment);
            if (member is null) return property;

            current = member;
        }

        return current;
    }

    public string FormatOperand(FRigVMOperand operand)
    {
        var name = GetRegisterName(operand);
        if (name is null) return $"unresolved_{operand.MemoryType.ToString().ToLower()}_{operand.RegisterIndex}";
        if (operand.RegisterOffset == ushort.MaxValue) return name;

        // A variable is read a member at a time, and the paths into it belong to the VM rather than to either
        // block of memory - so an external operand's offset resolves against a table the storages never see.
        var suffix = operand.MemoryType == ERigVMMemoryType.External
            ? GetExternalOffsetSuffix(operand)
            : GetOffsetSuffix(operand);

        return name + (suffix ?? "");
    }

    /// <summary>
    /// External operands address the host's own variables, which the engine enumerates as the class's
    /// non-native properties in declaration order (URigVMHost::GetExternalVariablesImpl) - i.e. exactly the
    /// generated class's ChildProperties.
    /// </summary>
    protected string[] ExternalNames = [];

    /// <summary>Paths into those variables, for operands that address one member rather than the whole thing.</summary>
    protected FRigVMPropertyPathDescription[] ExternalPropertyPaths = [];

    protected string? GetExternalName(FRigVMOperand operand) =>
        operand.RegisterIndex < ExternalNames.Length ? ExternalNames[operand.RegisterIndex] : null;

    private string? GetExternalOffsetSuffix(FRigVMOperand operand) =>
        operand.RegisterOffset < ExternalPropertyPaths.Length
            ? FormatSegmentPath(ExternalPropertyPaths[operand.RegisterOffset].SegmentPath)
            : null;

    public static RigVMStorage? Resolve(UClass uClass, URigVM vm)
    {
        if (vm.WorkMemoryStorage is { } work && vm.LiteralMemoryStorageOld is { } literal)
            return new ContainerStorage(work, literal);

        // UE 5.1+ moved the register tables onto the VM as property bags. The generated classes usually still
        // exist alongside them but are stale - shorter, and diverging in order partway through - so the bags
        // win wherever they are present.
        var storage = PropertyBagStorage.TryCreate(uClass, vm) ?? (RigVMStorage?) GeneratedClassStorage.TryCreate(uClass, vm);
        if (storage is not null)
        {
            storage.ExternalNames = (uClass.ChildProperties ?? []).Select(property => property.Name.Text).ToArray();
            storage.ExternalPropertyPaths = vm.ExternalPropertyPathDescriptions ?? [];
        }
        return storage;
    }

    /// <summary>
    /// Register names carry compiler markers that aren't part of the pin name: "ExecuteContext!" for the
    /// shared execution context and "Node.Pin::IO" for pins that are both an input and an output.
    /// </summary>
    protected static string StripMarkers(string name) => name.TrimEnd('!').Replace("::IO", "");

    /// <summary>A register: the name as stored, that name split into "Node.Pin", and the reflected property.</summary>
    protected readonly record struct Register(string RawName, string Name, FProperty? Property);

    /// <summary>
    /// Unit type names double as node-name stems, which is what lets a flattened "Node_Pin" property name be
    /// split back into its halves. A node keeps the full struct name unless it was renamed in the editor, so
    /// both spellings are candidates, longest first.
    /// </summary>
    protected static string[] GetUnitNames(URigVM vm) => (vm.FunctionNamesStorage ?? [])
        .Select(function => function.Text.SubstringBefore("::").TrimStart('F'))
        .SelectMany(name => new[]
        {
            name,
            name.SubstringAfter("RigUnit_"),
            name.SubstringAfter("RigVMFunction_"),
            // Dispatch factories are named "DISPATCH_RigVMDispatch_If" but their nodes are just "If".
            name.SubstringAfter("DISPATCH_RigVMDispatch_").SubstringAfter("RigVMDispatch_")
        })
        .Where(name => name.Length > 0)
        .Distinct()
        .OrderByDescending(name => name.Length)
        .ToArray();

    /// <summary>
    /// Turns a generated property name back into "Node.Pin". The generator flattens the pin path into the
    /// name ("GetInitialBoneTransform_0_Space__Const"), optionally behind the graph it belongs to
    /// ("RigVMModel___SphericalPoseReader_1_DriverItem__Const"), so the graph prefix is dropped and the node
    /// half is recovered by matching a known unit type name plus its instance suffix.
    /// </summary>
    protected static string NormalizeName(string propertyName, string[] unitNames)
    {
        // "__Const" marks a literal and "__IO" a pin that is both an input and an output; neither is part of
        // the pin name. Trailing '_' is how the generator escapes an otherwise empty tail.
        var name = propertyName;
        foreach (var marker in new[] { "__Const", "__IO" })
            if (name.EndsWith(marker, StringComparison.Ordinal)) name = name[..^marker.Length];
        name = name.TrimEnd('_');

        // "<Graph>___<Node>_<Pin>": the graph path separator survives as a triple underscore.
        const string graphSeparator = "___";
        var separatorIndex = name.LastIndexOf(graphSeparator, StringComparison.Ordinal);
        if (separatorIndex >= 0) name = name[(separatorIndex + graphSeparator.Length)..];

        foreach (var unitName in unitNames)
        {
            // A node is named after its unit type, but one authored inside a function carries that function's
            // scope in front of it ("Deform_UpperArm_FNC_ParentConstraint_3_Child"), so the unit name is looked
            // for at any underscore boundary rather than only at the start. Scanning from the end takes the
            // last match, since a function's own name can repeat the unit name earlier in the string.
            for (var start = name.Length - unitName.Length; start >= 0; start--)
            {
                if (string.CompareOrdinal(name, start, unitName, 0, unitName.Length) != 0) continue;
                if (start > 0 && name[start - 1] != '_') continue;

                // Consume the instance suffix the compiler appends to disambiguate nodes ("_0", "_1_2", ...).
                var index = start + unitName.Length;
                while (index < name.Length && name[index] == '_' && index + 1 < name.Length && char.IsDigit(name[index + 1]))
                {
                    index++;
                    while (index < name.Length && char.IsDigit(name[index])) index++;
                }

                if (index < name.Length && name[index] == '_')
                    return $"{name[..index]}.{name[(index + 1)..]}";
            }
        }

        return name;
    }

    /// <summary>Only the literal CDO matters: work memory holds runtime state, not authored values.</summary>
    protected static Dictionary<string, FPropertyTag> ReadLiteralCdo(UClass uClass) =>
        uClass.Owner?.GetExports()
            .FirstOrDefault(export => export.Name.StartsWith("Default__RigVMMemory_Literal", StringComparison.OrdinalIgnoreCase))
            ?.Properties
            .GroupBy(property => property.Name.Text)
            .ToDictionary(group => group.Key, group => group.First()) ?? [];

    /// <summary>
    /// UE 5 stores sub-pin paths as text, so no reconstruction is needed - only the array steps, which arrive
    /// as bare numbers ("BoneToModify/0/Transform"), need subscripting.
    /// </summary>
    protected static string? FormatSegmentPath(string? segmentPath)
    {
        if (string.IsNullOrEmpty(segmentPath)) return null;

        var suffix = new StringBuilder();
        foreach (var segment in segmentPath.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries))
            suffix.Append(segment.All(char.IsDigit) ? $"[{segment}]" : $".{segment}");
        return suffix.ToString();
    }

    protected static string FormatPropertyValue(FPropertyTag property, string? pinName)
    {
        var value = property.Tag?.GenericValue;
        switch (value)
        {
            // Byte-backed enums surface as a qualified FName ("EBoneGetterSetterMode::GlobalSpace"), which is
            // already valid C++ - only genuine name pins want the FName(...) wrapper.
            case FName enumValue when enumValue.Text.Contains("::"): return enumValue.Text;
            case FName name: return $"FName(\"{name.Text}\")";
            case string text: return $"\"{text}\"";
            case bool flag: return flag ? "true" : "false";
            case float or double: return Convert.ToString(value, CultureInfo.InvariantCulture) + "f";
            case byte or sbyte or short or ushort or int or uint or long or ulong: return value.ToString()!;
        }

        // Structs and arrays (a bone list, a transform, ...) go through the same renderer the class dump uses,
        // so they read as C++ initialisers rather than an internal type description. These carry real authored
        // data - the bone a node drives, its offset - so the budget is generous; only a genuinely unwieldy
        // list collapses, and then to a count rather than nothing.
        // A struct or an array keeps the layout it was rendered with - one field per line. These carry the
        // authored substance of a rig (the bone a node drives, the offset it applies), and collapsing them
        // onto one line to save space is what made them unreadable; the caller lifts anything multi-line out
        // of the argument list into a statement of its own.
        if (property.Tag is not null && BlueprintDecompilerUtils.GetPropertyTagVariable(property, out _, out var rendered) && rendered.Length > 0)
            return rendered.TrimEnd();

        return "default";
    }

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
    /// UE 5.1+: the register tables are property bags serialized on the VM itself. CUE4Parse reads the bags'
    /// descriptors but skips their value payload, so names and layout come from the bag while authored literal
    /// values are recovered by name from the legacy generated class's CDO where it still exists.
    /// </summary>
    private sealed class PropertyBagStorage : RigVMStorage
    {
        private readonly Dictionary<ERigVMMemoryType, Register[]> _registers;
        private readonly Dictionary<ERigVMMemoryType, FRigVMPropertyPathDescription[]> _propertyPaths;
        private readonly Dictionary<string, FPropertyTag> _literalValues;

        private PropertyBagStorage(Dictionary<ERigVMMemoryType, Register[]> registers,
            Dictionary<ERigVMMemoryType, FRigVMPropertyPathDescription[]> propertyPaths,
            Dictionary<string, FPropertyTag> literalValues)
        {
            _registers = registers;
            _propertyPaths = propertyPaths;
            _literalValues = literalValues;
        }

        public static PropertyBagStorage? TryCreate(UClass uClass, URigVM vm)
        {
            var bags = new (ERigVMMemoryType Type, FRigVMMemoryStorageStruct? Bag)[]
            {
                (ERigVMMemoryType.Literal, vm.LiteralMemoryStorage),
                (ERigVMMemoryType.Work, vm.DefaultWorkMemoryStorage),
                (ERigVMMemoryType.Debug, vm.DefaultDebugMemoryStorage)
            };
            if (bags.All(entry => entry.Bag is null or { PropertyDescs.Length: 0 })) return null;

            var unitNames = GetUnitNames(vm);
            var registers = new Dictionary<ERigVMMemoryType, Register[]>();
            var propertyPaths = new Dictionary<ERigVMMemoryType, FRigVMPropertyPathDescription[]>();
            foreach (var (memoryType, bag) in bags)
            {
                if (bag is null) continue;
                registers[memoryType] = bag.PropertyDescs
                    .Select(desc => new Register(desc.Name.Text, NormalizeName(desc.Name.Text, unitNames), null))
                    .ToArray();
                propertyPaths[memoryType] = bag.PropertyPathDescriptions ?? [];
            }

            // The bag carries its own values; the legacy generated class CDO only fills gaps for builds
            // where the bag payload could not be read.
            var literalValues = ReadLiteralCdo(uClass);
            foreach (var property in vm.LiteralMemoryStorage?.Properties ?? [])
                literalValues[property.Name.Text] = property;

            return new PropertyBagStorage(registers, propertyPaths, literalValues);
        }

        private Register? RegisterAt(FRigVMOperand operand) =>
            _registers.TryGetValue(operand.MemoryType, out var registers) && operand.RegisterIndex < registers.Length
                ? registers[operand.RegisterIndex]
                : null;

        public override string? GetRegisterName(FRigVMOperand operand) =>
            operand.MemoryType == ERigVMMemoryType.External ? GetExternalName(operand) : RegisterAt(operand)?.Name;

        public override string FormatLiteralValue(FRigVMOperand operand, string? pinName)
        {
            if (RegisterAt(operand) is not { } register) return "<unresolved>";
            return _literalValues.TryGetValue(register.RawName, out var property)
                ? FormatPropertyValue(ResolveSegment(property, operand), pinName)
                : "default";
        }

        protected override string? GetSegmentPath(FRigVMOperand operand) =>
            _propertyPaths.TryGetValue(operand.MemoryType, out var paths) && operand.RegisterOffset < paths.Length
                ? paths[operand.RegisterOffset].SegmentPath
                : null;

        protected override string? GetOffsetSuffix(FRigVMOperand operand) => FormatSegmentPath(GetSegmentPath(operand));
    }

    /// <summary>
    /// UE 5.0: registers are reflected properties on a generated class per memory type, and the authored
    /// literal values live on that class's CDO.
    /// </summary>
    private sealed class GeneratedClassStorage : RigVMStorage
    {
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

            var unitNames = GetUnitNames(vm);

            var registers = new Dictionary<ERigVMMemoryType, Register[]>();
            var workPropertyPaths = Array.Empty<FRigVMPropertyPathDescription>();
            foreach (var generatorClass in generatorClasses)
            {
                registers[generatorClass.MemoryType] = (generatorClass.ChildProperties ?? [])
                    .Select(property => new Register(property.Name.Text, NormalizeName(property.Name.Text, unitNames), property as FProperty))
                    .ToArray();

                if (generatorClass.MemoryType == ERigVMMemoryType.Work)
                    workPropertyPaths = generatorClass.PropertyPathDescriptions ?? [];
            }
            if (registers.Count == 0) return null;

            var literalValues = ReadLiteralCdo(uClass);

            return new GeneratedClassStorage(registers, literalValues, workPropertyPaths);
        }

        public override string? GetRegisterName(FRigVMOperand operand) =>
            operand.MemoryType == ERigVMMemoryType.External ? GetExternalName(operand) : RegisterAt(operand)?.Name;

        public override string FormatLiteralValue(FRigVMOperand operand, string? pinName)
        {
            if (RegisterAt(operand) is not { } register) return "<unresolved>";

            // The CDO is keyed by the raw generated property names.
            if (_literalValues.TryGetValue(register.RawName, out var property))
                return FormatPropertyValue(ResolveSegment(property, operand), pinName);

            // Unversioned property serialization omits anything still equal to its type default, so a literal
            // that is missing from the CDO is not unknown - it is the default, which is worth naming outright.
            return FormatTypeDefault(register.Property);
        }

        /// <summary>
        /// Names the zero value a property falls back to when the package stores nothing for it. Enums are the
        /// case that actually matters (a bare "default" hides whether a pin means LocalSpace or GlobalSpace),
        /// and their entry names come from the mappings since the enum itself lives in a script package.
        /// </summary>
        private static string FormatTypeDefault(FProperty? property)
        {
            var enumName = property switch
            {
                FEnumProperty enumProperty => enumProperty.Enum.Name,
                FByteProperty byteProperty => byteProperty.Enum.Name,
                _ => null
            };

            if (!string.IsNullOrEmpty(enumName) &&
                BlueprintDecompilerUtils.Mappings?.Enums.TryGetValue(enumName, out var entries) == true &&
                entries.TryGetValue(0, out var zeroName))
            {
                return zeroName.Contains("::") ? zeroName : $"{enumName}::{zeroName}";
            }

            return property switch
            {
                FBoolProperty => "false",
                FNameProperty => "FName(\"None\")",
                FStrProperty => "\"\"",
                FNumericProperty => "0",
                _ => "default"
            };
        }

        protected override string? GetSegmentPath(FRigVMOperand operand) =>
            operand.MemoryType == ERigVMMemoryType.Work && operand.RegisterOffset < _workPropertyPaths.Length
                ? _workPropertyPaths[operand.RegisterOffset].SegmentPath
                : null;

        protected override string? GetOffsetSuffix(FRigVMOperand operand)
        {
            // UE 5.0 stores the segment path as text, so the sub-pin name needs no reconstruction - only
            // the array steps, which arrive as bare numbers ("BoneToModify/0/Transform"), need subscripting.
            var segmentPath = GetSegmentPath(operand);
            if (string.IsNullOrEmpty(segmentPath)) return null;

            var suffix = new StringBuilder();
            foreach (var segment in segmentPath.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries))
                suffix.Append(segment.All(char.IsDigit) ? $"[{segment}]" : $".{segment}");
            return suffix.ToString();
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
