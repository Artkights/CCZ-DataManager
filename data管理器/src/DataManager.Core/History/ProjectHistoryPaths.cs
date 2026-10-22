using System.Security.Cryptography;
using System.Text;

namespace DataManager.Core.History;

/// <summary>All data-manager journals belong to the executable and an exact project directory identity.</summary>
public static class ProjectHistoryPaths
{
    private static readonly AsyncLocal<string?> BaseDirectoryOverride = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> ProjectOverrides = new();

    public static string BaseDirectory => BaseDirectoryOverride.Value ?? AppContext.BaseDirectory;

    public static string NormalizeProjectRoot(string projectRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

    public static string GetProjectStorageRoot(string projectRoot)
    {
        var normalized = NormalizeProjectRoot(projectRoot);
        var identity = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        if (ProjectOverrides.Value?.TryGetValue(identity, out var temporary) == true) return temporary;
        var name = new DirectoryInfo(normalized).Name;
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "项目";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(BaseDirectory, name + "_历史记录", hash);
    }

    public static string GetDataRoot(string projectRoot) => Path.Combine(GetProjectStorageRoot(projectRoot), "数据");
    public static string GetImageRoot(string projectRoot) => Path.Combine(GetProjectStorageRoot(projectRoot), "形象");
    public static string GetMigrationRoot(string projectRoot) => Path.Combine(GetProjectStorageRoot(projectRoot), "迁移备份");
    public static string GetLegacyDataManifest(string projectRoot)
        => ResolveInside(projectRoot, Path.Combine(".data管理器历史", "manifest.jsonl"));

    /// <summary>Read-only resolution; opening an untouched project never creates a history directory.</summary>
    public static string GetDataManifestForRead(string projectRoot)
    {
        var current = Path.Combine(GetDataRoot(projectRoot), "manifest.jsonl");
        return File.Exists(current) ? current : GetLegacyDataManifest(projectRoot);
    }

    /// <summary>Test hosts can isolate all executable-owned history without changing application defaults.</summary>
    public static IDisposable UseBaseDirectory(string baseDirectory)
    {
        var previous = BaseDirectoryOverride.Value;
        BaseDirectoryOverride.Value = Path.GetFullPath(baseDirectory);
        return new Scope(() => BaseDirectoryOverride.Value = previous);
    }

    /// <summary>Migration staging keeps temporary journals in the temporary project, including independent readers.</summary>
    public static IDisposable UseProjectStorage(string projectRoot, string storageRoot)
    {
        var previous = ProjectOverrides.Value;
        var identity = NormalizeProjectRoot(projectRoot);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var overrides = previous?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        overrides[identity] = Path.GetFullPath(storageRoot);
        ProjectOverrides.Value = overrides;
        return new Scope(() => ProjectOverrides.Value = previous);
    }

    public static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var parent = NormalizeProjectRoot(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, comparison);
    }

    /// <summary>Journals cannot redirect a restore through a relative escape or a linked child directory.</summary>
    public static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("历史路径必须相对于所属目录。");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        ValidateInside(path, root);
        return path;
    }

    public static void ValidateInside(string path, string root)
    {
        if (!IsInside(path, root)) throw new InvalidDataException("历史路径超出当前项目或快照目录：" + path);
        var normalizedRoot = NormalizeProjectRoot(root);
        var current = Path.GetFullPath(path);
        while (!current.Equals(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("历史路径包含链接，已停止恢复：" + current);
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("历史路径没有有效父目录。");
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
