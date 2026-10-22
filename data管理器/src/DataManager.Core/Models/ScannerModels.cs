namespace DataManager.Core.Models;

public enum ScanStatus
{
    Matched,
    Partial,
    Unmatched,
    MissingFiles
}

public sealed class ScanReport
{
    public ScanStatus Status { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public List<ScanFileReport> Files { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public ProfileMatchResult? ProfileMatch { get; init; }
    public string ProfileDisplayName { get; init; } = string.Empty;
    public string VariantDisplayName { get; init; } = string.Empty;
    public string LayoutRevision { get; init; } = string.Empty;
    public bool AllowsEditing => true;
    public string DisplayMode => ProfileMatch == null
        ? Title
        : $"{ProfileMatch.MatchText}；当前按 {ProfileDisplayName} / {VariantDisplayName} 读取与写入。";
}

public sealed record ScanFileReport(
    string RelativePath,
    bool Exists,
    long ActualLength,
    long MinimumLength,
    bool LengthMatches,
    string Sha256,
    string Message);
