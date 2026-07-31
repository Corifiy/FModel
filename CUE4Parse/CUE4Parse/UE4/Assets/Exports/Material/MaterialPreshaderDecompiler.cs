using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace CUE4Parse.UE4.Assets.Exports.Material;

/// <summary>
/// Opcodes of a UE 4.25-4.27 material preshader (EMaterialPreshaderOpcode, MaterialShared.h:213 in
/// 4.26 - byte-identical in 4.25 and 4.27, verified against all three engine sources). UE5 rewrote
/// both the opcode set and the operand encoding, so this enum is deliberately not used there.
/// </summary>
public enum EMaterialPreshaderOpcode : byte
{
    Nop,
    ConstantZero,
    Constant,
    ScalarParameter,
    VectorParameter,
    Add,
    Sub,
    Mul,
    Div,
    Fmod,
    Min,
    Max,
    Clamp,
    Sin,
    Cos,
    Tan,
    Asin,
    Acos,
    Atan,
    Atan2,
    Dot,
    Cross,
    Sqrt,
    Length,
    Saturate,
    Abs,
    Floor,
    Ceil,
    Round,
    Trunc,
    Sign,
    Frac,
    Fractional,
    Log2,
    Log10,
    ComponentSwizzle,
    AppendVector,
    TextureSize,
    TexelSize,
    ExternalTextureCoordinateScaleRotation,
    ExternalTextureCoordinateOffset,
    RuntimeVirtualTextureUniform,
}

/// <summary>
/// Decompiles a UE 4.25+ material preshader back into readable pseudocode.
///
/// <para>
/// 4.25 replaced the pre-4.25 symbolic <see cref="FMaterialUniformExpressionLegacy"/> tree with a
/// flat stack machine: every CPU-folded uniform value (the vector/scalar rows of the Material
/// constant buffer) is one opcode range inside a single shared
/// <see cref="FMaterialPreshaderData"/> byte array. This walks that range exactly the way
/// EvaluatePreshader (MaterialUniformExpressions.cpp:956) does - same opcodes, same operand widths -
/// but pushes printed expressions instead of FLinearColors, so the result is the authored math
/// rather than the value it folds to on one particular frame.
/// </para>
/// <para>
/// The parameter opcodes carry an index into the expression set's own
/// UniformScalarParameters/UniformVectorParameters, which serialize the real parameter name and the
/// base material's default - so, unlike the legacy tree, names come straight from the cook and are
/// never inferred.
/// </para>
/// </summary>
public static class MaterialPreshaderDecompiler
{
    /// <summary>
    /// True when this game's preshader stream matches the 4.25-4.27 encoding this decoder implements.
    /// UE5's opcode set, operand widths and name encoding all differ; it is reported as unsupported
    /// instead of being decoded into something plausible-looking but wrong.
    /// </summary>
    public static bool IsSupported(EGame game) => game is >= EGame.GAME_UE4_26 and < EGame.GAME_UE5_0;

    /// <summary>
    /// Decompiles one preshader range. Returns null when the range is empty or the stream runs out
    /// mid-instruction (a format mismatch), so callers can fall back rather than print garbage.
    /// </summary>
    public static string? Decompile(FUniformExpressionSet expressionSet, FMaterialUniformPreshaderHeader header,
        EGame game, IReadOnlyList<UTexture?>? referencedTextures = null,
        MaterialShaderDecompiler.InstanceParameterOverrides? overrides = null)
    {
        if (!IsSupported(game)) return null;
        var data = expressionSet.UniformPreshaderData?.Data;
        if (data == null || header.OpcodeSize == 0) return null;

        var start = (int) header.OpcodeOffset;
        var end = start + (int) header.OpcodeSize;
        if (start < 0 || end > data.Length) return null;

        try
        {
            var ctx = new Context(expressionSet, data, end, referencedTextures, overrides);
            var stack = new List<string>();
            while (ctx.Position < end)
                Step(ctx, stack);
            return stack.Count > 0 ? stack[^1] : null;
        }
        catch (Exception)
        {
            // A desync means this build's stream isn't the encoding above - say nothing rather than
            // print a half-decoded expression as if it were the material's real math.
            return null;
        }
    }

