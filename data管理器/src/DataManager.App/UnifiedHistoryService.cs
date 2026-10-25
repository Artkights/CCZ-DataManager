using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Images;

namespace DataManager.App;

public sealed record UnifiedHistoryEntry(string Source, string OperationId, DateTimeOffset Timestamp,
    string Operation, string Summary, string Status, IReadOnlyList<string> Targets,
    string? BeforeSha, string? AfterSha, bool CanUndo, bool CanRedo, string Details);

/// <summary>Reads both journals in place and routes restores through their original SHA checks.</summary>
public sealed class UnifiedHistoryService(ProjectWorkspace workspace, ResourceCatalogAdapter resources)
{
    private readonly ResourceHistoryAdapter _resourceHistory = new(resources);

    public IReadOnlyList<UnifiedHistoryEntry> List()
    {
        if (string.IsNullOrWhiteSpace(workspace.RootPath)) return [];
        var entries = workspace.GetHistory().Select(entry => new UnifiedHistoryEntry("数据", entry.OperationId,
            new DateTimeOffset(entry.CreatedAt), entry.OperationKind, entry.Summary, entry.Status switch
            {
                HistoryStatus.Active => "已保存", HistoryStatus.Reverted => "已撤回",
                HistoryStatus.Conflict => "文件冲突", HistoryStatus.Failed => "失败", _ => "待完成"
            }, entry.Files.Select(file => file.RelativePath).ToArray(),
            string.Join(Environment.NewLine, entry.Files.Select(file => file.BeforeHash)),
            string.Join(Environment.NewLine, entry.Files.Select(file => file.AfterHash)),
            entry.Status == HistoryStatus.Active && entry.Files.Count > 0,
            entry.Status == HistoryStatus.Reverted && entry.Files.Count > 0,
            $"版本：{entry.ProfileId}；布局：{entry.VariantId}；修订：{entry.LayoutRevision}{Environment.NewLine}" +
            string.Join(Environment.NewLine, entry.AppearanceSyncMessages.Concat(entry.Tables.Select(change =>
                $"{change.TableName} {change.Id}：{change.FieldName}，{change.OldValue} → {change.NewValue}（0x{change.AbsoluteOffset:X} +{change.ByteLength}）"))))).ToList();
        entries.AddRange(_resourceHistory.List(workspace.RootPath).Select(entry => new UnifiedHistoryEntry("形象",
            entry.OperationId, entry.Timestamp, entry.Operation, entry.Summary, entry.Status, entry.Targets,
            entry.BeforeSha, entry.AfterSha, entry.CanUndo, entry.CanRedo,
            string.Join(Environment.NewLine, entry.Targets))));
        return entries.OrderByDescending(entry => entry.Timestamp).ToArray();
    }

    public (bool Success, string Message) Restore(UnifiedHistoryEntry entry, bool redo)
    {
        if (!(redo ? entry.CanRedo : entry.CanUndo)) return (false, "所选记录的当前状态不支持此操作。");
        if (entry.Source == "数据")
        {
            var result = redo ? workspace.RedoSaved(entry.OperationId) : workspace.UndoSaved(entry.OperationId);
            return (result.Success, result.Message);
        }
        var resourceResult = redo ? _resourceHistory.Redo(workspace.RootPath, entry.OperationId) :
            _resourceHistory.Undo(workspace.RootPath, entry.OperationId);
        return (resourceResult.Success, resourceResult.Message);
    }

    public UnifiedHistoryEntry? FindDefault(bool redo)
    {
        var entries = List().Where(entry => redo ? entry.CanRedo : entry.CanUndo);
        // Replaying multiple undos must start with the earliest original save,
        // otherwise the later save's BeforeHash still belongs to its predecessor.
        return redo ? entries.OrderBy(entry => entry.Timestamp).FirstOrDefault() : entries.FirstOrDefault();
    }
}
