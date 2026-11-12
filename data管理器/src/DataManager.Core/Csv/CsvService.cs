using System.Data;
using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.Csv;

public sealed record CsvChange(int RowIndex, string ColumnName, object? OldValue, object? NewValue);
public sealed record CsvImportResult(int ImportedRows, int ChangedCells, int SkippedReadOnlyCells, IReadOnlyList<CsvChange> Changes);
public sealed record CsvContext(string ProfileId, string VariantId, string TableKey, string LayoutRevision);
public sealed record CsvBundleImportResult(IReadOnlyDictionary<string, CsvImportResult> Tables, int ChangedCells)
{
    public int ImportedRows => Tables.Values.Sum(result => result.ImportedRows);
    public int SkippedReadOnlyCells => Tables.Values.Sum(result => result.SkippedReadOnlyCells);
}

public sealed class CsvService
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const string MetadataMarker = "#DataManagerCSV";
    private const string SchemaVersion = "1";
    private readonly RecordCodecRegistry _codecs = new();

    /// <summary>Reads an optional source-table hint even when a CSV file has been renamed.</summary>
    public static CsvContext? ReadContext(string path)
    {
        var first = Parse(ReadText(path, new DataTable()), maxRecords: 1).FirstOrDefault();
        if (first == null || first.Count == 0 || !first[0].Equals(MetadataMarker, StringComparison.Ordinal)) return null;
        return ParseContext(first);
    }

    /// <summary>Shared CSV quoting and UTF-8/UTF-16/GBK decoding for native structures without fake physical rows.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadRecords(string path, IEnumerable<string> knownColumns)
    {
        var columns = new DataTable();
        foreach (var column in knownColumns.Distinct(StringComparer.OrdinalIgnoreCase)) columns.Columns.Add(column);
        return Parse(ReadText(path, columns)).Select(row => (IReadOnlyList<string>)row).ToArray();
    }

    public static void WriteRecords(string path, IEnumerable<IReadOnlyList<string>> records, Encoding? encoding = null)
    {
        var text = string.Join(Environment.NewLine, records.Select(row => string.Join(',', row.Select(Escape)))) + Environment.NewLine;
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(true));
    }

    public void Export(TableDocument document, string path, CsvContext? context = null)
    {
        var builder = new StringBuilder();
        if (context != null)
            builder.AppendLine(string.Join(',', new[] { MetadataMarker, SchemaVersion, context.ProfileId, context.VariantId,
                context.TableKey, context.LayoutRevision }.Select(Escape)));
        // Include every physical field, including preserved bytes that are
        // intentionally hidden from the DataTable. Derived UI summaries are
        // excluded because they do not belong to the on-disk schema.
        var fields = document.Definition.Fields.Where(field => field.Size > 0).ToArray();
        builder.AppendLine(string.Join(',', new[] { CsvDisplayNameRegistry.IdColumnName }
            .Concat(fields.Select(field => Escape(CsvDisplayNameRegistry.GetColumnName(field))))));
        for (var rowIndex = 0; rowIndex < document.Data.Rows.Count; rowIndex++)
        {
            var row = document.Data.Rows[rowIndex];
            builder.AppendLine(string.Join(',', new[] { Escape(Convert.ToString(row["ID"], CultureInfo.InvariantCulture) ?? string.Empty) }
                .Concat(fields.Select(field => Escape(Convert.ToString(GetFieldValue(document, rowIndex, field), CultureInfo.InvariantCulture) ?? string.Empty)))));
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    public CsvImportResult Import(TableDocument document, string path, bool matchById = true, CsvContext? context = null)
    {
        var records = Parse(ReadText(path, document.Data));
        if (records.Count == 0) throw new InvalidDataException("CSV 没有数据。");
        var hasMetadata = records[0].Count > 0 && records[0][0].Equals(MetadataMarker, StringComparison.Ordinal);
        if (hasMetadata)
        {
            // Older exports carry source identity here. It is informational:
            // the destination's actual columns and values determine compatibility.
            records.RemoveAt(0);
            if (records.Count == 0) throw new InvalidDataException("CSV 缺少物理表列名。");
        }
        var headers = records[0];
        var duplicateHeader = headers.GroupBy(header => header, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateHeader != null) throw new InvalidDataException($"CSV 列名重复：{duplicateHeader.Key}");
        var physicalFields = document.Definition.Fields.Where(field => field.Size > 0).ToArray();
        var columnMap = new List<(TableFieldDefinition Field, int Index)>();
        foreach (var (header, index) in headers.Select((header, index) => (header, index)))
        {
            if (CsvDisplayNameRegistry.IsIdHeader(header)) continue;
            var field = physicalFields.FirstOrDefault(candidate => CsvDisplayNameRegistry.MatchesField(candidate, header));
            if (field == null) throw new InvalidDataException($"CSV 物理列不存在：{header}");
            if (columnMap.Any(item => item.Field.Name.Equals(field.Name, StringComparison.Ordinal)))
                throw new InvalidDataException($"CSV 字段重复映射：{header}");
            columnMap.Add((field, index));
        }

        if (headers.Count(CsvDisplayNameRegistry.IsIdHeader) > 1) throw new InvalidDataException("CSV 编号列重复映射：不能同时提供 ID 和编号。");
        var useId = matchById && headers.Any(CsvDisplayNameRegistry.IsIdHeader);
        var idIndex = headers.FindIndex(CsvDisplayNameRegistry.IsIdHeader);
        var idLookup = document.Data.Rows.Cast<DataRow>().ToDictionary(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture));
        var changes = new List<CsvChange>();
        var usedIds = new HashSet<int>();
        var skipped = 0;
        var imported = 0;
        foreach (var values in records.Skip(1))
        {
            // Without IDs even an empty text record has a destination row.
            // Dropping it would shift every following record to the wrong ID.
            if (useId && values.All(string.IsNullOrWhiteSpace)) continue;
            if (values.Count != headers.Count) throw new InvalidDataException($"CSV 第 {imported + 2} 行列数不一致。");
            DataRow row;
            if (useId)
            {
                if (!int.TryParse(values[idIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    throw new InvalidDataException($"CSV 第 {imported + 2} 行的 ID 无效：{values[idIndex]}");
                }
                if (!usedIds.Add(id)) throw new InvalidDataException($"CSV 包含重复 ID：{id}");
                if (!idLookup.TryGetValue(id, out row!)) throw new InvalidDataException($"ID={id} 不存在。");
            }
            else
            {
                if (imported >= document.Data.Rows.Count) throw new InvalidDataException("CSV 行数超过当前表。");
                row = document.Data.Rows[imported];
            }

            var rowChanges = new List<CsvChange>();
            foreach (var (field, index) in columnMap)
            {
                var column = document.Data.Columns.Contains(field.Name) ? document.Data.Columns[field.Name] : null;
                if (field.Policy != FieldPolicy.Editable || column == null || column.ReadOnly || document.Definition.Capability != TableCapability.Editable)
                {
                    // Ignored source columns need not match this project's raw
                    // layout or value constraints, and are never decoded/applied.
                    skipped++;
                    continue;
                }
                var targetType = column?.DataType ?? field.Kind switch
                {
                    FieldKind.UInt32 => typeof(long),
                    FieldKind.UInt8 or FieldKind.UInt16 => typeof(int),
                    _ => typeof(string)
                };
                var oldValue = GetFieldValue(document, document.Data.Rows.IndexOf(row), field);
                // Unchanged legacy bytes can contain values outside a newly
                // documented enum/range. A full exported file must still be
                // importable without reinterpreting those values.
                object? converted;
                try { converted = ValuesEqual(oldValue, values[index]) ? oldValue : ParseValue(values[index], field, targetType); }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
                {
                    throw new InvalidDataException($"CSV {document.Definition.DisplayName} 第 {imported + (hasMetadata ? 3 : 2)} 条记录 ID={row["ID"]} 字段“{field.ExportName}”：{ex.Message}", ex);
                }
                if (ValuesEqual(oldValue, converted)) continue;
                rowChanges.Add(new CsvChange(document.Data.Rows.IndexOf(row), field.Name, oldValue, converted));
            }
            changes.AddRange(rowChanges);
            imported++;
        }
        foreach (var change in changes) document.Data.Rows[change.RowIndex][change.ColumnName] = change.NewValue ?? DBNull.Value;
        return new CsvImportResult(imported, changes.Count, skipped, changes);
    }

    private static object? ParseValue(string value, TableFieldDefinition? field, Type targetType)
    {
        if (field?.Kind == FieldKind.RawBytes) return Convert.ToHexString(BinaryValueCodec.Encode(value, field));
        if (targetType == typeof(string))
        {
            if (field?.Kind == FieldKind.FixedString && TextFieldConstraints.Get(field).Evaluate(value) is { CanEncode: false } validation)
            {
                throw new ArgumentOutOfRangeException(field.Name, validation.Error);
            }
            return value;
        }
        if (string.IsNullOrWhiteSpace(value)) return DBNull.Value;
        if (field != null && field.Kind == FieldKind.UInt32)
        {
            var physical = CsvImportPolicy.WithPhysicalBounds(field);
            return BinaryValueCodec.ParseInteger64(value, physical.Minimum, physical.Maximum, field.Name);
        }
        if (field != null && field.Kind is FieldKind.UInt8 or FieldKind.UInt16)
        {
            var physical = CsvImportPolicy.WithPhysicalBounds(field);
            return BinaryValueCodec.ParseInteger(value, physical.Minimum, physical.Maximum, field.Name);
        }
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private object? GetFieldValue(TableDocument document, int rowIndex, TableFieldDefinition field)
    {
        if (IsDocumentedForgeAlias(document, field)) return GetProjectedForgeAlias(document, rowIndex, field);
        if (document.Data.Columns.Contains(field.Name))
        {
            var value = document.Data.Rows[rowIndex][field.Name];
            return value == DBNull.Value ? null : value;
        }
        var rowOffset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        return _codecs.Get(document.Definition.RecordCodecKey).Decode(
            document.OriginalFileBytes.AsSpan(rowOffset, document.Definition.RowSize), field);
    }

    private static bool IsDocumentedForgeAlias(TableDocument document, TableFieldDefinition field)
        => document.Definition.Key == "forge" && field.Kind == FieldKind.RawBytes && field.Size == 1 &&
           field.Policy != FieldPolicy.Editable && (field.Name, field.Offset) is
               ("保留58", 58) or ("保留59", 59) or ("保留61", 61);

    private object GetProjectedForgeAlias(TableDocument document, int rowIndex, TableFieldDefinition alias)
    {
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        var raw = document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize).ToArray();
        var ownerName = alias.Offset switch { 58 => "材料项数", 59 => "商店编号", 61 => "剩余次数", _ => "" };
        var owner = document.Definition.Fields.FirstOrDefault(field => field.Name == ownerName &&
            field.Offset == alias.Offset && field.Size == 1 && field.Policy == FieldPolicy.Editable);
        if (owner != null && document.Data.Columns.Contains(owner.Name))
        {
            var value = document.Data.Rows[rowIndex][owner.Name];
            var original = _codecs.Get(document.Definition.RecordCodecKey).Decode(raw, owner);
            if (value != null && value != DBNull.Value && !ValuesEqual(original, value))
                foreach (var patch in _codecs.Get(document.Definition.RecordCodecKey).EncodeChangedField(raw, owner, value))
                    patch.Bytes.CopyTo(raw, patch.Offset);
        }
        return _codecs.Get(document.Definition.RecordCodecKey).Decode(raw, alias);
    }

    private static bool ValuesEqual(object? left, object? right)
        => string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static CsvContext? ParseContext(IReadOnlyList<string> metadata)
    {
        if (metadata.Count < 5 || string.IsNullOrWhiteSpace(metadata[4])) return null;
        return new CsvContext(metadata[2], metadata[3], metadata[4], metadata.ElementAtOrDefault(5) ?? string.Empty);
    }

    /// <summary>Excel pads a short metadata record to the width of the data table when saving CSV.</summary>
    public static IReadOnlyList<string> NormalizeMetadata(IReadOnlyList<string> metadata)
    {
        var count = metadata.Count;
        while (count > 6 && string.IsNullOrWhiteSpace(metadata[count - 1])) count--;
        return count == metadata.Count ? metadata : metadata.Take(count).ToArray();
    }

    private static string ReadText(string path, DataTable table)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            var utf8 = StrictUtf8.GetString(bytes);
            var gbk = EncodingService.GameEncoding.GetString(bytes);
            return Score(utf8, table) >= Score(gbk, table) ? utf8 : gbk;
        }
        catch (DecoderFallbackException)
        {
            return EncodingService.GameEncoding.GetString(bytes);
        }
    }

    private static int Score(string text, DataTable table)
    {
        var records = Parse(text);
        var header = records.FirstOrDefault() ?? [];
        if (header.FirstOrDefault() == MetadataMarker) header = records.Skip(1).FirstOrDefault() ?? [];
        return header.Count(value => CsvDisplayNameRegistry.IsIdHeader(value) || table.Columns.Contains(value) ||
            table.Columns.Cast<DataColumn>().Any(column => column.ExtendedProperties["Field"] is TableFieldDefinition field &&
                CsvDisplayNameRegistry.MatchesField(field, value)));
    }

    private static List<List<string>> Parse(string text, int maxRecords = int.MaxValue)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var recordStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else value.Append(ch);
            }
            else if (ch == '"') { quoted = true; recordStarted = true; }
            else if (ch == ',') { row.Add(value.ToString()); value.Clear(); recordStarted = true; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(value.ToString()); value.Clear(); rows.Add(row); row = []; recordStarted = false;
                if (rows.Count >= maxRecords) return rows;
            }
            else { value.Append(ch); recordStarted = true; }
        }
        if (quoted) throw new InvalidDataException("CSV 引号未闭合。");
        if (recordStarted || value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); rows.Add(row); }
        return rows;
    }

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return escaped.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{escaped}\"" : escaped;
    }
}