    private sealed class Context(FUniformExpressionSet expressionSet, byte[] data, int end,
        IReadOnlyList<UTexture?>? referencedTextures, MaterialShaderDecompiler.InstanceParameterOverrides? overrides)
    {
        public readonly FUniformExpressionSet ExpressionSet = expressionSet;
        public readonly IReadOnlyList<UTexture?>? ReferencedTextures = referencedTextures;
        public readonly MaterialShaderDecompiler.InstanceParameterOverrides? Overrides = overrides;
        private readonly byte[] _data = data;
        private readonly int _end = end;
        public int Position;

        private void Require(int count)
        {
            if (Position + count > _end) throw new EndOfStreamException("preshader stream ended mid-instruction");
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[Position++];
        }

        public ushort ReadUInt16()
        {
            Require(2);
            var value = BitConverter.ToUInt16(_data, Position);
            Position += 2;
            return value;
        }

        public int ReadInt32()
        {
            Require(4);
            var value = BitConverter.ToInt32(_data, Position);
            Position += 4;
            return value;
        }

        public float ReadSingle()
        {
            Require(4);
            var value = BitConverter.ToSingle(_data, Position);
            Position += 4;
            return value;
        }

        public FLinearColor ReadLinearColor()
        {
            var r = ReadSingle();
            var g = ReadSingle();
            var b = ReadSingle();
            return new FLinearColor(r, g, b, ReadSingle());
        }

        public FGuid ReadGuid() => new((uint) ReadInt32(), (uint) ReadInt32(), (uint) ReadInt32(), (uint) ReadInt32());

        /// <summary>
        /// Names are interned: the stream carries a uint16 index into the preshader's own Names array
        /// (FMaterialPreshaderData::WriteName, MaterialUniformExpressions.cpp:582).
        /// </summary>
        public string ReadName()
        {
            var index = ReadUInt16();
            var names = ExpressionSet.UniformPreshaderData?.Names;
            return names != null && index < names.Length ? names[index].Text : $"Name{index}";
        }

        /// <summary>FHashedMaterialParameterInfo: interned name, int32 index, byte association.</summary>
        public string ReadParameterInfo()
        {
            var name = ReadName();
            var index = ReadInt32();
            ReadByte(); // EMaterialParameterAssociation
            return index >= 0 ? $"{name}[{index}]" : name;
        }
    }

