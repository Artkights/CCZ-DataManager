using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>A dependency on a physical column, or on the entire document when FieldName is null.</summary>
public sealed record RecordQueryDependency(string TableKey, string? FieldName = null);

/// <summary>Capture-pinned rows report the active working set and may share storage with ResidentBytes.</summary>
public sealed record WorkspaceReadCacheStatistics(long Hits, long Misses, long Evictions,
    long ResidentBytes, int Entries, long RowIndexBuilds, long NavigationBuilds,
    long MaterializedColumns, long MaterializedValues,
    long CapturePinnedRowBytes = 0, long PeakCapturePinnedRowBytes = 0);

/// <summary>
/// Session-owned, bounded caches of data and indexes. All mutable-table reads and cache mutation
/// belong to the workspace's owning thread; only the resulting immutable query snapshots may be
/// matched on a worker. Entries never retain controls or property-reader delegates.
/// </summary>
public sealed class WorkspaceReadCache : IDisposable
{
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;
    private const string ExternalDependency = "$external";
    private static readonly ConditionalWeakTable<ProjectWorkspace, WorkspaceReadCache> Sessions = new();
    private static long _nextDocumentIdentity;
    private readonly ProjectWorkspace _workspace;
    private readonly Dictionary<string, DocumentEntry> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheItem> _recent = new();
    private long _budgetBytes = DefaultBudgetBytes;
    private long _residentBytes, _hits, _misses, _evictions;
    private long _rowIndexBuilds, _navigationBuilds, _materializedColumns, _materializedValues;
    private long _capturePinnedRowBytes, _peakCapturePinnedRowBytes;
    private long _externalVersion;
    private bool _disposed;

    private sealed record CacheItem(string Key, object Value, long Bytes);
    private sealed record NavigationData(IReadOnlyList<RecordPresentation> Records, IReadOnlyDictionary<int, string> Names);

    private WorkspaceReadCache(ProjectWorkspace workspace) => _workspace = workspace;

    public static WorkspaceReadCache For(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Sessions.GetValue(workspace, static value => new(value));
    }

