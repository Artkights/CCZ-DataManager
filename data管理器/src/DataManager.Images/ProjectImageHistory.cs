using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataManager.Core.History;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Adapts the shared image editor's journal without changing its standalone storage behavior.</summary>
public static class ProjectImageHistory
{
    private static readonly object Gate = new();

    // The shared service hashes RootPath case-sensitively. Give every entry point
    // the same Windows directory spelling, including after a differently cased reopen.
    public static ProjectProfile Canonicalize(ProjectProfile profile)
    {
        var root = ProjectHistoryPaths.NormalizeProjectRoot(profile.RootPath);
        if (OperatingSystem.IsWindows()) root = root.ToUpperInvariant();
        return profile with { RootPath = root };
    }

    public static HistoryService Create(ProjectProfile profile)
        => new(ProjectHistoryPaths.GetImageRoot(profile.RootPath));

    public static IReadOnlyList<HistoryRecord> List(ProjectProfile profile)
    {
        var canonical = Canonicalize(profile);
        var service = Create(canonical);
        var current = Path.Combine(service.BackupRoot(canonical), "History", "history.jsonl");
        if (File.Exists(current)) return service.List(canonical);
        return LegacyRecords(profile).Select(item => item.Record).ToArray();
    }

    public static MutationResult Commit(ProjectProfile profile, MutationPlan plan)
    {
        var canonical = Canonicalize(profile);
        foreach (var file in plan.Files) ProjectHistoryPaths.ValidateInside(file.TargetPath, canonical.RootPath);
        var history = PrepareForWrite(profile);
        return new ImageAppearanceSyncTransaction().Commit(canonical, plan, history);
    }

