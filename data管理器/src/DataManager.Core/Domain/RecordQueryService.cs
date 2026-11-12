using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    string Key, string DisplayName, Func<int, IReadOnlyList<string>> ReadValues)
{
    /// <summary>Null observes the workspace conservatively; an empty list declares static values.</summary>
    public IReadOnlyList<RecordQueryDependency>? Dependencies { get; init; }
}

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
        => Filter(conditions, CancellationToken.None);

    public IReadOnlyList<RecordPresentation> Filter(IReadOnlyList<RecordQueryCondition> conditions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var active = conditions.Select(condition => (condition.FieldKey,
                Tokens: (condition.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(condition => condition.Tokens.Length != 0).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (active.Length == 0) return Records;
        var columns = _values.Values.ToArray();
        var compiled = active.Select(condition => (condition.FieldKey, condition.Tokens,
            Values: condition.FieldKey is { } key && _values.TryGetValue(key, out var column) ? column : null)).ToArray();
        if (compiled.Any(condition => !string.IsNullOrWhiteSpace(condition.FieldKey) &&
                condition.FieldKey != RecordQueryService.IdentityKey && condition.Values == null)) return [];

        var result = new List<RecordPresentation>();
        for (var row = 0; row < Records.Count; row++)
        {
            if ((row & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var matches = true;
            string[]? identity = null;
            foreach (var condition in compiled)
            {
                foreach (var token in condition.Tokens)
                {
                    var all = string.IsNullOrWhiteSpace(condition.FieldKey);
                    var identityOnly = condition.FieldKey == RecordQueryService.IdentityKey;
                    var found = false;
                    if (all || identityOnly)
                    {
                        identity ??= [Records[row].Display, Records[row].Id.ToString(CultureInfo.InvariantCulture), Records[row].Name ?? string.Empty];
                        found = Contains(identity, token);
                    }
                    if (!found && all)
                    {
                        foreach (var column in columns)
                            if (Contains(column[row], token)) { found = true; break; }
                    }
                    else if (!found && !identityOnly) found = Contains(condition.Values![row], token);
                    if (!found) { matches = false; break; }
                }
                if (!matches) break;
            }
            if (matches) result.Add(Records[row]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static bool Contains(string[] values, string token)
    {
        foreach (var value in values)
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Reads current workspace properties using explicit detail bindings. It never joins unrelated tables
/// by row position, creates detail controls, changes a draft, or interprets query text as an expression.
/// </summary>
public sealed class RecordQueryService(ProjectWorkspace workspace)
{
    public const string IdentityKey = "identity";

    private sealed record FieldIndex(TableDocument Document, long SchemaVersion, IReadOnlyDictionary<string, TableFieldDefinition> Fields);
    private readonly Dictionary<string, FieldIndex> _fields = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<ReadSession?> _session = new();
    private static readonly ConditionalWeakTable<RecordQueryField, BindingIdentity> BindingIdentities = new();
    private static long _nextBindingIdentity;
    private sealed class BindingIdentity { public long Value { get; } = Interlocked.Increment(ref _nextBindingIdentity); }
    private WorkspaceReadCache Cache => WorkspaceReadCache.For(workspace);

    private sealed record PhysicalBinding(string[] TableKeys, string FieldName);
    private sealed record PhysicalColumn(DataColumn Column,
        IReadOnlyDictionary<int, DataRow> Rows, TableDocument[] ReferenceDocuments);

    private sealed class ReadSession(ProjectWorkspace workspace, WorkspaceReadCache cache) : IDisposable
    {
        public ProjectWorkspace Workspace { get; } = workspace;
        public Dictionary<string, IReadOnlyDictionary<int, string>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<PhysicalBinding, PhysicalColumn[]> Columns { get; } = [];
        private readonly Dictionary<TableDocument, WorkspaceReadCache.RowDirectoryLease> _rows = [];

        public IReadOnlyDictionary<int, DataRow> Rows(TableDocument document)
        {
            if (!_rows.TryGetValue(document, out var lease))
                _rows[document] = lease = cache.PinRowDirectory(document);
            return lease.Rows;
        }

        public void Dispose()
        {
            foreach (var lease in _rows.Values) lease.Dispose();
            _rows.Clear(); Columns.Clear(); Names.Clear();
        }
    }

    // The identity is supplied from the actual navigation records when materializing a snapshot.
    public RecordQueryField IdentityField() => new(IdentityKey, "ID：名称", _ => []) { Dependencies = [] };

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
        foreach (var key in keys)
            if (workspace.Documents.TryGetValue(key, out var source)) Cache.ValidateColumn(source, fieldName);
        var field = keys.Select(key => workspace.Documents.TryGetValue(key, out var document)
                ? ReadableField(document, fieldName) : null).FirstOrDefault(candidate => candidate != null);
        if (field == null) return null;
        var dependencies = keys.Select(key => new RecordQueryDependency(key, fieldName)).ToList();
        foreach (var key in keys)
        {
            if (!workspace.Documents.TryGetValue(key, out var document) ||
                ReadableField(document, fieldName)?.ReferenceTable is not { Length: > 0 } reference) continue;
            var targets = reference == "items" ? new[] { "items-low", "items-high", "items-extended" } : [reference];
            dependencies.AddRange(Cache.GetNavigationDependencies(targets));
        }
        var binding = new PhysicalBinding(keys, fieldName);
        return new(stableKey ?? $"field:{string.Join("|", keys)}:{fieldName}",
            displayName ?? CsvDisplayNameRegistry.GetColumnName(field), id => ReadPhysical(binding, id))
        { Dependencies = dependencies.Distinct().ToArray() };
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
        => BuildSnapshot(scope, records, requestedFieldKeys: null);

    /// <summary>Null keys read the whole scope. Empty keys capture identity only without reading a business field.</summary>
    public RecordQuerySnapshot BuildSnapshot(RecordQueryScope scope, IReadOnlyList<RecordPresentation> records,
        IReadOnlyCollection<string>? requestedFieldKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(records);
        var capturedRecords = records.ToArray();
        var values = new Dictionary<string, string[][]>(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        var groups = RequestedBindings(scope, requestedFieldKeys);
        var recordKey = RecordKey(capturedRecords);
        var previousSession = _session.Value;
        using var session = new ReadSession(workspace, Cache);
        _session.Value = session;
        try
        {
            foreach (var bindings in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = ColumnKey(scope.Key, bindings, recordKey);
                if (Cache.TryGetColumn(key, out var existing)) { values[bindings[0].Key] = existing; continue; }
                var captured = new string[capturedRecords.Length][];
                for (var index = 0; index < capturedRecords.Length; index++)
                {
                    if ((index & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    captured[index] = CaptureValues(bindings, capturedRecords[index].Id);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Cache.StoreColumn(key, captured);
                values[bindings[0].Key] = captured;
            }
        }
        finally { _session.Value = previousSession; }
        return new(capturedRecords, values);
    }

    public RecordQuerySnapshot BuildSnapshotForConditions(RecordQueryScope scope, IReadOnlyList<RecordPresentation> records,
        IReadOnlyList<RecordQueryCondition> conditions, CancellationToken cancellationToken = default)
        => BuildSnapshot(scope, records, RequestedFieldKeys(conditions), cancellationToken);

    /// <summary>Returns null for an active all-properties condition and an empty list for no active condition.</summary>
    public static IReadOnlyCollection<string>? RequestedFieldKeys(IReadOnlyList<RecordQueryCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var active = conditions.Where(condition => !string.IsNullOrWhiteSpace(condition.Text)).ToArray();
        if (active.Any(condition => string.IsNullOrWhiteSpace(condition.FieldKey))) return null;
        return active.Select(condition => condition.FieldKey!).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Capture stays on the owning synchronization context and yields in short slices. The caller
    /// supplies a UI-queue yield; a worker may subsequently Filter the returned immutable snapshot.
    /// A changed dependency aborts rather than publishing mixed draft revisions.
    /// </summary>
    public async Task<RecordQuerySnapshot> BuildSnapshotAsync(RecordQueryScope scope,
        IReadOnlyList<RecordPresentation> records, IReadOnlyCollection<string>? requestedFieldKeys,
        Func<CancellationToken, Task> yield, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(yield);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedRecords = records.ToArray();
        var groups = RequestedBindings(scope, requestedFieldKeys);
        var dependencies = Dependencies(groups.SelectMany(group => group));
        var initialVersion = Cache.GetVersion(dependencies);
        var recordKey = RecordKey(capturedRecords);
        var values = new Dictionary<string, string[][]>(StringComparer.Ordinal);
        var slice = Stopwatch.StartNew();
        var previousSession = _session.Value;
        using var session = new ReadSession(workspace, Cache);
        _session.Value = session;
        try
        {
            foreach (var bindings in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = ColumnKey(scope.Key, bindings, recordKey);
                if (Cache.TryGetColumn(key, out var existing))
                {
                    values[bindings[0].Key] = existing;
                    await YieldIfNeeded();
                    continue;
                }
                var captured = new string[capturedRecords.Length][];
                for (var index = 0; index < capturedRecords.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    captured[index] = CaptureValues(bindings, capturedRecords[index].Id);
                    await YieldIfNeeded();
                }
                Cache.StoreColumn(key, captured);
                values[bindings[0].Key] = captured;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Cache.GetVersion(dependencies) != initialVersion)
                throw new OperationCanceledException("查询读取期间草稿已改变，请重新查询。", cancellationToken);
        }
        finally { _session.Value = previousSession; }
        return new(capturedRecords, values);

        async Task YieldIfNeeded()
        {
            if (slice.ElapsedMilliseconds < 8) return;
            await yield(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Cache.GetVersion(dependencies) != initialVersion)
                throw new OperationCanceledException("查询读取期间草稿已改变，请重新查询。", cancellationToken);
            slice.Restart();
        }
    }

    private static RecordQueryField[][] RequestedBindings(RecordQueryScope scope, IReadOnlyCollection<string>? keys)
    {
        var requested = keys == null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        return scope.Fields.Where(field => field.Key != IdentityKey && (requested == null || requested.Contains(field.Key)))
            .GroupBy(field => field.Key, StringComparer.Ordinal).Select(group => group.ToArray()).ToArray();
    }

    private static IReadOnlyList<RecordQueryDependency>? Dependencies(IEnumerable<RecordQueryField> fields)
    {
        var captured = fields.ToArray();
        return captured.Any(field => field.Dependencies == null) ? null : captured.SelectMany(field => field.Dependencies!).Distinct().ToArray();
    }

    private string ColumnKey(string scopeKey, RecordQueryField[] bindings, string recordKey)
    {
        // Explicit dependencies declare stable field semantics. Undeclared custom readers get an
        // instance token, preventing accidental reuse after replacing a same-key callback.
        var bindingKey = string.Join(",", bindings.Where(field => field.Dependencies == null)
            .Select(field => BindingIdentities.GetValue(field, static _ => new()).Value));
        var fieldKey = bindings[0].Key;
        return $"column:{scopeKey.Length}:{scopeKey}:{fieldKey.Length}:{fieldKey}:{bindingKey}:{Cache.GetVersion(Dependencies(bindings))}:{recordKey}";
    }

    private static string RecordKey(IReadOnlyList<RecordPresentation> records)
    {
        var ids = new int[records.Count];
        for (var index = 0; index < ids.Length; index++) ids[index] = records[index].Id;
        return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(ids.AsSpan())));
    }

    private static string[] CaptureValues(RecordQueryField[] bindings, int id)
    {
        if (bindings.Length == 1)
        {
            var read = bindings[0].ReadValues(id);
            if (read.Count == 0) return [];
            if (read.Count == 1) return string.IsNullOrEmpty(read[0]) ? [] : [read[0]];
            return read.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
        }
        return bindings.SelectMany(field => field.ReadValues(id)).Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<string> ReadPhysical(PhysicalBinding binding, int id)
    {
        if (_session.Value is not { } session || !ReferenceEquals(session.Workspace, workspace))
            return ReadPhysical(binding.TableKeys, binding.FieldName, id);
        if (!session.Columns.TryGetValue(binding, out var columns))
        {
            var captured = new List<PhysicalColumn>();
            foreach (var key in binding.TableKeys)
            {
                if (!workspace.Documents.TryGetValue(key, out var document) ||
                    ReadableField(document, binding.FieldName) is not { } field) continue;
                string[] referenceKeys = field.ReferenceTable is not { Length: > 0 } reference
                    ? [] : reference == "items" ? new[] { "items-low", "items-high", "items-extended" } : [reference];
                captured.Add(new(document.Data.Columns[field.Name]!, session.Rows(document),
                    referenceKeys.Where(workspace.Documents.ContainsKey).Select(workspace.GetDocument).ToArray()));
            }
            session.Columns[binding] = columns = captured.ToArray();
        }
        var result = new List<string>();
        foreach (var column in columns)
        {
            if (!column.Rows.TryGetValue(id, out var row)) continue;
            var value = Text(row[column.Column]);
            if (value.Length == 0) continue;
            result.Add(value);
            if (column.ReferenceDocuments.Length == 0 ||
                !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var referenceId)) continue;
            foreach (var target in column.ReferenceDocuments)
            {
                if (!session.Rows(target).ContainsKey(referenceId)) continue;
                var name = ReferenceName(target, referenceId);
                if (name.Length == 0) continue;
                result.Add(name);
                result.Add($"{referenceId.ToString(CultureInfo.InvariantCulture)}：{name}");
            }
        }
        return result;
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
        if (_session.Value is not { } session || !ReferenceEquals(session.Workspace, workspace))
            return Cache.GetTableRowName(document, id);
        var key = document.Definition.Key;
        if (!session.Names.TryGetValue(key, out var names))
            session.Names[key] = names = Cache.GetNameDirectory(document);
        return names.GetValueOrDefault(id, string.Empty);
    }

    private DataRow? FindRow(TableDocument document, int id)
        => Cache.FindRow(document, id);

    private TableFieldDefinition? ReadableField(TableDocument document, string fieldName)
    {
        if (!document.Data.Columns.Contains(fieldName)) return null;
        var schemaVersion = Cache.GetSchemaRevision(document);
        if (!_fields.TryGetValue(document.Definition.Key, out var index) ||
            !ReferenceEquals(index.Document, document) || index.SchemaVersion != schemaVersion)
        {
            var fields = document.Definition.Fields.Where(field => field.Kind != FieldKind.RawBytes &&
                    field.Policy != FieldPolicy.Preserve && document.Data.Columns.Contains(field.Name))
                .GroupBy(field => field.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            _fields[document.Definition.Key] = index = new(document, schemaVersion, fields);
        }
        return index.Fields.GetValueOrDefault(fieldName);
    }

    private static string Text(object? value)
        => value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
