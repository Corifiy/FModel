using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CUE4Parse.GameTypes._2XKO.Kismet;
using CUE4Parse.GameTypes.Borderlands4.Kismet;
using CUE4Parse.GameTypes.DFHO.Kismet;
using CUE4Parse.GameTypes.WuWa.Kismet;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine.Ai;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Objects.Engine.GameFramework;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.Utils;
using Serilog;

namespace CUE4Parse.UE4.Objects.UObject.BlueprintDecompiler;

public static class BlueprintDecompilerUtils
{
    public static TypeMappings? Mappings { get; set; }
    public static UFunction Function { get; set; }
    private static readonly Stack<int> _executionFlowStack = new();

    public static string GetClassWithPrefix(UStruct? prefixClassStruct)
    {
        var prefix = GetPrefix(prefixClassStruct);
        return $"{prefix}{prefixClassStruct?.Name}";
    }

    private static string GetPrefix(UStruct? struc)
    {
        var current = struc;

        while (current != null)
        {
            if (current.Name == "Actor")
                return "A";
            if (current.Name == "Interface")
                return "I";
            if (current.Name == "Object")
                return "U";

            var next = current.SuperStruct?.Load<UStruct>();

            if (next is null && Mappings is not null &&
                Mappings.Types.TryGetValue(current.Name, out var structMappings))
            {
                if (string.IsNullOrEmpty(structMappings.SuperType) ||
                    current.Name == structMappings.SuperType)
                    break;

                if (structMappings.SuperType == "Actor")
                    return "A";
                if (structMappings.SuperType == "Interface")
                    return "I";
                if (structMappings.SuperType == "Object")
                    return "U";

                next = new UScriptClass(structMappings.SuperType);
            }

            current = next;
        }
        return "U";
    }

    private static string GetPrefix(string? strucName)
    {
        if (string.IsNullOrEmpty(strucName) || Mappings is null)
            return "U";

        var current = strucName;

        if (current == "Actor") return "A";
        if (current == "Interface") return "I";
        if (current == "Object") return "U";

        while (Mappings.Types.TryGetValue(current, out var structMappings))
        {
            var superType = structMappings.SuperType;

            if (string.IsNullOrEmpty(superType) || superType == current)
                break;

            if (superType == "Actor") return "A";
            if (superType == "Interface") return "I";
            if (superType == "Object") return "U";

            current = superType;
        }

        return "U";
    }

    // Add_IntInt = UKismetMathLibrary::Add_IntInt(Temp_int_Loop_Counter_Variable, 1);
    // to
    // Add_IntInt = Temp_int_Loop_Counter_Variable + 1;
    private static string MathFunctionCleaner(
        string className,
        string functionName,
        List<string> parametersList,
        string parameters)
    {
        if (className.StartsWith("SolarisMathLibrary_") || className == "KismetMathLibrary")
        {
            if (functionName.StartsWith("EqualEqual_ByteByte")) return $"((!{parametersList[0]}) == (!{parametersList[1]}))";
            if (functionName.StartsWith("NotEqual_ByteByte")) return $"((!{parametersList[0]}) !== (!{parametersList[1]}))";
            if (functionName.StartsWith("EqualEqual_")) return $"{parametersList[0]} == {parametersList[1]}";
            if (functionName.StartsWith("NotEqual_")) return $"({parametersList[0]} !== {parametersList[1]})";
            if (functionName.StartsWith("NotEqualExactly_")) return $"({parametersList[0]} != {parametersList[1]})";
            if (functionName.StartsWith("LessEqual_")) return $"({parametersList[0]} <= {parametersList[1]})";
            if (functionName.StartsWith("Less_")) return $"({parametersList[0]} < {parametersList[1]})";
            if (functionName.StartsWith("GreaterEqual_")) return $"({parametersList[0]} >= {parametersList[1]})";
            if (functionName.StartsWith("Greater_")) return $"({parametersList[0]} > {parametersList[1]})";

            if (functionName.StartsWith("Add_")) return $"{parametersList[0]} + {parametersList[1]}";
            if (functionName.StartsWith("Xor_"))  return $"({parametersList[0]} ^ {parametersList[1]})";
            if (functionName.StartsWith("Multiply_")) return $"({parametersList[0]} * {parametersList[1]})";
            if (functionName.StartsWith("Percent_")) return $"({parametersList[0]} % {parametersList[1]})";
            if (functionName.StartsWith("Or_")) return $"({parametersList[0]} | {parametersList[1]})";
            if (functionName.StartsWith("Subtract_")) return $"{parametersList[0]} - {parametersList[1]}";
            if (functionName.StartsWith("Not_PreBool")) return $"!{parametersList[0]}";
            if (functionName.StartsWith("Not_")) return $"(~{parametersList[0]})";
            if (functionName.StartsWith("Select")) return $"({parametersList[2]} ? {parametersList[0]} : {parametersList[1]})";
            if (functionName.StartsWith("AddEquals")) return $"({parametersList[0]} += {parametersList[1]})";
            if (functionName.StartsWith("Subtract")) return $"({parametersList[0]} - {parametersList[1]})";
            if (functionName.StartsWith("Divide")) return $"({parametersList[0]} / {parametersList[1]})";
            if (functionName.StartsWith("MultiplyByPi")) return $"({parametersList[0]} * π)";
            if (functionName.StartsWith("Multiply")) return $"({parametersList[0]} * {parametersList[1]})";
            if (functionName.StartsWith("BooleanAND")) return $"{parametersList[0]} && {parametersList[1]}";
            if (functionName.StartsWith("BooleanNAND")) return $"!({parametersList[0]} && {parametersList[1]})";
            if (functionName.StartsWith("BooleanOR")) return $"({parametersList[0]} || {parametersList[1]})";
            if (functionName.StartsWith("BooleanXOR")) return $"{parametersList[0]} ^ {parametersList[1]}";
            if (functionName.StartsWith("BooleanNOR")) return $"!({parametersList[0]} || {parametersList[1]})";
            if (functionName.StartsWith("Floor")) return $"Floor({parametersList[0]})";
            if (functionName.StartsWith("Abs")) return $"{parametersList[0]} < 0.0 ? -{parametersList[0]} : {parametersList[0]}";
            if (functionName.StartsWith("CheckConstrainedFloat")) return $"{parametersList[2]} < {parametersList[0]} or {parametersList[2]} > {parametersList[1]}";
            if (functionName == "Max") return $"(({parametersList[0]} > {parametersList[1]}) ? {parametersList[0]} : {parametersList[1]})";
            if (functionName.StartsWith("Negate")) return $"-{parametersList[0]}";
            if (functionName.StartsWith("Ceil")) return $"Ceil({parametersList[0]})";

            if (functionName.StartsWith("UncheckedConvertI32I64")) return $"{parametersList[0]}";
            if (functionName.StartsWith("MakeTransform")) return $"FTransform({parametersList[0]}, {parametersList[1]}, {parametersList[2]})";
            if (functionName.StartsWith("Conv_VectorToTransform")) return $"FTransform({parametersList[0]})";
            if (functionName.StartsWith("MakeVector2D")) return $"FVector({parametersList[0]}, {parametersList[1]})";
            if (functionName.StartsWith("MakeVector")) return $"FVector({parametersList[0]}, {parametersList[1]}, {parametersList[2]})";
            if (functionName.EndsWith("ToVector")) return $"FVector((float){parametersList[0]})";
            if (functionName.StartsWith("MakeRotator")) return $"FRotator({parametersList[0]}, {parametersList[1]}, {parametersList[2]})";
            if (functionName.StartsWith("MakeTimespan")) return $"FTimespan({parametersList[0]}, {parametersList[1]}, {parametersList[2]}, {parametersList[4]} * 1000 * 1000)";
            if (functionName.StartsWith("MakeColor")) return $"FLinearColor({parametersList[0]}, {parametersList[1]}, {parametersList[2]}, {parametersList[3]})";
            if (functionName.StartsWith("ComposeRotators")) return $"FRotator(FQuat({parametersList[0]}) * FQuat({parametersList[1]}))";
            if (functionName.EndsWith("ToLinearColor")) return $"FLinearColor({parametersList[0]})";

            if (functionName.StartsWith("Conv_IntToBool")) return $"({parametersList[0]} != 0)";
            if (functionName.StartsWith("Conv_BoolToInt")) return $"({parametersList[0]} ? 1 : 0)";
            if (functionName.StartsWith("Conv_BoolToByte")) return $"({parametersList[0]} ? 1 : 0)";
            if (functionName.StartsWith("Conv_BoolToFloat")) return $"({parametersList[0]} ? 1.0f : 0.0f)";
            if (functionName.StartsWith("Conv_BoolToDouble")) return $"({parametersList[0]} ? 1.0 : 0.0)";

            if (functionName.EndsWith("ToDouble")) return $"((double){parametersList[0]})";
            if (functionName.EndsWith("ToFloat")) return $"((float){parametersList[0]})";
            if (functionName.EndsWith("ToInt64")) return $"((int64){parametersList[0]})";
            if (functionName.EndsWith("ToInt")) return $"((int32){parametersList[0]})";
            if (functionName.EndsWith("ToByte")) return $"((uint8){parametersList[0]})";

            if (functionName == "BreakRotator")
            {
                return $@"{parametersList[1]} = {parametersList[0]}.Roll;
{parametersList[2]} = {parametersList[0]}.Pitch;
{parametersList[3]} = {parametersList[0]}.Yaw";
            }
            if (functionName == "BreakVector")
            {
                return $@"{parametersList[1]} = {parametersList[0]}.X;
{parametersList[2]} = {parametersList[0]}.Y;
{parametersList[3]} = {parametersList[0]}.Z";
            }
            if (functionName == "BreakVector2D")
            {
                return $@"{parametersList[1]} = {parametersList[0]}.X;
{parametersList[2]} = {parametersList[0]}.Y";
            }
            if (functionName == "BreakTransform")
            {
                return $@"{parametersList[1]} = {parametersList[0]}.Location;
{parametersList[2]} = {parametersList[0]}.Rotation;
{parametersList[3]} = {parametersList[0]}.Scale";
            }
            if (functionName == "BreakColor")
            {
                return $@"{parametersList[1]} = {parametersList[0]}.R;
{parametersList[2]} = {parametersList[0]}.G;
{parametersList[3]} = {parametersList[0]}.B;
{parametersList[4]} = {parametersList[0]}.A";
            }
        }
        if (className == "KismetStringLibrary")
        {
            if (functionName.StartsWith("EqualEqual_")) return $"{parametersList[0]} == {parametersList[1]}";
            if (functionName.StartsWith("NotEqual_")) return $"({parametersList[0]} !== {parametersList[1]})";

            if (functionName.EndsWith("ToDouble")) return $"(double){parametersList[0]}";
            if (functionName.EndsWith("ToFloat")) return $"(float){parametersList[0]}";
            if (functionName.EndsWith("ToInt64")) return $"(int64){parametersList[0]}";
            if (functionName.EndsWith("ToInt")) return $"(int32){parametersList[0]}";
            if (functionName.EndsWith("ToByte")) return $"(uint8){parametersList[0]}";

            if (functionName.StartsWith("Conv_BoolToString")) return $"{parametersList[0]} ? \"true\" : \"false\"";
            if (functionName.EndsWith("ToString")) return $"FString({parametersList[0]})";
            if (functionName.EndsWith("ToName")) return $"FName({parametersList[0]})";
            if (functionName.StartsWith("Concat_StrStr")) return string.Join(" += ", parametersList);
            if (functionName.StartsWith("ParseIntoArray")) return $"{parametersList[0]}.Split({parametersList[1]}, /* removeEmpty = */ {parametersList[2]})";
            if (functionName.StartsWith("Contains")) return $"{parametersList[0]}.Contains({parametersList[1]}, /* removeEmpty = */ {parametersList[2]})";
            if (functionName.StartsWith("JoinStringArray")) return $"{parametersList[0]}.Join({parametersList[1]})";
            if (functionName.StartsWith("Replace")) return $"{parametersList[0]}.Replace({parametersList[1]}, {parametersList[2]}, /* SearchCase = */ {parametersList[3]})";
            if (functionName.StartsWith("StartsWith")) return $"{parametersList[0]}.startswith({parametersList[1]}, /* SearchCase = */ {parametersList[2]})";
            if (functionName.StartsWith("Contains")) return $"{parametersList[0]}.Contains({parametersList[1]}, /* bUseCase = */ {parametersList[2]}, /* bSearchFromEnd = */ {parametersList[3]})";
            if (functionName.StartsWith("IsNumeric")) return $"{parametersList[0]}.IsNumeric()";
            if (functionName.StartsWith("Len")) return $"{parametersList[0]}.Length";
        }
        if (className == "KismetSystemLibrary")
        {
            if (functionName.StartsWith("IsValid") || functionName.StartsWith("Conv_SoftClassReferenceToClass") || functionName.StartsWith("Conv_SoftObjectReferenceToObject") || (functionName.StartsWith("Make") && parametersList.Count == 1))
            {
                return $"{parametersList[0]}";
            }
            if (functionName.StartsWith("Conv_ObjectToSoftObjectReference") || functionName.StartsWith("Conv_SoftObjPathToSoftObjRef")) return $"TSoftObjectPtr<UObject>({parametersList[0]})";
            if (functionName.StartsWith("Delay") && parametersList.Count == 3) return $"Delay({parametersList[1]}f);\n{parametersList[2]}";
            if (functionName.StartsWith("Conv_SoftClassPathToSoftClassRef")) return $"TSoftClassPtr<UObject>({parametersList[0]})";
            if (functionName.StartsWith("Conv_ClassToSoftClassReference")) return $"TSoftClassPtr<UObject>(*{parametersList[0]})";
        }
        if (className == "KismetInputLibrary" || className == "BlueprintGameplayTagLibrary" || className == "FortKismetLibrary" || className == "KismetTextLibrary")
        {
            if (functionName.StartsWith("EqualEqual_")) return $"{parametersList[0]} == {parametersList[1]}";
            if (functionName.StartsWith("NotEqual_")) return $"({parametersList[0]} !== {parametersList[1]})";
            if (functionName.EndsWith("ToText")) return $"FText({parametersList[0]})";
            if (functionName.EndsWith("ToString"))  return $"FString({parametersList[0]})";
        }

        // Doesn't work on UE4 as GetPrefix requires mappings
        return $"{GetPrefix(className)}{className}::{functionName}({parameters})";
    }

