using System.Globalization;

namespace DataManager.Core.Scanner;

/// <summary>
/// Per-base semantics of the one-byte suffix type code stored at effect-name
/// slot +0x0F. The same code means different things in 6.4, 6.5 and 6.6, and
/// codes beyond the base selector's range produce no suffix at all.
/// </summary>
public enum EngineEffectSuffixKind
{
    None,
    Numeric,
    BitMask,
    StrategyName,
    AttributeName,
    RangeShape,
    FormationRange,
    Terrain,
    AttackStrategyAll
}

public static class EngineEffectSuffixLabels
{
    private sealed record Entry(EngineEffectSuffixKind Kind, string Name, string Sample);

    private static readonly Entry None = new(EngineEffectSuffixKind.None, "无后缀", string.Empty);
    private static readonly Entry Plus = new(EngineEffectSuffixKind.Numeric, "+N", string.Empty);
    private static readonly Entry PlusPercent = new(EngineEffectSuffixKind.Numeric, "+N%", string.Empty);
    private static readonly Entry MinusPercent = new(EngineEffectSuffixKind.Numeric, "-N%", string.Empty);
    private static readonly Entry Percent = new(EngineEffectSuffixKind.Numeric, "N%", string.Empty);
    private static readonly Entry Number = new(EngineEffectSuffixKind.Numeric, "N", string.Empty);
    private static readonly Entry Times = new(EngineEffectSuffixKind.Numeric, "N次", string.Empty);
    private static readonly Entry BitMask = new(EngineEffectSuffixKind.BitMask, "位掩码组合", "攻击+防御");
    private static readonly Entry StrategyName = new(EngineEffectSuffixKind.StrategyName, "策略名", "火计");
    private static readonly Entry AttributeName = new(EngineEffectSuffixKind.AttributeName, "属性名", "攻击");
    private static readonly Entry FiveAttributes = new(EngineEffectSuffixKind.BitMask, "五项属性组合（位掩码）", "攻击+防御");
    private static readonly Entry SixAttributes = new(EngineEffectSuffixKind.BitMask, "六项属性组合（位掩码）", "攻击+防御");
    private static readonly Entry AttributeCombination = new(EngineEffectSuffixKind.BitMask, "属性组合（位掩码）", "攻击+防御");
    private static readonly Entry BitMask3F = new(EngineEffectSuffixKind.BitMask, "位掩码组合（0x3F）", "攻击+防御");
    private static readonly Entry BitMask1F = new(EngineEffectSuffixKind.BitMask, "位掩码组合（0x1F）", "攻击+防御");
    private static readonly Entry RangeShape = new(EngineEffectSuffixKind.RangeShape, "范围/形状名", "九宫");
    private static readonly Entry FormationRange = new(EngineEffectSuffixKind.FormationRange, "阵型/范围名", "八向");
    private static readonly Entry Terrain = new(EngineEffectSuffixKind.Terrain, "地形名", "山地");
    private static readonly Entry AttackStrategyAll =
        new(EngineEffectSuffixKind.AttackStrategyAll, "攻击/策略/ALL（6.6xKF 补丁）", "攻击");

    /// <summary>Only the three known bases are supported; unknown profiles follow 6.5.</summary>
    public static string NormalizeVersion(string? version)
    {
        if (version?.StartsWith("6.4", StringComparison.Ordinal) == true) return "6.4";
        if (version?.StartsWith("6.6", StringComparison.Ordinal) == true) return "6.6";
        return "6.5";
    }

    /// <summary>Highest type code handled by the base's name-suffix selector.</summary>
    public static byte MaximumSuffix(string? version) => NormalizeVersion(version) switch
    {
        "6.4" => 0x05,
        "6.6" => 0x0F,
        _ => 0x0A
    };

    public static bool Supports(string? version, int value)
        => value >= 0 && value <= MaximumSuffix(version);

    public static bool IsNumeric(string? version, int value)
        => Lookup(NormalizeVersion(version), value)?.Kind == EngineEffectSuffixKind.Numeric;

    /// <summary>0x0F is written by the 6.6xKF patch, not by the shared three-base table.</summary>
    public static bool IsPatchExtension(string? version, int value)
        => NormalizeVersion(version) == "6.6" && value == 0x0F;

    public static string Name(string? version, int value)
    {
        var normalized = NormalizeVersion(version);
        if (value is < 0 or > byte.MaxValue) return "未定义";
        var entry = Lookup(normalized, value);
        if (entry != null) return entry.Name;
        return $"无后缀（{normalized} 选择器不处理 0x{MaximumSuffix(normalized) + 1:X2}+）";
    }

    public static string Label(string? version, int value)
    {
        var normalized = NormalizeVersion(version);
        if (value is < 0 or > byte.MaxValue) return $"0x{value:X2} 未定义";
        return value == 0
            ? "0x00 无后缀"
            : $"0x{value:X2} {Name(normalized, value)}";
    }

    /// <summary>The suffix example N is a display sample only and is never written to the engine.</summary>
    public static string Display(string? version, int value, int sample = 50)
    {
        var normalized = NormalizeVersion(version);
        var entry = Lookup(normalized, value);
        if (entry == null) return string.Empty;
        if (entry.Kind != EngineEffectSuffixKind.Numeric) return entry.Sample;
        return value switch
        {
            1 => $"+{sample.ToString(CultureInfo.InvariantCulture)}",
            2 => $"+{sample.ToString(CultureInfo.InvariantCulture)}%",
            3 => $"-{sample.ToString(CultureInfo.InvariantCulture)}%",
            4 => $"{sample.ToString(CultureInfo.InvariantCulture)}%",
            5 => sample.ToString(CultureInfo.InvariantCulture),
            6 => $"{sample.ToString(CultureInfo.InvariantCulture)}次",
            _ => string.Empty
        };
    }

    private static Entry? Lookup(string normalizedVersion, int value)
    {
        if (value is < 0 or > byte.MaxValue) return null;
        return normalizedVersion switch
        {
            "6.4" => value switch
            {
                0 => None,
                1 => Plus,
                2 => PlusPercent,
                3 => MinusPercent,
                4 => Percent,
                5 => Number,
                _ => null
            },
            "6.6" => value switch
            {
                0 => None,
                1 => Plus,
                2 => PlusPercent,
                3 => MinusPercent,
                4 => Percent,
                5 => Number,
                6 => Times,
                7 => StrategyName,
                8 => AttributeName,
                9 => AttributeCombination,
                0x0A => BitMask3F,
                0x0B => BitMask1F,
                0x0C => RangeShape,
                0x0D => FormationRange,
                0x0E => Terrain,
                0x0F => AttackStrategyAll,
                _ => null
            },
            _ => value switch
            {
                0 => None,
                1 => Plus,
                2 => PlusPercent,
                3 => MinusPercent,
                4 => Percent,
                5 => Number,
                6 => BitMask,
                7 => StrategyName,
                8 => AttributeName,
                9 => FiveAttributes,
                0x0A => SixAttributes,
                _ => null
            }
        };
    }
}