    /// <summary>Release every old document subscription and cached value on close/reopen.</summary>
    public static void Reset(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Sessions.TryGetValue(workspace, out var cache)) cache.Dispose();
        Sessions.Remove(workspace);
    }

    public long BudgetBytes
    {
        get => _budgetBytes;
        set { ObjectDisposedException.ThrowIf(_disposed, this); _budgetBytes = Math.Max(0, value); Trim(); }
    }

    public WorkspaceReadCacheStatistics Statistics => new(_hits, _misses, _evictions, _residentBytes,
        _items.Count, _rowIndexBuilds, _navigationBuilds, _materializedColumns, _materializedValues,
        _capturePinnedRowBytes, _peakCapturePinnedRowBytes);

    /// <summary>Explicitly invalidate names provided by native resources after an external-file refresh.</summary>
    public void InvalidateExternal() => _externalVersion++;

    /// <summary>
    /// Null dependencies conservatively observe the whole workspace; an empty list observes no data.
    /// Row structure and document identity are included even for a single-column dependency.
    /// </summary>
    public string GetVersion(IEnumerable<RecordQueryDependency>? dependencies = null)
        => Version(dependencies, schemaOnly: false);

    public string GetSchemaVersion(IEnumerable<RecordQueryDependency>? dependencies = null)
        => Version(dependencies, schemaOnly: true);

    /// <summary>Share an immutable business directory within the same bounded cache. The factory is never retained.</summary>
    public T GetOrCreateData<T>(string key, IEnumerable<RecordQueryDependency>? dependencies,
        Func<T> build, Func<T, long> estimate) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(estimate);
        var cacheKey = $"data:{key.Length}:{key}:{GetVersion(dependencies)}";
        if (TryGet<T>(cacheKey, out var existing)) return existing;
        var created = build();
        ArgumentNullException.ThrowIfNull(created);
        Put(cacheKey, created, Math.Max(0, estimate(created)));
        return created;
    }

    private string Version(IEnumerable<RecordQueryDependency>? dependencies, bool schemaOnly)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var all = dependencies == null;
        if (all) PruneDocuments();
        var requested = dependencies ?? _workspace.Documents.Keys.Select(key => new RecordQueryDependency(key));
        var result = new StringBuilder();
        foreach (var dependency in requested.Distinct().OrderBy(value => value.TableKey, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.FieldName, StringComparer.Ordinal))
        {
            var key = dependency.TableKey;
            result.Append(key.Length).Append(':').Append(key).Append('=');
            if (key == ExternalDependency)
            {
                result.Append(_externalVersion).Append(';');
                continue;
            }
            if (!TryDocument(key, out var entry)) { result.Append("missing;"); continue; }
            if (schemaOnly || dependency.FieldName == null) entry.CheckSchema();
            else { entry.CheckColumn("ID"); entry.CheckColumn(dependency.FieldName); }
            result.Append(entry.Identity).Append('/').Append(entry.SchemaVersion);
            if (!schemaOnly)
            {
                result.Append('/').Append(entry.StructureVersion).Append('/');
                if (dependency.FieldName is { } field)
                    result.Append(field.Length).Append(':').Append(field).Append('/').Append(entry.ColumnVersion(field));
                else result.Append('*').Append(entry.ValueVersion);
            }
            result.Append(';');
        }
        if (all) result.Append("external=").Append(_externalVersion);
        return result.ToString();
    }

    internal long GetSchemaRevision(TableDocument document) => Observe(document).SchemaVersion;
    internal void ValidateColumn(TableDocument document, string fieldName) => Observe(document).CheckColumn(fieldName);

    public DataRow? FindRow(TableDocument document, int id)
        => GetRowDirectory(document).GetValueOrDefault(id);

    // A capture pins its current row directories separately from the persistent LRU. Otherwise
    // a zero/small cache budget can rebuild a whole table for every captured property value.
    internal RowDirectoryLease PinRowDirectory(TableDocument document)
    {
        var rows = GetRowDirectory(document);
        var bytes = 128L + rows.Count * 56L;
        _capturePinnedRowBytes += bytes;
        _peakCapturePinnedRowBytes = Math.Max(_peakCapturePinnedRowBytes, _capturePinnedRowBytes);
        return new(this, rows, bytes);
    }

    internal sealed class RowDirectoryLease(WorkspaceReadCache owner, IReadOnlyDictionary<int, DataRow> rows, long bytes) : IDisposable
    {
        private WorkspaceReadCache? _owner = owner;
        public IReadOnlyDictionary<int, DataRow> Rows { get; } = rows;
        public void Dispose()
        {
            if (_owner is not { } current) return;
            _owner = null;
            current._capturePinnedRowBytes -= bytes;
        }
    }

    private IReadOnlyDictionary<int, DataRow> GetRowDirectory(TableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entry = Observe(document);
        if (!document.Data.Columns.Contains("ID")) return new Dictionary<int, DataRow>();
        if (!TryGet<Dictionary<int, DataRow>>(entry.IndexKey, out var rows))
        {
            rows = new();
            var column = document.Data.Columns["ID"]!;
            foreach (DataRow row in document.Data.Rows)
                if (row.RowState != DataRowState.Deleted && TryId(row[column], out var rowId)) rows.TryAdd(rowId, row);
            Put(entry.IndexKey, rows, 128L + rows.Count * 56L);
            _rowIndexBuilds++;
        }
        return rows;
    }

    public IReadOnlyList<RecordPresentation> GetNavigationRecords(BusinessPageKind kind)
    {
        PruneDocuments();
        var keys = kind switch
        {
            BusinessPageKind.Persons => new[] { "persons" },
            BusinessPageKind.JobSeries => ["job-series"],
            BusinessPageKind.DetailedJobs => ["detailed-jobs"],
            BusinessPageKind.Items => ["items-low", "items-high", "items-extended"],
            BusinessPageKind.Strategies => ["strategies"],
            BusinessPageKind.Shop => ["shops"],
            BusinessPageKind.ExtendData => ["effect-archive"],
            _ => Array.Empty<string>()
        };
        var version = GetVersion(GetNavigationDependencies(keys));
        var cacheKey = $"navigation:{kind}:{version}";
        if (TryGet<IReadOnlyList<RecordPresentation>>(cacheKey, out var records)) return records;
        records = Array.AsReadOnly(keys.Where(_workspace.Documents.ContainsKey).Select(_workspace.GetDocument)
            .SelectMany(GetTableRecords).DistinctBy(record => record.Id).OrderBy(record => record.Id).ToArray());
        Put(cacheKey, records, EstimateRecords(records));
        return records;
    }

    public IReadOnlyList<RecordPresentation> GetTableRecords(TableDocument document) => Navigation(document).Records;

    public string GetTableRowName(TableDocument document, int id)
        => Navigation(document).Names.GetValueOrDefault(id, string.Empty);

    internal IReadOnlyDictionary<int, string> GetNameDirectory(TableDocument document) => Navigation(document).Names;

    private NavigationData Navigation(TableDocument document)
    {
        var entry = Observe(document);
        var cacheKey = $"names:{entry.Identity}:{GetVersion(GetNavigationDependencies([document.Definition.Key]))}";
        if (TryGet<NavigationData>(cacheKey, out var navigation)) return navigation;
        // Presentation resolves the table's business axis (shared quote slots, matrices, parent IDs).
        // A service is local to this build so its private indexes cannot survive a draft revision.
        var records = new RecordPresentationService(_workspace).GetTableRecords(document);
        var names = records.GroupBy(record => record.Id).ToDictionary(group => group.Key, group => group.First().Name);
        navigation = new(records, names);
        Put(cacheKey, navigation, EstimateRecords(records) + 128L + names.Count * 48L);
        _navigationBuilds++;
        return navigation;
    }

    /// <summary>Identity/name dependencies follow the same physical and business axes as presentation.</summary>
    public IReadOnlyList<RecordQueryDependency> GetNavigationDependencies(IEnumerable<string> tableKeys)
    {
        var result = new HashSet<RecordQueryDependency>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in tableKeys) Add(key);
        return result.ToArray();

        void Add(string key)
        {
            if (!visited.Add(key)) return;
            if (key == "items") { Add("items-low"); Add("items-high"); Add("items-extended"); return; }
            result.Add(new(key, "ID"));
            if (key == "person-critical-quotes")
            {
                result.Add(new(key, "台词"));
                result.Add(new("person-critical-special-map", "人物ID"));
                Add("persons");
                return;
            }
            if (key == "person-critical-special-map")
            { result.Add(new(key, "人物ID")); Add("persons"); return; }
            if (key == "person-retreat-quotes")
            { result.Add(new(key, "台词")); Add("persons"); return; }
            if (key is "person-r-assign" or "person-s-assign" or "person-r-images" or "person-s-images" or
                "effect-archive" or "strategy-study-extended") { Add("persons"); return; }
            if (key == "shops") { Add("scenario-names"); return; }
            if (key is "strategy-big-animation" or "strategy-small-animation") { Add("strategies"); return; }
            if (key == "effect-messages") { result.Add(new(ExternalDependency)); return; }
            if (key == EquipmentTypeCatalogService.DisplayTableKey)
            { result.Add(new(EquipmentTypeCatalogService.NamesTableKey, "名称")); return; }
            if (key is "job-series-attributes" or "strategy-terrain-availability" or "job-series-accessory-groups" ||
                key.StartsWith("item-effect-names", StringComparison.Ordinal)) return;
            if (!_workspace.Documents.TryGetValue(key, out var document)) { result.Add(new(key)); return; }
            var field = document.Definition.Fields.FirstOrDefault(value => value.Name is "名称" or "战役名称" or "策略名称");
            if (field != null) result.Add(new(key, field.Name));
            else if (document.Definition.ParentKey is { Length: > 0 } parent) Add(parent);
        }
    }

    internal bool TryGetColumn(string key, out string[][] values) => TryGet(key, out values);

    internal void StoreColumn(string key, string[][] values)
    {
        var bytes = 64L + values.Length * 8L;
        long count = 0;
        foreach (var row in values)
        {
            bytes += 24L + row.Length * 8L;
            foreach (var value in row) { bytes += 24L + value.Length * 2L; count++; }
        }
        Put(key, values, bytes);
        _materializedColumns++;
        _materializedValues += count;
    }

    private bool TryGet<T>(string key, out T value) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_items.TryGetValue(key, out var node) && node.Value.Value is T found)
        {
            _recent.Remove(node); _recent.AddFirst(node); _hits++;
            value = found; return true;
        }
        _misses++; value = null!; return false;
    }

    private void Put(string key, object value, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_items.Remove(key, out var previous))
        { _residentBytes -= previous.Value.Bytes; _recent.Remove(previous); }
        bytes += 24L + key.Length * 2L;
        // A single very large full-scope query may still run, but cannot evict every useful small entry.
        if (bytes > _budgetBytes) return;
        var item = new CacheItem(key, value, bytes);
        _items.Add(key, _recent.AddFirst(item)); _residentBytes += bytes;
        Trim();
    }

    private void Trim()
    {
        while (_residentBytes > _budgetBytes && _recent.Last is { } node)
        {
            _recent.RemoveLast(); _items.Remove(node.Value.Key);
            _residentBytes -= node.Value.Bytes; _evictions++;
        }
    }

    private DocumentEntry Observe(TableDocument document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = document.Definition.Key;
        if (_documents.TryGetValue(key, out var existing))
        {
            if (ReferenceEquals(existing.Document, document)) return existing;
            existing.Dispose();
            RemoveRowIndexes(existing.Identity);
        }
        return _documents[key] = new(document);
    }

    private bool TryDocument(string key, out DocumentEntry entry)
    {
        if (_workspace.Documents.TryGetValue(key, out var document)) { entry = Observe(document); return true; }
        if (_documents.Remove(key, out var old)) { old.Dispose(); RemoveRowIndexes(old.Identity); }
        entry = null!; return false;
    }

    private void PruneDocuments()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var pair in _documents.ToArray())
            if (!_workspace.Documents.TryGetValue(pair.Key, out var document) || !ReferenceEquals(document, pair.Value.Document))
            { pair.Value.Dispose(); RemoveRowIndexes(pair.Value.Identity); _documents.Remove(pair.Key); }
    }

    private void RemoveRowIndexes(long identity)
    {
        var prefix = $"rows:{identity}:";
        foreach (var pair in _items.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _residentBytes -= pair.Value.Value.Bytes;
            _recent.Remove(pair.Value); _items.Remove(pair.Key);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var entry in _documents.Values) entry.Dispose();
        _documents.Clear(); _items.Clear(); _recent.Clear(); _residentBytes = 0;
        _disposed = true;
    }

    private static long EstimateRecords(IReadOnlyList<RecordPresentation> records)
        => 64L + records.Sum(record => 80L + (record.Name.Length + record.SearchText.Length) * 2L);

    private static bool TryId(object? value, out int id) => int.TryParse(
        Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

    private sealed class DocumentEntry : IDisposable
    {
        private sealed record SchemaColumn(DataColumn Column, string Name, Type Type, string Expression);
        private readonly Dictionary<string, long> _columns = new(StringComparer.Ordinal);
        private readonly HashSet<DataRow> _columnReported = new();
        private SchemaColumn[] _schema = [];
        private Dictionary<string, SchemaColumn> _schemaNames = new(StringComparer.Ordinal);
        private long _unknownVersion;
        public DocumentEntry(TableDocument document)
        {
            Document = document;
            Identity = Interlocked.Increment(ref _nextDocumentIdentity);
            CaptureSchema();
            UpdateIndexKey();
            var table = document.Data;
            table.ColumnChanged += ColumnChanged;
            table.RowChanged += RowChanged;
            table.RowDeleted += RowDeleted;
            table.TableCleared += Cleared;
            table.Columns.CollectionChanged += SchemaChanged;
        }

        public TableDocument Document { get; }
        public long Identity { get; }
        public long SchemaVersion { get; private set; }
        public long StructureVersion { get; private set; }
        public long ValueVersion { get; private set; }
        public string IndexKey { get; private set; } = string.Empty;
        public long ColumnVersion(string field) => _columns.GetValueOrDefault(field) + _unknownVersion;

        public void CheckColumn(string field)
        {
            var column = Document.Data.Columns[field];
            _schemaNames.TryGetValue(field, out var known);
            if (!ReferenceEquals(column, known?.Column) || known != null && !SameColumn(known, column!)) CheckSchema();
        }

        public void CheckSchema()
        {
            var columns = Document.Data.Columns;
            var changed = columns.Count != _schema.Length;
            if (!changed)
                for (var index = 0; index < _schema.Length; index++)
                    if (!SameColumn(_schema[index], columns[index])) { changed = true; break; }
            if (changed) SchemaChanged(this, new(CollectionChangeAction.Refresh, null));
        }

        private static bool SameColumn(SchemaColumn known, DataColumn current)
            => ReferenceEquals(known.Column, current) && known.Name == current.ColumnName &&
                known.Type == current.DataType && known.Expression == current.Expression;

        private void CaptureSchema()
        {
            _schema = Document.Data.Columns.Cast<DataColumn>()
                .Select(column => new SchemaColumn(column, column.ColumnName, column.DataType, column.Expression)).ToArray();
            _schemaNames = _schema.ToDictionary(column => column.Name, StringComparer.Ordinal);
        }

        private void ColumnChanged(object? sender, DataColumnChangeEventArgs e)
        {
            var field = e.Column?.ColumnName;
            if (field == null) { UnknownChange(); return; }
            ValueVersion++;
            _columns[field] = _columns.GetValueOrDefault(field) + 1;
            _columnReported.Add(e.Row);
            if (field == "ID") StructureChanged();
        }

        private void RowChanged(object? sender, DataRowChangeEventArgs e)
        {
            var reported = _columnReported.Remove(e.Row);
            if (e.Action is DataRowAction.Add or DataRowAction.Rollback) { UnknownChange(); return; }
            // LoadDataRow and similar bulk operations may bypass individual column notifications.
            if (e.Action == DataRowAction.Change && !reported) UnknownChange();
        }

        private void RowDeleted(object? sender, DataRowChangeEventArgs e)
        { _columnReported.Remove(e.Row); UnknownChange(); }
        private void Cleared(object? sender, DataTableClearEventArgs e)
        { _columnReported.Clear(); UnknownChange(); }
        private void SchemaChanged(object? sender, CollectionChangeEventArgs e)
        { CaptureSchema(); SchemaVersion++; _columns.Clear(); UnknownChange(); }
        private void UnknownChange() { ValueVersion++; _unknownVersion++; StructureChanged(); }
        private void StructureChanged() { StructureVersion++; UpdateIndexKey(); }
        private void UpdateIndexKey() => IndexKey = $"rows:{Identity}:{StructureVersion}:{SchemaVersion}";

        public void Dispose()
        {
            var table = Document.Data;
            table.ColumnChanged -= ColumnChanged;
            table.RowChanged -= RowChanged;
            table.RowDeleted -= RowDeleted;
            table.TableCleared -= Cleared;
            table.Columns.CollectionChanged -= SchemaChanged;
            _columnReported.Clear();
        }
    }
}
