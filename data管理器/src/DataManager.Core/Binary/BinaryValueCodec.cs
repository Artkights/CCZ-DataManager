using System.Globalization;
using System.Buffers.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.Binary;

public static class BinaryValueCodec
{
    public static object Decode(ReadOnlySpan<byte> row, TableFieldDefinition field)
        => field.Kind switch
        {
            FieldKind.FixedString => EncodingService.DecodeFixedStringPreservingInvalidBytes(row.Slice(field.Offset, field.Size)),
            FieldKind.UInt8 => row[field.Offset],
            FieldKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(field.Offset, field.Size)),
            FieldKind.UInt32 => (long)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(field.Offset, field.Size)),
            FieldKind.RawBytes => Convert.ToHexString(row.Slice(field.Offset, field.Size)),
            _ => string.Empty
        };

    public static byte[] Encode(object? value, TableFieldDefinition field)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return field.Kind switch
        {
            FieldKind.FixedString => EncodingService.EncodeFixedString(text, TextFieldConstraints.Get(field)),
            FieldKind.UInt8 => [checked((byte)ParseInteger(text, field.Minimum, field.Maximum, field.Name))],
            FieldKind.UInt16 => EncodeUInt16(text, field),
            FieldKind.UInt32 => EncodeUInt32(text, field),
            FieldKind.RawBytes => ParseRawBytes(text, field.Size, field.Name),
            _ => throw new InvalidOperationException($"不支持字段类型：{field.Kind}")
        };
    }

    public static byte[] EncodeChanged(ReadOnlySpan<byte> originalRow, object? value, TableFieldDefinition field)
        => field.Kind == FieldKind.FixedString
            ? EncodingService.EncodeFixedStringPreservingTail(Convert.ToString(value, CultureInfo.InvariantCulture),
                originalRow.Slice(field.Offset, field.Size), TextFieldConstraints.Get(field))
            : Encode(value, field);

    public static int ParseInteger(string text, long minimum, long maximum, string fieldName)
        => checked((int)ParseInteger64(text, minimum, maximum, fieldName));

    public static long ParseInteger64(string text, long minimum, long maximum, string fieldName)
    {
        text = text.Trim();
        var style = NumberStyles.Integer;
        var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, style, CultureInfo.InvariantCulture);
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(fieldName, value, $"{fieldName} 必须在 {minimum}..{maximum} 范围内。");
        }
        return value;
    }

    private static byte[] EncodeUInt16(string text, TableFieldDefinition field)
    {
        var value = ParseInteger64(text, field.Minimum, field.Maximum, field.Name);
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        return bytes;
    }

    private static byte[] EncodeUInt32(string text, TableFieldDefinition field)
    {
        var value = ParseInteger64(text, field.Minimum, field.Maximum, field.Name);
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value));
        return bytes;
    }

    private static byte[] ParseRawBytes(string text, int size, string fieldName)
    {
        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (normalized.Length != size * 2)
        {
            throw new FormatException($"{fieldName} 必须是 {size} 字节十六进制值。");
        }
        try { return Convert.FromHexString(normalized); }
        catch (FormatException ex) { throw new FormatException($"{fieldName} 必须是 {size} 字节十六进制值。", ex); }
    }
}
