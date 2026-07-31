using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.Utils;

namespace CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;

/// <summary>
/// Recovers the inputs of an anim graph node in a cooked UE 5 AnimBlueprintGeneratedClass.
///
/// Up to UE 4.27 every pin value was simply a property on the node struct in the CDO, and anything driven at
/// runtime went through an <c>EvaluateGraphExposedInputs</c> handler naming a bytecode function - both readable
/// straight off the class dump. UE 5.0 moved both halves out of the node:
/// <list type="bullet">
/// <item>Pin values were "folded" into two flat tables on the class - constants into the sparse class data
/// (<c>AnimBlueprintGeneratedConstantData</c>) and per-instance values into <c>__AnimBlueprintMutables</c> -
/// leaving the node struct with little more than its pose links. The folded properties are
/// <c>WITH_EDITORONLY_DATA</c>, so they aren't in the mappings at all; the compiler bakes the layout it used
/// into <c>NodeTypeMap</c> instead, and <c>AnimNodeData[node].Entries[propertyIndex]</c> says which table slot
/// each property landed in.</item>
/// <item>Dynamic pins became property access copies: a <c>FPropertyAccessLibrary</c> of src/dest paths plus a
/// per-node <c>FAnimNodeExposedValueHandler_PropertyAccess</c> holding the indices of the copies that feed it.</item>
/// </list>
/// The net effect on a cooked package is that a node like AnimGraphNode_BlendListByBool serialises as nothing but
/// its BlendPose links - no bActiveValue, no BlendTime, no transition type - which is what this reassembles.
///
/// See Engine/Source/Runtime/Engine/Public/Animation/AnimNodeData.h and .../PropertyAccess.h.
/// </summary>
public sealed class AnimGraphStorage
{
    // FAnimNodeData entry encoding (AnimNodeData.h).
    private const uint InvalidEntry = 0xffffffff;
    private const uint InstanceDataFlag = 0x80000000;
    private const uint InstanceDataMask = ~InstanceDataFlag;

    // EAnimNodeDataFlags: which of FAnimNode_Base's three function refs the node actually binds. Every node
    // carries all three in its entry table, all pointing at the same "None" constant, so without this check
    // they would add three lines of noise to every node in the graph.
    private static readonly (uint Flag, string Property)[] _nodeFunctionFlags =
    [
        (0x1, "InitialUpdateFunction"),
        (0x2, "BecomeRelevantFunction"),
        (0x4, "UpdateFunction")
    ];

    // EPropertyAccessSegmentFlags::Function - the segment is a call rather than a property step.
    private const int SegmentFunctionFlag = 1 << 15;

    /// <summary>A single copy in the library, with both ends already rendered as paths.</summary>
    private readonly record struct Copy(string Source, string[] Destinations, string CopyType);

    /// <summary>What drives one node property, once the folding has been undone.</summary>
    private readonly record struct Binding(string Source, string CopyType, bool Negated, bool OnlyWhenActive, int CopyIndex, FName PostCopyOperation);

    private readonly record struct Node(string Name, string StructName);

    private readonly List<Node> _nodes = [];
    private readonly Dictionary<string, string[]> _propertyNamesByNodeStruct = [];
    private FStructFallback[] _animNodeData = [];

    private Assets.Exports.UObject? _classDefaultObject;
    private FStructFallback? _constantData;
    private FStructFallback? _mutableData;
    private string[] _constantNames = [];
    private string[] _mutableNames = [];
    private FProperty?[] _constantProperties = [];
    private FProperty?[] _mutableProperties = [];
    private string _mutablePropertyName = "__AnimBlueprintMutables";

    private Copy[] _copies = [];

    /// <summary>Destination path ("Node.Pin", or "__AnimBlueprintMutables.__BoolProperty_1") to what feeds it.</summary>
    private readonly Dictionary<string, Binding> _bindingsByDestination = [];
    private readonly HashSet<string> _emittedDestinations = [];

    /// <summary>Node property name to the bytecode handler it binds, for the nodes that still use one.</summary>
    private readonly Dictionary<string, (FName BoundFunction, FPackageIndex? Function)> _handlerFunctions = [];

    /// <summary>Node property name to the library copies its handler runs, in the order the handler lists them.</summary>
    private readonly Dictionary<string, List<int>> _copyIndicesByNode = [];

