using System.Text.Json;
using Ccz.HistoryStorage;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.History;

public sealed partial class HistoryStore
{
    private sealed record StoredChunk(long Offset, BlobReference Old, BlobReference New);
    private sealed record StoredFile(FileByteChange Metadata, IReadOnlyList<StoredChunk> Chunks);
    private sealed record StoredEntry(int StorageVersion, HistoryEntry Metadata, IReadOnlyList<StoredFile> Files);
    private sealed record StateEntry(HistoryStatus Status);
    private sealed record RestoreEntry(StoredEntry Entry, bool Redo, HistoryStatus OriginalStatus);
    private sealed record EntrySource(HistoryEntry Metadata, Func<HistoryEntry> ReadPayload, bool Legacy = false);

    public string GetV3Root(string projectRoot) => Path.Combine(GetRoot(projectRoot), "history-v3");
    private IHistoryLedger Ledger(string projectRoot) => new HistoryLedger(Path.Combine(GetV3Root(projectRoot), "ledger"));
    private IHistoryBlobStore Blobs(string projectRoot) => new BlobStore(Path.Combine(ProjectHistoryPaths.GetProjectStorageRoot(projectRoot), "history-v3", "blobs"));

    public IReadOnlyList<HistoryEntry> ListMetadata(string projectRoot) => Sources(projectRoot).Values.Select(source => source.Metadata).ToArray();
    public IReadOnlyList<HistoryEntry> Load(string projectRoot)
    {
        var entries = new List<HistoryEntry>();
        foreach (var source in Sources(projectRoot).Values)
        {
            try { entries.Add(source.ReadPayload()); }
            catch (Exception ex) when (source.Legacy && ex is JsonException or FormatException or InvalidDataException)
            { /* Older readers ignored malformed lines. Preserve those originals without replaying them. */ }
        }
        return entries;
    }
    public HistoryEntry LoadOperation(string projectRoot, string operationId)
        => Sources(projectRoot).TryGetValue(operationId, out var source) ? source.ReadPayload() : throw new InvalidOperationException("找不到历史记录：" + operationId);

