namespace DataManager.Core.Domain;

/// <summary>
/// Version-level native range contracts shared by consumers that only need
/// candidate IDs.  Geometry parsing and EXE writes remain in RangeManager;
/// this class intentionally exposes no E5 image count as a native limit.
/// </summary>
public static class NativeRangeDomain
{
    public const int LogicalAttackMaximum = 0x25;

    public static IReadOnlyList<int> LogicalAttackIds { get; } =
        Enumerable.Range(0, LogicalAttackMaximum + 1).ToArray();

    public static IReadOnlyList<int> LogicalAttackEditableIds { get; } =
        Enumerable.Range(0, LogicalAttackMaximum + 1)
            .Where(id => id is not (0x0A or 0x0B)).ToArray();

    public static int PierceMaximum(string? profileId)
        => profileId?.Trim().ToLowerInvariant() switch
        {
            "ccz-6.4" => 0x09,
            "ccz-6.6" => 0x0A,
            _ => 0x0B
        };

    public static string? SpecialAttackLabel(int id)
        => id switch
        {
            0x0A => "全场特殊分支",
            0x0B => "原生忽略编号",
            _ => null
        };
}