    /// <summary>
    /// One node's dynamically driven pins, shaped after the UE 4 <c>FExposedValueHandler</c> that used to sit on
    /// the class. <see cref="NodeIndex"/> is the AnimNodeProperties index that pose link IDs refer to.
    /// </summary>
    public sealed record ExposedValueHandler(int NodeIndex, string NodeName, string NodeStructName, FName BoundFunction, FPackageIndex? Function, IReadOnlyList<ExposedValueCopyRecord> CopyRecords);

    /// <summary>
    /// One driven pin. <see cref="SourcePropertyName"/>/<see cref="SourceSubPropertyName"/> split the library's
    /// source path the way UE 4 did, and <see cref="ViaMutable"/> names the mutable slot the copy physically
    /// writes when the pin was folded - the copy never mentions the pin itself in that case.
    /// </summary>
    public sealed record ExposedValueCopyRecord(string SourcePropertyName, string SourceSubPropertyName, int SourceArrayIndex, string DestPropertyName, string DestStructName, string DestPropertyType, FName PostCopyOperation, bool OnlyUpdateWhenActive, string CopyType, int CopyIndex, string? ViaMutable);

    /// <summary>Builds the resolver, or null when this isn't a UE 5 folded anim class.</summary>
    public static AnimGraphStorage? Create(UClass uClass, Assets.Exports.UObject? classDefaultObject)
    {
        var storage = new AnimGraphStorage();
        return storage.TryInitialize(uClass, classDefaultObject) ? storage : null;
    }

    /// <summary>
    /// Rebuilds the anim graph as pseudo-code, or null when this isn't a UE 5 folded anim class. Properties
    /// whose contents the graph reproduces in full are reported through <paramref name="suppressedProperties"/>
    /// so the class dump can collapse them to bare declarations instead of repeating the raw folded tables.
    /// </summary>
    public static string? Decompile(UClass uClass, Assets.Exports.UObject? classDefaultObject, out HashSet<string> suppressedProperties, out List<string> declarationOrder)
    {
        suppressedProperties = [];
        declarationOrder = [];

        if (Create(uClass, classDefaultObject) is not { } storage) return null;

        var text = storage.Emit();
        if (text is null) return null;

        // The folded tables and the layout map are pure compiler bookkeeping, and every value they hold is
        // reproduced below against the node property it actually belongs to.
        suppressedProperties.Add("AnimNodeData");
        suppressedProperties.Add("NodeTypeMap");
        suppressedProperties.Add(storage._mutablePropertyName);
        foreach (var node in storage._nodes) suppressedProperties.Add(node.Name);

        declarationOrder.AddRange(storage._nodes.Select(node => node.Name));
        return text;
    }

    private bool TryInitialize(UClass uClass, Assets.Exports.UObject? classDefaultObject)
    {
        if (classDefaultObject is null) return false;
        _classDefaultObject = classDefaultObject;

        // NodeTypeMap is the compiler's record of the editor-time property layout of every node struct it baked,
        // and cooking it is the only reason that layout survives at all. Nothing can be read out of the entry
        // tables without it, which also makes it the cleanest test for "is this a UE 5 folded anim class".
        if (!TryReadNodeTypeMap(uClass)) return false;

        _animNodeData = uClass.GetOrDefault<FStructFallback[]>("AnimNodeData", []);
        if (_animNodeData.Length == 0) return false;

        // AnimNodeProperties - and with it every node index in the graph, pose link IDs included - is the
        // class's anim node struct properties in declaration order. See UAnimBlueprintGeneratedClass::Link.
        foreach (var child in uClass.ChildProperties ?? [])
        {
            if (child is not FStructProperty structProperty) continue;

            if (IsMutableDataStruct(structProperty))
            {
                _mutablePropertyName = structProperty.Name.Text;
                continue;
            }

            var structName = structProperty.Struct.ResolvedObject?.Name.Text;
            if (structName is null || !_propertyNamesByNodeStruct.ContainsKey(structName)) continue;

            _nodes.Add(new Node(structProperty.Name.Text, structName));
        }
        if (_nodes.Count == 0) return false;

        _constantData = classDefaultObject.SerializedSparseClassData;
        (_constantNames, _constantProperties) = FlattenProperties(classDefaultObject.SerializedSparseClassDataStruct);

        _mutableData = classDefaultObject.GetOrDefault<FStructFallback?>(_mutablePropertyName, null);
        (_mutableNames, _mutableProperties) = FlattenProperties(FindMutableStruct(uClass));

        ReadPropertyAccessLibrary();
        ReadExposedValueHandlers();
        return true;
    }

