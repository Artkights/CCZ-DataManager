namespace DataManager.Core.Domain;

/// <summary>
/// 6.6 逐策略扩展位的已静态验证语义。6.4/6.5 的 0xA2CE0 是填充区，
/// 不存在逐策略模仿或穿透开关；本类不改变任何物理表布局。
/// </summary>
public static class StrategyExtensionSemantics
{
    public const string TableKey = "strategy-extension";
    public const string Profile66 = "ccz-6.6";

    public const byte MimicAi = 0x01;
    public const byte PenetrationEnhancement = 0x02;
    public const byte DefinedMask = MimicAi | PenetrationEnhancement;
    public const byte ReservedMask = 0xFC;

    // 6.5 强化策略穿透：特效 0xB6(182)，表 0x442871，按策略穿透码索引。
    public static readonly byte[] Penetration65 = [0x01, 0x02, 0x03, 0x06, 0x07, 0x04, 0x06, 0x05, 0x08];

    // 6.6 强化穿透-策略：特效 0xA8(168)，表 0x45D343，仅在逐策略 bit1 置位时查询。
    public static readonly byte[] Penetration66 = [0x01, 0x02, 0x03, 0x08, 0x07, 0x05, 0x06, 0x05, 0x06, 0x02, 0x0A];

    // 攻击/施法范围拓展：表 0x41CEAB，三基底一致，33 项范围等级码。
    public static readonly byte[] AttackRange =
    [
        0x01, 0x06, 0x03, 0x04, 0x12, 0x13, 0x0C, 0x08, 0x12, 0x1F, 0x0A,
        0x00, 0x0D, 0x21, 0x0F, 0x16, 0x11, 0x12, 0x25, 0x0C, 0x0C, 0x16,
        0x22, 0x01, 0x20, 0x1D, 0x03, 0x1C, 0x1D, 0x23, 0x1F, 0x03, 0x24
    ];

    public static bool SupportsPerStrategyFlags(string? profileId)
        => string.Equals(profileId, Profile66, StringComparison.Ordinal);

    /// <summary>保留未在界面显示的高位，只替换已定义的 bit0/bit1。</summary>
    public static byte PreserveReserved(byte original, int definedBits)
        => (byte)((original & ReservedMask) | (definedBits & DefinedMask));

    public static byte? MapPenetration(string? profileId, int code)
    {
        var table = profileId switch
        {
            "ccz-6.5" => Penetration65,
            Profile66 => Penetration66,
            _ => null
        };
        return table != null && code >= 0 && code < table.Length ? table[code] : null;
    }

    public static byte? MapAttackRange(int code)
        => code >= 0 && code < AttackRange.Length ? AttackRange[code] : null;
}
