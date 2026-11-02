namespace DataManager.Core.Scanner;

/// <summary>Native suffix numbers stored in the low nibble of every effect-name slot tail.</summary>
public static class EngineEffectSuffixLabels
{
    public const byte MaximumSuffix = 0x14;

    private static readonly string[] Names =
    [
        "原生/无后缀", "+N", "+N%", "-N%", "N%", "N", "属性关系",
        "策略名", "属性全名", "五项属性组合", "六项属性组合", "待确认", "地形名",
        "攻击/法术/ALL", "道具名", "攻击穿透(扩展)", "远距贯穿(扩展)", "天造地设(扩展)",
        "-N(扩展)", "N次(扩展)", "攻击范围1~32(扩展)"
    ];

    public static string Name(int value) => value >= 0 && value < Names.Length ? Names[value] : "未知";

    public static string Label(int value) => $"0x{value:X2} {Name(value)}";

    /// <summary>The suffix example N is a display sample only and is never written to the engine.</summary>
    public static string Display(int value, int sample = 50) => value switch
    {
        0 => string.Empty,
        1 => $"+{sample}",
        2 => $"+{sample}%",
        3 => $"-{sample}%",
        4 => $"{sample}%",
        5 => sample.ToString(System.Globalization.CultureInfo.InvariantCulture),
        6 => $"{sample}次",
        _ => Name(value)
    };

    public static bool IsCandidate(int value) => value is >= 7 and <= MaximumSuffix;
}
