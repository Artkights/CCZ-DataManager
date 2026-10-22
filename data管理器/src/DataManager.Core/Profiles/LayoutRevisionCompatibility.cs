using System.Security.Cryptography;
using System.Text;
using DataManager.Core.Models;

namespace DataManager.Core.Profiles;

/// <summary>
/// Explicit, one-way upgrade from the shipped ExtendData v1 manifests. A revision
/// is accepted only after reproducing it from the frozen schema and this project's
/// current physical geometry; unknown hashes and other schema changes stay blocked.
/// </summary>
public static class LayoutRevisionCompatibility
{
    private static readonly Lazy<ProfileRegistry> Current = new(() => new ProfileRegistry());
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
            var oldBase = Legacy.Value.Resolve(profileId, variantId);
            var current = new ProfileAugmentationService().Apply(projectRoot, currentBase);
            if (!current.LayoutRevision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase)) return false;
            if (GetPreviousAugmentationRevisions(currentBase, current).Contains(actualRevision, StringComparer.OrdinalIgnoreCase)) return true;
            return IsRegisteredUpgrade(oldBase, currentBase) &&
                GetLegacyRevisions(oldBase, current).Contains(actualRevision, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Known v1 identities for this exact project, for migration diagnostics and verification.</summary>
    public static IReadOnlyList<string> GetLegacyRevisions(string projectRoot, string profileId, string variantId)
    {
        var currentBase = Current.Value.Resolve(profileId, variantId);
        var oldBase = Legacy.Value.Resolve(profileId, variantId);
        var current = new ProfileAugmentationService().Apply(projectRoot, currentBase);
        return GetPreviousAugmentationRevisions(currentBase, current)
            .Concat(IsRegisteredUpgrade(oldBase, currentBase) ? GetLegacyRevisions(oldBase, current) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