    /// <summary>
    /// NodeTypeMap maps each anim node struct to the property name/index pairs the compiler used when it built
    /// that node's entry table. Inverted here, index to name, which is the direction the entry table is read in.
    /// </summary>
    private bool TryReadNodeTypeMap(UClass uClass)
    {
        var nodeTypeMap = uClass.GetOrDefault<UScriptMap?>("NodeTypeMap", null);
        if (nodeTypeMap is null) return false;

        foreach (var (key, value) in nodeTypeMap.Properties)
        {
            if (key.GenericValue is not FPackageIndex structIndex) continue;
            if (structIndex.ResolvedObject?.Name.Text is not { } structName) continue;
            if (value?.GetValue(typeof(FStructFallback)) is not FStructFallback structData) continue;

            var nameToIndex = structData.GetOrDefault<UScriptMap?>("NameToIndexMap", null);
            if (nameToIndex is null) continue;

            // NumProperties is what the entry table was sized against, so trust it over the map's own count:
            // an index we can't put a name to still has to keep the rest of the table aligned.
            var count = structData.GetOrDefault("NumProperties", nameToIndex.Properties.Count);
            if (count <= 0) continue;

            var names = new string[count];
            foreach (var (propertyName, propertyIndex) in nameToIndex.Properties)
            {
                if (propertyName.GenericValue is not FName name) continue;
                if (propertyIndex?.GenericValue is not int index || index < 0 || index >= count) continue;
                names[index] = name.Text;
            }

            _propertyNamesByNodeStruct[structName] = names;
        }

        return _propertyNamesByNodeStruct.Count > 0;
    }

    private void ReadPropertyAccessLibrary()
    {
        var library = _constantData?.GetOrDefault<FStructFallback?>("AnimBlueprintExtension_PropertyAccess", null)
            ?.GetOrDefault<FStructFallback?>("Library", null);
        if (library is null) return;

        var segments = library.GetOrDefault<FStructFallback[]>("PathSegments", []);
        var sourcePaths = library.GetOrDefault<FStructFallback[]>("SrcPaths", []);
        var destinationPaths = library.GetOrDefault<FStructFallback[]>("DestPaths", []);

        // Copies are grouped by the call site that runs them (EAnimPropertyAccessCallSite). Node handlers only
        // ever index the first batch, WorkerThread_Unbatched; the later batches are driven by the subsystem for
        // the class as a whole and belong to no single node.
        var batches = library.GetOrDefault<FStructFallback[]>("CopyBatchArray", []);
        if (batches.Length == 0) return;

        _copies = batches[0].GetOrDefault<FStructFallback[]>("Copies", []).Select(copy =>
        {
            var source = FormatPath(sourcePaths, copy.GetOrDefault("AccessIndex", -1), segments);

            var start = copy.GetOrDefault("DestAccessStartIndex", -1);
            var end = copy.GetOrDefault("DestAccessEndIndex", -1);
            var destinations = new List<string>();
            for (var i = start; i >= 0 && i < end; i++)
            {
                if (FormatPath(destinationPaths, i, segments) is { Length: > 0 } destination)
                    destinations.Add(destination);
            }

            return new Copy(source, destinations.ToArray(), copy.GetOrDefault("Type", new FName()).Text.SubstringAfter("::"));
        }).ToArray();

        // Index every copy by what it writes. The library alone is enough to know a pin is driven; the node
        // handlers below only add the per-record modifiers, so a copy whose handler can't be found still shows
        // up against its destination rather than being silently replaced by the stale CDO default.
        for (var copyIndex = 0; copyIndex < _copies.Length; copyIndex++)
            foreach (var destination in _copies[copyIndex].Destinations)
                _bindingsByDestination.TryAdd(destination, new Binding(_copies[copyIndex].Source, _copies[copyIndex].CopyType, false, false, copyIndex, new FName("EPostCopyOperation::None")));
    }

