using System.Security.Cryptography;
using System.Text;
using DataManager.Core.Models;

namespace DataManager.Core.Profiles;

/// <summary>
/// Explicit, one-way upgrade from the shipped extension and pre-simplification manifests. A revision
/// is accepted only after reproducing it from the frozen schema and this project's
/// current physical geometry; unknown hashes and other schema changes stay blocked.
/// </summary>
public static class LayoutRevisionCompatibility
{
    private static readonly Lazy<ProfileRegistry> Current = new(() => new ProfileRegistry());
    private static readonly Lazy<ProfileRegistry> BeforeSimplification = new(() => new ProfileRegistry(useLegacyExtensionManifest: false, usePreSimplificationManifest: true));
    private static readonly Lazy<ProfileRegistry> BeforeConfiguration64 = new(() => new ProfileRegistry(useLegacyExtensionManifest: false, usePreConfiguration64Manifest: true));
    private static readonly Lazy<ProfileRegistry> Legacy = new(() => new ProfileRegistry(useLegacyExtensionManifest: true));

    public static bool IsCompatible(string projectRoot, string profileId, string variantId,
        string actualRevision, string expectedRevision)
    {
        if (string.Equals(actualRevision, expectedRevision, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot) ||
            string.IsNullOrWhiteSpace(actualRevision) || string.IsNullOrWhiteSpace(expectedRevision)) return false;
        try
        {
            var currentBase = Current.Value.Resolve(profileId, variantId);
            var current = new ProfileAugmentationService().Apply(projectRoot, currentBase);
            if (!current.LayoutRevision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase)) return false;
            return GetRecognizedRevisions(projectRoot, profileId, variantId, currentBase, current)
                .Contains(actualRevision, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Known shipped identities for this exact project, for migration diagnostics and verification.</summary>
    public static IReadOnlyList<string> GetLegacyRevisions(string projectRoot, string profileId, string variantId)
    {
        var currentBase = Current.Value.Resolve(profileId, variantId);
        var current = new ProfileAugmentationService().Apply(projectRoot, currentBase);
        return GetRecognizedRevisions(projectRoot, profileId, variantId, currentBase, current);
    }

    private static IReadOnlyList<string> GetRecognizedRevisions(string projectRoot, string profileId, string variantId,
        ResolvedProfile currentBase, ResolvedProfile current)
    {
        // Reproduce only the exact preceding 6.4 manifest; never borrow 6.5/6.6 identities.
        if (profileId.Equals("ccz-6.4", StringComparison.OrdinalIgnoreCase))
            return GetPreConfiguration64Revisions(projectRoot, profileId, variantId, currentBase, current);
        // Frozen manifests only reproduce identities of shipped layouts. They never
        // supply active UI modules or restore the retired editing capabilities.
        var precedingBase = BeforeSimplification.Value.Resolve(profileId, variantId);
        if (!IsRegisteredSimplification(precedingBase, currentBase))
            return GetPreviousAugmentationRevisions(currentBase, current);
        var oldBase = Legacy.Value.Resolve(profileId, variantId);
        var preceding = new ProfileAugmentationService().Apply(projectRoot, precedingBase);
        return GetPreviousAugmentationRevisions(currentBase, current)
            .Append(preceding.LayoutRevision)
            .Concat(GetPreviousAugmentationRevisions(precedingBase, current))
            .Concat(IsRegisteredUpgrade(oldBase, precedingBase) ? GetLegacyRevisions(oldBase, current) : [])
            .Where(revision => !revision.Equals(current.LayoutRevision, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> GetPreConfiguration64Revisions(string projectRoot, string profileId,
        string variantId, ResolvedProfile currentBase, ResolvedProfile current)
    {
        var preceding = BeforeConfiguration64.Value.Resolve(profileId, variantId);
        // This exact transition opens previously preserved fields and adds a previously
        // unavailable text table. Every old writable byte retains its file/ID/offset.
        // Frozen schema hashes still include all unrelated tables, codecs and policies.
        var permissions = new[] { "普通剑", "特殊剑", "普通枪", "特殊枪", "普通弓", "特殊弓", "普通刀", "特殊刀",
            "普通炮车", "特殊炮车", "普通锤", "特殊锤", "普通斧", "特殊斧", "普通扇", "特殊扇", "普通宝剑", "特殊宝剑",
            "普通将剑", "特殊将剑", "普通铠甲", "特殊铠甲", "普通衣服", "特殊衣服", "普通袍服", "特殊袍服" };
        var upgraded = preceding.Tables.Select(table => table.Key switch
        {
            "persons" => table with { Fields = table.Fields.Where(field => field.Offset != 29)
                .Select(field => field.Offset is 27 or 28 ? field with { Policy = FieldPolicy.Editable } : field)
                .Concat(new[] { "默认武器", "默认防具", "默认辅助" }.Select((name, index) =>
                    new TableFieldDefinition(name, 29 + index, 1, FieldKind.UInt8, ReferenceTable: "items"))).ToArray() },
            "detailed-job-growth" => table with { Fields = table.Fields.Where(field => field.Offset != 9)
                .Concat(permissions.Select((name, index) => new TableFieldDefinition(name, 9 + index, 1, FieldKind.UInt8))).ToArray() },
            "person-retreat-quotes" => table with { DataOffset = 130000,
                CapabilityEvidence = "由洗隋49条上限/200字节步长/IMSG对象加载与对话调用链独立探测后开放。" },
            _ => table
        }).ToArray();
        if (!ProfileRegistry.ComputeLayoutRevision(profileId, variantId, upgraded).Equals(currentBase.LayoutRevision, StringComparison.Ordinal)) return [];
        // A damaged consumer cannot gain history authority through a formerly open gate.
        if (!current.Tables.Any(table => table.Key == "person-retreat-quotes" && table.Capability == TableCapability.Editable) ||
            !current.Tables.Any(table => table.Key == "person-critical-special-map" && table.Capability == TableCapability.Editable) ||
            !current.Tables.Where(table => table.Key is "persons" or "detailed-job-growth")
                .SelectMany(table => table.Fields.Where(field => table.Key == "persons" ? field.Offset >= 27 : field.Offset >= 9))
                .All(field => field.Policy == FieldPolicy.Editable)) return [];
        var prior = new ProfileAugmentationService().ApplyPreConfiguration64(projectRoot, preceding);
        return [prior.LayoutRevision];
    }

    private static bool IsRegisteredSimplification(ResolvedProfile preceding, ResolvedProfile current)
    {
        // Recognize this exact removal and the shared lookup's read-only policy.
        // Any unrelated schema change must have its own explicit compatibility rule.
        var simplified = preceding.Tables
            .Where(table => table.Key is not ("class-effects" or "personal-effects" or "effect-descriptions"))
            .Select(table => table.Key == "effect-names" ? table with { Capability = TableCapability.ReadOnly } : table);
        return ProfileRegistry.ComputeLayoutRevision(current.ProfileId, current.VariantId, simplified)
            .Equals(current.LayoutRevision, StringComparison.Ordinal);
    }

    // The equipment editor and native effect-name probe add only runtime tables/capabilities.
    // Recreate the immediately preceding release's exact hash algorithm, without accepting
    // arbitrary revisions or silently treating any equal-width table as interchangeable.
    private static IReadOnlyList<string> GetPreviousAugmentationRevisions(ResolvedProfile baseline, ResolvedProfile current)
    {
        var names = current.Tables.SingleOrDefault(table => table.Key == "equipment-type-names");
        var display = current.Tables.SingleOrDefault(table => table.Key == "equipment-type-display");
        if (names is not { DataOffset: 0x8AC70, RowCount: 15, RowSize: 5 } ||
            display is not { DataOffset: 0x81827, RowCount: 13, RowSize: 1 } ||
            !names.Fields.Any(field => field.Name == "名称" && field.Offset == 0 && field.Size == 4 && field.Kind == FieldKind.FixedString) ||
            !names.Fields.Any(field => field.Offset == 4 && field.Size == 1 && field.Policy == FieldPolicy.Preserve) ||
            !display.Fields.Any(field => field.Name == "显示值" && field.Offset == 0 && field.Size == 1 && field.Kind == FieldKind.UInt8)) return [];
        var geometry = string.Join("|", current.Tables.Where(table => table.Key is
                "person-r-assign" or "person-s-assign" or "person-critical-special-map" or "strategy-terrain-availability")
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}"));
        var extensions = current.Tables.Where(table => table.Key is "effect-archive" or "effect-messages" or "strategy-study-extended" or "forge")
            .Where(table => baseline.Tables.Any(original => original.Key == table.Key &&
                (original.RowCount != table.RowCount || original.Capability != table.Capability)))
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}").ToArray();
        return [Hash(baseline.LayoutRevision + "|" + geometry + (extensions.Length == 0 ? "" : "|" + string.Join("|", extensions)))];
    }

    private static bool IsRegisteredUpgrade(ResolvedProfile oldBase, ResolvedProfile currentBase)
    {
        // Removing precisely the newly documented bytes must recreate the entire
        // old manifest, including every unrelated table and capability. This is
        // not a general-purpose "same row width" compatibility escape hatch.
        var reduced = currentBase.Tables.Select(table => table with
        {
            Fields = table.Fields.Where(field => !IsAddedField(table.Key, field)).ToArray()
        }).ToArray();
        return ProfileRegistry.ComputeLayoutRevision(currentBase.ProfileId, currentBase.VariantId, reduced)
            .Equals(oldBase.LayoutRevision, StringComparison.Ordinal);
    }

    private static bool IsAddedField(string tableKey, TableFieldDefinition field)
    {
        if (field.Kind != FieldKind.UInt8 || field.Size != 1 || field.Policy != FieldPolicy.Editable) return false;
        if (tableKey == "forge")
            return (field.Name, field.Offset) is ("材料项数", 58) or ("商店编号", 59) or ("剩余次数", 61);
        if (tableKey != "effect-archive") return false;
        for (var slot = 0; slot < 256; slot++)
            if (field.Name == $"槽{slot}经验" && field.Offset == slot * 4 + 2 ||
                field.Name == $"槽{slot}剩余回合" && field.Offset == slot * 4 + 3) return true;
        return false;
    }

    private static IReadOnlyList<string> GetLegacyRevisions(ResolvedProfile oldBase, ResolvedProfile current)
    {
        // v1 predated physical extension rows; the subsequent physical-row patch
        // appended geometry only when it differed from the embedded defaults.
        // Both algorithms are frozen here and only these exact hashes are allowed.
        var geometry = string.Join("|", current.Tables.Where(table => table.Key is
                "person-r-assign" or "person-s-assign" or "person-critical-special-map" or "strategy-terrain-availability")
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}"));
        var extensions = current.Tables.Where(table => table.Key is "effect-archive" or "effect-messages" or "strategy-study-extended" or "forge")
            .Where(table => oldBase.Tables.Any(original => original.Key == table.Key &&
                (original.RowCount != table.RowCount || original.Capability != table.Capability)))
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}").ToArray();
        var oldRevision = Hash(oldBase.LayoutRevision + "|" + geometry);
        var physicalRevision = Hash(oldBase.LayoutRevision + "|" + geometry + (extensions.Length == 0 ? "" : "|" + string.Join("|", extensions)));
        return new[] { oldRevision, physicalRevision }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
