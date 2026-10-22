namespace DataManager.Core.Binary;

public static class ProjectFileLocator
{
    private static readonly string[] CommonSubdirectories = [string.Empty, "E5", "e5"];

    public static string Resolve(string projectRoot, string fileName)
    {
        return GetCandidates(projectRoot, fileName).FirstOrDefault() ?? Path.Combine(projectRoot, fileName);
    }

    /// <summary>Ordered physical sources; all readers and writers use the same root-first result.</summary>
    public static IReadOnlyList<string> GetCandidates(string projectRoot, string fileName)
    {
        var results = new List<string>();
        foreach (var subdirectory in CommonSubdirectories)
        {
            var directory = string.IsNullOrEmpty(subdirectory) ? projectRoot : Path.Combine(projectRoot, subdirectory);
            if (!Directory.Exists(directory)) continue;
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct)) { results.Add(direct); continue; }
            var match = Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
            if (match != null) results.Add(match);
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