    /// <summary>
    /// Each node with dynamic pins gets a handler in the sparse class data, named after the node property and
    /// listing the copies that write those pins, along with the modifiers applied as they land - a logical
    /// negate (how an inverted bool pin compiles) and whether the copy is skipped while the node is blending out.
    /// </summary>
    private void ReadExposedValueHandlers()
    {
        if (_constantData is null) return;

        foreach (var node in _nodes)
        {
            var handler = _constantData.GetOrDefault<FStructFallback?>(node.Name, null);
            if (handler is null) continue;

            // The bytecode path from UE 4 never went away - a pin the compiler couldn't turn into a copy still
            // binds an EvaluateGraphExposedInputs_* function - so it has to be carried alongside the copies.
            var boundFunction = handler.GetOrDefault("BoundFunction", new FName("None"));
            var function = handler.GetOrDefault<FPackageIndex?>("Function", null);
            if (!boundFunction.IsNone || function is { IsNull: false })
                _handlerFunctions[node.Name] = (boundFunction, function);

            foreach (var record in handler.GetOrDefault<FStructFallback[]>("CopyRecords", []))
            {
                var copyIndex = record.GetOrDefault("CopyIndex", -1);
                if (copyIndex < 0 || copyIndex >= _copies.Length) continue;

                if (!_copyIndicesByNode.TryGetValue(node.Name, out var owned))
                    _copyIndicesByNode[node.Name] = owned = [];
                owned.Add(copyIndex);

                var copy = _copies[copyIndex];
                var postCopy = record.GetOrDefault("PostCopyOperation", new FName("EPostCopyOperation::None"));
                var binding = new Binding(copy.Source, copy.CopyType, postCopy.Text.EndsWith("LogicalNegateBool", StringComparison.Ordinal),
                    record.GetOrDefault("bOnlyUpdateWhenActive", false), copyIndex, postCopy);

                foreach (var destination in copy.Destinations)
                    _bindingsByDestination[destination] = binding;
            }
        }
    }

    /// <summary>
    /// The reflected type of a driven pin, for spelling its destination the way the engine would
    /// ("BoolProperty'AnimNode_BlendListByBool:bActiveValue'").
    ///
    /// A pin still on the node struct is in the mappings, so its own serialised tag names the type outright. A
    /// folded pin is editor-only and in no mappings at all, but the mutable slot it was folded into is a real
    /// property of the same type. Only when neither exists does the copy type have to stand in for it.
    /// </summary>
    private string DestPropertyType(Node node, string propertyName, string? viaMutable, string copyType)
    {
        if (viaMutable is null)
        {
            var tag = _classDefaultObject?.GetOrDefault<FStructFallback?>(node.Name, null)
                ?.Properties.FirstOrDefault(property => property.Name.Text == propertyName);
            if (tag is not null && tag.PropertyType.Text is { Length: > 0 } tagType) return tagType;
        }
        else
        {
            var index = Array.IndexOf(_mutableNames, viaMutable);
            if (index >= 0 && index < _mutableProperties.Length && _mutableProperties[index] is { } property)
                return property.GetType().Name[1..];
        }

        return PropertyTypeForCopy(copyType);
    }

    /// <summary>Last resort: what EPropertyAccessCopyType implies about the destination's type.</summary>
    private static string PropertyTypeForCopy(string copyType) => copyType switch
    {
        "Bool" or "PromoteBoolToByte" or "PromoteBoolToInt32" or "PromoteBoolToInt64" or "PromoteBoolToFloat" or "PromoteBoolToDouble" => "BoolProperty",
        "Object" => "ObjectProperty",
        "Struct" => "StructProperty",
        "Name" => "NameProperty",
        "Array" or "PromoteArrayFloatToDouble" or "DemoteArrayDoubleToFloat" => "ArrayProperty",
        "DemoteDoubleToFloat" or "PromoteInt32ToFloat" or "PromoteByteToFloat" => "FloatProperty",
        "PromoteFloatToDouble" or "PromoteInt32ToDouble" or "PromoteByteToDouble" => "DoubleProperty",
        "PromoteByteToInt32" or "PromoteInt32ToInt64" or "PromoteByteToInt64" => "IntProperty",
        _ => "Property"
    };

    /// <summary>
    /// Where in the property access library a pin's value would be written, or null when nothing can write it.
    /// A pin left on the node struct is targeted directly; a folded pin is targeted through its mutable slot,
    /// since that is the only name the copy knows. Constants have no runtime writer at all.
    /// </summary>
    private (string Destination, string? ViaMutable)? DestinationFor(Node node, string propertyName, uint entry)
    {
        if (entry == InvalidEntry) return ($"{node.Name}.{propertyName}", null);
        if ((entry & InstanceDataFlag) == 0) return null;

        var index = (int) (entry & InstanceDataMask);
        var mutableName = index < _mutableNames.Length ? _mutableNames[index] : null;
        return mutableName is null ? null : ($"{_mutablePropertyName}.{mutableName}", mutableName);
    }

