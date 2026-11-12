using System.Text.Json;
using System.Text.Json.Serialization;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.History;

public sealed partial class HistoryStore : Ccz.HistoryStorage.IHistoryRepository<string, HistoryEntry, HistoryEntry>
{
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string GetRoot(string projectRoot) => ProjectHistoryPaths.GetDataRoot(projectRoot);

    public void Append(string projectRoot, HistoryEntry entry)
        => WriteV3(projectRoot, entry);

    public void Replace(string projectRoot, IReadOnlyList<HistoryEntry> entries)
    {
        using var projectLock = Ccz.HistoryStorage.ProjectWriteLock.Acquire(projectRoot);
        foreach (var entry in entries) WriteV3(projectRoot, entry);
    }

    public void Update(string projectRoot, HistoryEntry entry)
        => WriteV3(projectRoot, entry);

    public HistoryOperationResult Undo(string projectRoot, string profileId, string variantId, string layoutRevision, string? operationId = null)
    {
        using var projectLock = Ccz.HistoryStorage.ProjectWriteLock.Acquire(projectRoot);
        Recover(projectRoot);
        var entries = ListMetadata(projectRoot).ToList();
        var entry = entries.LastOrDefault(candidate => candidate.Status == HistoryStatus.Active && candidate.Files.Count > 0 &&
            (operationId == null || candidate.OperationId == operationId));
        if (entry == null) return new HistoryOperationResult(false, "没有可撤回的已保存历史记录。");
        return Restore(projectRoot, entries, LoadOperation(projectRoot, entry.OperationId), profileId, variantId, layoutRevision, redo: false);
    }

    public HistoryOperationResult Redo(string projectRoot, string profileId, string variantId, string layoutRevision, string? operationId = null)
    {
        using var projectLock = Ccz.HistoryStorage.ProjectWriteLock.Acquire(projectRoot);
        Recover(projectRoot);
        var entries = ListMetadata(projectRoot).ToList();
        var entry = entries.FirstOrDefault(candidate => candidate.Status == HistoryStatus.Reverted && candidate.Files.Count > 0 &&
            (operationId == null || candidate.OperationId == operationId));
        if (entry == null) return new HistoryOperationResult(false, "没有可前进的历史记录。");
        return Restore(projectRoot, entries, LoadOperation(projectRoot, entry.OperationId), profileId, variantId, layoutRevision, redo: true);
    }

    private HistoryOperationResult Restore(string projectRoot, List<HistoryEntry> entries, HistoryEntry entry,
        string profileId, string variantId, string layoutRevision, bool redo, StoredEntry? preparedStorage = null)
    {
        var writer = new BinaryTableWriter();
        var attempted = new List<FileByteChange>();
        var originalStatus = entry.Status;
        string? transactionId = null;
        try
        {
            ValidateLayout(entry, projectRoot, profileId, variantId, layoutRevision);
            ValidateAllFiles(entry.Files, projectRoot, useNewBytes: redo);
            transactionId = PrepareRestore(projectRoot, entry, redo, preparedStorage);
            var ordered = redo ? entry.Files : entry.Files.AsEnumerable().Reverse();
            foreach (var file in ordered)
            {
                attempted.Add(file);
                writer.Write(file, projectRoot, useNewBytes: redo);
            }
            entry.Status = redo ? HistoryStatus.Active : HistoryStatus.Reverted;
            AppendState(projectRoot, entry.OperationId, entry.Status, "RestoreCommit", transactionId);
            return new HistoryOperationResult(true, $"已{(redo ? "前进" : "撤回")}：{entry.Summary}", entry);
        }
        catch (Exception ex)
        {
            var failures = new List<string>();
            foreach (var file in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    // A failed create/delete can already be at its original
                    // existence state. Otherwise Write checks the exact state
                    // produced by this operation before restoring anything.
                    if (BinaryTableWriter.MatchesState(file, projectRoot, useAfter: !redo)) continue;
                    writer.Write(file, projectRoot, useNewBytes: !redo);
                }
                catch (Exception rollback) { failures.Add($"{file.RelativePath}：{rollback.Message}"); }
            }
            entry.Status = failures.Count == 0 ? originalStatus : HistoryStatus.Conflict;
            // The manifest is atomically replaced, so a failed replacement leaves its old state intact.
            // Persist a conflict only if restoring files failed; never mask the original error.
            if (transactionId != null)
            {
                try { AppendState(projectRoot, entry.OperationId, entry.Status, failures.Count > 0 ? "Conflict" : "RestoreAbort", transactionId); }
                catch (Exception history) { failures.Add($"历史状态：{history.Message}"); }
            }
            return new HistoryOperationResult(false, ex.Message + (failures.Count == 0 ? "；文件与历史已保持操作前状态。" : $"；恢复失败：{string.Join("；", failures)}"), entry);
        }
    }

    private static void ValidateAllFiles(IEnumerable<FileByteChange> files, string projectRoot, bool useNewBytes)
    {
        foreach (var file in files)
        {
            BinaryTableWriter.VerifyState(file, projectRoot, useAfter: !useNewBytes);
        }
    }

    private static void ValidateLayout(HistoryEntry entry, string projectRoot, string profileId, string variantId, string layoutRevision)
    {
        if (entry.SchemaVersion is not ("2.0" or "2.1") ||
            string.IsNullOrWhiteSpace(entry.ProfileId) ||
            string.IsNullOrWhiteSpace(entry.LayoutRevision))
        {
            throw new InvalidOperationException("历史记录缺少 Profile/布局信息，不能在双 Profile 模式下重放。");
        }
        if (!string.Equals(entry.ProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.VariantId, variantId, StringComparison.OrdinalIgnoreCase) ||
            !DataManager.Core.Profiles.LayoutRevisionCompatibility.IsCompatible(projectRoot, profileId, variantId, entry.LayoutRevision, layoutRevision))
        {
            throw new InvalidOperationException($"历史记录布局冲突：记录为 {entry.ProfileId}/{entry.VariantId}/{entry.LayoutRevision}，当前为 {profileId}/{variantId}/{layoutRevision}。已停止字节重放。");
        }
    }
}
