namespace DataManager.Core.Domain;

/// <summary>
/// Filtering rules shared by all ID/name combo boxes.  Numeric input uses
/// contains matching, so typing 12 includes 12 and 120-129 while retaining
/// ordinary name matching.  An unknown numeric token can be submitted by the
/// caller and is deliberately not discarded by this helper.
/// </summary>
public static class IdNameSearch
{
    public static IReadOnlyList<IdNameOption> Filter(
        IEnumerable<IdNameOption> options,
        string? query,
        bool includeUnknown = false)
    {
        var source = options?.ToArray() ?? [];
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return source;

        var numeric = text.All(char.IsDigit);
        var result = source.Where(option =>
            numeric
                ? option.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal)
                : option.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                  option.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal));
        var list = result.OrderBy(option => option.Id).ToList();
        if (includeUnknown && numeric && int.TryParse(text, out var id) && list.All(option => option.Id != id))
        {
            list.Insert(0, new IdNameOption(id, "未确认/需实测"));
        }
        return list;
    }

    public static bool TryParseUnknownId(string? text, out int id)
        => int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out id);
}
