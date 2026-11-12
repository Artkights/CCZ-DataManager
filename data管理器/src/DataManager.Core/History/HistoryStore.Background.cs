using System.Text.Json;
using Ccz.HistoryStorage;
using DataManager.Core.Models;

namespace DataManager.Core.History;

public sealed partial class HistoryStore
{
    /// <summary>Call while the UI owns stable drafts; no DataTable or mutable UI object is retained.</summary>
    public FrozenHistoryWrite FreezeWrite(string projectRoot, HistoryEntry entry)
    {
        var copy = CopyMetadata(entry, entry.Files.Select(file => file with
        { Chunks = file.Chunks.Select(chunk => new ByteChunk(chunk.Offset, (byte[])chunk.OldBytes.Clone(), (byte[])chunk.NewBytes.Clone())).ToArray() }).ToList());
        return new(ProjectHistoryPaths.NormalizeProjectRoot(projectRoot), copy);
    }

    /// <summary>Only immutable payloads are compressed here. No target file or ledger state is changed.</summary>
    public Task<PreparedHistoryWrite> PrepareWriteAsync(FrozenHistoryWrite frozen, CancellationToken cancellationToken = default)
    {
        var entry = frozen.Entry ?? throw new ObjectDisposedException(nameof(FrozenHistoryWrite));
        return Task.Run(() => PrepareStoredWrite(frozen.ProjectRoot, entry, cancellationToken), cancellationToken);
    }

    private PreparedHistoryWrite PrepareStoredWrite(string root, HistoryEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stored = Encode(root, entry);
        token.ThrowIfCancellationRequested();
        var kind = entry.Status switch { HistoryStatus.Pending => "Prepare", HistoryStatus.Active => "Commit", HistoryStatus.Failed => "Abort", _ => "Record" };
        return new(ProjectHistoryPaths.NormalizeProjectRoot(root), entry.OperationId, entry.Status, kind, JsonSerializer.SerializeToElement(stored, _json));
    }

    /// <summary>The caller's file transaction still owns baseline verification and Prepare-before-write ordering.</summary>
    public void AppendPrepared(PreparedHistoryWrite prepared)
    {
        using var projectLock = ProjectWriteLock.Acquire(prepared.ProjectRoot);
        Ledger(prepared.ProjectRoot).Append(prepared.Kind, prepared.OperationId, prepared.Payload);
        if (prepared.Status == HistoryStatus.Conflict)
            AppendState(prepared.ProjectRoot, prepared.OperationId, prepared.Status, "Conflict");
    }

    /// <summary>Reuses durable payload references; only transaction metadata changes after files are committed.</summary>
    public PreparedHistoryWrite UpdatePreparedMetadata(PreparedHistoryWrite prepared, HistoryEntry entry)
    {
        if (entry.OperationId != prepared.OperationId) throw new InvalidDataException("准备的历史操作身份不一致。");
        var original = prepared.Payload.Deserialize<StoredEntry>(_json) ?? throw new InvalidDataException("准备的历史元数据无效。");
        var files = entry.Files.Select(file =>
        {
            var stored = original.Files.SingleOrDefault(item => item.Metadata.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("提交文件未包含在写前恢复材料中：" + file.RelativePath);
            if (stored.Metadata.BeforeHash != file.BeforeHash || stored.Metadata.AfterHash != file.AfterHash ||
                stored.Metadata.BeforeExists != file.BeforeExists || stored.Metadata.AfterExists != file.AfterExists || stored.Chunks.Count != file.Chunks.Count)
                throw new InvalidDataException("提交文件与冻结恢复材料不一致：" + file.RelativePath);
            return stored with { Metadata = file with { Chunks = [] } };
        }).ToArray();
        var storedEntry = new StoredEntry(3, CopyMetadata(entry, files.Select(file => file.Metadata).ToList()), files);
        var kind = entry.Status switch { HistoryStatus.Pending => "Prepare", HistoryStatus.Active => "Commit", HistoryStatus.Failed => "Abort", _ => "Record" };
        return new(prepared.ProjectRoot, entry.OperationId, entry.Status, kind, JsonSerializer.SerializeToElement(storedEntry, _json));
    }

    /// <summary>Loads and verifies one operation on a worker without retaining a workspace or touching live drafts.</summary>
    public Task<PreparedHistoryRestore> PrepareRestoreAsync(string projectRoot, string profileId, string variantId,
        string layoutRevision, string operationId, bool redo, CancellationToken cancellationToken = default)
    {
        var root = ProjectHistoryPaths.NormalizeProjectRoot(projectRoot);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var projectLock = ProjectWriteLock.Acquire(root);
            var entry = LoadOperation(root, operationId);
            if (entry.Status != (redo ? HistoryStatus.Reverted : HistoryStatus.Active))
                throw new InvalidOperationException("历史状态不支持所选恢复方向。");
            ValidateLayout(entry, root, profileId, variantId, layoutRevision);
            ValidateAllFiles(entry.Files, root, useNewBytes: redo);
            var storage = JsonSerializer.SerializeToElement(Encode(root, entry), _json);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedHistoryRestore(root, profileId, variantId, layoutRevision, entry, redo, storage);
        }, cancellationToken);
    }

    /// <summary>Revalidates status, layout and current hashes at commit. Hosts refresh their workspace after success.</summary>
    public HistoryOperationResult RestorePrepared(PreparedHistoryRestore prepared)
    {
        var entry = prepared.Entry ?? throw new ObjectDisposedException(nameof(PreparedHistoryRestore));
        using var projectLock = ProjectWriteLock.Acquire(prepared.ProjectRoot);
        var metadata = ListMetadata(prepared.ProjectRoot).SingleOrDefault(item => item.OperationId == entry.OperationId);
        if (metadata?.Status != entry.Status) return new(false, "历史状态在准备恢复后已改变，请重新选择。", entry);
        return Restore(prepared.ProjectRoot, [], entry, prepared.ProfileId, prepared.VariantId, prepared.LayoutRevision,
            prepared.Redo, prepared.Storage.Deserialize<StoredEntry>(_json) ?? throw new InvalidDataException("准备的历史存储无效。"));
    }
}

public sealed class FrozenHistoryWrite : IDisposable
{
    internal FrozenHistoryWrite(string projectRoot, HistoryEntry entry) { ProjectRoot = projectRoot; Entry = entry; }
    internal string ProjectRoot { get; }
    internal HistoryEntry? Entry { get; private set; }
    public void Dispose() => Entry = null;
}

public sealed class PreparedHistoryWrite
{
    internal PreparedHistoryWrite(string root, string operation, HistoryStatus status, string kind, JsonElement payload)
    { ProjectRoot = root; OperationId = operation; Status = status; Kind = kind; Payload = payload; }
    internal string ProjectRoot { get; }
    internal string Kind { get; }
    internal JsonElement Payload { get; }
    public string OperationId { get; }
    public HistoryStatus Status { get; }
}

public sealed class PreparedHistoryRestore : IDisposable
{
    internal PreparedHistoryRestore(string root, string profile, string variant, string layout, HistoryEntry entry, bool redo, JsonElement storage)
    { ProjectRoot = root; ProfileId = profile; VariantId = variant; LayoutRevision = layout; Entry = entry; Redo = redo; Storage = storage; OperationId = entry.OperationId; }
    internal string ProjectRoot { get; }
    internal string ProfileId { get; }
    internal string VariantId { get; }
    internal string LayoutRevision { get; }
    internal HistoryEntry? Entry { get; private set; }
    internal JsonElement Storage { get; }
    public string OperationId { get; }
    public bool Redo { get; }
    public void Dispose() => Entry = null;
}
