namespace ImageManager.Core;

public sealed partial class HistoryService
{
    /// <summary>Uses only owned metadata/files on a worker. Cancellation is honored before the transaction begins.</summary>
    public Task<MutationResult> RestoreAsync(ProjectProfile project, string historyId, bool redo, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeProfile(project);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ChangeState(frozen, historyId, redo);
        }, cancellationToken);
    }

    internal static ProjectProfile FreezeProfile(ProjectProfile project) => project with
    {
        ResourcePaths = project.ResourcePaths.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        Diagnostics = project.Diagnostics.ToArray()
    };
}

public sealed partial class ImageMutationService
{
    /// <summary>Snapshots plan bytes and dictionaries while the editor owns them; no Bitmap/control is retained.</summary>
    public FrozenImageMutation Freeze(ProjectProfile project, MutationPlan plan) => new(HistoryService.FreezeProfile(project), plan with
    {
        Files = plan.Files.Select(file => file with { CandidateBytes = (byte[])file.CandidateBytes.Clone() }).ToArray(),
        Warnings = plan.Warnings.ToArray(),
        Metadata = plan.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value),
        SourceSnapshots = plan.SourceSnapshots?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        ExpectedImages = plan.ExpectedImages.ToArray()
    });

    /// <summary>Standalone independent-file transaction; integrated save companions remain coordinated by their host.</summary>
    public Task<MutationResult> CommitFrozenAsync(FrozenImageMutation frozen, CancellationToken cancellationToken = default)
    {
        var plan = frozen.Plan ?? throw new ObjectDisposedException(nameof(FrozenImageMutation));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Commit(frozen.Project, plan);
        }, cancellationToken);
    }
}

public sealed class FrozenImageMutation : IDisposable
{
    internal FrozenImageMutation(ProjectProfile project, MutationPlan plan) { Project = project; Plan = plan; }
    internal ProjectProfile Project { get; }
    internal MutationPlan? Plan { get; private set; }
    public void Dispose() => Plan = null;
}