    private static void Step(Context ctx, List<string> stack)
    {
        var opcode = (EMaterialPreshaderOpcode) ctx.ReadByte();
        switch (opcode)
        {
            case EMaterialPreshaderOpcode.Nop:
                break;
            case EMaterialPreshaderOpcode.ConstantZero:
                stack.Add("0");
                break;
            case EMaterialPreshaderOpcode.Constant:
                stack.Add(FormatConstant(ctx.ReadLinearColor()));
                break;
            case EMaterialPreshaderOpcode.ScalarParameter:
                stack.Add(DescribeScalarParameter(ctx, ctx.ReadUInt16()));
                break;
            case EMaterialPreshaderOpcode.VectorParameter:
                stack.Add(DescribeVectorParameter(ctx, ctx.ReadUInt16()));
                break;

            case EMaterialPreshaderOpcode.Add: Binary(stack, "+"); break;
            case EMaterialPreshaderOpcode.Sub: Binary(stack, "-"); break;
            case EMaterialPreshaderOpcode.Mul: Binary(stack, "*"); break;
            case EMaterialPreshaderOpcode.Div: Binary(stack, "/"); break;
            case EMaterialPreshaderOpcode.Fmod: Call(stack, "fmod", 2); break;
            case EMaterialPreshaderOpcode.Min: Call(stack, "min", 2); break;
            case EMaterialPreshaderOpcode.Max: Call(stack, "max", 2); break;
            case EMaterialPreshaderOpcode.Atan2: Call(stack, "atan2", 2); break;
            case EMaterialPreshaderOpcode.Clamp: Call(stack, "clamp", 3); break;

            case EMaterialPreshaderOpcode.Sin: Call(stack, "sin", 1); break;
            case EMaterialPreshaderOpcode.Cos: Call(stack, "cos", 1); break;
            case EMaterialPreshaderOpcode.Tan: Call(stack, "tan", 1); break;
            case EMaterialPreshaderOpcode.Asin: Call(stack, "asin", 1); break;
            case EMaterialPreshaderOpcode.Acos: Call(stack, "acos", 1); break;
            case EMaterialPreshaderOpcode.Atan: Call(stack, "atan", 1); break;
            case EMaterialPreshaderOpcode.Sqrt: Call(stack, "sqrt", 1); break;
            case EMaterialPreshaderOpcode.Saturate: Call(stack, "saturate", 1); break;
            case EMaterialPreshaderOpcode.Abs: Call(stack, "abs", 1); break;
            case EMaterialPreshaderOpcode.Floor: Call(stack, "floor", 1); break;
            case EMaterialPreshaderOpcode.Ceil: Call(stack, "ceil", 1); break;
            case EMaterialPreshaderOpcode.Round: Call(stack, "round", 1); break;
            case EMaterialPreshaderOpcode.Trunc: Call(stack, "trunc", 1); break;
            case EMaterialPreshaderOpcode.Sign: Call(stack, "sign", 1); break;
            case EMaterialPreshaderOpcode.Frac: Call(stack, "frac", 1); break;
            case EMaterialPreshaderOpcode.Fractional: Call(stack, "fractional", 1); break;
            case EMaterialPreshaderOpcode.Log2: Call(stack, "log2", 1); break;
            case EMaterialPreshaderOpcode.Log10: Call(stack, "log10", 1); break;

            // These three carry a trailing MaterialValueType byte describing the operand width.
            case EMaterialPreshaderOpcode.Dot: ctx.ReadByte(); Call(stack, "dot", 2); break;
            case EMaterialPreshaderOpcode.Cross: ctx.ReadByte(); Call(stack, "cross", 2); break;
            case EMaterialPreshaderOpcode.Length: ctx.ReadByte(); Call(stack, "length", 1); break;

            case EMaterialPreshaderOpcode.ComponentSwizzle:
            {
                var numElements = ctx.ReadByte();
                var components = new[] { ctx.ReadByte(), ctx.ReadByte(), ctx.ReadByte(), ctx.ReadByte() };
                var value = Pop(stack);
                var mask = string.Empty;
                for (var i = 0; i < Math.Min((int) numElements, 4); i++)
                    mask += "xyzw"[Math.Min((int) components[i], 3)];
                stack.Add($"{Wrap(value)}.{(mask.Length > 0 ? mask : "x")}");
                break;
            }
            case EMaterialPreshaderOpcode.AppendVector:
            {
                ctx.ReadByte(); // NumComponentsA
                var b = Pop(stack);
                var a = Pop(stack);
                stack.Add($"append({a}, {b})");
                break;
            }

            case EMaterialPreshaderOpcode.TextureSize:
                stack.Add($"TextureSize({DescribeTextureOperand(ctx)})");
                break;
            case EMaterialPreshaderOpcode.TexelSize:
                stack.Add($"TexelSize({DescribeTextureOperand(ctx)})");
                break;
            case EMaterialPreshaderOpcode.ExternalTextureCoordinateScaleRotation:
                stack.Add($"ExternalTextureCoordinateScaleRotation({DescribeExternalTextureOperand(ctx)})");
                break;
            case EMaterialPreshaderOpcode.ExternalTextureCoordinateOffset:
                stack.Add($"ExternalTextureCoordinateOffset({DescribeExternalTextureOperand(ctx)})");
                break;
            case EMaterialPreshaderOpcode.RuntimeVirtualTextureUniform:
            {
                var parameter = ctx.ReadParameterInfo();
                var textureIndex = ctx.ReadInt32();
                var vectorIndex = ctx.ReadInt32();
                stack.Add($"RuntimeVirtualTextureUniform({DescribeTexture(ctx, parameter, textureIndex)}, {vectorIndex})");
                break;
            }

            default:
                throw new InvalidDataException($"unknown preshader opcode {(byte) opcode}");
        }
    }

    private static string DescribeTextureOperand(Context ctx)
    {
        var parameter = ctx.ReadParameterInfo();
        return DescribeTexture(ctx, parameter, ctx.ReadInt32());
    }

    private static string DescribeExternalTextureOperand(Context ctx)
    {
        var name = ctx.ReadName();
        ctx.ReadGuid();
        var textureIndex = ctx.ReadInt32();
        return DescribeTexture(ctx, name, textureIndex);
    }

