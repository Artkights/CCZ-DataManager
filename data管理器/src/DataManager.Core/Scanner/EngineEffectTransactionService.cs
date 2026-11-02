using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>One baseline, backup and rollback boundary for all three engine pages.</summary>
public sealed class EngineEffectTransactionService
{
    public static IReadOnlyList<WorkspaceBinaryCandidate> MergeCandidates(
        IEnumerable<WorkspaceBinaryCandidate> candidates)
    {
        var result = new List<WorkspaceBinaryCandidate>();
        foreach (var group in candidates.GroupBy(candidate => Path.GetFullPath(candidate.FilePath),
                     StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.ToArray();
            var before = parts[0].BeforeBytes;
            if (parts.Any(part => !before.AsSpan().SequenceEqual(part.BeforeBytes)))
                throw new InvalidDataException($"{Path.GetFileName(group.Key)} 的草稿基线不同，请重新扫描。");
            var lengths = parts.Where(part => part.AfterBytes.Length != before.Length)
                .Select(part => part.AfterBytes.Length).Distinct().ToArray();
            if (lengths.Length > 1)
                throw new InvalidDataException($"{Path.GetFileName(group.Key)} 的文件长度修改冲突。");
            var length = lengths.Length == 0 ? before.Length : lengths[0];
            var after = new byte[length];
            before.AsSpan(0, Math.Min(length, before.Length)).CopyTo(after);
            var written = new bool[length];
            foreach (var part in parts)
            {
                if (part.AfterBytes.Length > length && part.AfterBytes.Length != before.Length)
                    throw new InvalidDataException($"{Path.GetFileName(group.Key)} 的截断与其他修改冲突。");
                for (var offset = 0; offset < Math.Min(length, part.AfterBytes.Length); offset++)
                {
                    if (offset < before.Length && before[offset] == part.AfterBytes[offset]) continue;
                    if (written[offset] && after[offset] != part.AfterBytes[offset])
                        throw new InvalidDataException($"{Path.GetFileName(group.Key)} 偏移 0x{offset:X} 存在不同修改。");
                    after[offset] = part.AfterBytes[offset];
                    written[offset] = true;
                }
                if (length < before.Length && part.AfterBytes.Length == before.Length &&
                    !part.AfterBytes.AsSpan(length).SequenceEqual(before.AsSpan(length)))
                    throw new InvalidDataException($"{Path.GetFileName(group.Key)} 被截断区域仍有草稿修改。");
            }
            result.Add(new WorkspaceBinaryCandidate(group.Key, before, after,
                string.Join("、", parts.Select(part => part.Module).Distinct())));
        }
        return result;
    }

    public EngineEffectWriteResult Commit(ProjectWorkspace workspace, string summary,
        IReadOnlyList<WorkspaceBinaryCandidate> candidates, Action? verify = null)
    {
        try
        {
            if (EngineEffectWriteService.IsGameRunning(workspace))
                throw new IOException("游戏正在运行，请先完全退出游戏再写入引擎。");
            using var writeLock = EngineEffectWriteLock.Acquire(workspace.RootPath);
            var merged = MergeCandidates(candidates);
            if (merged.Count == 0) throw new InvalidOperationException("没有需要提交的引擎修改。");
            var backup = EngineEffectWriteService.CreateBackup(workspace.RootPath, merged, out var backupError);
            if (backup == null) throw new IOException(backupError ?? "写前备份失败。");
            var saved = new WorkspaceBinaryTransaction().Commit(workspace, summary, merged, () =>
            {
                foreach (var candidate in merged)
                    if (!HashService.Sha256(candidate.FilePath).Equals(HashService.Sha256(candidate.AfterBytes),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("写后整文件校验失败。");
                verify?.Invoke();
            });
            return new EngineEffectWriteResult
            {
                Success = saved.Saved, AppliedChanges = saved.ChangedBytes, History = saved.History,
                BackupPath = backup,
                Message = string.Join(Environment.NewLine, saved.Messages.Append($"写前备份：{backup}"))
            };
        }
        catch (Exception ex)
        {
            return new EngineEffectWriteResult { Success = false, Message = "引擎事务中止：" + ex.Message };
        }
    }
}
