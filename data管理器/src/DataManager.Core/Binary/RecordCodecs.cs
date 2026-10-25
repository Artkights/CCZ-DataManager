using DataManager.Core.Models;

namespace DataManager.Core.Binary;

public sealed record RecordPatch(int Offset, byte[] Bytes);

public interface IRecordCodec
{
    string Key { get; }
    object Decode(ReadOnlySpan<byte> row, TableFieldDefinition field);
    IReadOnlyList<RecordPatch> EncodeChangedField(ReadOnlySpan<byte> originalRow, TableFieldDefinition field, object? value);
}

public sealed class FixedRecordCodec : IRecordCodec
{
    public string Key => "fixed-record-v1";

    public object Decode(ReadOnlySpan<byte> row, TableFieldDefinition field)
        => BinaryValueCodec.Decode(row, field);

    public IReadOnlyList<RecordPatch> EncodeChangedField(ReadOnlySpan<byte> originalRow, TableFieldDefinition field, object? value)
        => [new RecordPatch(field.Offset, BinaryValueCodec.EncodeChanged(originalRow, value, field))];
}

public sealed class Item66RecordCodec : IRecordCodec
{
    private const int TypeOffset = 0x11;
    private const int MarkerOffset = 0x12;
    public string Key => "item-66-15-v1";

    public object Decode(ReadOnlySpan<byte> row, TableFieldDefinition field)
    {
        var markerRow = IsMarkerRow(row);
        return field.Name switch
        {
            "宝物类型" => markerRow ? row[MarkerOffset] : row[TypeOffset],
            "装备特效号" => markerRow ? row[TypeOffset] : row[MarkerOffset],
            _ => BinaryValueCodec.Decode(row, field)
        };
    }

    public IReadOnlyList<RecordPatch> EncodeChangedField(ReadOnlySpan<byte> originalRow, TableFieldDefinition field, object? value)
    {
        var markerRow = IsMarkerRow(originalRow);
        if (field.Name == "宝物类型")
        {
            var encoded = BinaryValueCodec.Encode(value, field);
            if (markerRow && encoded[0] is not (2 or 3))
            {
                throw new InvalidOperationException("6.6 辅助/道具行的宝物类型只能在类别标记 2/3 内修改；首版不自动转换记录类别。");
            }
            if (!markerRow && encoded[0] >= 26 && originalRow[MarkerOffset] is 2 or 3)
                throw new InvalidOperationException("该类型会把普通装备转换为辅助/道具并改变字段地址；请保留原记录类别。");
            return [new RecordPatch(markerRow ? MarkerOffset : TypeOffset, encoded)];
        }
        if (field.Name == "装备特效号")
        {
            var encoded = BinaryValueCodec.Encode(value, field);
            if (markerRow && encoded[0] < 26)
                throw new InvalidOperationException("辅助/道具的实际特效号小于 26 会被引擎作为普通装备类型读取；请保留原记录类别。");
            if (!markerRow && originalRow[TypeOffset] >= 26 && encoded[0] is 2 or 3)
            {
                throw new InvalidOperationException("6.6 普通装备的特效号 2/3 会改变物理类别标记；首版禁止自动转换记录类别。");
            }
            return [new RecordPatch(markerRow ? TypeOffset : MarkerOffset, encoded)];
        }
        if (field.Kind == FieldKind.FixedString && field.Offset == 0 && field.Size == 15 && field.TextConstraint == null)
            field = field with { TextConstraint = new(15, 14, true, "6.6 物品运行时在 row[14] 追加终止符。") };
        return [new RecordPatch(field.Offset, BinaryValueCodec.EncodeChanged(originalRow, value, field))];
    }

    private static bool IsMarkerRow(ReadOnlySpan<byte> row)
        // 40B444 first classifies +11 <20/<26 as weapon/armor. Only
        // larger types consume the category byte (file +12 -> runtime +14).
        => row.Length > MarkerOffset && row[TypeOffset] >= 26 && row[MarkerOffset] is 2 or 3;
}

public sealed class RecordCodecRegistry
{
    private readonly Dictionary<string, IRecordCodec> _codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fixed-record-v1"] = new FixedRecordCodec(),
        ["item-65-17-v1"] = new FixedRecordCodec(),
        ["item-66-15-v1"] = new Item66RecordCodec()
    };

    public IRecordCodec Get(string key)
        => _codecs.TryGetValue(key, out var codec)
            ? codec
            : throw new InvalidDataException($"未知记录 codec：{key}");
}

internal readonly record struct RecordByteRange(int Offset, int Length);

/// <summary>
/// Value-independent physical write ranges for migration alias checks. This
/// does not replace EncodeChangedField or relax any value validation.
/// </summary>
internal static class RecordCodecWriteFootprints
{
    public static int? GetLayoutKey(IRecordCodec codec, ReadOnlySpan<byte> row)
        => codec switch
        {
            FixedRecordCodec => 0,
            Item66RecordCodec => IsItemMarker(row) ? 1 : 0,
            _ => null
        };

    public static IReadOnlyList<RecordByteRange> Get(IRecordCodec codec, ReadOnlySpan<byte> row, TableFieldDefinition field)
    {
        if (codec is FixedRecordCodec or Item66RecordCodec)
        {
            var offset = codec is Item66RecordCodec ? field.Name switch
            {
                "宝物类型" => IsItemMarker(row) ? 0x12 : 0x11,
                "装备特效号" => IsItemMarker(row) ? 0x11 : 0x12,
                _ => field.Offset
            } : field.Offset;
            var length = field.Kind switch
            {
                FieldKind.UInt8 => 1,
                FieldKind.UInt16 => 2,
                FieldKind.UInt32 => 4,
                _ => field.Size
            };
            return [new RecordByteRange(offset, length)];
        }
        // A future codec stays conservative until its independent physical
        // layout is supplied; retain actual encode/decode semantics meanwhile.
        return codec.EncodeChangedField(row, field, codec.Decode(row, field))
            .Select(patch => new RecordByteRange(patch.Offset, patch.Bytes.Length)).ToArray();
    }

    private static bool IsItemMarker(ReadOnlySpan<byte> row)
        => row.Length > 0x12 && row[0x11] >= 26 && row[0x12] is 2 or 3;
}
