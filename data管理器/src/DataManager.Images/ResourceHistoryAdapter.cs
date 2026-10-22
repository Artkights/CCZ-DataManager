using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Projects executable-owned resource journals into the main editor.</summary>
public sealed class ResourceHistoryAdapter(ResourceCatalogAdapter? resources = null)
{
    public IReadOnlyList<ResourceHistoryEntry> List(string rootPath)
    {
        var profile = new ProjectProfileService().Detect(rootPath);
        return ProjectImageHistory.List(profile).Select(record =>
        {
            var transition = record.Kind is MutationKind.Undo or MutationKind.Redo;
            var recoverable = (record.Targets.Count > 0 || record.RenameTargets.Count > 0) && ProjectImageHistory.CanRestore(profile, record);
            return new ResourceHistoryEntry(record.Id, new DateTimeOffset(record.CreatedAt), OperationName(record.Kind),
                record.Summary, StatusName(record.Status), record.Targets.Select(target => target.TargetPath)
                    .Concat(record.RenameTargets.Select(target => target.SourcePath + " → " + target.TargetPath)).ToArray(),
                string.Join(Environment.NewLine, record.Targets.Select(target => target.BeforeSha256).Concat(record.RenameTargets.Select(target => target.BeforeFingerprint))),
                string.Join(Environment.NewLine, record.Targets.Select(target => target.AfterSha256).Concat(record.RenameTargets.Select(target => target.AfterFingerprint))),
                !transition && recoverable && record.Status == HistoryStatus.Active,
                !transition && recoverable && record.Status == HistoryStatus.Undone, transition);
        }).ToArray();
    }

    public ResourceHistoryOperationResult Undo(string rootPath, string operationId) => Restore(rootPath, operationId, false);
    public ResourceHistoryOperationResult Redo(string rootPath, string operationId) => Restore(rootPath, operationId, true);

    private ResourceHistoryOperationResult Restore(string rootPath, string operationId, bool redo)
    {
        try
        {
            var profile = new ProjectProfileService().Detect(rootPath);
            if (profile.IsReadOnly) return new(false, "项目版本未确认，无法修改资源历史。");
            var service = ProjectImageHistory.PrepareForWrite(profile);
            profile = ProjectImageHistory.Canonicalize(profile);
            var selected = service.List(profile).LastOrDefault(record => record.Id == operationId);
            if (selected == null) return new(false, "找不到资源历史记录。");
            if (selected.Kind is MutationKind.Undo or MutationKind.Redo || selected.Targets.Count == 0 && selected.RenameTargets.Count == 0)
                return new(false, "该记录是历史操作或没有文件快照，请选择原始资源修改记录。");
            ProjectImageHistory.ValidateRestore(profile, selected);
            var result = redo ? service.Redo(profile, operationId) : service.Undo(profile, operationId);
            if (result.Success) resources?.Invalidate(profile.RootPath);
            return new(result.Success, result.Success ? result.Summary : string.Join(Environment.NewLine, new[] { result.Summary }.Concat(result.Diagnostics)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(false, "资源历史操作失败：" + ex.Message);
        }
    }

    private static string OperationName(MutationKind kind) => kind switch
    {
        MutationKind.Append => "新增资源",
        MutationKind.Delete => "删除资源",
        MutationKind.Replace => "替换资源",
        MutationKind.PixelEdit => "图片编辑",
        MutationKind.ImportReference => "导入参考图",
        MutationKind.Rename => "批量重命名",
        MutationKind.AssignmentSave => "形象指定",
        MutationKind.Undo => "撤回资源修改",
        MutationKind.Redo => "重做资源修改",
        MutationKind.WholeFileReplace => "替换资源文件",
        _ => "资源修改"
    };

    private static string StatusName(HistoryStatus status) => status switch
    {
        HistoryStatus.Active => "已保存",
        HistoryStatus.Undone => "已撤回",
        HistoryStatus.Conflict => "文件冲突",
        HistoryStatus.Failed => "失败",
        _ => "未确认"
    };
}

public sealed record ResourceHistoryEntry(string OperationId, DateTimeOffset Timestamp, string Operation, string Summary,
    string Status, IReadOnlyList<string> Targets, string BeforeSha, string AfterSha, bool CanUndo, bool CanRedo, bool IsTransition);

public sealed record ResourceHistoryOperationResult(bool Success, string Message);
