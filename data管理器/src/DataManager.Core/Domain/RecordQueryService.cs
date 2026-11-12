using System.Data;
using System.Globalization;
using DataManager.Core.Csv;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>A blank field selects every entity property in the active detail scope.</summary>
public sealed record RecordQueryCondition(string? FieldKey, string Text);

public enum RecordQueryContextKind { Business, PhysicalTable, ExtendData }

/// <summary>A stable detail context, independent of the selected entity or transient controls.</summary>
public sealed record RecordQueryContext(RecordQueryContextKind Kind, string ScopeKey,
    BusinessPageKind? BusinessPage = null, string? TableKey = null, string? ExtendCategory = null);

/// <summary>The explicitly submitted conditions, kept separate from text still being edited.</summary>
public sealed record RecordQueryAppliedState(IReadOnlyList<RecordQueryCondition> Conditions)
{
    public RecordQueryAppliedState() : this(RecordQueryState.EmptyConditions()) { }
}

/// <summary>Memory-only inputs and submitted state for exactly three independent condition groups.</summary>
public class RecordQueryState
{
    public RecordQueryCondition[] Inputs { get; set; } = EmptyConditions();
    public RecordQueryAppliedState Applied { get; set; } = new();

    public static RecordQueryCondition[] EmptyConditions()
        => Enumerable.Range(0, 3).Select(_ => new RecordQueryCondition(null, string.Empty)).ToArray();
}

/// <summary>A stable property identity and its read-only business mapping from a real entity ID.</summary>
public sealed record RecordQueryField(
    string Key, string DisplayName, Func<int, IReadOnlyList<string>> ReadValues);

/// <summary>The properties belonging to one physical table or stable business detail subpage.</summary>
public sealed record RecordQueryScope(string Key, IReadOnlyList<RecordQueryField> Fields)
{
    public RecordQueryContext? Context { get; init; }
}

/// <summary>Materialized property values from one draft revision, independent of visible controls or paging.</summary>
public sealed class RecordQuerySnapshot
{
    private readonly IReadOnlyDictionary<string, string[][]> _values;

    internal RecordQuerySnapshot(RecordPresentation[] records, IReadOnlyDictionary<string, string[][]> values)
    {
        Records = Array.AsReadOnly(records);
        _values = values;
    }

    public IReadOnlyList<RecordPresentation> Records { get; }

