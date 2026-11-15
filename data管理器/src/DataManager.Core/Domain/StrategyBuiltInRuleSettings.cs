namespace DataManager.Core.Domain;

public enum StrategyBuiltInMatchKind
{
    StrategyId,
    StrategyType
}

/// <summary>
/// Editable values of the two built-in strategy modifiers:
/// receiver fire series bonus and caster charm series bonus.
/// </summary>
public sealed record StrategyBuiltInRuleSettings(
    bool FireEnabled,
    byte FireTriggerType,
    byte FireSeries,
    sbyte FireBonus,
    bool CharmEnabled,
    byte CharmDamageValue,
    byte CharmHitValue,
    byte CharmSeries)
{
    public const byte DisabledSeries = 0x40;
    public const byte DisabledValue = 0xFF;

    public static StrategyBuiltInRuleSettings Default => new(
        false, 0, 0, 0, false, 0x14, 0x14, 0x0C);

    public string Signature()
        => $"{FireEnabled}|{FireTriggerType}|{FireSeries}|{FireBonus}|{CharmEnabled}|{CharmDamageValue}|{CharmHitValue}|{CharmSeries}";
}

/// <summary>
/// Read-only diagnostics returned with the current built-in settings.
/// </summary>
public sealed record StrategyBuiltInRuleInfo(
    bool Supported,
    StrategyBuiltInRuleSettings Current,
    StrategyBuiltInRuleSettings Defaults,
    StrategyBuiltInMatchKind CharmDamageKind,
    StrategyBuiltInMatchKind CharmHitKind,
    string Diagnostic)
{
    public static StrategyBuiltInRuleInfo Unsupported(string diagnostic) => new(
        false, StrategyBuiltInRuleSettings.Default, StrategyBuiltInRuleSettings.Default,
        StrategyBuiltInMatchKind.StrategyId, StrategyBuiltInMatchKind.StrategyId, diagnostic);
}
