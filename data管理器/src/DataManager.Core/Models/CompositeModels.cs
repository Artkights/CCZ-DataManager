namespace DataManager.Core.Models;

/// <summary>
/// A cell edit that belongs to a page-level operation.  A page may map to
/// several physical tables and all edits are validated before any one of them
/// is applied.
/// </summary>
public sealed record CompositeCellEdit(
    string TableKey,
    int RowIndex,
    string FieldName,
    object? NewValue);

public sealed record CompositeApplyResult(
    bool Applied,
    int ChangedCells,
    IReadOnlyList<string> Messages)
{
    public static CompositeApplyResult NoChanges(string message = "没有需要应用的修改。")
        => new(true, 0, [message]);
}
