using System.Data;
using System.Globalization;

namespace DataManager.Core.Models;

public sealed class TableDocument
{
    // Each capture is immutable after publication. Recovery can retain the old
    // baseline without duplicating a dictionary entry for every editable cell.
    private Dictionary<string, object?[]> _originalValues = new(StringComparer.Ordinal);
    private bool? _dirtyCache;
    private bool _observingChanges;

    public required TableDefinition Definition { get; init; }
    public required string FilePath { get; init; }
    public required DataTable Data { get; init; }
    public required byte[] OriginalFileBytes { get; set; }
    public required string OriginalFileHash { get; set; }

    public bool IsDirty => _dirtyCache ??= Definition.Fields
        .Where(field => field.Policy == FieldPolicy.Editable && Data.Columns.Contains(field.Name))
        .Any(field => Enumerable.Range(0, Data.Rows.Count).Any(row =>
            !ValuesEqual(GetOriginal(row, field.Name), Data.Rows[row][field.Name])));

    public void CaptureOriginalValues()
    {
        if (!_observingChanges)
        {
            // Pending text and layout notifications do not mutate the table.
            // Invalidate only when data changes, rather than rescanning the
            // million cells in Effect.e5 on every UI notification.
            Data.ColumnChanged += (_, _) => _dirtyCache = null;
            Data.RowChanged += (_, _) => _dirtyCache = null;
            Data.RowDeleted += (_, _) => _dirtyCache = null;
            Data.TableCleared += (_, _) => _dirtyCache = null;
            Data.Columns.CollectionChanged += (_, _) => _dirtyCache = null;
            _observingChanges = true;
        }
        var originals = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var field in Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable && Data.Columns.Contains(field.Name)))
        {
            var values = new object?[Data.Rows.Count];
            var column = Data.Columns[field.Name]!;
            var rowIndex = 0;
            foreach (DataRow row in Data.Rows)
            {
                var value = row[column];
                values[rowIndex++] = value == DBNull.Value ? null : value;
            }
            originals[field.Name] = values;
        }
        Data.AcceptChanges();
        _originalValues = originals;
        _dirtyCache = false;
    }

    public object? GetOriginal(int rowIndex, string fieldName)
        => _originalValues.TryGetValue(fieldName, out var values) && (uint)rowIndex < (uint)values.Length
            ? values[rowIndex] : null;

    public void MarkSaved(string newHash)
    {
        OriginalFileHash = newHash;
        OriginalFileBytes = File.ReadAllBytes(FilePath);
        CaptureOriginalValues();
    }

    public void MarkSaved(TableDocument verified)
    {
        if (verified.Definition.Key != Definition.Key || verified.Data.Rows.Count != Data.Rows.Count)
            throw new InvalidDataException("复读文档的身份或记录数不一致。");
        foreach (var field in Definition.Fields.Where(field => Data.Columns.Contains(field.Name) && verified.Data.Columns.Contains(field.Name)))
        {
            var column = Data.Columns[field.Name]!;
            var wasReadOnly = column.ReadOnly;
            column.ReadOnly = false;
            try
            {
                for (var row = 0; row < Data.Rows.Count; row++)
                {
                    var value = verified.Data.Rows[row][field.Name];
                    if (!ValuesEqual(Data.Rows[row][column], value)) Data.Rows[row][column] = value;
                }
            }
            finally { column.ReadOnly = wasReadOnly; }
        }
        OriginalFileBytes = verified.OriginalFileBytes;
        OriginalFileHash = verified.OriginalFileHash;
        Data.ExtendedProperties["TextDecodeWarnings"] = verified.Data.ExtendedProperties["TextDecodeWarnings"];
        CaptureOriginalValues();
    }

    /// <summary>Keep both the saved baseline and the user's current draft recoverable until acceptance finishes.</summary>
    internal Action CaptureSaveRecovery()
    {
        var originalBytes = OriginalFileBytes;
        var originalHash = OriginalFileHash;
        var originalValues = _originalValues;
        var values = Data.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray();
        var warnings = Data.ExtendedProperties["TextDecodeWarnings"];
        return () =>
        {
            foreach (DataColumn column in Data.Columns)
            {
                if (column.Expression.Length != 0) continue;
                var readOnly = column.ReadOnly; column.ReadOnly = false;
                try { for (var row = 0; row < values.Length; row++) Data.Rows[row][column] = values[row][column.Ordinal]; }
                finally { column.ReadOnly = readOnly; }
            }
            OriginalFileBytes = originalBytes; OriginalFileHash = originalHash;
            _originalValues = originalValues;
            _dirtyCache = null;
            Data.ExtendedProperties["TextDecodeWarnings"] = warnings;
        };
    }

    public IEnumerable<TableCellChange> EnumerateChanges()
    {
        var fields = Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable && Data.Columns.Contains(field.Name))
            .Select(field => (Field: field, Column: Data.Columns[field.Name]!)).ToArray();
        var rowIndex = 0;
        foreach (DataRow row in Data.Rows)
        {
            foreach (var (field, column) in fields)
            {
                var oldValue = GetOriginal(rowIndex, field.Name);
                var value = row[column];
                var newValue = value == DBNull.Value ? null : value;
                if (!ValuesEqual(oldValue, newValue))
                {
                    yield return new TableCellChange(
                        Definition,
                        rowIndex,
                        Definition.BeginId + rowIndex,
                        field,
                        oldValue,
                        newValue);
                }
            }
            rowIndex++;
        }
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is DBNull) left = null;
        if (right is DBNull) right = null;
        if (left is null || right is null) return left is null && right is null;
        return string.Equals(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }
}

public sealed record TableCellChange(
    TableDefinition Table,
    int RowIndex,
    int Id,
    TableFieldDefinition Field,
    object? OldValue,
    object? NewValue);