    public static HistoryService PrepareForWrite(ProjectProfile profile)
    {
        var canonical = Canonicalize(profile);
        var service = Create(canonical);
        var destination = service.BackupRoot(canonical);
        var journal = Path.Combine(destination, "History", "history.jsonl");
        lock (Gate)
        {
            if (File.Exists(journal)) return service;
            var records = LegacyRecords(profile).ToArray();
            if (records.Length == 0) return service;
            var temporary = destination + ".import-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(Path.Combine(temporary, "History"));
                var lines = new List<string>();
                foreach (var item in records)
                {
                    var node = JsonNode.Parse(item.Line)!.AsObject();
                    if (node[nameof(HistoryRecord.Targets)] is JsonArray targets)
                        foreach (var target in targets.OfType<JsonObject>())
                        {
                            Relocate(target, nameof(HistoryTarget.SnapshotPath));
                            Relocate(target, nameof(HistoryTarget.AfterSnapshotPath));
                        }
                    lines.Add(node.ToJsonString());

                    void Relocate(JsonObject target, string property)
                    {
                        var source = target[property]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(source)) return;
                        ProjectHistoryPaths.ValidateInside(source, item.Root);
                        var relative = Path.GetRelativePath(item.Root, source);
                        var final = ProjectHistoryPaths.ResolveInside(destination, relative);
                        var staged = ProjectHistoryPaths.ResolveInside(temporary, relative);
                        target[property] = final;
                        if (!File.Exists(source)) return; // Preserve the old missing-snapshot failure behavior.
                        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                        if (File.Exists(staged))
                        {
                            if (HistoryService.HashFile(staged) != HistoryService.HashFile(source))
                                throw new InvalidDataException("旧资源历史存在冲突快照，未迁移：" + relative);
                        }
                        else File.Copy(source, staged);
                    }
                }
                File.WriteAllLines(Path.Combine(temporary, "History", "history.jsonl"), lines, new UTF8Encoding(false));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination))
                {
                    // A previous failed resource commit can leave unreferenced snapshots.
                    // Keep them; the imported journal and referenced snapshots are merged atomically per file.
                    foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)
                                 .Where(file => !Path.GetFileName(file).Equals("history.jsonl", StringComparison.Ordinal)))
                    {
                        var target = ProjectHistoryPaths.ResolveInside(destination, Path.GetRelativePath(temporary, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!File.Exists(target)) File.Copy(file, target);
                        else if (HistoryService.HashFile(file) != HistoryService.HashFile(target))
                            throw new InvalidDataException("资源历史快照与既有文件冲突。");
                    }
                    File.Move(Path.Combine(temporary, "History", "history.jsonl"), journal, false);
                }
                else Directory.Move(temporary, destination);
            }
            finally
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }
        return service;
    }

    public static bool CanRestore(ProjectProfile profile, HistoryRecord record)
    {
        try
        {
            var canonical = Canonicalize(profile);
            var root = Create(canonical).BackupRoot(canonical);
            var current = File.Exists(Path.Combine(root, "History", "history.jsonl"));
            ValidateTargets(profile, record, current ? root : null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static void ValidateRestore(ProjectProfile profile, HistoryRecord record)
    {
        var canonical = Canonicalize(profile);
        ValidateTargets(canonical, record, Create(canonical).BackupRoot(canonical));
    }

    private static void ValidateTargets(ProjectProfile profile, HistoryRecord record, string? snapshotRoot)
    {
        if (record.Targets == null || record.RenameTargets == null || record.Metadata == null)
            throw new InvalidDataException("资源历史记录结构不完整。");
        foreach (var target in record.Targets)
        {
            ProjectHistoryPaths.ValidateInside(target.TargetPath, profile.RootPath);
            if (snapshotRoot == null) continue;
            if (!string.IsNullOrWhiteSpace(target.SnapshotPath)) ProjectHistoryPaths.ValidateInside(target.SnapshotPath, snapshotRoot);
            if (!string.IsNullOrWhiteSpace(target.AfterSnapshotPath)) ProjectHistoryPaths.ValidateInside(target.AfterSnapshotPath, snapshotRoot);
        }
        // Batch rename deliberately supports a separately selected material directory.
        // Its own recorded root, rather than the game's root, bounds that transaction.
        var renameRoot = record.Metadata.GetValueOrDefault("RenameRoot", profile.RootPath);
        foreach (var target in record.RenameTargets)
        {
            ProjectHistoryPaths.ValidateInside(target.SourcePath, renameRoot);
            ProjectHistoryPaths.ValidateInside(target.TargetPath, renameRoot);
        }
    }

    private static IEnumerable<LegacyRecord> LegacyRecords(ProjectProfile profile)
    {
        var container = Path.Combine(profile.RootPath, ".data管理器形象历史");
        if (!Directory.Exists(container)) yield break;
        ProjectHistoryPaths.ValidateInside(container, profile.RootPath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in Directory.EnumerateDirectories(container).Order(StringComparer.Ordinal))
        {
            ProjectHistoryPaths.ValidateInside(root, container);
            var journal = ProjectHistoryPaths.ResolveInside(root, Path.Combine("History", "history.jsonl"));
            if (!File.Exists(journal)) continue;
            foreach (var line in File.ReadLines(journal, Encoding.UTF8))
            {
                LegacyRecord? result = null;
                try
                {
                    var record = JsonSerializer.Deserialize<HistoryRecord>(line);
                    if (record == null || string.IsNullOrWhiteSpace(record.Id)) continue;
                    if (!IsLegacyOwner(root, container, profile, record)) continue;
                    ValidateTargets(profile, record, root);
                    if (seen.Add(record.Id)) result = new(root, line, record);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    // Keep legacy originals untouched. A copied project's absolute targets,
                    // malformed records and escaping snapshots never acquire a new identity.
                }
                if (result != null) yield return result;
            }
        }
    }

    private static bool IsLegacyOwner(string root, string container, ProjectProfile profile, HistoryRecord record)
    {
        if (record.Targets == null || record.RenameTargets == null || record.Metadata == null) return false;
        var normalized = ProjectHistoryPaths.NormalizeProjectRoot(profile.RootPath);
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            profile.RootPath, normalized, normalized + Path.DirectorySeparatorChar,
            Canonicalize(profile).RootPath, Canonicalize(profile).RootPath + Path.DirectorySeparatorChar
        };
        // Older journals preserve the original spelling in their absolute targets.
        // It can recover an old hash after a case-only reopen, but cannot bind a
        // copied project's journal to a different absolute directory.
        foreach (var target in record.Targets)
            if (ProjectHistoryPaths.IsInside(target.TargetPath, normalized) && target.TargetPath.Length > normalized.Length)
            {
                var spelling = target.TargetPath[..normalized.Length];
                candidates.Add(spelling);
                candidates.Add(spelling + Path.DirectorySeparatorChar);
            }
        var old = new HistoryService(container);
        return candidates.Any(candidate => old.BackupRoot(profile with { RootPath = candidate })
            .Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    private sealed record LegacyRecord(string Root, string Line, HistoryRecord Record);
}