    /// <summary>
    /// The graph's dynamic inputs in the shape UE 4 published them in, one entry per node that has any, with
    /// every copy resolved back to the pin it ends up in rather than the mutable slot it physically writes.
    /// </summary>
    public List<ExposedValueHandler> BuildExposedValueHandlers()
    {
        var handlers = new List<ExposedValueHandler>();

        for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
        {
            var node = _nodes[nodeIndex];
            var entries = nodeIndex < _animNodeData.Length ? _animNodeData[nodeIndex].GetOrDefault<uint[]>("Entries", []) : [];
            var propertyNames = _propertyNamesByNodeStruct.GetValueOrDefault(node.StructName, []);

            var records = new List<ExposedValueCopyRecord>();
            for (var propertyIndex = 0; propertyIndex < entries.Length && propertyIndex < propertyNames.Length; propertyIndex++)
            {
                var propertyName = propertyNames[propertyIndex];
                if (string.IsNullOrEmpty(propertyName)) continue;

                if (DestinationFor(node, propertyName, entries[propertyIndex]) is not { } target) continue;
                if (!_bindingsByDestination.TryGetValue(target.Destination, out var binding)) continue;

                // UE 4 split a source path into exactly two halves ("SkydivingState" + "SkydiveAimYaw"); the
                // library allows deeper paths, so anything past the first segment stays joined in the tail.
                var separator = binding.Source.IndexOf('.');
                var head = separator < 0 ? binding.Source : binding.Source[..separator];
                var tail = separator < 0 ? "None" : binding.Source[(separator + 1)..];

                records.Add(new ExposedValueCopyRecord(head, tail, 0, propertyName, node.StructName,
                    DestPropertyType(node, propertyName, target.ViaMutable, binding.CopyType),
                    binding.PostCopyOperation, binding.OnlyWhenActive, binding.CopyType, binding.CopyIndex, target.ViaMutable));
            }

            // Copies the node's handler runs that don't land on one of its own pins. A Control Rig or linked
            // layer node feeds class-level "__CustomProperty_..." slots that the sub-graph reads, so the pin
            // walk above never sees them, but they are still that node's inputs and drop out entirely otherwise.
            var resolved = records.Select(record => record.CopyIndex).ToHashSet();
            foreach (var copyIndex in _copyIndicesByNode.GetValueOrDefault(node.Name, []))
            {
                if (!resolved.Add(copyIndex)) continue;

                var copy = _copies[copyIndex];
                var binding = copy.Destinations.Length > 0 && _bindingsByDestination.TryGetValue(copy.Destinations[0], out var found)
                    ? found
                    : new Binding(copy.Source, copy.CopyType, false, false, copyIndex, new FName("EPostCopyOperation::None"));

                var separator = copy.Source.IndexOf('.');
                foreach (var destination in copy.Destinations)
                {
                    records.Add(new ExposedValueCopyRecord(
                        separator < 0 ? copy.Source : copy.Source[..separator],
                        separator < 0 ? "None" : copy.Source[(separator + 1)..],
                        0, destination, string.Empty, PropertyTypeForCopy(copy.CopyType),
                        binding.PostCopyOperation, binding.OnlyWhenActive, copy.CopyType, copyIndex, null));
                }
            }

            var (boundFunction, function) = _handlerFunctions.GetValueOrDefault(node.Name, (new FName("None"), null));
            if (records.Count == 0 && boundFunction.IsNone && function is null or { IsNull: true }) continue;

            // A handler is only useful attached to the node it names, and readers look that node up in the CDO.
            // Unversioned serialisation drops a node whose struct is entirely default, so a handler for one of
            // those would point at nothing - rare, but it makes the entry worse than useless.
            if (_classDefaultObject?.Properties.Any(property => property.Name.Text == node.Name) != true) continue;

            handlers.Add(new ExposedValueHandler(nodeIndex, node.Name, node.StructName, boundFunction, function, records));
        }

        return handlers;
    }

    private string? Emit()
    {
        var builder = new CustomStringBuilder();
        builder.AppendLine("// Decompiled AnimGraph. Pin values the compiler folded into the class are marked [const]");
        builder.AppendLine("// or [mutable]; pins driven at runtime by the property access library use '<-'.");
        builder.AppendLine("AnimGraph");
        builder.OpenBlock();

        for (var nodeIndex = 0; nodeIndex < _nodes.Count; nodeIndex++)
        {
            if (nodeIndex > 0) builder.AppendLine();
            EmitNode(builder, nodeIndex);
        }

        EmitUnattributedCopies(builder);

        builder.CloseBlock();
        return builder.ToString();
    }