    private static string FinalFunctionCleaner(
        string className,
        string functionName,
        List<string> parametersList,
        string parameters)
    {
        if (className == "KismetArrayLibrary")
        {
            if (functionName.StartsWith("Array_Length")) return $"{parametersList[0]}.Length";
            if (functionName.StartsWith("Array_IsNotEmpty")) return $"{parametersList[0]}.Length > 0";
            if (functionName.StartsWith("Array_LastIndex")) return $"{parametersList[0]}.Length - 1";
            if (functionName.StartsWith("Array_Clear")) return $"{parametersList[0]}.Clear()";
            if (functionName.StartsWith("Array_Identical")) return $"{parametersList[0]} == {parametersList[1]}";
            if (functionName.StartsWith("Array_Remove")) return $"{parametersList[0]}.Remove({parametersList[1]})";
            if (functionName.StartsWith("Array_Add")) return $"{parametersList[0]}.Add({parametersList[1]})";
            if (functionName.StartsWith("Array_Get")) return $"{parametersList[2]} = {parametersList[0]}[{parametersList[1]}]";
            if (functionName.StartsWith("Array_Contains")) return $"{parametersList[0]}[{parametersList[1]}]";
            if (functionName.StartsWith("Array_IsValidIndex")) return $"{parametersList[0]}[{parametersList[1]}]";
            if (functionName.StartsWith("Array_Insert")) return $"{parametersList[0]}[{parametersList[2]}] = {parametersList[1]}";
        }

        if (className == "BlueprintMapLibrary")
        {
            if (functionName.StartsWith("Map_Length")) return $"{parametersList[0]}.Length";
            if (functionName.StartsWith("Map_Remove")) return $"{parametersList[0]}.Remove({parametersList[1]})";
            if (functionName.StartsWith("Map_Contains")) return $"{parametersList[0]}[{parametersList[1]}]";
            if (functionName.StartsWith("Map_Get")) return $"{parametersList[2]} = {parametersList[0]}[{parametersList[1]}]";
        }

        if (className == "BlueprintSetLibrary")
        {
            if (functionName.StartsWith("Set_AddItems")) return $"{parametersList[0]}.Add({parametersList[1]})";
            if (functionName.StartsWith("Set_Clear")) return $"{parametersList[0]}.Clear()";
            if (functionName.StartsWith("Set_Difference")) return $"{parametersList[2]} = {parametersList[0]} == {parametersList[1]}";
            if (functionName.StartsWith("Set_IsEmpty")) return $"{parametersList[0]}.Length == 0";
        }

        // Doesn't work on UE4 as GetPrefix requires mappings
        return $"{GetPrefix(className)}{className}::{functionName}({parameters})";
    }


    private static string GetTagTypes(FPropertyTagData? tagType) => tagType?.Type switch
        {
            "EnumProperty" or "ByteProperty" when tagType.EnumName != null => tagType.EnumName,
            "IntProperty" => "int",
            "Int8Property" => "int8",
            "Int16Property" => "int16",
            "Int64Property" => "int64",
            "UInt16Property" => "uint16",
            "UInt32Property" => "uint32",
            "UInt64Property" => "uint64",
            "ByteProperty" => "byte",
            "BoolProperty" => "bool",
            "StrProperty" => "string",
            "VerseStringProperty" => "string",
            "DoubleProperty" => "double",
            "NameProperty" => "FName",
            "TextProperty" => "FText",
            "FloatProperty" => "float",
            "SoftObjectProperty" or "AssetObjectProperty" => "FSoftObjectPath",
            "ObjectProperty" or "ClassProperty" => "UObject*",
            "StructProperty" => $"F{tagType.StructType}",
            "InterfaceProperty" => $"I{tagType.StructType}",
            _ => throw new NotSupportedException($"PropertyType {tagType?.Type} is currently not supported")
        };

