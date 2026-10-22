using System.Security.Cryptography;
using System.Text;

namespace ImageManager.Core;

internal static class RenameTransaction
{
    public static string Fingerprint(string path, bool directory)
    {
        if (!directory) return MutationSourceSnapshot.Capture(path) is { Exists: true } snapshot
            ? snapshot.Length + ":" + snapshot.Sha256 : throw new FileNotFoundException("改名源文件不存在。", path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("目录改名不跟随重解析点：" + path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directoryPath))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("目录改名不跟随重解析点：" + entry);
                entries.Add(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
        foreach (var entry in entries.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(path, entry);
            var text = Directory.Exists(entry) ? "D:" + relative : "F:" + relative + ":" + Fingerprint(entry, false);
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string ResolveTarget(string target, IReadOnlyList<RenameItem> items)
    {
        foreach (var parent in items.Where(item => item.IsDirectory).OrderByDescending(item => item.SourcePath.Length))
            if (target.StartsWith(parent.SourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                target = parent.TargetPath + target[parent.SourcePath.Length..];
        return target;
    }

    // Staging lives outside the renamed tree. Moving a parent therefore never
    // strands the temporary path of one of its already staged children.
    public static MutationResult Execute(string root, string operationId, string summary,
        IReadOnlyList<RenameHistoryTarget> targets, bool forward, Action persist)
    {
        var moves = targets.Select(target => new Move(target,
            forward ? target.SourcePath : target.TargetPath,
            forward ? target.TargetPath : target.SourcePath)).ToArray();
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            ?? Path.GetPathRoot(root)!, ".imagemanager-rename-" + Guid.NewGuid().ToString("N"));
        var staged = new List<Move>();
        var installed = new List<Move>();
        var createdParents = new List<string>();
        try
        {
            foreach (var move in moves)
            {
                var expected = forward ? move.Target.BeforeFingerprint : move.Target.AfterFingerprint;
                if (Fingerprint(move.Source, move.Target.IsDirectory) != expected)
                    throw new IOException("改名预览后内容已改变，请重新预检：" + move.Source);
                if ((File.Exists(move.Destination) || Directory.Exists(move.Destination)) && !moves.Any(other =>
                        other.Source.Equals(move.Destination, StringComparison.OrdinalIgnoreCase) || other.Target.IsDirectory &&
                        move.Destination.StartsWith(other.Source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("改名目标已存在，拒绝覆盖：" + move.Destination);
            }
            Directory.CreateDirectory(staging);
            foreach (var move in moves.OrderByDescending(move => move.Source.Length))
            {
                move.Temporary = Path.Combine(staging, staged.Count.ToString("D6"));
                MovePath(move.Source, move.Temporary, move.Target.IsDirectory);
                staged.Add(move);
            }
            foreach (var move in moves.OrderBy(move => move.Destination.Length))
            {
                EnsureParent(move.Destination, createdParents);
                MovePath(move.Temporary, move.Destination, move.Target.IsDirectory);
                installed.Add(move);
            }
            foreach (var move in moves)
            {
                var actual = Fingerprint(move.Destination, move.Target.IsDirectory);
                var expected = forward ? move.Target.AfterFingerprint : move.Target.BeforeFingerprint;
                if (expected.Length > 0 && actual != expected)
                    throw new IOException("改名后内容校验失败：" + move.Destination);
                if (forward && expected.Length == 0) move.Target.AfterFingerprint = actual;
            }
            persist();
            return new(operationId, true, summary, operationId, moves.Select(move => move.Destination).ToArray(), []);
        }
        catch (Exception ex)
        {
            var errors = new List<string> { ex.Message };
            foreach (var move in installed.OrderByDescending(move => move.Destination.Length))
            {
                try { MovePath(move.Destination, move.Temporary, move.Target.IsDirectory); }
                catch (Exception failure) { errors.Add("无法移回临时区：" + move.Destination + "；" + failure.Message); }
            }
            foreach (var parent in createdParents.AsEnumerable().Reverse()) TryRemoveEmptyDirectory(parent);
            foreach (var move in staged.OrderBy(move => move.Source.Length))
            {
                try
                {
                    if (File.Exists(move.Temporary) || Directory.Exists(move.Temporary))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Source)!);
                        MovePath(move.Temporary, move.Source, move.Target.IsDirectory);
                    }
                }
                catch (Exception failure) { errors.Add("无法恢复原路径：" + move.Source + "；" + failure.Message); }
            }
            foreach (var move in staged)
            {
                try
                {
                    var expected = forward ? move.Target.BeforeFingerprint : move.Target.AfterFingerprint;
                    if (Fingerprint(move.Source, move.Target.IsDirectory) != expected)
                        errors.Add("恢复后内容校验失败：" + move.Source);
                }
                catch (Exception failure) { errors.Add("恢复校验失败：" + move.Source + "；" + failure.Message); }
            }
            errors.Add(staged.Count == 0 ? "未更改任何路径。" : errors.Count == 1
                ? "已恢复全部原路径及内容，历史未提交。" : "恢复未完成，临时文件保留于：" + staging);
            return new(operationId, false, summary + "失败", null, [], errors);
        }
        finally { TryRemoveEmptyDirectory(staging); }
    }

    private static void EnsureParent(string path, List<string> created)
    {
        var missing = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent != null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
            missing.Push(parent);
        while (missing.TryPop(out var parent))
        {
            Directory.CreateDirectory(parent);
            created.Add(parent);
        }
    }

    private static void MovePath(string source, string destination, bool directory)
    {
        if (directory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Move(RenameHistoryTarget target, string source, string destination)
    {
        public RenameHistoryTarget Target { get; } = target;
        public string Source { get; } = source;
        public string Destination { get; } = destination;
        public string Temporary { get; set; } = string.Empty;
    }
}
