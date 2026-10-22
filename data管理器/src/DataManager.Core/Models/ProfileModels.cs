namespace DataManager.Core.Models;

public enum ProfileMatchKind
{
    Exact,
    Compatible,
    Partial,
    Forced,
    Fallback,
    MissingFiles
}

public sealed class ProfileFingerprint
{
    public string SampleId { get; init; } = string.Empty;
    public Dictionary<string, long> FileLengths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Sha256 { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileVariant
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string LayoutId { get; init; } = string.Empty;
    public List<ProfileFingerprint> Fingerprints { get; init; } = [];
}

public sealed class DataProfile
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DefaultVariantId { get; init; } = string.Empty;
    public IReadOnlyList<ProfileVariant> Variants { get; init; } = [];
}

public sealed class ResolvedProfile
{
    public required DataProfile Profile { get; init; }
    public required ProfileVariant Variant { get; init; }
    public required IReadOnlyList<TableDefinition> Tables { get; init; }
    public required IReadOnlyList<ModuleDefinition> Modules { get; init; }
    public required string LayoutRevision { get; init; }
    public string ProfileId => Profile.Id;
    public string VariantId => Variant.Id;
    public string LayoutId => Variant.LayoutId;
}

public sealed class ProfileMatchResult
{
    public required string ProfileId { get; init; }
    public required string VariantId { get; init; }
    public required ProfileMatchKind MatchKind { get; init; }
    public required string DetectionMethod { get; init; }
    public string? SampleId { get; init; }
    public bool IsManualOverride { get; init; }
    public List<string> Evidence { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
    public Dictionary<string, string> CoreFileFingerprints { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string MatchText => MatchKind switch
    {
        ProfileMatchKind.Exact => "精确匹配",
        ProfileMatchKind.Compatible => "兼容匹配",
        ProfileMatchKind.Partial => "部分匹配",
        ProfileMatchKind.Forced => "手动强制",
        ProfileMatchKind.Fallback => "默认回退",
        ProfileMatchKind.MissingFiles => "缺少文件",
        _ => MatchKind.ToString()
    };
}
