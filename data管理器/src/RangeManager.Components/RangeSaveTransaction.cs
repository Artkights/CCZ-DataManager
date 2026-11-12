using System.Security.Cryptography;
using System.Text.Json;

namespace RangeManager.App;

/// <summary>A recoverable commit of the executable and its range resources.</summary>
public static class RangeSaveTransaction
{
    private const string JournalName = ".ccz-range-save.json";
    private sealed record FileChange(string Path, string Backup, string Staged, string BeforeHash, string AfterHash);
    private sealed record Journal(string Phase, List<FileChange> Files);
    public static string Hash(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void Commit(string root, IReadOnlyDictionary<string, byte[]> candidates,
        IReadOnlyDictionary<string, string> expectedHashes, Action<int>? beforeReplace = null)
    {
        root = System.IO.Path.GetFullPath(root);
        Recover(root);
        foreach (var (path, expected) in expectedHashes)
            if (!StringComparer.Ordinal.Equals(Hash(path), expected))
                throw new IOException($"文件在载入后被外部修改，请重新打开基底：{path}");
        if (candidates.Count == 0) return;
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        string journalPath = System.IO.Path.Combine(root, JournalName);
        var files = new List<FileChange>();
        bool journalWritten = false;
        try
        {
            foreach (var (path, bytes) in candidates)
            {
                ValidateTarget(root, path);
                if (!expectedHashes.TryGetValue(path, out var expected) || expected.Length == 0)
                    throw new InvalidDataException("没有该文件的已载入基线，无法同步保存：" + path);
                if (Hash(bytes) == expected) continue;
                string backup = path + ".bak-range-" + id;
                string staged = path + ".range-stage-" + id;
                File.Copy(path, backup, overwrite: false);
                WriteDurable(staged, bytes);
                if (Hash(backup) != expected || Hash(staged) != Hash(bytes))
                    throw new IOException("备份或候选内容复读不一致：" + path);
                files.Add(new(path, backup, staged, expected, Hash(bytes)));
            }
            if (files.Count == 0) return;
            WriteJournal(journalPath, new("prepared", files));
            journalWritten = true;
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                beforeReplace?.Invoke(i);
                if (Hash(file.Path) != file.BeforeHash)
                    throw new IOException("提交期间文件发生外部变化：" + file.Path);
                File.Move(file.Staged, file.Path, overwrite: true);
                if (Hash(file.Path) != file.AfterHash) throw new IOException("提交后复读不一致：" + file.Path);
            }
            WriteJournal(journalPath, new("committed", files));
            File.Delete(journalPath);
        }
        catch
        {
            if (journalWritten) Recover(root);
            throw;
        }
        finally
        {
            foreach (var file in files)
                if (File.Exists(file.Staged)) File.Delete(file.Staged);
        }
    }

    public static void Recover(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        string journalPath = System.IO.Path.Combine(root, JournalName);
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath))
            ?? throw new InvalidDataException("范围保存恢复记录为空");
        if (journal.Phase is not ("prepared" or "committed") || journal.Files.Count is < 1 or > 3)
            throw new InvalidDataException("范围保存恢复记录无效");
        foreach (var file in journal.Files)
        {
            ValidateTarget(root, file.Path);
            if (!file.Backup.StartsWith(file.Path + ".bak-range-", StringComparison.OrdinalIgnoreCase) ||
                !file.Staged.StartsWith(file.Path + ".range-stage-", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetDirectoryName(file.Backup) != System.IO.Path.GetDirectoryName(file.Path) ||
                System.IO.Path.GetDirectoryName(file.Staged) != System.IO.Path.GetDirectoryName(file.Path))
                throw new InvalidDataException("恢复记录的备份或候选路径不合法");
            if (journal.Phase == "prepared")
            {
                string current = Hash(file.Path);
                if (current != file.BeforeHash && current != file.AfterHash)
                    throw new IOException("恢复时发现额外的外部改动，已保留恢复记录和备份：" + file.Path);
                if (Hash(file.Backup) != file.BeforeHash)
                    throw new IOException("恢复备份校验失败：" + file.Backup);
                if (current != file.BeforeHash)
                {
                    string restorePath = file.Path + ".range-restore-" + Guid.NewGuid().ToString("N");
                    File.Copy(file.Backup, restorePath);
                    File.Move(restorePath, file.Path, overwrite: true);
                }
            }
            if (File.Exists(file.Staged)) File.Delete(file.Staged);
        }
        File.Delete(journalPath);
    }

    private static void ValidateTarget(string root, string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string parent = System.IO.Path.GetDirectoryName(full)!;
        string name = System.IO.Path.GetFileName(full);
        bool inRoot = StringComparer.OrdinalIgnoreCase.Equals(parent, root);
        bool inE5 = StringComparer.OrdinalIgnoreCase.Equals(parent, System.IO.Path.Combine(root, "E5"));
        if (!(inRoot && name.Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)) &&
            !((inRoot || inE5) && (name.Equals("Hitarea.e5", StringComparison.OrdinalIgnoreCase) || name.Equals("Effarea.e5", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("范围事务目标不属于当前基底：" + full);
    }

    private static void WriteJournal(string path, Journal journal)
    {
        string staged = path + ".tmp";
        WriteDurable(staged, JsonSerializer.SerializeToUtf8Bytes(journal));
        File.Move(staged, path, overwrite: true);
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
