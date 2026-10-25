using DataManager.Core.Appearance;
using DataManager.Core.Editing;

namespace DataManager.Images;

public sealed partial class ImageMigrationResourceConverter
{
    public IReadOnlyList<PersonAppearanceUpdate> GetAppearanceUpdates(string stagingRoot, string targetRoot,
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var changes = new List<RsResourceChange>();
        foreach (var relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!RsResourceImpactAnalyzer.IsRsResourceName(relative)) continue;
            var staged = Path.GetFullPath(Path.Combine(stagingRoot, relative));
            var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
            RequireInside(stagingRoot, staged);
            RequireInside(targetRoot, target);
            if (!File.Exists(staged)) continue;
            changes.Add(new RsResourceChange(relative, File.Exists(target) ? File.ReadAllBytes(target) : [],
                File.ReadAllBytes(staged)));
        }
        var analyzer = new RsResourceImpactAnalyzer();
        var impact = analyzer.Analyze(changes, cancellationToken);
        if (!impact.HasChanges) return [];
        var workspace = new ProjectWorkspace();
        workspace.Open(stagingRoot, cancellationToken: cancellationToken);
        return analyzer.CreatePersonUpdates(workspace, impact);
    }

    private static void RequireInside(string root, string path)
    {
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("形象差异候选超出当前迁移项目。");
    }
}
