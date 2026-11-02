using System.Globalization;
using System.Text.RegularExpressions;
using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Searches physical gallery entries through the caller's current draft assignments.</summary>
public sealed class SImageSearchIndex(IReadOnlyList<SImageAssignmentReference> references)
{
    public bool Matches(LogicalImageAsset asset, string query)
    {
        if (asset.Kind != ResourceKind.SComposite)
            return asset.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   asset.LogicalId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase);
        var physical = SResourceExchangeNaming.PhysicalId(asset);
        var assigned = references.Where(item => item.PhysicalImageNumbers.Contains(physical)).ToArray();
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(token =>
        {
            var person = Regex.Match(token, @"^人物[:：#]?(\d+)$");
            if (person.Success) return assigned.Any(item => NumericEquals(person.Groups[1].Value, item.PersonId));
            var compact = Regex.Match(token, @"^(?:紧凑S|DataS|S)[:：=]?(\d+)$", RegexOptions.IgnoreCase);
            if (compact.Success) return assigned.Any(item => NumericEquals(compact.Groups[1].Value, item.SId));
            var unit = Regex.Match(token, @"^(?:Unit|物理|S_|#)[:：#=]?(\d+)$", RegexOptions.IgnoreCase);
            if (unit.Success) return NumericEquals(unit.Groups[1].Value, physical);
            return asset.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                   asset.Status.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                   $"S_{physical:000}".Contains(token, StringComparison.OrdinalIgnoreCase) ||
                   physical.ToString(CultureInfo.InvariantCulture).Contains(token, StringComparison.OrdinalIgnoreCase) ||
                   assigned.Any(item => item.PersonName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       item.PersonId.ToString(CultureInfo.InvariantCulture).Equals(token, StringComparison.Ordinal));
        });
    }

    public string DescribeReferences(LogicalImageAsset asset)
    {
        if (asset.Kind != ResourceKind.SComposite) return string.Empty;
        var physical = SResourceExchangeNaming.PhysicalId(asset);
        var assigned = references.Where(item => item.PhysicalImageNumbers.Contains(physical)).ToArray();
        return assigned.Length == 0 ? string.Empty : "；当前草稿引用：" + string.Join("、", assigned.Select(item =>
            $"人物 {item.PersonId} {item.PersonName}（紧凑 S={item.SId}）"));
    }

    private static bool NumericEquals(string text, int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == number;
}
