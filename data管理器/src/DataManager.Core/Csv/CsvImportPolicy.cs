using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.Csv;

/// <summary>
/// CSV exchange accepts values that the destination record can represent.
/// UI choices and cross-table business rules do not narrow this import format.
/// </summary>
public static class CsvImportPolicy
{
    private static readonly RecordCodecRegistry Codecs = new();

    public static TableFieldDefinition WithPhysicalBounds(TableFieldDefinition field)
        => field.Kind switch
        {
            FieldKind.UInt8 => field with { Minimum = byte.MinValue, Maximum = byte.MaxValue },
            FieldKind.UInt16 => field with { Minimum = ushort.MinValue, Maximum = ushort.MaxValue },
            FieldKind.UInt32 => field with { Minimum = uint.MinValue, Maximum = uint.MaxValue },
            _ => field
        };

    /// <summary>
    /// Returns the field used to encode an accepted CSV value. Actual record
    /// codecs still enforce byte layout, text capacity and category markers.
    /// </summary>
    public static TableFieldDefinition ValidateCell(TableDocument document, int rowIndex, string columnName, object? value)
    {
        if (document.Definition.Capability != TableCapability.Editable)
            throw new InvalidOperationException($"表不可写：{document.Definition.DisplayName}。");
        var field = TableFieldIndex.Find(document.Definition, columnName)
            ?? throw new InvalidOperationException($"字段不存在：{columnName}");
        if (field.Policy != FieldPolicy.Editable || !document.Data.Columns.Contains(columnName) ||
            document.Data.Columns[columnName]!.ReadOnly)
            throw new InvalidOperationException($"字段不可编辑：{columnName}");
        if (rowIndex < 0 || rowIndex >= document.Data.Rows.Count) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        var physical = WithPhysicalBounds(field);
        var rowOffset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        try
        {
            var row = document.OriginalFileBytes.AsSpan(rowOffset, document.Definition.RowSize);
            var patches = Codecs.Get(document.Definition.RecordCodecKey).EncodeChangedField(row, physical, value);
            foreach (var patch in patches)
                if (patch.Offset < 0 || patch.Offset > document.Definition.RowSize - patch.Bytes.Length)
                    throw new InvalidDataException("字段编码超出目标记录的物理边界。");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException($"CSV {document.Definition.DisplayName} ID={document.Definition.BeginId + rowIndex} 字段“{field.ExportName}”：{ex.Message}", ex);
        }
        return physical;
    }
}
