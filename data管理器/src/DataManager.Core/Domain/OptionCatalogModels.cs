namespace DataManager.Core.Domain;

/// <summary>
/// Semantic candidate catalogs used by business editors.  The catalog kind is
/// deliberately independent from the Chinese display label: the same label
/// can refer to an E5 preview index, a legacy Data.e5 byte, or an Ekd5 native
/// shape depending on its table and field.
/// </summary>
public enum OptionCatalogKind
{
    LegacyDataAttackRange,
    NativeJobShape,
    E5AttackRange,
    E5PierceRange,
    StrategyType,
    StrategyTarget,
    JobAttribute,
    ConsumableEffect
}

public enum OptionSource
{
    NativeExe,
    DataTable,
    E5Resource,
    KnownName,
    CurrentValue
}

public enum OptionStatus
{
    Known,
    ReferencedByBase,
    PreviewAvailable,
    NoPreview,
    Special,
    Unknown,
    ResourceUnavailable,
    OutsideNativeContract
}

/// <summary>
/// One numeric candidate plus evidence for diagnostics and future UI
/// projections.  <see cref="Selectable"/> is separate from the status so a
/// special native entry can be shown while remaining read-only.
/// </summary>
public sealed record RangeOption(
    int Id,
    string Label,
    OptionCatalogKind CatalogKind,
    OptionSource Source,
    OptionStatus Status,
    bool Selectable,
    bool PreviewAvailable,
    string Evidence);

public sealed record OptionCatalogSnapshot(
    string ProfileId,
    string VariantId,
    string EngineVersion,
    string ExecutableHash,
    int? PreviewEntryCount,
    IReadOnlyList<RangeOption> Options,
    IReadOnlyList<string> Diagnostics)
{
    public static OptionCatalogSnapshot Empty(string profileId = "", string variantId = "")
        => new(profileId, variantId, string.Empty, string.Empty, null, [], []);
}

/// <summary>Inputs required to build one field's semantic candidate catalog.</summary>
public sealed record OptionCatalogRequest(
    string ProfileId,
    string VariantId,
    string EngineVersion,
    string ExecutableHash,
    string TableKey,
    string FieldName,
    OptionCatalogKind CatalogKind,
    int Minimum,
    int Maximum,
    int? CurrentValue,
    IReadOnlyCollection<int> ActualValues,
    IReadOnlyDictionary<int, string> KnownNames,
    int? PreviewEntryCount = null,
    int? NativeMaximum = null,
    bool ResourceAvailable = true,
    IReadOnlyList<string>? Diagnostics = null);

/// <summary>
/// Merges verified domains, base references, the current draft and naming
/// dictionaries without treating a UI list size as a semantic limit.
/// </summary>
public sealed class OptionCatalogService
{
    public OptionCatalogSnapshot Build(OptionCatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = new SortedSet<int>();
        var primary = new HashSet<int>();
        var source = new Dictionary<int, OptionSource>();

        if (request.CatalogKind is OptionCatalogKind.LegacyDataAttackRange or
            OptionCatalogKind.E5AttackRange or OptionCatalogKind.E5PierceRange)
        {
            if (request.PreviewEntryCount is > 0)
            {
                foreach (var id in Enumerable.Range(0, request.PreviewEntryCount.Value))
                {
                    if (id < request.Minimum || id > request.Maximum) continue;
                    ids.Add(id); primary.Add(id); source[id] = OptionSource.E5Resource;
                }
            }
        }
        else if (request.CatalogKind == OptionCatalogKind.NativeJobShape && request.NativeMaximum is >= 0)
        {
            var upper = Math.Min(request.Maximum, request.NativeMaximum.Value);
            if (upper >= request.Minimum)
                for (var id = request.Minimum; id <= upper; id++)
                {
                    ids.Add(id); primary.Add(id); source[id] = OptionSource.NativeExe;
                }
        }

        foreach (var pair in request.KnownNames)
        {
            if (pair.Key < request.Minimum || pair.Key > request.Maximum) continue;
            ids.Add(pair.Key);
            source.TryAdd(pair.Key, OptionSource.KnownName);
        }

        foreach (var id in request.ActualValues)
        {
            if (id < request.Minimum || id > request.Maximum) continue;
            ids.Add(id);
            source[id] = OptionSource.DataTable;
        }

        if (request.CurrentValue is { } current)
        {
            // Preserve a loaded value even when a malformed/custom profile
            // reports a value outside its declared semantic domain.  Storage
            // validation still controls whether a later edit may change it.
            ids.Add(current);
            source[current] = OptionSource.CurrentValue;
        }

        var options = ids.Select(id => BuildOption(request, id, primary.Contains(id), source.GetValueOrDefault(id)))
            .ToArray();
        return new OptionCatalogSnapshot(request.ProfileId, request.VariantId, request.EngineVersion,
            request.ExecutableHash, request.PreviewEntryCount, options,
            request.Diagnostics ?? []);
    }

    private static RangeOption BuildOption(OptionCatalogRequest request, int id, bool primary, OptionSource? source)
    {
        var isPreview = request.CatalogKind is OptionCatalogKind.LegacyDataAttackRange or
            OptionCatalogKind.E5AttackRange or OptionCatalogKind.E5PierceRange;
        var previewAvailable = !isPreview || request.PreviewEntryCount is > 0 && id >= 0 && id < request.PreviewEntryCount;
        var status = OptionStatus.Known;
        if (request.CatalogKind == OptionCatalogKind.NativeJobShape && request.NativeMaximum is { } nativeMax && id > nativeMax)
            status = OptionStatus.OutsideNativeContract;
        else if (isPreview && request.PreviewEntryCount is null && !request.ResourceAvailable)
            status = OptionStatus.ResourceUnavailable;
        else if (isPreview && !previewAvailable)
            status = OptionStatus.NoPreview;
        else if (source == OptionSource.DataTable || source == OptionSource.CurrentValue)
            status = OptionStatus.ReferencedByBase;
        else if (isPreview && previewAvailable)
            status = OptionStatus.PreviewAvailable;

        var label = request.KnownNames.TryGetValue(id, out var known)
            ? known
            : request.CatalogKind == OptionCatalogKind.NativeJobShape
                ? $"原生穿透范围 {id}（基底原值）"
                : $"基底范围 {id}（图号 {id + 1}；基底原值/未确认）";
        if (status == OptionStatus.NoPreview) label += "（无图）";
        else if (status == OptionStatus.ResourceUnavailable) label += "（资源不可用）";
        else if (status == OptionStatus.OutsideNativeContract) label += "（超出原生契约）";
        return new RangeOption(id, label, request.CatalogKind, source ?? OptionSource.KnownName,
            status, Selectable: true, previewAvailable, $"{request.TableKey}/{request.FieldName}");
    }
}

