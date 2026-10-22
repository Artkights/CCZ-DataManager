using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Domain;

/// <summary>Collects and atomically applies edits spanning several physical tables.</summary>
public sealed class CompositePageTransaction
{
    private readonly ProjectWorkspace _workspace;
    private readonly HashSet<string> _allowedTables;
    private readonly List<CompositeCellEdit> _edits = [];

    internal CompositePageTransaction(ProjectWorkspace workspace, string description, IReadOnlyList<string> allowedTables)
    {
        _workspace = workspace;
        Description = description;
        _allowedTables = new HashSet<string>(allowedTables, StringComparer.OrdinalIgnoreCase);
    }

    public string Description { get; }
    public IReadOnlyList<CompositeCellEdit> Edits => _edits;

    public CompositePageTransaction Set(string tableKey, int rowIndex, string fieldName, object? value)
    {
        if (!_allowedTables.Contains(tableKey)) throw new InvalidOperationException($"表 {tableKey} 不属于当前业务页。请先通过对应页面创建事务。");
        var existing = _edits.FindIndex(edit => edit.TableKey.Equals(tableKey, StringComparison.OrdinalIgnoreCase) && edit.RowIndex == rowIndex && edit.FieldName.Equals(fieldName, StringComparison.Ordinal));
        var edit = new CompositeCellEdit(tableKey, rowIndex, fieldName, value);
        if (existing >= 0) _edits[existing] = edit;
        else _edits.Add(edit);
        return this;
    }

    public CompositePageTransaction SetById(string tableKey, int id, string fieldName, object? value)
    {
        if (!_workspace.Documents.TryGetValue(tableKey, out var document)) throw new InvalidOperationException($"表未加载：{tableKey}");
        // A profile's BeginId is only a geometry hint; sparse or variant
        // tables can carry an explicit ID column that does not equal the row
        // ordinal. Resolve the physical row by its actual primary key.
        var rowMatch = document.Data.Rows.Cast<System.Data.DataRow>()
            .Select((row, index) => new { Row = row, Index = index })
            .FirstOrDefault(item => Convert.ToInt32(item.Row["ID"], System.Globalization.CultureInfo.InvariantCulture) == id);
        if (rowMatch is null || rowMatch.Index < 0 || rowMatch.Index >= document.Data.Rows.Count)
            throw new KeyNotFoundException($"表 {tableKey} 中不存在 ID={id}。");
        return Set(tableKey, rowMatch.Index, fieldName, value);
    }

    public CompositeApplyResult Apply() => _workspace.ApplyCompositeTransaction(Description, _edits);

    public void Clear() => _edits.Clear();
}