    /// <summary>
    /// Copies whose destination isn't a pin on any node in this graph - class properties fed for a linked anim
    /// layer, or the "__CustomProperty_..." slots a sub-graph reads. They're still graph inputs, so they're
    /// listed rather than dropped.
    /// </summary>
    private void EmitUnattributedCopies(CustomStringBuilder builder)
    {
        var remaining = _bindingsByDestination
            .Where(pair => !_emittedDestinations.Contains(pair.Key))
            .OrderBy(pair => pair.Value.CopyIndex)
            .ToArray();
        if (remaining.Length == 0) return;

        builder.AppendLine();
        builder.AppendLine("// Property access copies that don't target a node in this graph:");
        foreach (var (destination, binding) in remaining)
            builder.AppendLine($"{destination} <- {FormatBinding(binding)}");
    }

    private void EmitNode(CustomStringBuilder builder, int nodeIndex)
    {
        var node = _nodes[nodeIndex];
        builder.AppendLine($"F{node.StructName} {FormatNodeReference(nodeIndex)}");
        builder.OpenBlock();

        var nodeData = nodeIndex < _animNodeData.Length ? _animNodeData[nodeIndex] : null;
        var entries = nodeData?.GetOrDefault<uint[]>("Entries", []) ?? [];
        var flags = nodeData?.GetOrDefault<uint>("Flags", 0) ?? 0;
        var propertyNames = _propertyNamesByNodeStruct.GetValueOrDefault(node.StructName, []);
        var serialized = _classDefaultObject?.GetOrDefault<FStructFallback?>(node.Name, null)?.Properties ?? [];

        var lines = 0;
        for (var propertyIndex = 0; propertyIndex < entries.Length; propertyIndex++)
        {
            var propertyName = propertyIndex < propertyNames.Length ? propertyNames[propertyIndex] : null;
            if (string.IsNullOrEmpty(propertyName)) continue;

            // Unbound anim node functions sit on every node and say nothing; the flags exist to tell them apart.
            var function = _nodeFunctionFlags.FirstOrDefault(f => f.Property == propertyName);
            if (function.Property is not null && (flags & function.Flag) == 0) continue;

            if (EmitProperty(builder, node, propertyName, entries[propertyIndex], serialized)) lines++;
        }

        // Anything the node serialised that the entry table didn't account for. The table is built from the
        // editor-time struct, so a property the two views disagree on would otherwise vanish without trace.
        var covered = new HashSet<string>(propertyNames.Where(name => !string.IsNullOrEmpty(name)));
        foreach (var property in serialized)
        {
            if (covered.Contains(property.Name.Text)) continue;
            EmitValueLine(builder, property.Name.Text, property, null);
            lines++;
        }

        if (lines == 0) builder.AppendLine("// no inputs");
        builder.CloseBlock();
        builder.AppendLine();
    }

    /// <summary>
    /// Emits one property, resolving it through whichever of the three places its value ended up in: a class
    /// constant, a per-instance mutable, or still on the node struct itself.
    /// </summary>
    private bool EmitProperty(CustomStringBuilder builder, Node node, string propertyName, uint entry, IReadOnlyList<FPropertyTag> serialized)
    {
        if (entry == InvalidEntry)
        {
            // Not folded: a normal property on the node struct. Only pin values with nowhere else to go stay
            // here, which is most of what a skeletal control node exposes - Alpha, the bone to modify, the
            // spaces - along with the pose links that make the graph traversable.
            //
            // A connected pin still leaves its last authored literal behind in the CDO, so the copy has to win:
            // showing the literal would claim ModifyBone.Alpha is a hardcoded 1 when it is wired to a variable.
            if (TakeBinding(DestinationFor(node, propertyName, entry)?.Destination) is { } direct)
            {
                builder.AppendLine($"{propertyName} <- {FormatBinding(direct)}");
                return true;
            }

            var tag = serialized.FirstOrDefault(property => property.Name.Text == propertyName);
            if (tag is null) return false;

            EmitValueLine(builder, propertyName, tag, null);
            return true;
        }

        var index = (int) (entry & InstanceDataMask);
        if ((entry & InstanceDataFlag) != 0)
        {
            var mutableName = index < _mutableNames.Length ? _mutableNames[index] : null;
            if (mutableName is null)
            {
                builder.AppendLine($"{propertyName} = <mutable slot {index}>;");
                return true;
            }

            // By far the common shape: a graph pin writes the mutable through the property access library and
            // the node reads it back, so the mutable slot is a rename of the real source rather than a value.
            if (TakeBinding(DestinationFor(node, propertyName, entry)?.Destination) is { } binding)
            {
                builder.AppendLine($"{propertyName} <- {FormatBinding(binding)} (via {mutableName})");
                return true;
            }

            EmitValueLine(builder, propertyName, Find(_mutableData, mutableName), $"[mutable] {mutableName}", index, _mutableProperties);
            return true;
        }

        var constantName = index < _constantNames.Length ? _constantNames[index] : null;
        if (constantName is null)
        {
            builder.AppendLine($"{propertyName} = <constant slot {index}>;");
            return true;
        }

        EmitValueLine(builder, propertyName, Find(_constantData, constantName), "[const]", index, _constantProperties);
        return true;
    }

