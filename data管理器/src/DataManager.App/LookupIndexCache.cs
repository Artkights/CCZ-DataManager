using System.Data;
using System.Globalization;
using DataManager.Core.Models;

namespace DataManager.App;

/// <summary>Indexes immutable record IDs while invalidating names when a draft changes.</summary>
internal sealed class LookupIndexCache : IDisposable
{
    private readonly Dictionary<TableDocument, Entry> _entries = [];
    private readonly Dictionary<string, ReferenceEntry> _references = new(StringComparer.Ordinal);
    private static readonly string[] ItemTableKeys = ["items-low", "items-high", "items-extended"];

    public void Prune(IEnumerable<TableDocument> documents)
    {
        var current = documents.ToHashSet();
        foreach (var obsolete in _entries.Keys.Where(document => !current.Contains(document)).ToArray())
        {
            _entries[obsolete].Dispose();
            _entries.Remove(obsolete);
        }
        foreach (var key in _references.Where(pair => pair.Value.Documents.Any(document => !current.Contains(document)))
                     .Select(pair => pair.Key).ToArray())
            _references.Remove(key);
    }

    public (DataRow Row, int Index)? Find(TableDocument document, int id)
    {
        var entry = Get(document);
        entry.Rows ??= document.Data.Rows.Cast<DataRow>().Select((row, index) => (row, index))
            .Where(item => item.row.RowState != DataRowState.Deleted)
            .GroupBy(item => Convert.ToInt32(item.row["ID"], CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => (group.First().row, group.First().index));
        return entry.Rows.TryGetValue(id, out var row) ? row : null;
    }

    public IReadOnlyList<IdNameOption> Options(TableDocument document)
    {
        var entry = Get(document);
        if (entry.Options != null) return entry.Options;
        var name = document.Data.Columns.Cast<DataColumn>().FirstOrDefault(column =>
            column.ColumnName is "名称" or "战役名称" || column.ColumnName.EndsWith("名称", StringComparison.Ordinal));
        return entry.Options = document.Data.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted)
            .Select(row => new IdNameOption(Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture),
                name == null ? string.Empty : Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? string.Empty)).ToArray();
    }

    /// <summary>Reuse merged lists only while every source document and its name snapshot are current.</summary>
    public IReadOnlyList<IdNameOption> ReferenceOptions(string tableKey, IReadOnlyDictionary<string, TableDocument> documents)
    {
        var count = tableKey == "items" ? ItemTableKeys.Length : 1;
        if (_references.TryGetValue(tableKey, out var cached))
        {
            var source = 0;
            var valid = true;
            for (var index = 0; index < count; index++)
            {
                var key = tableKey == "items" ? ItemTableKeys[index] : tableKey;
                if (!documents.TryGetValue(key, out var document)) continue;
                if (source >= cached.Documents.Length || !ReferenceEquals(document, cached.Documents[source]) ||
                    !ReferenceEquals(Options(document), cached.Sources[source]))
                { valid = false; break; }
                source++;
            }
            if (valid && source == cached.Documents.Length) return cached.Options;
        }

        var sources = new List<IReadOnlyList<IdNameOption>>(count);
        var sourceDocuments = new List<TableDocument>(count);
        var result = new List<IdNameOption>();
        var ids = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var key = tableKey == "items" ? ItemTableKeys[index] : tableKey;
            if (!documents.TryGetValue(key, out var document)) continue;
            var options = Options(document);
            sourceDocuments.Add(document); sources.Add(options);
            foreach (var option in options)
                if (ids.Add(option.Id)) result.Add(option);
        }
        if (tableKey == "persons") result.Add(new IdNameOption(65535, "未指定"));
        if (tableKey == "items") result.Add(new IdNameOption(255, "空物品槽"));
        var entry = new ReferenceEntry(sourceDocuments.ToArray(), sources.ToArray(), result.ToArray());
        _references[tableKey] = entry;
        return entry.Options;
    }

    private Entry Get(TableDocument document)
    {
        if (_entries.TryGetValue(document, out var entry)) return entry;
        entry = new Entry(document.Data);
        _entries.Add(document, entry);
        return entry;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
        _references.Clear();
    }

    private sealed record ReferenceEntry(TableDocument[] Documents, IReadOnlyList<IdNameOption>[] Sources, IdNameOption[] Options);

    private sealed class Entry : IDisposable
    {
        private readonly DataTable _table;
        public Dictionary<int, (DataRow Row, int Index)>? Rows;
        public IdNameOption[]? Options;
        public Entry(DataTable table)
        {
            _table = table;
            table.RowChanged += Changed;
            table.RowDeleted += Changed;
            table.ColumnChanged += ColumnChanged;
            table.TableCleared += Cleared;
            table.Columns.CollectionChanged += ColumnsChanged;
        }
        private void Changed(object? sender, DataRowChangeEventArgs args)
        {
            if (args.Action is DataRowAction.Add or DataRowAction.Delete or DataRowAction.Rollback)
            { Rows = null; Options = null; }
        }
        private void ColumnChanged(object? sender, DataColumnChangeEventArgs args)
        {
            var name = args.Column?.ColumnName ?? string.Empty;
            if (name == "ID") { Rows = null; Options = null; }
            else if (name.EndsWith("名称", StringComparison.Ordinal)) Options = null;
        }
        private void Cleared(object? sender, DataTableClearEventArgs args) { Rows = null; Options = null; }
        private void ColumnsChanged(object? sender, System.ComponentModel.CollectionChangeEventArgs args) { Rows = null; Options = null; }
        public void Dispose()
        {
            _table.RowChanged -= Changed;
            _table.RowDeleted -= Changed;
            _table.ColumnChanged -= ColumnChanged;
            _table.TableCleared -= Cleared;
            _table.Columns.CollectionChanged -= ColumnsChanged;
        }
    }
}
