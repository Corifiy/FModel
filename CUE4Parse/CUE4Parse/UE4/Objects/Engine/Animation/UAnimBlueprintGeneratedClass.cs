using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Objects.Engine.Animation;

public class UAnimBlueprintGeneratedClass : UBlueprintGeneratedClass
{
    private List<AnimGraphStorage.ExposedValueHandler>? _exposedValueHandlers;
    private bool _exposedValueHandlersBuilt;

    /// <summary>
    /// Up to UE 4.27 the class published "EvaluateGraphExposedInputs", one FExposedValueHandler per anim node
    /// naming the variables its pins were wired to. UE 5 replaced it with a property access library plus per-node
    /// handlers in sparse class data, and folded most pin values off the node structs entirely - so a cooked UE 5
    /// anim BP exports node objects holding little more than pose links, and anything reading these dumps loses
    /// the wiring completely. This rebuilds it in the shape it used to have.
    /// </summary>
    private List<AnimGraphStorage.ExposedValueHandler> ExposedValueHandlers
    {
        get
        {
            if (_exposedValueHandlersBuilt) return _exposedValueHandlers ?? [];
            _exposedValueHandlersBuilt = true;

            try
            {
                _exposedValueHandlers = AnimGraphStorage.Create(this, ClassDefaultObject?.Load())?.BuildExposedValueHandlers();
            }
            catch (Exception e)
            {
                // Reconstructed convenience data: an anim class this can't make sense of should still export.
                Log.Warning(e, "Failed to rebuild EvaluateGraphExposedInputs for {Class}", Name);
            }

            return _exposedValueHandlers ?? [];
        }
    }

    protected internal override bool HasReconstructedProperties => ExposedValueHandlers.Count > 0;

    protected internal override void WriteReconstructedProperties(JsonWriter writer, JsonSerializer serializer)
    {
        base.WriteReconstructedProperties(writer, serializer);

        var handlers = ExposedValueHandlers;
        if (handlers.Count == 0) return;

        writer.WritePropertyName("EvaluateGraphExposedInputs");
        writer.WriteStartArray();
        foreach (var handler in handlers)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("BoundFunction");
            writer.WriteValue(handler.BoundFunction.Text);

            writer.WritePropertyName("CopyRecords");
            writer.WriteStartArray();
            foreach (var record in handler.CopyRecords)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("SourcePropertyName");
                writer.WriteValue(record.SourcePropertyName);

                writer.WritePropertyName("SourceSubPropertyName");
                writer.WriteValue(record.SourceSubPropertyName);

                writer.WritePropertyName("SourceArrayIndex");
                writer.WriteValue(record.SourceArrayIndex);

                writer.WritePropertyName("bInstanceIsTarget");
                writer.WriteValue(false);

                writer.WritePropertyName("PostCopyOperation");
                writer.WriteValue(record.PostCopyOperation.Text);

                // UE 5 node properties are FFields rather than exports, so there is nothing to point ObjectPath
                // at. ObjectName keeps the engine's "Type'Owner:Name'" spelling, which is what readers split on.
                writer.WritePropertyName("DestProperty");
                writer.WriteStartObject();
                writer.WritePropertyName("ObjectName");
                writer.WriteValue(record.DestStructName.Length > 0
                    ? $"{record.DestPropertyType}'{record.DestStructName}:{record.DestPropertyName}'"
                    : $"{record.DestPropertyType}'{Name}:{record.DestPropertyName}'");
                writer.WriteEndObject();

                writer.WritePropertyName("DestArrayIndex");
                writer.WriteValue(0);

                // Past the UE 4 shape: the pin name on its own so nothing has to parse ObjectName, plus what the
                // library says about how the value gets there.
                writer.WritePropertyName("DestPropertyName");
                writer.WriteValue(record.DestPropertyName);

                writer.WritePropertyName("CopyType");
                writer.WriteValue(record.CopyType);

                writer.WritePropertyName("bOnlyUpdateWhenActive");
                writer.WriteValue(record.OnlyUpdateWhenActive);

                writer.WritePropertyName("LibraryCopyIndex");
                writer.WriteValue(record.CopyIndex);

                // Set when the pin was folded off the node: the copy physically writes this mutable slot and the
                // node reads it back through its entry table, so the copy alone never names the pin.
                if (record.ViaMutable is not null)
                {
                    writer.WritePropertyName("FoldedMutableProperty");
                    writer.WriteValue(record.ViaMutable);
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("Function");
            if (handler.Function is { IsNull: false } function) serializer.Serialize(writer, function);
            else writer.WriteNull();

            writer.WritePropertyName("ValueHandlerNodeProperty");
            writer.WriteStartObject();
            writer.WritePropertyName("ObjectName");
            writer.WriteValue($"StructProperty'{Name}:{handler.NodeName}'");
            writer.WriteEndObject();

            // The AnimNodeProperties index, which is what every pose link's LinkID refers to. UE 4 left readers
            // to derive it from the order the node properties happened to be exported in.
            writer.WritePropertyName("NodeIndex");
            writer.WriteValue(handler.NodeIndex);

            writer.WritePropertyName("NodeName");
            writer.WriteValue(handler.NodeName);

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
