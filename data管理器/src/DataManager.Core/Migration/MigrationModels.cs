using DataManager.Core.Models;

namespace DataManager.Core.Migration;

public sealed record MigrationProgress(string Stage, string Detail, int Completed = 0, int Total = 0);

public enum MigrationChoice
{
    Skip,
    Overwrite,
    Merge
}

public enum MigrationChangeKind
{
    Added,
    Changed,
    MissingTargetTable,
    MissingSourceTable,
    Incompatible,
    ResourceAdded,
    ResourceChanged,
    ResourceMissingSource,
    PhysicalFileAdded,
    NativeStructureChanged,
    /// <summary>A complete logical segment intentionally left untouched because the destination has no verified consumer.</summary>
    Ignored
}

public sealed record MigrationChange(
    string TableKey,
    string TableDisplayName,
    string FieldName,
    int Id,
    object? SourceValue,
    object? TargetValue,
    MigrationChangeKind Kind,
    string? Reason = null)
{
    /// <summary>Existing target storage location, which can differ between root and E5 layouts.</summary>
    public string? TargetRelativePath { get; init; }

    public string DisplayValue(object? value)
        => value is null or DBNull ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}

public sealed class MigrationPlan
{
    public required string SourceRoot { get; init; }
    public required string TargetRoot { get; init; }
    public required string SourceProfileId { get; init; }
    public required string SourceVariantId { get; init; }
    public required string TargetProfileId { get; init; }
    public required string TargetVariantId { get; init; }
    public required IReadOnlyList<MigrationChange> Changes { get; init; }
    /// <summary>Absent auxiliary files that can be initialized from verified target geometry.</summary>
    public IReadOnlyList<MigrationPhysicalFileCreation> PhysicalFileCreations { get; init; } = [];
    /// <summary>Physical data-file hashes captured during preview, including auxiliary files and absent-file markers.</summary>
    public IReadOnlyDictionary<string, string> SourceCoreFileFingerprints { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> TargetCoreFileFingerprints { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Project-local configuration used while resolving layouts and item category boundaries.</summary>
    public IReadOnlyDictionary<string, string> SourceEvidenceFingerprints { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> TargetEvidenceFingerprints { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public IReadOnlyList<MigrationChange> WritableChanges
        => Changes.Where(change => change.Kind is MigrationChangeKind.Added or MigrationChangeKind.Changed or MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged or MigrationChangeKind.PhysicalFileAdded or MigrationChangeKind.NativeStructureChanged).ToArray();

    public IReadOnlyList<MigrationChange> IgnoredChanges
        => Changes.Where(change => change.Kind == MigrationChangeKind.Ignored).ToArray();
}

public sealed record MigrationPhysicalFileCreation(string RelativePath, int Length, IReadOnlyList<string> TableKeys);

public sealed record MigrationApplyResult(
    bool Success,
    int AppliedChanges,
    int SkippedChanges,
    string? BackupPath,
    IReadOnlyList<string> Messages)
{
    public DataManager.Core.Appearance.AppearanceSyncReport? AppearanceSync { get; init; }
}
