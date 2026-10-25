using DataManager.Core.Appearance;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Joins successful save groups to the image journal without making a bad save block a picture edit.</summary>
internal sealed class ImageAppearanceSyncTransaction
{
    private const string SkippedPrefix = "存档同步跳过：";

    public MutationResult Commit(ProjectProfile profile, MutationPlan plan, HistoryService history)
    {
        var resourceFiles = plan.Files.Where(file => RsResourceImpactAnalyzer.IsRsResourceName(file.TargetPath)).ToArray();
        var mutations = new ImageMutationService(history);
        if (resourceFiles.Length == 0) return mutations.Commit(profile, plan);

        AppearanceSyncSession? session = null;
        var diagnostics = new List<string>();
        var contributionSkipped = false;
        var dependencies = new Dictionary<string, MutationSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var changes = new List<RsResourceChange>();
            foreach (var file in resourceFiles)
            {
                // Only the active project's actual resource sources can select people.
                var activePath = profile.TryGetResource(Path.GetFileName(file.TargetPath));
                if (activePath == null || !Path.GetFullPath(activePath).Equals(Path.GetFullPath(file.TargetPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("R/S 资源不是当前项目已识别的物理来源。");
                file.ExpectedSource.Verify(file.TargetPath);
                changes.Add(new RsResourceChange(Path.GetFileName(file.TargetPath),
                    File.Exists(file.TargetPath) ? File.ReadAllBytes(file.TargetPath) : [],
                    file.DeleteTarget ? null : file.CandidateBytes));
                file.ExpectedSource.Verify(file.TargetPath);
            }
            var analyzer = new RsResourceImpactAnalyzer();
            var impact = analyzer.Analyze(changes);
            if (!impact.HasChanges) return mutations.Commit(profile, plan);

            foreach (var name in new[] { "Ekd5.exe", "Data.e5", "System.ini" })
            {
                var path = ProjectFileLocator.Resolve(profile.RootPath, name);
                dependencies[path] = MutationSourceSnapshot.Capture(path);
            }
            // This independent read deliberately excludes every unsaved UI/DataRow draft.
            var saved = new ProjectWorkspace();
            saved.Open(profile.RootPath);
            var updates = analyzer.CreatePersonUpdates(saved, impact);
            if (updates.Count == 0) return mutations.Commit(profile, plan);
            var executablePath = ProjectFileLocator.Resolve(saved.RootPath, "Ekd5.exe");
            var executableBytes = File.ReadAllBytes(executablePath);
            foreach (var dependency in dependencies) dependency.Value.Verify(dependency.Key);
            session = SaveAppearanceSyncService.Prepare(saved.RootPath, saved.ActiveProfile.ProfileId, executableBytes, updates);
            foreach (var dependency in dependencies) dependency.Value.Verify(dependency.Key);
        }
        catch (Exception ex) when (IsSyncFailure(ex))
        {
            contributionSkipped = true;
            diagnostics.Add(SkippedPrefix + ex.Message);
        }

        var metadata = plan.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, string>();
        var result = mutations.Commit(profile, plan with { Metadata = metadata }, snapshotDirectory =>
        {
            if (session == null) return [];
            try
            {
                // A concurrently changed saved assignment affects only this contribution.
                // The image candidate still follows its original dependency/write contract.
                foreach (var dependency in dependencies) dependency.Value.Verify(dependency.Key);
            }
            catch (Exception ex) when (IsSyncFailure(ex))
            {
                contributionSkipped = true;
                diagnostics.Add(SkippedPrefix + "已保存人物指定在图片提交期间改变，" + ex.Message);
                return [];
            }
            try
            {
                session.Commit();
                foreach (var dependency in dependencies) dependency.Value.Verify(dependency.Key);
                var targets = new List<HistoryTarget>();
                foreach (var file in session.AppliedFiles)
                {
                    var path = Path.GetFullPath(Path.Combine(profile.RootPath, file.RelativePath));
                    ProjectHistoryPaths.ValidateInside(path, profile.RootPath);
                    var stem = "appearance_" + targets.Count.ToString("D4") + "_" + Path.GetFileName(path);
                    var beforePath = Path.Combine(snapshotDirectory, stem + ".before");
                    var afterPath = Path.Combine(snapshotDirectory, stem + ".after");
                    File.WriteAllBytes(beforePath, file.BeforeBytes);
                    File.WriteAllBytes(afterPath, file.AfterBytes);
                    if (!HashService.Sha256(beforePath).Equals(file.Change.BeforeHash, StringComparison.OrdinalIgnoreCase) ||
                        !HashService.Sha256(afterPath).Equals(file.Change.AfterHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("存档形象历史快照复读不一致：" + file.RelativePath);
                    targets.Add(new HistoryTarget
                    {
                        TargetPath = path, SnapshotPath = beforePath, AfterSnapshotPath = afterPath,
                        BeforeSha256 = file.Change.BeforeHash, AfterSha256 = file.Change.AfterHash,
                        BeforeLength = file.BeforeBytes.LongLength, AfterLength = file.AfterBytes.LongLength,
                        BeforeExists = true, AfterExists = true,
                        BeforeAttributes = file.BeforeAttributes, AfterAttributes = File.GetAttributes(path)
                    });
                }
                metadata["AppearanceSync"] = session.Report.Summary;
                metadata["AppearanceSyncDiagnostics"] = string.Join(Environment.NewLine, session.Report.Diagnostics);
                return targets;
            }
            catch (Exception ex) when (IsSyncFailure(ex))
            {
                // Until all companion snapshots exist, the shared image transaction cannot
                // recover them. Restore this contribution before allowing its caller to continue.
                session.Rollback();
                contributionSkipped = true;
                diagnostics.Add(SkippedPrefix + "本次存档贡献已恢复，" + ex.Message);
                return [];
            }
        }, () => session?.Rollback());

        if (!result.Success) return result;
        var report = session?.Report;
        if (report != null)
        {
            diagnostics.AddRange(report.Diagnostics);
            if (report.SkippedSaves > 0 || report.Diagnostics.Count > 0) diagnostics.Add(SkippedPrefix + report.Summary);
        }
        var summary = result.Summary + (contributionSkipped ? "；存档形象同步已跳过" : report != null ? "；" + report.Summary :
            diagnostics.Count > 0 ? "；存档形象同步已跳过" : string.Empty);
        return result with { Summary = summary, Diagnostics = result.Diagnostics.Concat(diagnostics).ToArray() };
    }

    private static bool IsSyncFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or NotSupportedException or System.Security.SecurityException or OverflowException or FormatException;
}