    private void EmitValueLine(CustomStringBuilder builder, string propertyName, FPropertyTag? tag, string? note, int index = -1, FProperty?[]? properties = null)
    {
        // Pose links are the graph's edges, so they get spelled out one per entry with the node they point at
        // rather than collapsed into a struct literal full of raw LinkIDs.
        if (tag is not null && TryFormatPoseLinks(tag, out var links))
        {
            var suffix = note is null ? ";" : $"; // {note}";
            if (links.Count == 1 && !links[0].Indexed)
            {
                builder.AppendLine($"{propertyName} = {links[0].Text}{suffix}");
                return;
            }

            for (var i = 0; i < links.Count; i++)
                builder.AppendLine($"{propertyName}[{i}] = {links[i].Text}{suffix}");
            if (links.Count == 0) builder.AppendLine($"{propertyName} = {{}}{suffix}");
            return;
        }

        var value = tag is not null
            ? FormatTag(tag)
            : FormatTypeDefault(properties is not null && index >= 0 && index < properties.Length ? properties[index] : null);

        builder.AppendLine(note is null ? $"{propertyName} = {value};" : $"{propertyName} = {value}; // {note}");
    }

    /// <summary>
    /// FPoseLink/FComponentSpacePoseLink hold a LinkID indexing AnimNodeProperties, and pose arrays (a blend
    /// list's BlendPose, a layered blend's BlendPoses) hold one per entry.
    /// </summary>
    private bool TryFormatPoseLinks(FPropertyTag tag, out List<(string Text, bool Indexed)> links)
    {
        links = [];

        switch (tag.Tag?.GenericValue)
        {
            case FScriptStruct { StructType: FStructFallback single } when TryGetLinkId(single, out var linkId):
                links.Add((FormatNodeReference(linkId), false));
                return true;

            case UScriptArray array:
            {
                foreach (var element in array.Properties)
                {
                    if (element?.GenericValue is not FScriptStruct { StructType: FStructFallback entry } || !TryGetLinkId(entry, out var linkId))
                        return false;
                    links.Add((FormatNodeReference(linkId), true));
                }

                return links.Count > 0;
            }

            default:
                return false;
        }
    }

    private static bool TryGetLinkId(FStructFallback fallback, out int linkId)
    {
        linkId = fallback.GetOrDefault("LinkID", int.MinValue);
        return linkId != int.MinValue;
    }

    /// <summary>
    /// Looks a destination up and marks it accounted for, so whatever is left at the end can be reported rather
    /// than quietly dropped - copies also target class properties (linked layer inputs, custom node properties)
    /// that no node in this graph declares.
    /// </summary>
    private Binding? TakeBinding(string? destination)
    {
        if (destination is null || !_bindingsByDestination.TryGetValue(destination, out var binding)) return null;
        _emittedDestinations.Add(destination);
        return binding;
    }

    private string FormatBinding(Binding binding)
    {
        var source = binding.Negated ? $"!{binding.Source}" : binding.Source;
        var notes = new List<string> { $"copy #{binding.CopyIndex}" };
        if (binding.CopyType.Length > 0) notes.Add(binding.CopyType);
        if (binding.OnlyWhenActive) notes.Add("only when active");
        return $"{source}; // {string.Join(", ", notes)}";
    }