    private Dictionary<string, EntrySource> Sources(string projectRoot)
    {
        var sources = new Dictionary<string, EntrySource>(StringComparer.Ordinal);
        if (ProjectHistoryPaths.GetEvidenceSource(projectRoot) is { } evidenceRoot)
            foreach (var pair in Sources(evidenceRoot)) sources[pair.Key] = pair.Value;
        var legacy = ProjectHistoryPaths.GetDataManifestForRead(projectRoot);
        var indexPath = Path.Combine(GetV3Root(projectRoot), "index", "legacy.json");
        foreach (var position in LegacyIndex.Read(legacy, indexPath))
        {
            try
            {
                var metadata = position.Metadata.Deserialize<HistoryEntry>(_json);
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.OperationId)) continue;
                var captured = position;
                sources[metadata.OperationId] = new(metadata, () =>
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(LegacyIndex.ReadPayload(legacy, captured), _json)
                        ?? throw new InvalidDataException("旧历史记录无法读取。");
                    if (entry.OperationId != metadata.OperationId) throw new InvalidDataException("旧历史索引身份不一致。");
                    entry.Status = sources[metadata.OperationId].Metadata.Status;
                    return entry;
                }, true);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        foreach (var item in Ledger(projectRoot).Read())
        {
            if (item.Kind is "Prepare" or "Commit" or "Abort" or "Record")
            {
                var stored = item.Payload.Deserialize<StoredEntry>(_json) ?? throw new InvalidDataException("v3历史元数据无效。");
                if (stored.StorageVersion != 3 || stored.Metadata.OperationId != item.OperationId) throw new InvalidDataException("v3历史身份或版本无效。");
                sources[item.OperationId] = new(stored.Metadata, () => Decode(projectRoot, stored, sources[item.OperationId].Metadata.Status));
            }
            else if (item.Kind is "State" or "RestoreCommit" or "RestoreAbort" or "AbortRecovered" or "Conflict")
            {
                if (sources.TryGetValue(item.OperationId, out var source))
                    source.Metadata.Status = item.Payload.Deserialize<StateEntry>(_json)?.Status ?? throw new InvalidDataException("历史状态无效。");
            }
        }
        return sources;
    }

    private void WriteV3(string projectRoot, HistoryEntry entry)
    {
        AppendPrepared(PrepareStoredWrite(projectRoot, entry, CancellationToken.None));
    }

    private StoredEntry Encode(string projectRoot, HistoryEntry entry)
    {
        if (entry.SchemaVersion is not ("2.0" or "2.1")) throw new InvalidDataException("不支持的历史域模型版本。");
        var blobs = Blobs(projectRoot);
        var files = entry.Files.Select(file => new StoredFile(file with { Chunks = [] }, file.Chunks.Select(chunk =>
            new StoredChunk(chunk.Offset, blobs.Put(chunk.OldBytes), blobs.Put(chunk.NewBytes))).ToArray())).ToArray();
        return new(3, CopyMetadata(entry, files.Select(file => file.Metadata).ToList()), files);
    }
    private HistoryEntry Decode(string projectRoot, StoredEntry stored, HistoryStatus status)
    {
        if (stored.StorageVersion != 3 || stored.Metadata.SchemaVersion is not ("2.0" or "2.1"))
            throw new InvalidDataException("不支持的历史存储或域模型版本。");
        var blobs = Blobs(projectRoot);
        var entry = CopyMetadata(stored.Metadata, stored.Files.Select(file => file.Metadata with
        { Chunks = file.Chunks.Select(chunk => new ByteChunk(chunk.Offset, blobs.Read(chunk.Old), blobs.Read(chunk.New))).ToArray() }).ToList());
        entry.Status = status;
        return entry;
    }
    private static HistoryEntry CopyMetadata(HistoryEntry entry, List<FileByteChange> files) => new()
    {
        SchemaVersion = entry.SchemaVersion, OperationId = entry.OperationId, CreatedAt = entry.CreatedAt,
        OperationKind = entry.OperationKind, Summary = entry.Summary, Status = entry.Status, ProfileId = entry.ProfileId,
        VariantId = entry.VariantId, LayoutRevision = entry.LayoutRevision, DetectionMethod = entry.DetectionMethod,
        CoreFileFingerprints = new(entry.CoreFileFingerprints, StringComparer.OrdinalIgnoreCase), Modules = [..entry.Modules],
        Tables = [..entry.Tables], AppearanceSyncMessages = [..entry.AppearanceSyncMessages], Files = files
    };
    private string PrepareRestore(string root, HistoryEntry entry, bool redo, StoredEntry? stored = null)
    {
        var transaction = Guid.NewGuid().ToString("N");
        Ledger(root).Append("RestorePrepare", entry.OperationId, new RestoreEntry(stored ?? Encode(root, entry), redo, entry.Status), transaction);
        return transaction;
    }
    private void AppendState(string root, string operation, HistoryStatus status, string kind = "State", string? transaction = null)
        => Ledger(root).Append(kind, operation, new StateEntry(status), transaction);

    public void RecordRollback(string root, string operation, bool conflict)
    {
        using var projectLock = ProjectWriteLock.Acquire(root);
        AppendState(root, operation, conflict ? HistoryStatus.Conflict : HistoryStatus.Failed, conflict ? "Conflict" : "AbortRecovered");
    }

    /// <summary>Only unfinished v3 transactions are recovered. Unknown external content is never overwritten.</summary>
    public void Recover(string projectRoot)
    {
        var ledger = Ledger(projectRoot);
        if (!ledger.Exists) return;
        using var projectLock = ProjectWriteLock.Acquire(projectRoot);
        var events = ledger.Read();
        var pending = new Dictionary<string, LedgerEvent>(StringComparer.Ordinal);
        foreach (var item in events)
            if (item.Kind is "Prepare" or "RestorePrepare") pending[item.TransactionId] = item;
            else if (item.Kind is "Commit" or "Abort" or "AbortRecovered" or "RestoreCommit" or "RestoreAbort" or "Conflict") pending.Remove(item.TransactionId);
        foreach (var item in pending.Values)
        {
            var restore = item.Kind == "RestorePrepare" ? item.Payload.Deserialize<RestoreEntry>(_json) : null;
            var stored = restore?.Entry ?? item.Payload.Deserialize<StoredEntry>(_json) ?? throw new InvalidDataException("未完成历史事务无效。");
            var entry = Decode(projectRoot, stored, stored.Metadata.Status);
            if (entry.Files.Any(file => !BinaryTableWriter.MatchesState(file, projectRoot, false) && !BinaryTableWriter.MatchesState(file, projectRoot, true)))
            {
                AppendState(projectRoot, item.OperationId, HistoryStatus.Conflict, "Conflict", item.TransactionId);
                throw new IOException("未完成历史事务遇到外部修改，当前文件和全部恢复材料已保留：" + entry.Summary);
            }
            // Both directions have already been decoded and verified before touching any target.
            var originalAfter = restore != null && !restore.Redo;
            foreach (var file in entry.Files.AsEnumerable().Reverse())
                if (!BinaryTableWriter.MatchesState(file, projectRoot, originalAfter))
                    new BinaryTableWriter().Write(file, projectRoot, originalAfter);
            AppendState(projectRoot, item.OperationId, restore?.OriginalStatus ?? HistoryStatus.Failed, restore == null ? "AbortRecovered" : "RestoreAbort", item.TransactionId);
        }
    }
}