    public IReadOnlyList<RecordPresentation> Filter(IReadOnlyList<RecordQueryCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var active = conditions.Select(condition => (condition.FieldKey,
                Tokens: (condition.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(condition => condition.Tokens.Length != 0).ToArray();
        if (active.Length == 0) return Records;

        var result = new List<RecordPresentation>();
        for (var row = 0; row < Records.Count; row++)
        {
            var matches = true;
            foreach (var condition in active)
            {
                if (!string.IsNullOrWhiteSpace(condition.FieldKey))
                {
                    // A removed property must not silently become an all-properties query.
                    if (!_values.TryGetValue(condition.FieldKey, out var values) ||
                        !condition.Tokens.All(token => Contains(values[row], token)))
                    {
                        matches = false;
                        break;
                    }
                }
                else if (!condition.Tokens.All(token => _values.Values.Any(values => Contains(values[row], token))))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) result.Add(Records[row]);
        }
        return result;
    }

    private static bool Contains(IEnumerable<string> values, string token)
        => values.Any(value => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads current workspace properties using explicit detail bindings. It never joins unrelated tables
/// by row position, creates detail controls, changes a draft, or interprets query text as an expression.
/// </summary>
public sealed class RecordQueryService(ProjectWorkspace workspace)
{
    public const string IdentityKey = "identity";

    private sealed record RowIndex(long Revision, IReadOnlyDictionary<int, DataRow> Rows);
    private sealed record FieldIndex(long Revision, IReadOnlyDictionary<string, TableFieldDefinition> Fields);
    private readonly Dictionary<TableDocument, RowIndex> _rows = new();
    private readonly Dictionary<TableDocument, FieldIndex> _fields = new();
    private ReadSession? _session;

    private sealed class ReadSession(ProjectWorkspace workspace)
    {
        public RecordPresentationService Presentation { get; } = new(workspace);
        public Dictionary<(string Table, int Id), string> Names { get; } = new();
    }

    // The identity is supplied from the actual navigation records when materializing a snapshot.
    public RecordQueryField IdentityField() => new(IdentityKey, "ID：名称", _ => []);

    /// <summary>
    /// Binds the same business property across explicitly selected physical segments. A missing or
    /// undisplayed field is omitted, and each value is looked up by the record's actual ID.
    /// </summary>
    public RecordQueryField? PhysicalField(IReadOnlyList<string> tableKeys, string fieldName,
        string? displayName = null, string? stableKey = null)
    {
        ArgumentNullException.ThrowIfNull(tableKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        var keys = tableKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var field = keys.Select(key => workspace.Documents.TryGetValue(key, out var document)
                ? ReadableField(document, fieldName) : null).FirstOrDefault(candidate => candidate != null);
        if (field == null) return null;
        return new(stableKey ?? $"field:{string.Join("|", keys)}:{fieldName}",
            displayName ?? CsvDisplayNameRegistry.GetColumnName(field), id => ReadPhysical(keys, fieldName, id));
    }

    /// <summary>Only physical, displayed schema columns and the displayed record identity are searchable.</summary>
    public RecordQueryScope TableScope(TableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fields = new List<RecordQueryField> { IdentityField() };
        foreach (var definition in document.Definition.Fields)
            if (PhysicalField([document.Definition.Key], definition.Name) is { } field) fields.Add(field);
        return new($"table:{document.Definition.Key}", fields);
    }

    public RecordQuerySnapshot BuildSnapshot(RecordQueryScope scope, IReadOnlyList<RecordPresentation> records)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(records);
        var capturedRecords = records.ToArray();
        var values = new Dictionary<string, string[][]>(StringComparer.Ordinal);
        // A new read session discards cross-table name caches after edits, imports, undo/redo or reloads.
        var previousSession = _session;
        _session = new(workspace);
        try
        {
            foreach (var group in scope.Fields.Where(field => field.Key != IdentityKey).GroupBy(field => field.Key, StringComparer.Ordinal))
            {
                var bindings = group.ToArray();
                var captured = new string[capturedRecords.Length][];
                for (var index = 0; index < capturedRecords.Length; index++)
                    captured[index] = bindings.SelectMany(field => field.ReadValues(capturedRecords[index].Id))
                        .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
                values[group.Key] = captured;
            }
            values[IdentityKey] = capturedRecords.Select(record => new[]
            {
                record.Display, record.Id.ToString(CultureInfo.InvariantCulture), record.Name ?? string.Empty
            }).ToArray();
        }
        finally { _session = previousSession; }
        return new(capturedRecords, values);
    }

    private IReadOnlyList<string> ReadPhysical(IReadOnlyList<string> tableKeys, string fieldName, int id)
    {
        var result = new List<string>();
        foreach (var key in tableKeys)
        {
            if (!workspace.Documents.TryGetValue(key, out var document) ||
                ReadableField(document, fieldName) is not { } field || FindRow(document, id) is not { } row) continue;
            var value = Text(row[field.Name]);
            if (value.Length == 0) continue;
            result.Add(value);
            if (field.ReferenceTable is not { Length: > 0 } reference ||
                !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var referenceId)) continue;
            var referenceTables = reference == "items" ? new[] { "items-low", "items-high", "items-extended" } : [reference];
            foreach (var referenceKey in referenceTables)
            {
                if (!workspace.Documents.TryGetValue(referenceKey, out var target) || FindRow(target, referenceId) == null) continue;
                var name = ReferenceName(target, referenceId);
                if (name.Length == 0) continue;
                result.Add(name);
                result.Add($"{referenceId.ToString(CultureInfo.InvariantCulture)}：{name}");
            }
        }
        return result;
    }

    private string ReferenceName(TableDocument document, int id)
    {
        if (_session == null) return new RecordPresentationService(workspace).GetTableRowName(document, id);
        var key = (document.Definition.Key, id);
        if (!_session.Names.TryGetValue(key, out var name))
            _session.Names[key] = name = _session.Presentation.GetTableRowName(document, id);
        return name;
    }

    private DataRow? FindRow(TableDocument document, int id)
    {
        if (!document.Data.Columns.Contains("ID")) return null;
        if (!_rows.TryGetValue(document, out var index) || index.Revision != document.Revision)
        {
            var rows = new Dictionary<int, DataRow>();
            foreach (DataRow row in document.Data.Rows)
                if (row.RowState != DataRowState.Deleted && int.TryParse(Text(row["ID"]), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var rowId)) rows.TryAdd(rowId, row);
            _rows[document] = index = new(document.Revision, rows);
        }
        return index.Rows.GetValueOrDefault(id);
    }

    private TableFieldDefinition? ReadableField(TableDocument document, string fieldName)
    {
        if (!_fields.TryGetValue(document, out var index) || index.Revision != document.Revision)
        {
            var fields = document.Definition.Fields.Where(field => field.Kind != FieldKind.RawBytes &&
                    field.Policy != FieldPolicy.Preserve && document.Data.Columns.Contains(field.Name))
                .GroupBy(field => field.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            _fields[document] = index = new(document.Revision, fields);
        }
        return index.Fields.GetValueOrDefault(fieldName);
    }

    private static string Text(object? value)
        => value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