    /// <summary>
    /// A texture operand names its parameter when it has one, otherwise it is a hard reference and
    /// only its index into the material's referenced-texture list identifies it - resolved to the
    /// asset name when that list is available, left as the bare index when it isn't.
    /// </summary>
    private static string DescribeTexture(Context ctx, string parameter, int textureIndex)
    {
        if (!string.IsNullOrEmpty(parameter) && parameter != "None")
            return $"TextureParameter'{parameter}'";
        if (ctx.ReferencedTextures is { } textures && textureIndex >= 0 && textureIndex < textures.Count && textures[textureIndex] is { } texture)
            return $"Texture'{texture.Name}'";
        return $"Texture[{textureIndex}]";
    }

    /// <summary>
    /// The serialized default is the base material's; when this decompile is for a material instance
    /// that overrides the parameter, the value it actually renders with is shown instead (and the
    /// default kept alongside), mirroring what the legacy printer does.
    /// </summary>
    private static string DescribeScalarParameter(Context ctx, int index)
    {
        var parameters = ctx.ExpressionSet.UniformScalarParameters;
        if (parameters == null || index < 0 || index >= parameters.Length) return $"ScalarParameter[{index}]";
        var parameter = parameters[index];
        var name = GetParameterName(parameter);
        var note = name != null && ctx.Overrides?.Scalars.TryGetValue(name, out var overridden) == true && !overridden.Equals(parameter.DefaultValue)
            ? $"{FormatFloat(overridden)}, default {FormatFloat(parameter.DefaultValue)}"
            : $"default {FormatFloat(parameter.DefaultValue)}";
        return $"ScalarParameter'{name ?? index.ToString()}' /* {note} */";
    }

    private static string DescribeVectorParameter(Context ctx, int index)
    {
        var parameters = ctx.ExpressionSet.UniformVectorParameters;
        if (parameters == null || index < 0 || index >= parameters.Length) return $"VectorParameter[{index}]";
        var parameter = parameters[index];
        var name = GetParameterName(parameter);
        var note = name != null && ctx.Overrides?.Vectors.TryGetValue(name, out var overridden) == true && !overridden.Equals(parameter.DefaultValue)
            ? $"{FormatVector(overridden)}, default {FormatVector(parameter.DefaultValue)}"
            : $"default {FormatVector(parameter.DefaultValue)}";
        return $"VectorParameter'{name ?? index.ToString()}' /* {note} */";
    }

    /// <summary>
    /// 4.26+ stores the parameter name as a real FName in the memory image; 4.25 kept a hashed info
    /// plus a separate name string. Both shapes are exposed by <see cref="FMaterialBaseParameterInfo"/>.
    /// </summary>
    public static string? GetParameterName(FMaterialBaseParameterInfo parameter)
    {
        var name = parameter.ParameterInfo?.Name.Text ?? parameter.ParameterName;
        return string.IsNullOrEmpty(name) || name == "None" ? null : name;
    }

    private static void Binary(List<string> stack, string op)
    {
        var rhs = Pop(stack);
        var lhs = Pop(stack);
        stack.Add($"({lhs} {op} {rhs})");
    }

    private static void Call(List<string> stack, string name, int argumentCount)
    {
        var arguments = new string[argumentCount];
        for (var i = argumentCount - 1; i >= 0; i--) arguments[i] = Pop(stack);
        stack.Add($"{name}({string.Join(", ", arguments)})");
    }

    private static string Pop(List<string> stack)
    {
        if (stack.Count == 0) return "?";
        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static string Wrap(string value) =>
        value.Length > 0 && (value[0] == '(' || !value.Contains(' ')) ? value : $"({value})";

    /// <summary>A parameter's default is always four components, so it always prints as one.</summary>
    private static string FormatVector(FLinearColor value) =>
        $"float4({FormatFloat(value.R)}, {FormatFloat(value.G)}, {FormatFloat(value.B)}, {FormatFloat(value.A)})";

    private static string FormatConstant(FLinearColor value) =>
        value.R.Equals(value.G) && value.G.Equals(value.B) && value.B.Equals(value.A)
            ? FormatFloat(value.R)
            : $"float4({FormatFloat(value.R)}, {FormatFloat(value.G)}, {FormatFloat(value.B)}, {FormatFloat(value.A)})";

    private static string FormatFloat(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
