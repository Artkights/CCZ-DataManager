using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageManager.Core;

public sealed partial class HistoryService : IHistoryService,
    Ccz.HistoryStorage.IHistoryRepository<ProjectProfile, HistoryRecord, HistoryRecord>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _backupContainer;
    private readonly object _gate = new();

    public HistoryService(string backupContainer, string? blobRoot = null)
    {
        _backupContainer = Path.GetFullPath(backupContainer);
        _blobRoot = Path.GetFullPath(blobRoot ?? Path.Combine(_backupContainer, "history-v3", "blobs"));
    }

    public string BackupRoot(ProjectProfile project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(project.DisplayName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (clean.Length == 0) clean = "项目";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project.RootPath)))).Substring(0, 10);
        return Path.Combine(_backupContainer, $"{clean}_{hash}");
    }

    internal bool IsInsideBackup(string path)
        => Path.GetFullPath(path).Equals(_backupContainer, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(path).StartsWith(_backupContainer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public HistoryRecord Record(ProjectProfile project, MutationPlan plan, IReadOnlyList<HistoryTarget> targets)
        => Record(project, plan, targets, []);

    internal HistoryRecord Record(ProjectProfile project, MutationPlan plan, IReadOnlyList<HistoryTarget> targets,
        IReadOnlyList<RenameHistoryTarget> renameTargets)
    {
        var record = new HistoryRecord
        {
            Id = plan.OperationId,
            Kind = plan.Kind,
            Summary = plan.Summary,
            Targets = targets.ToList(),
            RenameTargets = renameTargets.ToList(),
            Metadata = plan.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? []
        };
        lock (_gate)
        {
            WriteV3Record(project, record, "Commit");
        }
        return record;
    }

    public MutationResult Undo(ProjectProfile project, string historyId)
        => ChangeState(project, historyId, useAfter: false);

    public MutationResult Redo(ProjectProfile project, string historyId)
        => ChangeState(project, historyId, useAfter: true);

    private MutationResult ChangeState(ProjectProfile project, string historyId, bool useAfter)
    {
        using var projectLock = Ccz.HistoryStorage.ProjectWriteLock.Acquire(project.RootPath);
        lock (_gate)
        {
            var action = useAfter ? "前进" : "撤回";
            var records = List(project).ToList();
            var source = records.LastOrDefault(record => record.Id == historyId)
                         ?? throw new InvalidOperationException("找不到历史记录：" + historyId);
            if (source.Status != (useAfter ? HistoryStatus.Undone : HistoryStatus.Active))
                throw new InvalidOperationException("历史状态不允许" + action + "。");
            if (source.RenameTargets.Count > 0)
                return RenameTransaction.Execute(source.Metadata.TryGetValue("RenameRoot", out var renameRoot) ? renameRoot : project.RootPath,
                    Guid.NewGuid().ToString("N"), action + "：" + source.Summary,
                    source.RenameTargets, forward: useAfter, persist: () =>
                    {
                        source.Status = useAfter ? HistoryStatus.Active : HistoryStatus.Undone;
                        records.Add(CreateTransition(source, useAfter ? MutationKind.Redo : MutationKind.Undo, action + "：" + source.Summary));
                        Rewrite(project, records);
                    }) with { HistoryId = source.Id };
            if (source.Targets.Count == 0)
                throw new InvalidOperationException("该旧记录没有可恢复的文件快照或改名映射，无法撤回/前进。");
            var restored = new List<HistoryTarget>();
            string? restoreTransaction = null;
            try
            {
                foreach (var target in source.Targets)
                {
                    if (!MatchesState(target.TargetPath, useAfter ? target.BeforeExists : target.AfterExists,
                            useAfter ? target.BeforeSha256 : target.AfterSha256))
                    {
                        source.Status = HistoryStatus.Conflict;
                        Rewrite(project, records);
                        return Conflict(source, action + "失败：当前文件与历史 SHA-256 不一致。", target.TargetPath);
                    }
                    // Validate both directions before changing anything: the opposite
                    // snapshot is needed if a later file or history write fails.
                    if (target.BeforeExists) ResourceFileRecovery.VerifySnapshot(target, false);
                    if (target.AfterExists) ResourceFileRecovery.VerifySnapshot(target, true);
                }
                restoreTransaction = PrepareRestore(project, source, useAfter);
                foreach (var target in source.Targets)
                {
                    ResourceFileRecovery.Verify(target.TargetPath, useAfter ? target.BeforeExists : target.AfterExists,
                        useAfter ? target.BeforeLength : target.AfterLength, useAfter ? target.BeforeSha256 : target.AfterSha256);
                    restored.Add(target);
                    ResourceFileRecovery.Restore(target, useAfter);
                }
                source.Status = useAfter ? HistoryStatus.Active : HistoryStatus.Undone;
                var transition = CreateTransition(source, useAfter ? MutationKind.Redo : MutationKind.Undo, action + "：" + source.Summary);
                // State and transition are installed together; there is no second
                // append that can fail after the new status has become visible.
                CommitRestore(project, source, transition, restoreTransaction);
                return new MutationResult(Guid.NewGuid().ToString("N"), true, "已" + action + "：" + source.Summary, source.Id,
                    source.Targets.Select(target => target.TargetPath).ToArray(), Array.Empty<string>());
            }
            catch (Exception ex)
            {
                var recoveryErrors = ResourceFileRecovery.RestoreAll(restored, !useAfter);
                if (restoreTransaction != null)
                    try { AppendV3State(project, source.Id, recoveryErrors.Count == 0 ? (useAfter ? HistoryStatus.Undone : HistoryStatus.Active) : HistoryStatus.Conflict,
                        source.KeepForever, recoveryErrors.Count == 0 ? "RestoreAbort" : "Conflict", restoreTransaction); }
                    catch (Exception history) { recoveryErrors = recoveryErrors.Concat(["历史状态：" + history.Message]).ToArray(); }
                return new MutationResult(Guid.NewGuid().ToString("N"), false, action + "失败：" + source.Summary, source.Id,
                    recoveryErrors.Count == 0 ? Array.Empty<string>() : restored.Select(target => target.TargetPath).ToArray(),
                    [ex.Message, .. recoveryErrors, recoveryErrors.Count == 0
                        ? "文件已保持或恢复操作前状态，并通过长度与 SHA-256 校验；历史状态未提交。"
                        : "部分文件恢复失败，请使用历史中的写前/写后快照恢复。"]);
            }
        }
    }

    public void SetKeepForever(ProjectProfile project, string historyId, bool keepForever)
    {
        lock (_gate)
        {
            var records = List(project).ToList();
            var record = records.LastOrDefault(item => item.Id == historyId)
                         ?? throw new InvalidOperationException("找不到历史记录：" + historyId);
            record.KeepForever = keepForever;
            Rewrite(project, records);
        }
    }

    public string SnapshotDirectory(ProjectProfile project, string operationId)
    {
        var path = Path.Combine(BackupRoot(project), "History", "snapshots", operationId);
        Directory.CreateDirectory(path);
        return path;
    }

    public string ReportsDirectory(ProjectProfile project)
    {
        var path = Path.Combine(BackupRoot(project), "History", "reports");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private MutationResult Conflict(HistoryRecord record, string message, string path)
    {
        record.Status = HistoryStatus.Conflict;
        return new MutationResult(Guid.NewGuid().ToString("N"), false, message, record.Id, Array.Empty<string>(), [path]);
    }

    private static HistoryRecord CreateTransition(HistoryRecord source, MutationKind kind, string summary)
    {
        var transition = new HistoryRecord
        {
            Kind = kind,
            Summary = summary,
            ParentId = source.Id,
            RenameTargets = source.RenameTargets.ToList(),
            Targets = source.Targets.Select(target => new HistoryTarget
            {
                TargetPath = target.TargetPath,
                SnapshotPath = kind == MutationKind.Undo ? target.AfterSnapshotPath : target.SnapshotPath,
                AfterSnapshotPath = kind == MutationKind.Undo ? target.SnapshotPath : target.AfterSnapshotPath,
                BeforeBlob = kind == MutationKind.Undo ? target.AfterBlob : target.BeforeBlob,
                AfterBlob = kind == MutationKind.Undo ? target.BeforeBlob : target.AfterBlob,
                BlobStoreRoot = target.BlobStoreRoot,
                BeforeSha256 = kind == MutationKind.Undo ? target.AfterSha256 : target.BeforeSha256,
                AfterSha256 = kind == MutationKind.Undo ? target.BeforeSha256 : target.AfterSha256,
                BeforeLength = kind == MutationKind.Undo ? target.AfterLength : target.BeforeLength,
                AfterLength = kind == MutationKind.Undo ? target.BeforeLength : target.AfterLength,
                BeforeExists = kind == MutationKind.Undo ? target.AfterExists : target.BeforeExists,
                AfterExists = kind == MutationKind.Undo ? target.BeforeExists : target.AfterExists,
                BeforeAttributes = kind == MutationKind.Undo ? target.AfterAttributes : target.BeforeAttributes,
                AfterAttributes = kind == MutationKind.Undo ? target.BeforeAttributes : target.AfterAttributes
            }).ToList()
        };
        return transition;
    }

    private string HistoryFile(ProjectProfile project) => Path.Combine(BackupRoot(project), "History", "history.jsonl");

    private static bool MatchesState(string path, bool shouldExist, string sha256)
        => shouldExist ? File.Exists(path) && HashFile(path) == sha256 : !File.Exists(path);

    private void Rewrite(ProjectProfile project, IReadOnlyList<HistoryRecord> records)
    {
        using var projectLock = Ccz.HistoryStorage.ProjectWriteLock.Acquire(project.RootPath);
        var existing = ListV3(project).ToDictionary(record => record.Id, StringComparer.Ordinal);
        foreach (var record in records)
            if (!existing.TryGetValue(record.Id, out var previous)) WriteV3Record(project, record, "Record");
            else if (previous.Status != record.Status || previous.KeepForever != record.KeepForever)
                AppendV3State(project, record.Id, record.Status, record.KeepForever);
    }

}
