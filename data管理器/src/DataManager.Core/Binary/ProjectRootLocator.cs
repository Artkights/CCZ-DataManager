namespace DataManager.Core.Binary;

/// <summary>Unwraps one packaging directory without requiring every optional project file.</summary>
public static class ProjectRootLocator
{
    private static readonly string[] Anchors =
        ["Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe", "Effect.e5", "Effectmsg.e5", "MeffStudy.e5", "Forge.e5"];

    public static IReadOnlyList<string> GetCandidates(string selectedRoot)
    {
        var root = Path.GetFullPath(selectedRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        // Selecting a resource subfolder should still open its enclosing game.
        if (Path.GetFileName(root).Equals("E5", StringComparison.OrdinalIgnoreCase) &&
            Directory.GetParent(root) is { } parent && File.Exists(Path.Combine(parent.FullName, "Ekd5.exe")))
            return [parent.FullName];
        if (HasAnchor(root)) return [root];
        var candidates = Directory.EnumerateDirectories(root)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                !Path.GetFileName(path).StartsWith('.') && HasAnchor(path))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
        // Keep partial/empty directories inspectable, preserving existing scanner behavior.
        return candidates.Length == 0 ? [root] : candidates;
    }

    public static string Resolve(string selectedRoot)
    {
        var candidates = GetCandidates(selectedRoot);
        if (candidates.Count != 1)
            throw new InvalidOperationException("所选目录包含多个游戏基底，请选择实际游戏目录：" + string.Join("；", candidates));
        return candidates[0];
    }

    private static bool HasAnchor(string directory)
        => Anchors.Any(name => File.Exists(ProjectFileLocator.Resolve(directory, name)));
}
