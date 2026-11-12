using System.Text.Json;
using Ccz.HistoryStorage;

namespace ImageManager.Core;

public sealed partial class HistoryService
{
    private readonly string _blobRoot;
    private sealed record StoredImage(int StorageVersion, HistoryRecord Record, bool RollbackUseAfter = false, HistoryStatus OriginalStatus = HistoryStatus.Failed);
    private sealed record ImageState(HistoryStatus Status, bool KeepForever, HistoryRecord? Transition = null);
    public string V3Root(ProjectProfile project) => Path.Combine(BackupRoot(project), "History", "history-v3");
    private IHistoryLedger Ledger(ProjectProfile project) => new HistoryLedger(Path.Combine(V3Root(project), "ledger"));
    private IHistoryBlobStore Blobs => new BlobStore(_blobRoot);
    public bool HasHistory(ProjectProfile project) => File.Exists(HistoryFile(project)) || Ledger(project).Exists;
    public IReadOnlyList<HistoryRecord> ListMetadata(ProjectProfile project) => List(project);
    public HistoryRecord LoadOperation(ProjectProfile project, string operationId) => List(project).LastOrDefault(record => record.Id == operationId)
        ?? throw new InvalidOperationException("找不到资源历史记录：" + operationId);

    public IReadOnlyList<HistoryRecord> List(ProjectProfile project)
    {
        using var projectLock = ProjectWriteLock.Acquire(project.RootPath);
        Recover(project);
        return ListV3(project);
    }
    private IReadOnlyList<HistoryRecord> ListV3(ProjectProfile project)
    {
        var records = new Dictionary<string, HistoryRecord>(StringComparer.Ordinal);
        foreach (var position in LegacyIndex.Read(HistoryFile(project), Path.Combine(V3Root(project), "index", "legacy.json")))
        {
            try
            {
                var record = position.Metadata.Deserialize<HistoryRecord>(JsonOptions);
                if (record != null && !string.IsNullOrWhiteSpace(record.Id)) records[record.Id] = record;
            }
            catch (JsonException) { }
        }
        foreach (var item in Ledger(project).Read())
            if (item.Kind is "Prepare" or "Commit" or "Record")
            {
                var stored = item.Payload.Deserialize<StoredImage>(JsonOptions) ?? throw new InvalidDataException("资源历史记录无效。");
                if (stored.StorageVersion != 3 || stored.Record.Id != item.OperationId) throw new InvalidDataException("资源历史身份或版本无效。");
                if (item.Kind == "Prepare") stored.Record.Status = HistoryStatus.Failed;
                records[item.OperationId] = stored.Record;
            }
            else if (item.Kind is "State" or "RestoreCommit" or "RestoreAbort" or "Abort" or "Conflict")
            {
                var state = item.Payload.Deserialize<ImageState>(JsonOptions) ?? throw new InvalidDataException("资源历史状态无效。");
                if (records.TryGetValue(item.OperationId, out var record)) { record.Status = state.Status; record.KeepForever = state.KeepForever; }
                if (state.Transition != null) records[state.Transition.Id] = state.Transition;
            }
        foreach (var record in records.Values) BindAndValidate(project, record);
        return records.Values.ToArray();
    }
    private void BindAndValidate(ProjectProfile project, HistoryRecord record)
    {
        foreach (var target in record.Targets)
        {
            StoragePaths.ValidateInside(target.TargetPath, project.RootPath);
            if (target.BeforeBlob != null || target.AfterBlob != null) target.BlobStoreRoot = _blobRoot;
            if (!string.IsNullOrEmpty(target.SnapshotPath)) StoragePaths.ValidateInside(target.SnapshotPath, BackupRoot(project));
            if (!string.IsNullOrEmpty(target.AfterSnapshotPath)) StoragePaths.ValidateInside(target.AfterSnapshotPath, BackupRoot(project));
        }
    }
    private void WriteV3Record(ProjectProfile project, HistoryRecord record, string kind)
    {
        using var projectLock = ProjectWriteLock.Acquire(project.RootPath);
        BindAndValidate(project, record);
        foreach (var target in record.Targets)
        {
            if (target.BeforeExists && target.BeforeBlob == null) target.BeforeBlob = Blobs.PutFile(target.SnapshotPath);
            if (target.AfterExists && target.AfterBlob == null) target.AfterBlob = Blobs.PutFile(target.AfterSnapshotPath);
            target.BlobStoreRoot = _blobRoot;
            if (target.BeforeExists) ResourceFileRecovery.VerifySnapshot(target, false);
            if (target.AfterExists) ResourceFileRecovery.VerifySnapshot(target, true);
        }
        Ledger(project).Append(kind, record.Id, new StoredImage(3, record));
    }
    public HistoryTarget CaptureTarget(ProjectProfile project, string targetPath, byte[]? before, byte[]? after,
        FileAttributes? beforeAttributes = null, FileAttributes? afterAttributes = null)
    {
        StoragePaths.ValidateInside(targetPath, project.RootPath);
        var oldBlob = before == null ? null : Blobs.Put(before);
        var newBlob = after == null ? null : Blobs.Put(after);
        return new HistoryTarget { TargetPath = targetPath, BeforeExists = before != null, AfterExists = after != null,
            BeforeBlob = oldBlob, AfterBlob = newBlob, BlobStoreRoot = _blobRoot, BeforeSha256 = oldBlob?.Hash ?? "",
            AfterSha256 = newBlob?.Hash ?? "", BeforeLength = before?.LongLength ?? 0, AfterLength = after?.LongLength ?? 0,
            BeforeAttributes = beforeAttributes, AfterAttributes = afterAttributes };
    }
    internal HistoryTarget CaptureMutationTarget(ProjectProfile project, MutationFile file)
    {
        StoragePaths.ValidateInside(file.TargetPath, project.RootPath);
        file.ExpectedSource.Verify(file.TargetPath);
        var before = File.Exists(file.TargetPath) ? Blobs.PutFile(file.TargetPath) : null;
        if (before != null && (before.Hash != file.ExpectedSource.Sha256 || before.Length != file.ExpectedSource.Length))
            throw new IOException("资源在历史快照期间被外部修改。");
        var after = file.DeleteTarget ? null : Blobs.Put(file.CandidateBytes);
        return new HistoryTarget { TargetPath = Path.GetFullPath(file.TargetPath), BeforeBlob = before, AfterBlob = after,
            BlobStoreRoot = _blobRoot, BeforeExists = before != null, AfterExists = after != null, BeforeSha256 = before?.Hash ?? "",
            AfterSha256 = after?.Hash ?? "", BeforeLength = before?.Length ?? 0, AfterLength = after?.Length ?? 0,
            BeforeAttributes = before != null ? File.GetAttributes(file.TargetPath) : null,
            AfterAttributes = before != null ? File.GetAttributes(file.TargetPath) : FileAttributes.Normal };
    }
    internal void PrepareMutation(ProjectProfile project, MutationPlan plan, IReadOnlyList<HistoryTarget> targets)
    {
        var record = new HistoryRecord { Id = plan.OperationId, Kind = plan.Kind, Summary = plan.Summary, Targets = targets.ToList(),
            Metadata = plan.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [] };
        BindAndValidate(project, record);
        foreach (var target in record.Targets)
        {
            if (!MatchesState(target.TargetPath, target.BeforeExists, target.BeforeSha256))
                throw new IOException("准备资源历史事务时目标基线已改变：" + target.TargetPath);
            if (target.BeforeExists) ResourceFileRecovery.VerifySnapshot(target, false);
            if (target.AfterExists) ResourceFileRecovery.VerifySnapshot(target, true);
        }
        Ledger(project).Append("Prepare", record.Id, new StoredImage(3, record));
    }
    internal void AbortMutation(ProjectProfile project, string operation, bool conflict)
        => AppendV3State(project, operation, conflict ? HistoryStatus.Conflict : HistoryStatus.Failed, false, conflict ? "Conflict" : "Abort");
    private string PrepareRestore(ProjectProfile project, HistoryRecord record, bool useAfter)
    {
        var transaction = Guid.NewGuid().ToString("N");
        Ledger(project).Append("RestorePrepare", record.Id, new StoredImage(3, record, !useAfter, record.Status), transaction);
        return transaction;
    }
    private void CommitRestore(ProjectProfile project, HistoryRecord record, HistoryRecord transition, string transaction)
        => Ledger(project).Append("RestoreCommit", record.Id, new ImageState(record.Status, record.KeepForever, transition), transaction);
    private void AppendV3State(ProjectProfile project, string operation, HistoryStatus status, bool keepForever, string kind = "State", string? transaction = null)
        => Ledger(project).Append(kind, operation, new ImageState(status, keepForever), transaction);