    private static bool IsPointer(FProperty property) => property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ||
                                                         property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) ||
                                                         property.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ||
                                                         property.GetType() == typeof(FObjectProperty);

    private static bool IsPointer(UProperty property) => property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ||
                                                         property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) ||
                                                         property.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ||
                                                         property.GetType() == typeof(UObjectProperty);

    public static (string?, string?) GetPropertyType(FProperty property)
    {
        string? value = null;
        string? type = null;

        var propertyFlags = property.PropertyFlags;
        if (propertyFlags.HasFlag(EPropertyFlags.ConstParm))
        {
            type += "const ";
        }

        switch (property)
        {
            case FObjectProperty objectProperty:
            {
                // Looks bad and provides useless information.
                // value = objectProperty.PropertyClass.ToString();
                type += $"class {GetClassWithPrefix(objectProperty.PropertyClass.Load<UStruct>())}";
                break;
            }
            case FArrayProperty arrayProperty:
            {
                var (_, innerType) = GetPropertyType(arrayProperty.Inner!);

                // Looks bad and provides useless information.
                //var customStringBuilder = new CustomStringBuilder();
                //customStringBuilder.OpenBlock("[");
                //customStringBuilder.AppendLine(innerValue!);
                //customStringBuilder.CloseBlock("]");

                //value = customStringBuilder.ToString();
                type += $"TArray<{innerType}>";
                break;
            }
            case FStructProperty structProperty:
            {
                var structType = structProperty.Struct.Name;

                type += $"struct F{structType}";
                value = structProperty.Struct.ToString();
                break;
            }
            case FNumericProperty:
            {
                if (property is FByteProperty byteProperty && byteProperty.Enum.TryLoad(out var enumObj))
                {
                    type = enumObj.Name;
                }
                else
                {
                    type = property.GetType().Name.SubstringAfter("F").SubstringBefore("Property").ToLowerInvariant();
                }
                break;
            }
            case FInterfaceProperty interfaceProperty:
            {
                type = $"F{interfaceProperty.InterfaceClass.Name}";
                break;
            }
            case FBoolProperty boolProperty:
            {
                type = boolProperty.bIsNativeBool ? "bool" : "uint8";
                break;
            }
            case FStrProperty:
            case FVerseStringProperty:
            {
                type = "FString";
                break;
            }
            case FTextProperty:
            {
                type = "FText";
                break;
            }
            case FNameProperty:
            {
                type = "FName";
                break;
            }
            case FMapProperty mapProperty:
            {
                var (_, keyinnerType) = GetPropertyType(mapProperty.KeyProp!);
                var (_, valueinnerType) = GetPropertyType(mapProperty.ValueProp!);
                type = $"TMap<{keyinnerType}, {valueinnerType}>";
                break;
            }
            case FEnumProperty enumProperty:
            {
                type = $"{enumProperty.Enum.Name}";
                break;
            }
            case FVerseDynamicProperty:
            {
                type = "DynamicVerse";
                break;
            }
            case FVerseFunctionProperty:
            {
                type = "VerseFunc";
                break;
            }
            case FOptionalProperty optionalProperty:
            {
                var (_, keyinnerType) = GetPropertyType(optionalProperty.ValueProperty!);
                type = $"TOptional<{keyinnerType}>";
                break;
            }
            default:
            {
                Log.Warning("Property Value '{type}' is currently not supported", property.GetType().Name);
                break;
            }
        }

        if (IsPointer(property))
            type += "*";

        if (propertyFlags.HasFlag(EPropertyFlags.OutParm) && !propertyFlags.HasFlag(EPropertyFlags.ReturnParm))
            type += "&";

        return (value, type);
    }

    // Legacy UProperty Support
     public static (string?, string?) GetPropertyType(UProperty property)
    {
        string? value = null;
        string? type = null;

        var propertyFlags = property.PropertyFlags;
        if (propertyFlags.HasFlag(EPropertyFlags.ConstParm))
        {
            type += "const ";
        }

        switch (property)
        {
            case UObjectProperty objectProperty:
            {
                // Looks bad and provides useless information.
                // value = objectProperty.PropertyClass.ToString();
                type += $"class {GetClassWithPrefix(objectProperty.PropertyClass.Load<UStruct>())}";
                break;
            }
            case UArrayProperty arrayProperty:
            {
                if (arrayProperty.Inner.Load() is UProperty innerProp)
                {
                    var (_, innerType) = GetPropertyType(innerProp);

                    // Looks bad and provides useless information.
                    //var customStringBuilder = new CustomStringBuilder();
                    //customStringBuilder.OpenBlock("[");
                    //customStringBuilder.AppendLine(innerValue!);
                    //customStringBuilder.CloseBlock("]");

                    //value = customStringBuilder.ToString();
                    type += $"TArray<{innerType}>";
                }
                break;
            }
            case UStructProperty structProperty:
            {
                var structType = structProperty.Struct.Name;

                type += $"struct F{structType}";
                value = structProperty.Struct.ToString();
                break;
            }
            case UNumericProperty:
            {
                if (property is UByteProperty byteProperty && byteProperty.Enum.TryLoad(out var enumObj))
                {
                    type = enumObj.Name;
                }
                else
                {
                    type = property.GetType().Name.SubstringAfter("U").SubstringBefore("Property").ToLowerInvariant();
                }
                break;
            }
            case UInterfaceProperty interfaceProperty:
            {
                type = $"F{interfaceProperty.InterfaceClass.Name}";
                break;
            }
            case UBoolProperty boolProperty:
            {
                type = boolProperty.bIsNativeBool ? "bool" : "uint8";
                break;
            }
            case UStrProperty:
            {
                type = "FString";
                break;
            }
            case UTextProperty:
            {
                type = "FText";
                break;
            }
            case UNameProperty:
            {
                type = "FName";
                break;
            }
            case UMapProperty mapProperty:
            {
                if (mapProperty.KeyProp.Load() is UProperty innerProp && mapProperty.KeyProp.Load() is UProperty valueProp)
                {
                    var (_, keyinnerType) = GetPropertyType(innerProp);
                    var (_, valueinnerType) = GetPropertyType(valueProp);
                    type = $"TMap<{keyinnerType}, {valueinnerType}>";
                }
                break;
            }
            case UEnumProperty enumProperty:
            {
                type = $"{enumProperty.Enum.Name}";
                break;
            }
            default:
            {
                Log.Warning("Property Value '{type}' is currently not supported", property.GetType().Name);
                break;
            }
        }

        if (IsPointer(property))
            type += "*";

        if (propertyFlags.HasFlag(EPropertyFlags.OutParm) && !propertyFlags.HasFlag(EPropertyFlags.ReturnParm))
            type += "&";

        return (value, type);
    }

    private static T GetGenericValue<T>(this FPropertyTag propertyTag) => (T) propertyTag.Tag?.GenericValue!;
    private static string GetGenericValueStr<T>(this FPropertyTag propertyTag) => propertyTag.Tag?.GenericValue?.ToString()!;

    public static bool GetPropertyTagVariable(FPropertyTag propertyTag, out string type, out string value)
    {
        type = string.Empty;
        value = string.Empty;

        if (!Enum.TryParse<EPropertyType>(propertyTag.PropertyType.ToString(), out var propertyType))
        {
            Log.Warning("Unable to Parse {0} while trying to get PropertyEnum Type",
                propertyTag.PropertyType.ToString());
            return false;
        }

        switch (propertyType)
        {
            case EPropertyType.ByteProperty:
            {
                if (propertyTag.Tag?.GenericValue is FName name)
                {
                    var enumValue = name.ToString();

                    value = $"{enumValue}";
                    type = $"enum {enumValue.SubstringBefore("::")}";
                }
                else
                {
                    value = propertyTag.GetGenericValueStr<byte>();
                    type = "byte";
                }

                break;
            }
            case EPropertyType.BoolProperty:
            {
                type = "bool";
                value = propertyTag.GetGenericValueStr<bool>().ToLowerInvariant();
                break;
            }
            case EPropertyType.IntProperty:
            {
                type = "int32";
                value = propertyTag.GetGenericValueStr<int>();
                break;
            }
            case EPropertyType.FloatProperty:
            {
                type = "float";
                value = propertyTag.GetGenericValue<float>().ToString(CultureInfo.InvariantCulture);
                break;
            }
            case EPropertyType.ObjectProperty or EPropertyType.ClassProperty:
            {
                var pkgIndex = propertyTag.GetGenericValueStr<FPackageIndex>();
                if (pkgIndex is null or "0")
                {
                    type = "class UObject*";
                    value = "nullptr";
                }
                else
                {
                    type = $"class U{pkgIndex.SubstringBefore("'")}*";
                    value = $"\"{pkgIndex}\"";
                }

                break;
            }
            case EPropertyType.NameProperty:
            {
                type = "FName";
                value = $"FName(\"{propertyTag.GetGenericValueStr<FName>()}\")";
                break;
            }
            case EPropertyType.DoubleProperty:
            {
                type = "double";
                value = propertyTag.GetGenericValue<double>().ToString(CultureInfo.InvariantCulture);
                break;
            }
            case EPropertyType.ArrayProperty:
            {
                var scriptArray = propertyTag.GetGenericValue<UScriptArray>();
                if (scriptArray == null || scriptArray.Properties == null || scriptArray.InnerType == null || scriptArray.InnerTagData == null)
                {
                    value = "{}";
                    type = "TArray<unknown>";
                    break;
                }

                if (scriptArray.Properties.Count == 0)
                {
                    value = "{}";
                    var innerType = GetTagTypes(scriptArray.InnerTagData);

                    type = $"TArray<{innerType}>";
                }
                else
                {
                    var customStringBuilder = new CustomStringBuilder();
                    customStringBuilder.OpenBlock();
                    for (int i = 0; i < scriptArray.Properties.Count; i++)
                    {
                        var property = scriptArray.Properties[i];
                        if (!GetPropertyTagVariable(
                                new FPropertyTag(new FName(scriptArray.InnerType), property, scriptArray.InnerTagData),
                                out type, out var innerValue))
                        {
                            Log.Warning("Failed to get ArrayElement of type {type}", scriptArray.InnerType);
                            continue;
                        }

                        if (scriptArray.InnerType == "EnumProperty")
                        {
                            innerValue = innerValue.SubstringAfter("::");
                        }

                        if (i < scriptArray.Properties.Count - 1)
                            customStringBuilder.AppendLine(innerValue + ",");
                        else
                            customStringBuilder.AppendLine(innerValue);
                    }

                    customStringBuilder.CloseBlock();
                    type = $"TArray<{type}>";
                    value = customStringBuilder.ToString();
                }

                break;
            }
            case EPropertyType.StructProperty:
            {
                var structType = propertyTag.GetGenericValue<FScriptStruct>();
                if (!GetPropertyTagVariable(structType, out value))
                {
                    Log.Error("Unable to get struct value or type for FScriptStruct type {structType}",
                        structType.GetType().Name);
                    return false;
                }

                type = $"struct F{propertyTag.TagData?.StructType}";
                break;
            }
            case EPropertyType.StrProperty:
            {
                type = "FString";
                value = $"\"{propertyTag.GetGenericValueStr<string>()}\"";
                break;
            }
            case EPropertyType.VerseStringProperty:
            {
                type = "FString";
                value = $"\"{propertyTag.GetGenericValueStr<string>()}\"";
                break;
            }
            case EPropertyType.MulticastDelegateProperty:
            {
                type = "FMulticastScriptDelegate";
                var list = propertyTag.GetGenericValue<FMulticastScriptDelegate>().InvocationList;

                if (list.Length == 0)
                {
                    value = "[]";
                }
                else
                {
                    var functions = string.Join(", ", list.Select(x => $"\"{x.FunctionName}\""));
                    value = $"[{functions}]";
                }
                break;
            }
            case EPropertyType.TextProperty:
            {
                var genericValue = propertyTag.GetGenericValue<FText>();

                var flags = genericValue.Flags.ToStringBitfield();
                //var historyText = genericValue.TextHistory.Text;
                var text = genericValue.Text;

                // TODO: find a way to show TextHistory?
                // none Base type ^^ as that's just text

                type = "FText";
                value = genericValue.Flags == 0
                    ? $"FText(\"{text}\")"
                    : $"FText(\"{text}\", {flags})";

                break;
            }
            case EPropertyType.AssetObjectProperty: // AssetObjectProperty is the old name of SoftObjectProperty
            {
                var softObjectPath = propertyTag.Tag.GenericValue;

                type = "FSoftObjectPath";
                value = $"FSoftObjectPath(\"{softObjectPath}\")";
                break;
            }
            case EPropertyType.SoftObjectProperty or EPropertyType.SoftClassProperty:
            {
                var softObjectPath = propertyTag.GetGenericValueStr<FSoftObjectPath>();

                type = "FSoftObjectPath";
                value = $"FSoftObjectPath(\"{softObjectPath}\")";
                break;
            }
            case EPropertyType.UInt64Property:
            {
                type = "uint64";
                value = propertyTag.GetGenericValueStr<ulong>();
                break;
            }
            case EPropertyType.UInt32Property:
            {
                type = "uint32";
                value = propertyTag.GetGenericValueStr<uint>();
                break;
            }
            case EPropertyType.UInt16Property:
            {
                type = "uint16";
                value = propertyTag.GetGenericValueStr<ushort>();
                break;
            }
            case EPropertyType.Int64Property:
            {
                type = "int64";
                value = propertyTag.GetGenericValueStr<long>();
                break;
            }
            case EPropertyType.Int16Property:
            {
                type = "int16";
                value = propertyTag.GetGenericValueStr<short>();
                break;
            }
            case EPropertyType.Int8Property:
            {
                type = "int8";
                value = propertyTag.GetGenericValueStr<sbyte>();
                break;
            }
            case EPropertyType.MapProperty:
            {
                var scriptMap = propertyTag.GetGenericValue<UScriptMap>();

                if (scriptMap == null || scriptMap.Properties == null)
                {
                    value = "{}";
                    type = "TMap<unknown, unknown>";
                    break;
                }

                if (scriptMap.Properties.Count > 0)
                {
                    var keyType = string.Empty;
                    var valueType = string.Empty;

                    var customStringBuilder = new CustomStringBuilder();
                    var keyValueList = new List<string>(scriptMap.Properties.Count);

                    customStringBuilder.OpenBlock();
                    foreach (var (mapKey, mapValue) in scriptMap.Properties)
                    {
                        var innerTypeData = propertyTag.TagData?.InnerTypeData;
                        var keyProperty = new FPropertyTag(new FName(innerTypeData?.Type), mapKey, innerTypeData);

                        if (!GetPropertyTagVariable(keyProperty, out keyType, out var keyValue))
                        {
                            Log.Warning("Unable to get KeyValue for UScriptMap of type: {type}", mapKey.GetType().Name);
                            continue;
                        }

                        var valueTypeData = propertyTag.TagData?.ValueTypeData;
                        var valueProperty = new FPropertyTag(new FName(valueTypeData?.Type), mapValue, valueTypeData);

                        if (!GetPropertyTagVariable(valueProperty, out valueType, out var valueValue))
                        {
                            Log.Warning("Unable to get MapValue for UScriptMap of type: {type}",
                                mapValue.GetType().Name);
                        }

                        keyValueList.Add($"{{ {keyValue}, {valueValue} }}");
                    }

                    var keyValueString = string.Join(", \n", keyValueList);

                    customStringBuilder.AppendLine($"{keyValueString}");
                    customStringBuilder.CloseBlock();

                    type = $"TMap<{keyType}, {valueType}>";
                    value = customStringBuilder.ToString();
                }
                else
                {
                    var keyType = GetTagTypes(propertyTag.TagData?.InnerTypeData);
                    var valueType = GetTagTypes(propertyTag.TagData?.ValueTypeData);

                    type = $"TMap<{keyType}, {valueType}>";
                    value = "{}";
                }

                break;
            }
            case EPropertyType.EnumProperty:
            {
                value = propertyTag.GetGenericValueStr<FName>(); // .SubstringAfter("::")
                type = $"enum";//{propertyTag.TagData?.EnumName}
                break;
            }
            case EPropertyType.FieldPathProperty:
            {
                value = propertyTag.GetGenericValueStr<FFieldPath>();
                type = "FieldPath";
                break;
            }
            // todo
            case EPropertyType.WeakObjectProperty:
            {
                type = "WeakObject";
                return true;
            }
            case EPropertyType.InterfaceProperty:
            {
                type = "Interface";
                return true;
            }
            case EPropertyType.OptionalProperty:
            {
                type = $"TOptional<{propertyTag.TagData?.InnerType}>";
                return true;
            }
            case EPropertyType.SetProperty:
            {
                type = "TArray";
                return true;
            }
            case EPropertyType.DelegateProperty:
            {
                type = "Delegate";
                return true;
            }
            case EPropertyType.MulticastInlineDelegateProperty:
            {
                type = "FMulticastScriptDelegate";
                return true;
            }
            case EPropertyType.VerseFunctionProperty:
            {
                type = "VerseFunction";
                return true;
            }
            default:
            {
                Log.Warning($"EPropertyType {propertyTag.TagData?.Type} is currently not implemented");
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool GetPropertyTagVariable(FScriptStruct scriptStruct, out string value) =>
        GetPropertyTagVariable(scriptStruct.StructType, out value);

    /// <summary>
    /// A transform serialises rotation first, but every place a person meets one - the details panel, a pin,
    /// the viewport gizmo - puts the position first and the rotation after it. Reading order follows that,
    /// since these are numbers someone is going to compare against the editor. Any other struct is left in
    /// the order it was declared, which is the order its own fields are meant to be read in.
    /// </summary>
    private static IReadOnlyList<FPropertyTag> OrderStructMembers(List<FPropertyTag> properties)
    {
        var rotation = properties.FindIndex(property => property.Name.Text == "Rotation");
        var translation = properties.FindIndex(property => property.Name.Text == "Translation");
        if (rotation < 0 || translation < 0 || translation < rotation) return properties;

        var ordered = new List<FPropertyTag>(properties);
        ordered.RemoveAt(translation);
        ordered.Insert(rotation, properties[translation]);

        return ordered;
    }

    private static bool GetPropertyTagVariable(IUStruct uStruct, out string value)
    {
        value = string.Empty;

        switch (uStruct)
        {
            case FStructFallback fallback:
            {
                if (fallback.Properties.Count == 0)
                {
                    value = "{}";
                }
                else
                {
                    var properties = OrderStructMembers(fallback.Properties);
                    var stringBuilder = new CustomStringBuilder();
                    stringBuilder.OpenBlock();
                    for (int i = 0; i < properties.Count; i++)
                    {
                        var property = properties[i];
                        GetPropertyTagVariable(property, out string _, out string tagValue);
                        bool isLast = i == properties.Count - 1;
                        stringBuilder.AppendLine($"\"{property.Name}\": {tagValue}{(isLast ? "" : ",")}");
                    }

                    stringBuilder.CloseBlock();

                    value = stringBuilder.ToString();
                }
                break;
            }
            case FVector vector:
            {
                var x = vector.X;
                var y = vector.Y;
                var z = vector.Z;

                value = $"FVector({x}, {y}, {z})";
                break;
            }
            case FGuid guid:
            {
                var a = $"0x{guid.A:X8}";
                var b = $"0x{guid.B:X8}";
                var c = $"0x{guid.C:X8}";
                var d = $"0x{guid.D:X8}";

                value = $"FGuid({a}, {b}, {c}, {d})";
                break;
            }
            case FVector4 vector4:
            {
                var x = vector4.X;
                var y = vector4.Y;
                var z = vector4.Z;
                var w = vector4.W;

                value = $"FVector4({x}, {y}, {z}, {w})";
                break;
            }
            case TIntVector2<float> floatVector2:
            {
                var x = floatVector2.X;
                var y = floatVector2.Y;
                value = $"TIntVector2<float>({x}, {y})";
                break;
            }
            case FIntPoint intPoint:
            {
                var x = intPoint.X;
                var y = intPoint.Y;
                value = $"FIntPoint({x}, {y})";
                break;
            }
            case TIntVector3<float> floatVector3:
            {
                var x = floatVector3.X;
                var y = floatVector3.Y;
                var z = floatVector3.Z;
                value = $"TIntVector3<float>({x}, {y}, {z})";
                break;
            }
            case TIntVector4<float> floatVector3:
            {
                var x = floatVector3.X;
                var y = floatVector3.Y;
                var z = floatVector3.Z;
                value = $"TIntVector4<float>({x}, {y}, {z})";
                break;
            }
            case FVector2D vector2d:
            {
                var x = vector2d.X;
                var y = vector2d.Y;

                value = $"FVector2D({x}, {y})";
                break;
            }
            case FQuat fQuat:
            {
                // Nobody authors a rotation as four components, and the editor never shows one that way, so it
                // is printed as the euler angles it stands for - the same numbers a rotator pin displays.
                var rotator = fQuat.Rotator();

                value = $"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})";
                break;
            }
            case FBox box:
            {
                GetPropertyTagVariable(box.Min, out var min);
                GetPropertyTagVariable(box.Max, out var max);
                var isValid = box.IsValid;

                value = $"FBox({min}, {max}, {isValid})";
                break;
            }
            case TBox2<FVector2D> box2D:
            {
                GetPropertyTagVariable(box2D.Min, out var min);
                GetPropertyTagVariable(box2D.Max, out var max);
                var isValid = box2D.bIsValid;

                value = $"FBox2D({min}, {max}, {isValid})";
                break;
            }
            case FRotator rotator:
            {
                var pitch = rotator.Pitch;
                var yaw = rotator.Yaw;
                var roll = rotator.Roll;

                value = $"FRotator({pitch}, {yaw}, {roll})";
                break;
            }
            case FLinearColor linearColor:
            {
                var r = linearColor.R;
                var g = linearColor.G;
                var b = linearColor.B;
                var a = linearColor.A;

                value = $"FLinearColor({r}, {g}, {b}, {a})";
                break;
            }
            case FUniqueNetIdRepl netId:
            {
                var id = netId.UniqueNetId;
                value = $"FUniqueNetIdRepl({id})";
                break;
            }
            case FNavAgentSelector agent:
            {
                var bits = agent.PackedBits;
                value = $"FNavAgentSelector({bits})";
                break;
            }
            case FGameplayTagContainer gameplayTagContainer:
            {
                var gameplayTagsList = new List<string>(gameplayTagContainer.GameplayTags.Length);
                foreach (var gameplayTag in gameplayTagContainer.GameplayTags)
                {
                    gameplayTagsList.Add($"FGameplayTag::RequestGameplayTag(FName(\"{gameplayTag.TagName.ToString()}\"))");
                }

                var customStringBuilder = new CustomStringBuilder();

                if (gameplayTagsList.Count == 0)
                {
                    customStringBuilder.Append("FGameplayTagContainer({})");
                }
                else
                {
                    var gameplayTags = string.Join(",\n", gameplayTagsList);
                    customStringBuilder.OpenBlock("FGameplayTagContainer({");
                    customStringBuilder.AppendLine(gameplayTags);
                    customStringBuilder.CloseBlock("})");
                }

                value = customStringBuilder.ToString();

                break;
            }
            case FDateTime dateTime:
            {
                value = $"FDateTime(\"{dateTime}\")";
                break;
            }
            case FSoftObjectPath softObjectPath:
            {
                value = $"FSoftObjectPath(\"{softObjectPath.ToString()}\")";
                break;
            }
            case FColor color:
            {
                var r = color.B;
                var g = color.G;
                var b = color.B;
                var a = color.A;

                value = $"FColor({r}, {g}, {b}, {a})";
                break;
            }
            case FRichCurveKey richCurve:
            {
                var InterpMode = richCurve.InterpMode;
                var TangentMode = richCurve.TangentMode;
                var TangentWeightMode = richCurve.TangentWeightMode;
                var Time = richCurve.Time;
                var Value = richCurve.Value;
                var ArriveTangent = richCurve.ArriveTangent;
                var ArriveTangentWeight = richCurve.ArriveTangentWeight;
                var LeaveTangent = richCurve.LeaveTangent;
                var LeaveTangentWeight = richCurve.LeaveTangentWeight;

                value = $"FRichCurveKey({InterpMode}, {TangentMode}, {TangentWeightMode}, {Time}, {Value}, {ArriveTangent}, {ArriveTangentWeight}, {LeaveTangent}, {LeaveTangentWeight})";
                break;
            }
            default:
            {
                value = uStruct.ToString() ?? string.Empty;
                Log.Warning("Property Type '{type}' is currently not supported for FScriptStruct", uStruct.GetType().Name);
                break;
            }
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    public static string GetLineExpression(KismetExpression expression)
    {
        switch (expression)
        {
            case EX_VariableBase variableBase:
            {
                return variableBase.Variable.ToString();
            }
            case EX_LetValueOnPersistentFrame persistent:
            {
                var variableAssignment = GetLineExpression(persistent.AssignmentExpression);
                var variableToBeAssigned = persistent.DestinationProperty.ToString();
                return $"{(variableToBeAssigned.Contains("K2Node_") ? "UberGraphFrame->" + variableToBeAssigned : variableToBeAssigned)} = {variableAssignment}";
            }
            case EX_LetBool letBool:
            {
                var assignment = GetLineExpression(letBool.Assignment);
                var variable = GetLineExpression(letBool.Variable);

                return $"{variable} = {assignment}";
            }
            case EX_Let let:
            {
                var assignment = GetLineExpression(let.Assignment);
                var variable = GetLineExpression(let.Variable);

                return $"{variable} = {assignment}";
            }
            case EX_LetBase letBase:
            {
                var assignment = GetLineExpression(letBase.Assignment);
                var variable = GetLineExpression(letBase.Variable);

                return $"{variable} = {assignment}";
            }
            case EX_Context context:
            {
                var function = context?.ContextExpression is not null ? GetLineExpression(context?.ContextExpression).SubstringAfter("::") : "failedplaceholder";
                var obj = context?.ObjectExpression is not null ? GetLineExpression(context?.ObjectExpression) : "failedplaceholder";

                var customStringBuilder = new CustomStringBuilder();
                if (expression is EX_Context_FailSilent)
                {
                    customStringBuilder.AppendLine($"if ({obj})");
                    customStringBuilder.IncreaseIndentation();
                }

                if (obj == "FindObject<UObject>(nullptr, this)" || obj.Contains("KismetArrayLibrary") || (!function.EndsWith("Map_Find") && obj.Contains("BlueprintMapLibrary")) || obj.Contains("BlueprintSetLibrary"))
                {
                    customStringBuilder.Append(function);
                }
                else
                {
                    customStringBuilder.Append($"{obj}->{function}");
                }

                return customStringBuilder.ToString();
            }
            case EX_FinalFunction final:
            {
                var parametersList = new List<string>(final.Parameters.Length);
                foreach (var parameter in final.Parameters)
                {
                    var prm = GetLineExpression(parameter);
                    if (!string.IsNullOrWhiteSpace(prm))
                    {
                        parametersList.Add(prm);
                    }
                }

                var parameters = string.Join(", ", parametersList);
                var stackNode = final.StackNode.ToString();
                var functionName = stackNode.SubstringAfter(':').Trim('\'');
                var className = stackNode.SubstringAfter('.').SubstringBefore(':');

                if (expression is EX_LocalFinalFunction) return $"{(stackNode.Contains("/Script/") ? $"{GetPrefix(className)}{className}::{functionName}" : functionName)}({parameters})";

                return FinalFunctionCleaner(className, functionName, parametersList, parameters);
            }
            case EX_VirtualFunction virtualFunc:
            {
                var parametersList = new List<string>(virtualFunc.Parameters.Length);
                foreach (var parameter in virtualFunc.Parameters)
                {
                    parametersList.Add(GetLineExpression(parameter));
                }

                var parameters = string.Join(", ", parametersList);
                var functionName = virtualFunc.VirtualFunctionName.Text;

                return $"{functionName}({parameters})";
            }
            case EX_TextConst textConst:
            {
                return textConst.Value.SourceString is null ? "nullptr" : GetLineExpression(textConst.Value.SourceString);
            }
            case EX_SetSet setSet:
            {
                var target = GetLineExpression(setSet.SetProperty);
                if (setSet.Elements.Length == 0)
                {
                    return $"{target} = TArray {{ }};";
                }

                var values = new List<string>(setSet.Elements.Length);
                foreach (var element in setSet.Elements)
                {
                    values.Add(GetLineExpression(element));
                }

                var joined = string.Join(", ", values);
                return $"{target} = TArray {{ {joined} }};";
            }
            case EX_SetConst setConst:
            {
                if (setConst.Elements.Length == 0)
                {
                    return "TArray { };";
                }

                var values = new List<string>(setConst.Elements.Length);
                foreach (var element in setConst.Elements)
                {
                    values.Add(GetLineExpression(element));
                }

                var joined = string.Join(", ", values);
                return $"TArray {{ {joined} }};";
            }
            case EX_ArrayConst constArray:
            {
                var values = new List<string>(constArray.Elements.Length);
                foreach (var element in constArray.Elements)
                {
                    values.Add(GetLineExpression(element));
                }

               // var arrayProp = constArray.InnerProperty.New.ResolvedOwner.Load<UArrayProperty>();
               // var objProp = arrayProp.Inner.Load<UObjectProperty>();
              //  return objProp.PropertyClass?.Name ?? "Unknown";

                return $"TArray<{constArray.InnerProperty}>({string.Join(", ", values)})";
            }
            case EX_SetArray setArray:
            {
                var variable = GetLineExpression(setArray.AssigningProperty);

                var values = new List<string>(setArray.Elements.Length);
                foreach (var element in setArray.Elements)
                {
                    values.Add(GetLineExpression(element));
                }

                return $"{variable} = {(values.Count > 0 ? "[ " + string.Join(", ", values) + " ]" : "[]")}";
            }
            case EX_IntConst intConst:
            {
                return intConst.Value.ToString();
            }
            case KismetExpression<byte> byteConst:
            {
                return $"0x{byteConst.Value:X}";
            }
            case EX_ObjectConst objectConst:
            {
                var pkgIndex = objectConst.Value.ToString();

                if (!string.IsNullOrEmpty(pkgIndex) && pkgIndex.Contains('\''))
                {
                    var parts = pkgIndex.Split('\'');
                    var typeName = parts[0];
                    var path = parts[1];

                    var classPkgType = $"U{typeName}";
                    return $"FindObject<{classPkgType}>(nullptr, \"{path}\")";
                }

                // package not found, sometimes it also is a "this"
                return $"FindObject<UObject>(nullptr, \"{pkgIndex}\")";
            }
            case EX_NameConst nameConst:
            {
                return $"\"{nameConst.Value.Text}\"";
            }
            case EX_Vector3fConst vectorF:
            {
                var value = vectorF.Value;
                return $"FVector3f({value.X}, {value.Y}, {value.Z})";
            }
            case EX_VectorConst vectorD:
            {
                var value = vectorD.Value;
                return $"FVector({value.X}, {value.Y}, {value.Z})";
            }
            case EX_TransformConst xf:
            {
                var value = xf.Value;
                return $"FTransform(FQuat({value.Rotation.X}, {value.Rotation.Y}, {value.Rotation.Z}, {value.Rotation.W}), FVector({value.Translation.X}, {value.Translation.Y}, {value.Translation.Z}), FVector({value.Scale3D.X}, {value.Scale3D.Y}, {value.Scale3D.Z}))";
            }
            case EX_Int64Const i64:
            {
                return i64.Value.ToString();
            }
            case EX_UInt64Const ui64:
            {
                return ui64.Value.ToString();
            }
            case EX_BitFieldConst bit:
            {
                return bit.ConstValue.ToString();
            }
            case KismetExpression<string> stringConst:
            {
                return $"\"{stringConst.Value.Replace("\r\n", "\\n").Replace("\n", "\\n")}\"";
            }
            case EX_InstanceDelegate del:
            {
                return $"\"{del.FunctionName}\"";
            }
            case EX_IntOne:
            {
                return "1";
            }
            case EX_IntZero:
            {
                return "0";
            }
            case EX_True:
            {
                return "true";
            }
            case EX_False:
            {
                return "false";
            }
            case EX_Self:
            {
                return "this";
            }
            case EX_Cast cast:
            {
                var target = GetLineExpression(cast.Target);
                var conversionType = cast.ConversionType switch
                {
                    ECastToken.CST_ObjectToBool or ECastToken.CST_ObjectToBool2 or ECastToken.CST_InterfaceToBool or ECastToken.CST_InterfaceToBool2 => "bool",
                    ECastToken.CST_DoubleToFloat => "float",
                    ECastToken.CST_FloatToDouble => "double",
                    ECastToken.CST_ObjectToInterface => "Interface",
                    _ => throw new NotImplementedException($"ConversionType {cast.ConversionType} is currently not implemented")
                };

                return $"Cast<{conversionType}>({target})";
            }
            case EX_PopExecutionFlowIfNot popExecutionFlowIfNot:
            {
                var booleanExpression = GetLineExpression(popExecutionFlowIfNot.BooleanExpression);

                var customStringBuilder = new CustomStringBuilder();
                customStringBuilder.AppendLine($"if (!{booleanExpression})");
                customStringBuilder.IncreaseIndentation();

                if (_executionFlowStack.Count == 0)
                {
                    customStringBuilder.Append("return");
                }
                else
                {
                    var target = _executionFlowStack.Pop();
                    customStringBuilder.Append($"goto Label_{target}");
                }

                return customStringBuilder.ToString();
            }
            case EX_PushExecutionFlow pushExecutionFlow:
            {
                var targetIndex = (int)pushExecutionFlow.PushingAddress;

                _executionFlowStack.Push(targetIndex);
                return "";
            }
            case EX_PopExecutionFlow:
            {
                if (_executionFlowStack.Count == 0)
                    return "return";

                var target = _executionFlowStack.Pop();
                return $"goto Label_{target}";
            }
            case EX_JumpIfNot jumpIfNot:
            {
                var booleanExpression = GetLineExpression(jumpIfNot.BooleanExpression);
                var customStringBuilder = new CustomStringBuilder();
                customStringBuilder.AppendLine($"if (!{booleanExpression})");
                customStringBuilder.IncreaseIndentation();
                var targetIndex = (int)jumpIfNot.CodeOffset;
                targetIndex = Array.FindIndex(Function.ScriptBytecode, stmt => stmt.StatementIndex == targetIndex);
                if (targetIndex >= 0 && targetIndex < Function.ScriptBytecode.Length && (Function.ScriptBytecode[targetIndex] is EX_Return || Function.ScriptBytecode[targetIndex++] is EX_Return))
                {
                    customStringBuilder.Append($"return");
                }
                else
                {
                    customStringBuilder.Append($"goto Label_{jumpIfNot.CodeOffset}");
                }

                return customStringBuilder.ToString();
            }
            case EX_Jump jump:
            {
                var targetIndex = (int)jump.CodeOffset;
                targetIndex = Array.FindIndex(Function.ScriptBytecode, stmt => stmt.StatementIndex == targetIndex);
                if (targetIndex >= 0 && targetIndex < Function.ScriptBytecode.Length && (Function.ScriptBytecode[targetIndex] is EX_Return || Function.ScriptBytecode[targetIndex++] is EX_Return))
                {
                    return "return";
                }

                return $"goto Label_{jump.CodeOffset}";
            }
            case EX_SkipOffsetConst skipOffsetConst:
            {
                return $"goto Label_{skipOffsetConst.Value}";
            }
            case EX_ComputedJump computedJump:
            {
                if (computedJump.CodeOffsetExpression is EX_VariableBase)
                {
                    return $"goto {GetLineExpression(computedJump.CodeOffsetExpression)}";
                }

                return GetLineExpression(computedJump.CodeOffsetExpression);
            }
            case EX_ArrayGetByRef arrayRef:
            {
                var arrayIndex = GetLineExpression(arrayRef.ArrayIndex);
                var arrayVariable = GetLineExpression(arrayRef.ArrayVariable);

                return $"{arrayVariable}[{arrayIndex}]";
            }
            case EX_InterfaceContext interfaceContext:
            {
                return GetLineExpression(interfaceContext.InterfaceValue);
            }
            case EX_NoInterface:
            case EX_NoObject:
            {
                return "nullptr";
            }
            case EX_Return returnExpr:
            {
                if (returnExpr.ReturnExpression.Token == EExprToken.EX_Nothing)
                {
                    return "return";
                }

                var value = GetLineExpression(returnExpr.ReturnExpression);
                return $"return {value}";
            }
            case EX_SoftObjectConst objectConst:
            {
                var value = GetLineExpression(objectConst.Value);
                return $"FSoftObjectPath({value})";
            }
            case EX_FieldPathConst fieldPathConst:
            {
                var value = GetLineExpression(fieldPathConst.Value);
                return value;
            }
            case EX_CastBase cast:
            {
                var variable = GetLineExpression(cast.Target);
                var classType = cast.ClassPtr.Name;

                string castFunc;
                switch (expression.Token)
                {
                    case EExprToken.EX_MetaCast:
                        castFunc = $"CastClass<{GetClassWithPrefix(cast.ClassPtr.Load<UStruct>())}>";
                        break;
                    case EExprToken.EX_DynamicCast:
                    case EExprToken.EX_CrossInterfaceCast:
                    case EExprToken.EX_InterfaceToObjCast:
                        castFunc = $"Cast<{GetClassWithPrefix(cast.ClassPtr.Load<UStruct>())}>";
                        break;
                    case EExprToken.EX_ObjToInterfaceCast:
                        castFunc = $"Cast<{GetClassWithPrefix(cast.ClassPtr.Load<UStruct>())}*>";
                        break;
                    default:
                        castFunc = $"Cast<{classType}>";
                        break;
                }

                return $"{castFunc}({variable})";
            }
            case EX_BindDelegate bindDelegate:
            {
                var delegateVar = GetLineExpression(bindDelegate.Delegate);
                var objectTerm = GetLineExpression(bindDelegate.ObjectTerm);
                var functionName = $"FName(\"{bindDelegate.FunctionName.Text}\")";

                return $"{delegateVar}->BindUFunction({objectTerm}, {functionName})";
            }
            case EX_StructConst structConst:
            {
                var properties = new List<string>(structConst.Properties.Length);
                foreach (var property in structConst.Properties)
                {
                    properties.Add(GetLineExpression(property));
                }

                if (structConst.Struct.Name == "LatentActionInfo") return properties[0]; // used for cleaning code output.

                return $"F{structConst.Struct.Name}({string.Join(", ", properties)})";
            }
            case EX_FloatConst floatConst:
            {
                return floatConst.Value.ToString(CultureInfo.CurrentCulture);
            }
            case EX_DoubleConst doubleConst:
            {
                return doubleConst.Value.ToString(CultureInfo.CurrentCulture);
            }
            case EX_AddMulticastDelegate multicastDelegate:
            {
                var delegatee = GetLineExpression(multicastDelegate.Delegate);
                var delegateToAdd = GetLineExpression(multicastDelegate.DelegateToAdd);

                return $"{delegatee}->Add({delegateToAdd})";
            }
            case EX_RotationConst rotationConst:
            {
                var pitch = rotationConst.Value.Pitch;
                var roll = rotationConst.Value.Roll;
                var yaw = rotationConst.Value.Yaw;

                return $"FRotator({pitch}, {roll}, {yaw})";
            }
            case EX_SetMap setMap:
            {
                var target = GetLineExpression(setMap.MapProperty);
                if (setMap.Elements.Length == 0)
                {
                    return $"{target} = TMap {{ }}";
                }

                var stringBuilder = new CustomStringBuilder();

                // todo: add inner type for TMap Set/Const
                //FortniteGame/Content/UI/InGame/HUD/WBP_QuickEditGrid.uasset
                //<{keyinnerType}, {valueinnerType}>
                stringBuilder.Append($"{target} = TMap {{ ");

                var elements = setMap.Elements;

                for (int i = 0; i < elements.Length; i += 2)
                {
                    var keyText = GetLineExpression(elements[i]);
                    var valueText = GetLineExpression(elements[i + 1]);

                    stringBuilder.Append($"{keyText}: {valueText}");

                    if (i + 2 < elements.Length)
                        stringBuilder.Append(", ");
                }

                stringBuilder.Append(" }");
                return stringBuilder.ToString();
            }
            case EX_MapConst mapConst:
            {
                if (mapConst.Elements.Length == 0)
                {
                    return "TMap { }";
                }

                var stringBuilder = new CustomStringBuilder();
                stringBuilder.Append("TMap { ");

                var elements = mapConst.Elements;

                for (int i = 0; i < elements.Length; i += 2)
                {
                    var keyText = GetLineExpression(elements[i]);
                    var valueText = GetLineExpression(elements[i + 1]);

                    stringBuilder.Append($"{keyText}: {valueText}");

                    if (i + 2 < elements.Length)
                        stringBuilder.Append(", ");
                }

                stringBuilder.Append(" }");
                return stringBuilder.ToString();
            }
            case EX_SwitchValue switchValue:
            {
                if (switchValue.Cases.Length == 2)
                {
                    var indexTerm = GetLineExpression(switchValue.IndexTerm);

                    var case0 = GetLineExpression(switchValue.Cases[0].CaseTerm);
                    var case1 = GetLineExpression(switchValue.Cases[1].CaseTerm);

                    return $"{indexTerm} ? {case1} : {case0}";
                }

                var stringBuilder = new CustomStringBuilder();
                stringBuilder.AppendLine($"switch ({GetLineExpression(switchValue.IndexTerm)})");
                stringBuilder.OpenBlock();

                foreach (var caseItem in switchValue.Cases)
                {
                    stringBuilder.AppendLine($"case {GetLineExpression(caseItem.CaseIndexValueTerm)}:");
                    stringBuilder.OpenBlock();

                    stringBuilder.AppendLine($"return {GetLineExpression(caseItem.CaseTerm)};");
                    stringBuilder.AppendLine("break;");

                    stringBuilder.CloseBlock("}\n");
                }

                stringBuilder.AppendLine("default:");
                stringBuilder.OpenBlock();

                stringBuilder.AppendLine($"return {GetLineExpression(switchValue.DefaultTerm)};");
                stringBuilder.AppendLine("break;");

                stringBuilder.CloseBlock("}\n");
                stringBuilder.CloseBlock();

                return stringBuilder.ToString();
            }
            case EX_StructMemberContext structMemberContext:
            {
                var property = structMemberContext.Property.ToString();
                var structExpression = GetLineExpression(structMemberContext.StructExpression);

                return $"{structExpression}.{property}";
            }
            case EX_CallMulticastDelegate callMulticastDelegate:
            {
                var parameters = new List<string>(callMulticastDelegate.Parameters.Length);
                foreach (var parameter in callMulticastDelegate.Parameters)
                {
                    parameters.Add(GetLineExpression(parameter));
                }

                var parametersString = string.Join(", ", parameters);

                var callDelegate = GetLineExpression(callMulticastDelegate.Delegate);
                return $"{callDelegate}->Broadcast({parametersString})";
            }
            case EX_RemoveMulticastDelegate removeMulticastDelegate:
            {
                var delegateExpr = removeMulticastDelegate.Delegate;
                var delegateTarget = GetLineExpression(delegateExpr);
                var delegateToRemove = GetLineExpression(removeMulticastDelegate.DelegateToAdd);

                var separator = delegateExpr.Token == EExprToken.EX_Context ? "->" : ".";

                return $"{delegateTarget}{separator}RemoveDelegate({delegateToRemove})";
            }
            case EX_ClearMulticastDelegate clearMulticastDelegate:
            {
                var delegateTarget = GetLineExpression(clearMulticastDelegate.DelegateToClear);
                return $"{delegateTarget}.Clear()";
            }
            case EX_PropertyConst propertyConst:
            {
                return propertyConst.Property.ToString();
            }
            case EX_WireTracepoint:
            case EX_Tracepoint:
            {
#if DEBUG
                return "throw std::runtime_error(\"TracePoint hit\");";
#endif
                return "";
            }
            case EX_Breakpoint:
            {
#if DEBUG
                return "breakpoint;";
#endif
                return "";
            }
            case EX_Nothing:
            case EX_NothingInt32:
            case EX_EndFunctionParms:
            case EX_EndStructConst:
            case EX_EndArray:
            case EX_EndArrayConst:
            case EX_EndSet:
            case EX_EndMap:
            case EX_EndMapConst:
            case EX_EndSetConst:
            case EX_EndOfScript:
            case EX_AutoRtfmStopTransact:
            case EX_AutoRtfmTransact:
            case EX_AutoRtfmAbortIfNot:
            {
                // added as sometimes it's throwing not supported
                return "";
            }

            // Custom Game Expressions
            case EX_FixedPointConst fp:
            {
                return fp.Value.ToString();
            }
            case EX_WuWaInstr1:
            case EX_WuWaInstr2:
            {
                return expression.ToString() ?? "";
            }
            case EX_DFInstr:
            case EX_DamageSourceContainer:
            case EX_GbxDefPtr:
            case EX_GameDataHandle:
                return "";
            /*
                EExprToken.EX_Assert
                EExprToken.EX_Skip
                EExprToken.EX_InstrumentationEvent
                EExprToken.EX_ClassContext it's like EX_Context
            */
            default:
                throw new NotImplementedException($"KismetExpression '{expression.GetType().Name}' is currently not supported");
        }
    }

    // FControlRigOperator::PropertyPath1/2 changed type at FControlRigObjectVersion.OperatorsStoringPropertyPaths
    // (UE 4.23/4.24): plain FString -> FCachedPropertyPath, a reflected USTRUCT (PropertyPathHelpers.h) with no
    // custom Serialize() override, storing TArray<FPropertyPathSegment> Segments { FName Name; int32 ArrayIndex }.
    // Handles both eras: try the old string field first, then reconstruct from Segments if it's the newer struct.
    private static string GetOperatorPropertyPath(IPropertyHolder op, string legacyFieldName, string cachedFieldName)
    {
        var asString = op.GetOrDefault(legacyFieldName, string.Empty);
        if (!string.IsNullOrEmpty(asString)) return asString;

        var segments = op.GetOrDefault<FStructFallback?>(cachedFieldName, null)?.GetOrDefault<FStructFallback[]>("Segments", []);
        if (segments is null || segments.Length == 0) return string.Empty;

        var parts = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var name = segment.GetOrDefault<FName>("Name", new FName()).Text;
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            var arrayIndex = segment.GetOrDefault("ArrayIndex", -1);
            parts.Add(arrayIndex >= 0 ? $"{name}[{arrayIndex}]" : name);
        }
        return string.Join(".", parts);
    }

    // The Operators stream moved between eras: on UE 4.19-4.22 it's a UPROPERTY on UControlRig, so the populated
    // copy serializes on the CDO. From 4.23 the compiler writes it to the generated class object instead
    // (UControlRigBlueprintGeneratedClass::Operators, see ControlRigBlueprintCompiler.cpp PostCompile), and the
    // CDO may still carry a stale copy whose paths are all empty. Prefer whichever copy actually resolves.
    private static FStructFallback[] SelectOperatorsSource(IPropertyHolder? owningClass, IPropertyHolder classDefaultObject)
    {
        var fromClass = owningClass?.GetOrDefault<FStructFallback[]>("Operators", []) ?? [];
        foreach (var op in fromClass)
        {
            if (!string.IsNullOrEmpty(GetOperatorPropertyPath(op, "PropertyPath1", "CachedPropertyPath1")) ||
                !string.IsNullOrEmpty(GetOperatorPropertyPath(op, "PropertyPath2", "CachedPropertyPath2")))
                return fromClass;
        }
        return classDefaultObject.GetOrDefault<FStructFallback[]>("Operators", []);
    }

    // Legacy (pre-RigVM) ControlRig assets don't compile their graph to Kismet bytecode at all: FuncMap is empty.
    // Instead the graph is baked into an "Operators" stream (TArray<FControlRigOperator>), a flat Copy/Exec
    // instruction list operating over property paths into per-node RigUnit struct properties on the class.
    // See Engine/Plugins/Experimental/ControlRig/Source/ControlRig/Public/ControlRigDefines.h (FControlRigOperator)
    // and ControlRig.h (UControlRig::Execute / ControlRigVM::Execute).
    public static string? DecompileControlRigOperators(IPropertyHolder? owningClass, IPropertyHolder? classDefaultObject, out HashSet<string> nodePropertyNames, out List<string> declarationOrder)
    {
        nodePropertyNames = [];
        declarationOrder = [];
        if (classDefaultObject is null) return null;

        var operators = SelectOperatorsSource(owningClass, classDefaultObject);
        if (operators.Length == 0) return null;

        // Node execution order, in first-seen order (dictionary preserves insertion order).
        var nodeTypes = new Dictionary<string, string>();
        var execOrder = new List<string>();
        // Every Copy op's target tells us exactly which node.pin receives the wire, regardless of
        // where in the stream the Copy happens to sit relative to that node's Exec.
        var incomingLinksByNode = new Dictionary<string, List<(string From, string ToPin)>>();
        var strayLinks = new List<(string From, string To)>();

        // Tracks whether ANY operator actually resolved to a usable path, as opposed to just having an empty
        // dictionary - a stream of e.g. all-Exec operators with unresolved paths still populates nodeTypes
        // (every one collapses onto the same "" key), so dictionary emptiness alone can't detect that failure.
        var hasAnyResolvedPath = false;

        foreach (var op in operators)
        {
            var opCode = op.GetOrDefault("OpCode", new FName("EControlRigOpCode::Invalid")).Text.SubstringAfter("::");
            var path1 = GetOperatorPropertyPath(op, "PropertyPath1", "CachedPropertyPath1");
            var path2 = GetOperatorPropertyPath(op, "PropertyPath2", "CachedPropertyPath2");
            if (!string.IsNullOrEmpty(path1) || !string.IsNullOrEmpty(path2))
                hasAnyResolvedPath = true;

            switch (opCode)
            {
                case "Copy":
                {
                    var targetNode = path2.SubstringBefore('.');
                    var targetPin = path2.Contains('.') ? path2.SubstringAfter('.') : null;

                    if (targetPin is null || string.IsNullOrEmpty(targetNode))
                    {
                        strayLinks.Add((path1, path2));
                        break;
                    }

                    if (!incomingLinksByNode.TryGetValue(targetNode, out var list))
                        incomingLinksByNode[targetNode] = list = [];
                    list.Add((path1, targetPin));
                    break;
                }
                case "Exec":
                {
                    var nodeName = path1;
                    if (string.IsNullOrEmpty(nodeName))
                    {
                        strayLinks.Add(("<unresolved Exec target>", path2));
                        break;
                    }

                    if (!nodeTypes.ContainsKey(nodeName))
                    {
                        var rigUnitStructName = classDefaultObject
                            .GetOrDefault<FStructFallback?>(nodeName, null)
                            ?.GetOrDefault("RigUnitStructName", new FName()).Text;

                        if (string.IsNullOrEmpty(rigUnitStructName) || rigUnitStructName == "None")
                        {
                            // Some units never fill RigUnitStructName (e.g. RigUnit_BeginExecution on 4.23-era
                            // rigs) - fall back to the struct type recorded on the node's own property tag.
                            rigUnitStructName = classDefaultObject.Properties
                                .FirstOrDefault(p => p.Name.Text == nodeName)?.TagData?.StructType;
                        }

                        nodeTypes[nodeName] = string.IsNullOrEmpty(rigUnitStructName) || rigUnitStructName == "None"
                            ? "?"
                            : rigUnitStructName;
                    }
                    execOrder.Add(nodeName);
                    break;
                }
                case "Done":
                    break;
                default:
                    strayLinks.Add(($"<unhandled opcode '{opCode}'> {path1}", path2));
                    break;
            }
        }

        // Safety net: if no operator resolved to a usable path (both the class copy and the CDO copy were
        // empty), bail out cleanly rather than emit a graph made entirely of blank node names and unresolved
        // links. The caller falls back to showing the class's raw properties, same as if there were no
        // Operators stream at all.
        if (operators.Length > 0 && !hasAnyResolvedPath)
        {
            Log.Warning("ControlRig Operators stream had {Count} entries but none resolved to a node - no usable property paths in either the class or CDO copy", operators.Length);
            return null;
        }

        nodePropertyNames = new HashSet<string>(nodeTypes.Keys);
        foreach (var nodeName in incomingLinksByNode.Keys)
            nodePropertyNames.Add(nodeName);

        // A handful of pins (almost always HierarchyRef <- the single rig-wide hierarchy variable) are wired
        // identically on nearly every node. Hoist whichever source wins a strict majority for a given pin name
        // into one shared line instead of repeating it on every node; only genuine per-node overrides stay inline.
        var pinSourceCounts = new Dictionary<string, Dictionary<string, int>>();
        foreach (var links in incomingLinksByNode.Values)
        foreach (var (from, toPin) in links)
        {
            if (!pinSourceCounts.TryGetValue(toPin, out var sources))
                pinSourceCounts[toPin] = sources = [];
            sources[from] = sources.GetValueOrDefault(from) + 1;
        }

        var defaultSourceForPin = new Dictionary<string, string>();
        foreach (var (toPin, sources) in pinSourceCounts)
        {
            var total = sources.Values.Sum();
            var (bestFrom, bestCount) = sources.MaxBy(kv => kv.Value);
            if (bestCount >= 2 && bestCount * 2 > total)
                defaultSourceForPin[toPin] = bestFrom;
        }

        // Full render order: exec'd nodes first (in exec order), then any node that only ever receives a
        // wire but is never Exec'd on its own (pure data pass-through, or couldn't be resolved to a pin).
        var orderedNodeNames = new List<string>();
        var seenNodes = new HashSet<string>();
        foreach (var nodeName in execOrder)
            if (seenNodes.Add(nodeName)) orderedNodeNames.Add(nodeName);
        foreach (var nodeName in incomingLinksByNode.Keys)
            if (seenNodes.Add(nodeName)) orderedNodeNames.Add(nodeName);

        // Broader "first use" order for declaring member variables: an external source referenced by a
        // node's wiring (e.g. the rig-wide hierarchy variable feeding every HierarchyRef pin) is declared
        // right before the first node that actually reads it, rather than wherever it happened to serialize.
        var declared = new HashSet<string>();
        foreach (var nodeName in orderedNodeNames)
        {
            if (incomingLinksByNode.TryGetValue(nodeName, out var nodeLinks))
            {
                foreach (var (from, _) in nodeLinks)
                {
                    var root = from.SubstringBefore('.');
                    if (!string.IsNullOrEmpty(root) && !nodePropertyNames.Contains(root) && declared.Add(root))
                        declarationOrder.Add(root);
                }
            }
            if (declared.Add(nodeName))
                declarationOrder.Add(nodeName);
        }

        // Gather each node's unwired literal field values up front (needed for both per-node rendering and
        // the per-type default hoisting below).
        var literalFieldsByNode = new Dictionary<string, Dictionary<string, string>>();
        foreach (var nodeName in orderedNodeNames)
        {
            var wiredPins = incomingLinksByNode.TryGetValue(nodeName, out var links) ? links.Select(l => l.ToPin).ToHashSet() : [];
            var fields = new Dictionary<string, string>();
            var nodeStruct = classDefaultObject.GetOrDefault<FStructFallback?>(nodeName, null);
            if (nodeStruct != null)
            {
                foreach (var property in nodeStruct.Properties)
                {
                    var propName = property.Name.Text;
                    if (propName is "RigUnitName" or "RigUnitStructName" or "ExecutionType") continue;
                    if (wiredPins.Contains(propName)) continue;

                    var formatted = FormatLiteralPinValue(property);
                    if (formatted != null) fields[propName] = formatted;
                }
            }
            literalFieldsByNode[nodeName] = fields;
        }

        // Many nodes of the same RigUnit type share identical config (e.g. every RigUnit_ApplyFK defaults to
        // ApplyTransformMode=Override). Hoist whichever value wins a strict majority per (type, field) into a
        // one-time header instead of repeating it on every node of that type.
        var fieldValueCountsByType = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();
        var instanceCountByType = new Dictionary<string, int>();
        foreach (var nodeName in orderedNodeNames)
        {
            if (!nodeTypes.TryGetValue(nodeName, out var type)) continue;
            instanceCountByType[type] = instanceCountByType.GetValueOrDefault(type) + 1;
            if (!fieldValueCountsByType.TryGetValue(type, out var fieldCounts))
                fieldValueCountsByType[type] = fieldCounts = [];
            foreach (var (field, value) in literalFieldsByNode[nodeName])
            {
                if (!fieldCounts.TryGetValue(field, out var valueCounts))
                    fieldCounts[field] = valueCounts = [];
                valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
            }
        }

        var defaultFieldsByType = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (type, fieldCounts) in fieldValueCountsByType)
        {
            if (instanceCountByType[type] < 2) continue; // nothing to hoist for a one-off node type
            foreach (var (field, valueCounts) in fieldCounts)
            {
                var total = valueCounts.Values.Sum();
                var (bestValue, bestCount) = valueCounts.MaxBy(kv => kv.Value);
                if (bestCount >= 2 && bestCount * 2 > total)
                {
                    if (!defaultFieldsByType.TryGetValue(type, out var defaults))
                        defaultFieldsByType[type] = defaults = [];
                    defaults[field] = bestValue;
                }
            }
        }

        // Rendered as real statements (assignment, member access, function calls) rather than arrow/bracket
        // notation, so the C++ syntax highlighter tokenizes it correctly instead of mangling it; only the
        // per-node/per-type annotations are "//" comments, same as any other decompiled code would use.
        var stringBuilder = new CustomStringBuilder();
        stringBuilder.AppendLine("// Decompiled ControlRig graph (legacy operator stream, pre-RigVM).");
        stringBuilder.AppendLine("// This system has no pure/impure node split like modern RigVM: every RigUnit below, including");
        stringBuilder.AppendLine("// plain getters like RigUnit_GetJointTransform, is explicitly exec'd - that's what .Execute() calls.");
        foreach (var (type, defaults) in defaultFieldsByType)
            stringBuilder.AppendLine($"// Defaults - {type}: {string.Join(", ", defaults.Select(kv => $"{kv.Key} = {kv.Value}"))}");
        foreach (var (toPin, from) in defaultSourceForPin)
            stringBuilder.AppendLine($"// Unless overridden below: *.{toPin} = {from};");
        stringBuilder.AppendLine("void Execute()");
        stringBuilder.OpenBlock();

        var step = 1;
        foreach (var nodeName in orderedNodeNames)
        {
            var hasExecIndex = nodeTypes.TryGetValue(nodeName, out var typeLabel);
            typeLabel ??= "not directly executed";
            var typeDefaults = hasExecIndex && defaultFieldsByType.TryGetValue(typeLabel, out var d) ? d : null;

            var literalParts = new List<string>();
            foreach (var (field, value) in literalFieldsByNode[nodeName])
            {
                if (typeDefaults != null && typeDefaults.TryGetValue(field, out var defaultValue) && defaultValue == value)
                    continue;
                literalParts.Add($"{field} = {value}");
            }

            var comment = hasExecIndex ? $"// [{step}] {nodeName} ({typeLabel})" : $"// {nodeName} ({typeLabel}, wiring only)";
            if (literalParts.Count > 0)
                comment += $": {string.Join(", ", literalParts)}";
            stringBuilder.AppendLine(comment);

            incomingLinksByNode.TryGetValue(nodeName, out var links);
            var relevantLinks = links?.Where(l => !(defaultSourceForPin.TryGetValue(l.ToPin, out var defaultFrom) && defaultFrom == l.From)).ToList();
            if (relevantLinks is { Count: > 0 })
            {
                foreach (var (from, toPin) in relevantLinks)
                    stringBuilder.AppendLine($"{nodeName}.{toPin} = {from};");
            }

            if (hasExecIndex)
            {
                stringBuilder.AppendLine($"{nodeName}.Execute();");
                stringBuilder.AppendLine();
                step++;
            }
            else
            {
                stringBuilder.AppendLine();
            }
        }

        if (strayLinks.Count > 0)
        {
            stringBuilder.AppendLine("// Unresolved links:");
            foreach (var (from, to) in strayLinks)
                stringBuilder.AppendLine($"// {from} -> {to}");
        }

        stringBuilder.CloseBlock();
        return stringBuilder.ToString();
    }

    // "Output" and "Result" are always baked/computed at bake time (never authored), so they're excluded even
    // though they're transform-shaped - showing them would just reintroduce the noise this exists to remove.
    private static readonly HashSet<string> _computedTransformFieldNames = ["Output", "Result"];

    private static string? FormatLiteralPinValue(FPropertyTag property)
    {
        var value = property.Tag?.GenericValue;
        var scalar = value switch
        {
            FName { IsNone: false } fname => fname.Text.Contains("::") ? fname.Text.SubstringAfterLast("::") : fname.Text,
            string s when !string.IsNullOrEmpty(s) => s,
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double => value.ToString(),
            _ => null
        };
        if (scalar != null) return scalar;

        // Struct-typed fields (Filter, HierarchyRef, Output, Result, ...) are boilerplate or computed runtime
        // state and stay hidden - except a non-identity transform offset (e.g. BaseTransform), which is real
        // authored data and would otherwise vanish with no trace.
        if (_computedTransformFieldNames.Contains(property.Name.Text))
            return null;

        return property.Tag?.GetValue(typeof(FStructFallback)) is FStructFallback structValue
            ? FormatIfNonIdentityTransform(structValue)
            : null;
    }

    private static string? FormatIfNonIdentityTransform(FStructFallback structValue)
    {
        var rotation = structValue.GetOrDefault<FQuat?>("Rotation", null);
        var translation = structValue.GetOrDefault<FVector?>("Translation", null) ?? structValue.GetOrDefault<FVector?>("Location", null);
        var scale = structValue.GetOrDefault<FVector?>("Scale3D", null) ?? structValue.GetOrDefault<FVector?>("Scale", null);
        if (rotation is null && translation is null && scale is null) return null;

        var parts = new List<string>();
        if (translation is { } loc && (loc.X != 0 || loc.Y != 0 || loc.Z != 0))
            parts.Add($"loc=({loc.X:0.###}, {loc.Y:0.###}, {loc.Z:0.###})");
        if (rotation is { } rot && (rot.X != 0 || rot.Y != 0 || rot.Z != 0 || rot.W != 1))
            parts.Add($"rot=({rot.X:0.###}, {rot.Y:0.###}, {rot.Z:0.###}, {rot.W:0.###})");
        if (scale is { } scl && (scl.X != 1 || scl.Y != 1 || scl.Z != 1))
            parts.Add($"scale=({scl.X:0.###}, {scl.Y:0.###}, {scl.Z:0.###})");

        return parts.Count > 0 ? $"{{{string.Join(", ", parts)}}}" : null;
    }

    // From SwitchedToRigVM (UE 4.25) the graph no longer bakes to a FControlRigOperator stream: it compiles
    // to RigVM bytecode on a URigVM object (serialized inline on the generated class and as a "VM" export).
    // Work/literal memory registers are named "<Node>.<Pin>", so the instruction stream fully describes the
    // original graph: Execute ops are node invocations, Copy ops are pin wires, literal registers hold the
    // authored pin values. See Engine/Source/Runtime/RigVM/Public/RigVMCore/RigVMByteCode.h.
    public static string? DecompileRigVMByteCode(UClass uClass, out HashSet<string> suppressedProperties, out List<string> declarationOrder)
    {
        // Aliased so the per-instruction writer below can reach them: a local function cannot capture an out
        // parameter, and both are reference types, so the caller sees every addition made through the alias.
        var suppressed = new HashSet<string>();
        var declared = new List<string>();
        suppressedProperties = suppressed;
        declarationOrder = declared;

        if (uClass is not URigVMBlueprintGeneratedClass { VM: { } vm }) return null;
        if (vm.ByteCodeStorage is not { Instructions.Count: > 0 } byteCode) return null;

        var storage = RigVMStorage.Resolve(uClass, vm);
        var functionNames = vm.FunctionNamesStorage ?? [];
        if (storage is null) return null;

        foreach (var name in new[] { "VM", "Hierarchy", "HierarchyContainer", "DrawContainer", "DynamicHierarchy" })
            suppressed.Add(name);

        string FormatOperand(FRigVMOperand operand) => storage.FormatOperand(operand);

        var stringBuilder = new CustomStringBuilder();
        stringBuilder.AppendLine("// Decompiled ControlRig graph (RigVM bytecode, UE 4.25+).");
        stringBuilder.AppendLine("// Registers are named <Node>.<Pin>; each Execute() runs one rig unit, cross-node arguments show");
        stringBuilder.AppendLine("// the pin wiring, and plain assignments are the VM's explicit copy instructions. Authored constants");
        stringBuilder.AppendLine("// (from literal memory) are shown on each node's comment line - the compiler dedupes identical");
        stringBuilder.AppendLine("// constants across nodes, so a label may carry the name of the first pin that used the value.");
        stringBuilder.AppendLine("// Execute() arguments are the instruction's operands in the unit's own pin order (the shared");
        stringBuilder.AppendLine("// ExecuteContext is omitted): a bare name is one of this node's pins, anything else is the value");
        stringBuilder.AppendLine("// wired into that position. E.g. RigVMDispatch_If takes (Condition, True, False, Result).");
        stringBuilder.AppendLine("void Execute()");
        stringBuilder.OpenBlock();

        // Registers already touched by an earlier instruction. A unit's own output register is written here for
        // the first time, which is what separates "this node" from an upstream node of the same unit type
        // feeding one of its inputs (e.g. two chained MathTransformMakeRelative nodes).
        var writtenRegisters = new HashSet<(ERigVMMemoryType, ushort)>();

        var step = 1;

        // A pin marked lazy is compiled into a block of its own, parked after the main body and reached only
        // through a RunInstructions op. Walking the instructions in order would print such a block at the end,
        // long after the node that consumes its result - so the block's instructions are held back here and
        // written out where the op actually runs them.
        var lazyBlockInstructions = new HashSet<int>();
        foreach (var instruction in byteCode.Instructions)
        {
            if (instruction is not FRigVMRunInstructionsOp lazyOp) continue;
            for (var lazy = lazyOp.StartInstruction; lazy <= lazyOp.EndInstruction; lazy++)
                lazyBlockInstructions.Add(lazy);
        }

        var emittedLazyBlocks = new HashSet<(int Start, int End)>();

        void EmitInstruction(int index)
        {
            switch (byteCode.Instructions[index])
            {
                case FRigVMExecuteOp executeOp:
                {
                    var functionName = executeOp.FunctionIndex < functionNames.Length
                        ? functionNames[executeOp.FunctionIndex].Text
                        : $"UnknownFunction_{executeOp.FunctionIndex}";
                    var unitType = functionName.SubstringBefore("::").TrimStart('F');
                    // The node-name stem drops the family prefix: a "DISPATCH_RigVMDispatch_If" instruction
                    // belongs to a node the editor simply calls "If".
                    var unitShortName = unitType
                        .SubstringAfter("RigUnit_")
                        .SubstringAfter("RigVMFunction_")
                        .SubstringAfter("DISPATCH_RigVMDispatch_")
                        .SubstringAfter("RigVMDispatch_");

                    // The node instance name is the "<Node>." prefix of the arguments' register names, restricted
                    // to prefixes whose stem matches this instruction's unit type (arguments referencing another
                    // node's output pin carry that node's prefix instead). Where several still match - two chained
                    // nodes of the same unit type - the node is the one whose register this instruction writes,
                    // i.e. the one not already produced upstream.
                    string? nodeName = null;
                    var freshPrefixes = new List<string>();
                    var allPrefixes = new List<string>();
                    foreach (var argument in executeOp.Arguments)
                    {
                        var registerName = storage.GetRegisterName(argument);
                        if (registerName is null || !registerName.Contains('.')) continue;
                        var prefix = registerName.SubstringBefore('.');

                        // A node authored inside a function keeps that function's scope in its name
                        // ("Deform_UpperArm_FNC_ParentConstraint_3"), so the unit type it ends with is what
                        // identifies it - anchoring at the start would only ever match the top-level graph.
                        if (!prefix.TrimEnd("_0123456789".ToCharArray()).EndsWith(unitShortName, StringComparison.Ordinal)) continue;
                        allPrefixes.Add(prefix);
                        if (argument.MemoryType == ERigVMMemoryType.Work && !writtenRegisters.Contains((argument.MemoryType, argument.RegisterIndex)))
                            freshPrefixes.Add(prefix);
                    }
                    nodeName = freshPrefixes.FirstOrDefault() ?? allPrefixes.FirstOrDefault() ?? $"{unitShortName}_{step}";

                    var bulkLiterals = new List<string>();
                    // Each operand as it will be written, paired with the pin it feeds where that is knowable.
                    var callArguments = new List<(string Text, string? Pin)>();
                    foreach (var argument in executeOp.Arguments)
                    {
                        var registerName = storage.GetRegisterName(argument);
                        if (registerName is null) continue;
                        if (registerName == "ExecuteContext") continue;

                        if (argument.MemoryType == ERigVMMemoryType.Literal)
                        {
                            var pin = registerName.Contains('.') ? registerName.SubstringAfter('.') : registerName;
                            var literalValue = storage.FormatLiteralValue(argument, pin);

                            // The compiler shares one register between every pin holding the same constant, so
                            // its name may belong to an unrelated node and is only trustworthy for this node's own.
                            var ownPin = !registerName.Contains('.') || registerName.StartsWith(nodeName + '.', StringComparison.Ordinal)
                                ? pin
                                : null;

                            // A value too large to sit in the argument list (a bone list, say) becomes a real
                            // assignment above the call, so nothing is summarised out of the graph.
                            if (literalValue.Contains('\n'))
                            {
                                var assignment = $"{nodeName}.{pin} = {literalValue};";
                                if (!bulkLiterals.Contains(assignment)) bulkLiterals.Add(assignment);
                                callArguments.Add((pin, ownPin));
                                continue;
                            }

                            callArguments.Add((literalValue, ownPin));
                        }
                        else if (registerName.StartsWith(nodeName + '.', StringComparison.Ordinal))
                        {
                            // One of this node's own pins - name it, so its position in the call is readable.
                            var pin = registerName.SubstringAfter('.');
                            callArguments.Add((pin, pin));
                        }
                        else
                        {
                            // A foreign node's register used directly as an argument = a wire into this node.
                            callArguments.Add((FormatOperand(argument), null));
                        }
                    }

                    foreach (var argument in executeOp.Arguments)
                        writtenRegisters.Add((argument.MemoryType, argument.RegisterIndex));

                    if (suppressed.Add(nodeName)) declared.Add(nodeName);

                    stringBuilder.AppendLine($"// [{step}] {nodeName} ({unitType})");
                    foreach (var assignment in bulkLiterals) stringBuilder.AppendLine(assignment);
                    AppendCall(stringBuilder, nodeName, callArguments);
                    stringBuilder.AppendLine();
                    step++;
                    break;
                }
                case FRigVMCopyOp copyOp:
                {
                    var source = copyOp.Source.MemoryType == ERigVMMemoryType.Literal
                        ? storage.FormatLiteralValue(copyOp.Source, null)
                        : FormatOperand(copyOp.Source);
                    stringBuilder.AppendLine($"{FormatOperand(copyOp.Target)} = {source};");
                    stringBuilder.AppendLine();
                    writtenRegisters.Add((copyOp.Target.MemoryType, copyOp.Target.RegisterIndex));
                    break;
                }
                case FRigVMUnaryOp unaryOp:
                {
                    var target = FormatOperand(unaryOp.Arg);
                    var statement = unaryOp.OpCode switch
                    {
                        ERigVMOpCode.Zero => $"{target} = 0;",
                        ERigVMOpCode.BoolFalse => $"{target} = false;",
                        ERigVMOpCode.BoolTrue => $"{target} = true;",
                        ERigVMOpCode.Increment => $"{target}++;",
                        ERigVMOpCode.Decrement => $"{target}--;",
                        _ => $"// {unaryOp.OpCode} {target}"
                    };
                    stringBuilder.AppendLine(statement);
                    break;
                }
                case FRigVMComparisonOp comparisonOp:
                    stringBuilder.AppendLine($"{FormatOperand(comparisonOp.Result)} = {FormatOperand(comparisonOp.A)} {(comparisonOp.OpCode == ERigVMOpCode.Equals ? "==" : "!=")} {FormatOperand(comparisonOp.B)};");
                    break;
                case FRigVMJumpOp jumpOp:
                    stringBuilder.AppendLine($"// {jumpOp.OpCode} -> instruction {jumpOp.InstructionIndex}");
                    break;
                case FRigVMJumpIfOp jumpIfOp:
                    stringBuilder.AppendLine($"// {jumpIfOp.OpCode} -> instruction {jumpIfOp.InstructionIndex} if {FormatOperand(jumpIfOp.Arg)} == {(jumpIfOp.Condition ? "true" : "false")}");
                    break;
                case FRigVMInvokeEntryOp invokeEntryOp:
                    stringBuilder.AppendLine($"InvokeEntry(\"{invokeEntryOp.EntryName}\");");
                    break;
                case FRigVMBaseOp { OpCode: ERigVMOpCode.Exit }:
                    stringBuilder.AppendLine("return;");
                    break;
                case FRigVMBaseOp baseOp:
                    stringBuilder.AppendLine($"// {baseOp.OpCode}");
                    break;
                case FRigVMRunInstructionsOp runOp:
                {
                    var target = FormatOperand(runOp.Arg);

                    // An empty range is the compiler saying the value is already up to date at this point.
                    if (runOp.EndInstruction < runOp.StartInstruction)
                    {
                        stringBuilder.AppendLine($"// {target} is already computed here");
                        break;
                    }

                    // Several nodes can depend on the same lazy block; it is written out once, at the first
                    // node that needs it, and referred back to afterwards rather than repeated.
                    if (!emittedLazyBlocks.Add((runOp.StartInstruction, runOp.EndInstruction)))
                    {
                        stringBuilder.AppendLine($"// runs the block computing {target} again (written out above)");
                        break;
                    }

                    stringBuilder.AppendLine($"// {target} is computed on demand here:");
                    stringBuilder.IncreaseIndentation();
                    for (var lazy = runOp.StartInstruction; lazy <= runOp.EndInstruction && lazy < byteCode.Instructions.Count; lazy++)
                        EmitInstruction(lazy);
                    stringBuilder.DecreaseIndentation();
                    break;
                }
                case FRigVMJumpToBranchOp branchOp:
                    stringBuilder.AppendLine($"// JumpToBranch on {FormatOperand(branchOp.Arg)} (branch table from {branchOp.FirstBranchInfoIndex})");
                    break;
                case FRigVMTernaryOp ternaryOp:
                    stringBuilder.AppendLine($"// {ternaryOp.OpCode}({FormatOperand(ternaryOp.ArgA)}, {FormatOperand(ternaryOp.ArgB)}, {FormatOperand(ternaryOp.ArgC)})");
                    break;
                case FRigVMBinaryOp binaryOp:
                    stringBuilder.AppendLine($"// {binaryOp.OpCode}({FormatOperand(binaryOp.ArgA)}, {FormatOperand(binaryOp.ArgB)})");
                    break;
                default:
                    stringBuilder.AppendLine($"// <{byteCode.Instructions[index].GetType().Name}>");
                    break;
            }
        }

        for (var index = 0; index < byteCode.Instructions.Count; index++)
        {
            if (lazyBlockInstructions.Contains(index)) continue;
            EmitInstruction(index);
        }

        stringBuilder.CloseBlock();
        return stringBuilder.ToString();
    }

    /// <summary>
    /// Writes a rig unit call, breaking it over several lines once it stops being readable on one. Each
    /// argument then gets its own line tagged with the pin it feeds, which is the only place that mapping is
    /// recorded - the bytecode itself only carries position.
    /// </summary>
    private static void AppendCall(CustomStringBuilder stringBuilder, string nodeName, List<(string Text, string? Pin)> arguments)
    {
        const int singleLineBudget = 110;

        var singleLine = $"{nodeName}.Execute({string.Join(", ", arguments.Select(argument => argument.Text))});";
        if (arguments.Count <= 1 || singleLine.Length <= singleLineBudget)
        {
            stringBuilder.AppendLine(singleLine);
            return;
        }

        stringBuilder.AppendLine($"{nodeName}.Execute(");
        stringBuilder.IncreaseIndentation();
        var labelled = new HashSet<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var (text, pin) = arguments[i];
            var separator = i < arguments.Count - 1 ? "," : "";

            // Drop the label when it says nothing the argument doesn't, and when it has already been used:
            // a constant shared between two pins carries only the first one's name, so repeating it here
            // would attribute the value to the wrong pin.
            if (pin is not null && (pin == text || !labelled.Add(pin))) pin = null;

            // A value that kept its own layout is written out as it stands, with the pin it feeds on its
            // opening line - the separator belongs after the closing brace, where it would otherwise land
            // in the middle of the value.
            if (text.Contains('\n'))
            {
                var lines = text.Split('\n');
                if (pin is not null) lines[0] = lines[0].TrimEnd() + $" // {pin}";
                lines[^1] = lines[^1].TrimEnd() + separator;

                stringBuilder.AppendLine(string.Join('\n', lines));
                continue;
            }

            stringBuilder.AppendLine(pin is null ? $"{text}{separator}" : $"{text}{separator} // {pin}");
        }
        stringBuilder.DecreaseIndentation();
        stringBuilder.AppendLine(");");
    }
}
