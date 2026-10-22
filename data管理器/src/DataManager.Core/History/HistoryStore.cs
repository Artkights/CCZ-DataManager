using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataManager.Core.Binary;
using DataManager.Core.Models;

namespace DataManager.Core.History;

public sealed class HistoryStore
{
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string GetRoot(string projectRoot) => Path.Combine(projectRoot, ".data管理器历史");

    public IReadOnlyList<HistoryEntry> Load(string projectRoot)
    {
        var path = Path.Combine(GetRoot(projectRoot), "manifest.jsonl");
        if (!File.Exists(path)) return [];
        var entries = new List<HistoryEntry>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, _json);
                if (entry != null) entries.Add(entry);
            }
            catch
            {
                // A malformed line is reported through the history UI rather than blocking data editing.
            }
        }
        return entries;
    }

    public void Append(string projectRoot, HistoryEntry entry)
        => WriteRecord(projectRoot, entry, updateExisting: false);

    public void Replace(string projectRoot, IReadOnlyList<HistoryEntry> entries)
    {
        var root = GetRoot(projectRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "manifest.jsonl");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, entries.Select(entry => JsonSerializer.Serialize(entry, _json)), new UTF8Encoding(true));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Update(string projectRoot, HistoryEntry entry)
        => WriteRecord(projectRoot, entry, updateExisting: true);

    private void WriteRecord(string projectRoot, HistoryEntry entry, bool updateExisting)
    {
        var root = GetRoot(projectRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "manifest.jsonl");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var replacement = JsonSerializer.Serialize(entry, _json);
            var replaced = false;
            using (var output = new StreamWriter(temporary, false, new UTF8Encoding(true)))
            {
                // Older records can contain large base64 snapshots. Preserve
                // their lines instead of decoding every snapshot and then
                // serializing the entire history for one new/status record.
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (updateExisting && !replaced && HasOperationId(line, entry.OperationId))
                        { output.WriteLine(replacement); replaced = true; }
                        else output.WriteLine(line);
                    }
                if (!replaced) output.WriteLine(replacement);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool HasOperationId(string line, string operationId)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.ValueKind != JsonValueKind.Object ||
                !record.RootElement.TryGetProperty(nameof(HistoryEntry.OperationId), out var id) ||
                id.ValueKind != JsonValueKind.String || id.GetString() != operationId) return false;
            // Only the matching candidate needs typed validation. A valid
            // JSON object can still contain an invalid Files/base64 field;
            // Load ignores it, so Update must append rather than replace it.
            return JsonSerializer.Deserialize<HistoryEntry>(line, _json)?.OperationId == operationId;
        }
        catch
        {
            // Load still ignores malformed records. Preserve their original
            // lines during appending/updating rather than deleting evidence.
            return false;
        }
    }

    public HistoryOperationResult Undo(string projectRoot, string profileId, string variantId, string layoutRevision, string? operationId = null)
    {
        var entries = Load(projectRoot).ToList();
        var entry = entries.LastOrDefault(candidate => candidate.Status == HistoryStatus.Active && candidate.Files.Count > 0 &&
            (operationId == null || candidate.OperationId == operationId));
        if (entry == null) return new HistoryOperationResult(false, "没有可撤回的已保存历史记录。");
        return Restore(projectRoot, entries, entry, profileId, variantId, layoutRevision, redo: false);
    }

    public HistoryOperationResult Redo(string projectRoot, string profileId, string variantId, string layoutRevision, string? operationId = null)
    {
        var entries = Load(projectRoot).ToList();
        var entry = entries.FirstOrDefault(candidate => candidate.Status == HistoryStatus.Reverted && candidate.Files.Count > 0 &&
            (operationId == null || candidate.OperationId == operationId));
        if (entry == null) return new HistoryOperationResult(false, "没有可前进的历史记录。");
        return Restore(projectRoot, entries, entry, profileId, variantId, layoutRevision, redo: true);
    }

    private HistoryOperationResult Restore(string projectRoot, List<HistoryEntry> entries, HistoryEntry entry,
        string profileId, string variantId, string layoutRevision, bool redo)
    {
        var writer = new BinaryTableWriter();
        var attempted = new List<FileByteChange>();
        var originalStatus = entry.Status;
        try
        {
            ValidateLayout(entry, projectRoot, profileId, variantId, layoutRevision);
            ValidateAllFiles(entry.Files, projectRoot, useNewBytes: redo);
            var ordered = redo ? entry.Files : entry.Files.AsEnumerable().Reverse();
            foreach (var file in ordered)
            {
                attempted.Add(file);
                writer.Write(file, projectRoot, useNewBytes: redo);
            }
            entry.Status = redo ? HistoryStatus.Active : HistoryStatus.Reverted;
            Replace(projectRoot, entries);
            return new HistoryOperationResult(true, $"已{(redo ? "前进" : "撤回")}：{entry.Summary}", entry);
        }
        catch (Exception ex)
        {
            var failures = new List<string>();
            foreach (var file in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var hash = HashService.Sha256(Path.Combine(projectRoot, file.RelativePath));
                    var originalHash = redo ? file.BeforeHash : file.AfterHash;
                    if (hash.Equals(originalHash, StringComparison.OrdinalIgnoreCase)) continue;
                    writer.Write(file, projectRoot, useNewBytes: !redo);
                }
                catch (Exception rollback) { failures.Add($"{file.RelativePath}：{rollback.Message}"); }
            }
            entry.Status = failures.Count == 0 ? originalStatus : HistoryStatus.Conflict;
            // The manifest is atomically replaced, so a failed replacement leaves its old state intact.
            // Persist a conflict only if restoring files failed; never mask the original error.
            if (failures.Count > 0)
            {
                try { Replace(projectRoot, entries); }
                catch (Exception history) { failures.Add($"历史状态：{history.Message}"); }
            }
            return new HistoryOperationResult(false, ex.Message + (failures.Count == 0 ? "；文件与历史已保持操作前状态。" : $"；恢复失败：{string.Join("；", failures)}"), entry);
        }
    }

    private static void ValidateAllFiles(IEnumerable<FileByteChange> files, string projectRoot, bool useNewBytes)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(projectRoot, file.RelativePath);
            if (!File.Exists(path)) throw new IOException($"历史记录冲突：{file.RelativePath} 不存在。");
            var expected = useNewBytes ? file.BeforeHash : file.AfterHash;
            if (!string.Equals(HashService.Sha256(path), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"历史记录冲突：{file.RelativePath} 当前文件已被外部修改。");
            }
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