    private static FPropertyTag? Find(IPropertyHolder? holder, string name) =>
        holder?.Properties.FirstOrDefault(property => property.Name.Text == name);

    private static string FormatTag(FPropertyTag tag)
    {
        if (BlueprintDecompilerUtils.GetPropertyTagVariable(tag, out _, out var value) && value.Length > 0)
            return Collapse(value);

        // A few property kinds - weak and lazy object refs, interfaces - have no renderer in the shared
        // formatter and come back blank. Anim nodes hold plenty of those (SourceMeshComponent on every
        // CopyPoseFromMesh), and the bare reference still says more than nothing.
        return tag.Tag?.GenericValue switch
        {
            null => "nullptr",
            FPackageIndex { IsNull: true } => "nullptr",
            FPackageIndex index => $"\"{index}\"",
            var generic => Collapse(generic.ToString() ?? string.Empty)
        };
    }

    private static string Collapse(string value)
    {
        var singleLine = string.Join(' ', value.Split('\n').Select(line => line.Trim())).Trim();
        if (singleLine.Length == 0) return "default";
        return singleLine.Length <= 220 ? singleLine : singleLine[..217] + "...";
    }

    /// <summary>
    /// Names the value a slot falls back to when the package stores nothing for it. Unversioned property
    /// serialisation omits anything still at its default, so a missing slot isn't unknown - it is the default,
    /// and for enums that distinction carries the whole meaning of the pin.
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
            FObjectProperty or FClassProperty or FSoftObjectProperty => "nullptr",
            FArrayProperty => "{}",
            FNumericProperty => "0",
            _ => "default"
        };
    }

    private string FormatNodeReference(int nodeIndex) =>
        nodeIndex >= 0 && nodeIndex < _nodes.Count ? $"[{nodeIndex}] {_nodes[nodeIndex].Name}" : $"[{nodeIndex}] <no node>";

    /// <summary>Turns a library path index into "Segment.Segment", with calls and array steps spelled out.</summary>
    private static string FormatPath(FStructFallback[] paths, int pathIndex, FStructFallback[] segments)
    {
        if (pathIndex < 0 || pathIndex >= paths.Length) return string.Empty;

        var start = paths[pathIndex].GetOrDefault("PathSegmentStartIndex", -1);
        var count = paths[pathIndex].GetOrDefault("PathSegmentCount", 0);
        if (start < 0 || count <= 0) return string.Empty;

        var parts = new List<string>(count);
        for (var i = start; i < start + count && i < segments.Length; i++)
        {
            var segment = segments[i];
            var name = segment.GetOrDefault("Name", new FName()).Text;
            if (name.Length == 0) continue;

            if ((segment.GetOrDefault("Flags", 0) & SegmentFunctionFlag) != 0) name += "()";

            var arrayIndex = segment.GetOrDefault("ArrayIndex", -1);
            if (arrayIndex >= 0) name += $"[{arrayIndex}]";

            parts.Add(name);
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// Property names in declaration order, which is the order the constant and mutable tables are indexed in,
    /// alongside the reflected property itself - that is what names a type default when the CDO stored nothing.
    /// </summary>
    private static (string[], FProperty?[]) FlattenProperties(UStruct? struc)
    {
        if (struc is null) return ([], []);

        var names = new List<string>();
        var properties = new List<FProperty?>();
        for (var current = struc; current is not null; current = current.SuperStruct?.Load<UStruct>())
        {
            foreach (var child in current.ChildProperties ?? [])
            {
                names.Add(child.Name.Text);
                properties.Add(child as FProperty);
            }
        }

        return (names.ToArray(), properties.ToArray());
    }

    private static bool IsMutableDataStruct(FStructProperty structProperty)
    {
        for (var struc = structProperty.Struct.Load<UStruct>(); struc is not null; struc = struc.SuperStruct?.Load<UStruct>())
            if (struc.Name == "AnimBlueprintMutableData") return true;

        // The native base isn't in the package, so the chain usually runs out at the generated struct itself.
        return structProperty.Struct.ResolvedObject?.Name.Text.EndsWith("MutableData", StringComparison.Ordinal) == true;
    }

    private static UStruct? FindMutableStruct(UClass uClass)
    {
        foreach (var child in uClass.ChildProperties ?? [])
            if (child is FStructProperty structProperty && IsMutableDataStruct(structProperty))
                return structProperty.Struct.Load<UStruct>();
        return null;
    }
}