    public bool Recover(ProjectProfile project)
    {
        var ledger = Ledger(project); if (!ledger.Exists) return false;
        using var projectLock = ProjectWriteLock.Acquire(project.RootPath);
        var pending = new Dictionary<string, LedgerEvent>(StringComparer.Ordinal);
        foreach (var item in ledger.Read())
            if (item.Kind is "Prepare" or "RestorePrepare") pending[item.TransactionId] = item;
            else if (item.Kind is "Commit" or "Abort" or "RestoreCommit" or "RestoreAbort" or "Conflict") pending.Remove(item.TransactionId);
        foreach (var item in pending.Values)
        {
            var stored = item.Payload.Deserialize<StoredImage>(JsonOptions) ?? throw new InvalidDataException("未完成资源事务无效。");
            if (stored.StorageVersion != 3) throw new InvalidDataException("未完成资源事务版本无效。");
            BindAndValidate(project, stored.Record);
            if (stored.Record.RenameTargets.Count != 0 || stored.Record.Targets.Any(target =>
                !MatchesState(target.TargetPath, target.BeforeExists, target.BeforeSha256) && !MatchesState(target.TargetPath, target.AfterExists, target.AfterSha256)))
            {
                AppendV3State(project, item.OperationId, HistoryStatus.Conflict, stored.Record.KeepForever, "Conflict", item.TransactionId);
                throw new IOException("未完成资源历史事务存在外部修改或改名事务，原文件及全部恢复材料已保留。");
            }
            foreach (var target in stored.Record.Targets)
            {
                if (target.BeforeExists) ResourceFileRecovery.VerifySnapshot(target, false);
                if (target.AfterExists) ResourceFileRecovery.VerifySnapshot(target, true);
            }
            var errors = ResourceFileRecovery.RestoreAll(stored.Record.Targets, stored.RollbackUseAfter);
            if (errors.Count != 0) throw new IOException("资源历史崩溃恢复未完成：" + string.Join("；", errors));
            AppendV3State(project, item.OperationId, item.Kind == "Prepare" ? HistoryStatus.Failed : stored.OriginalStatus,
                stored.Record.KeepForever, item.Kind == "Prepare" ? "Abort" : "RestoreAbort", item.TransactionId);
        }
        return pending.Count != 0;
    }
}
