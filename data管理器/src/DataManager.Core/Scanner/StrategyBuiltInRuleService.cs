using DataManager.Core.Domain;

namespace DataManager.Core.Scanner;

/// <summary>
/// Reads and writes the two built-in strategy modifiers:
/// receiver fire series bonus and caster charm series bonus.
/// The writes only touch the existing comparison/immediate bytes; the
/// multipliers and the surrounding damage pipeline are left untouched.
/// </summary>
public sealed class StrategyBuiltInRuleService
{
    public const uint FireTriggerVa = 0x0043C18A;
    public const uint FireSeriesVa = 0x0043C191;
    public const uint FireBonusVa = 0x0043C197;
    public const uint CharmDamageVa = 0x0043C1F0;
    public const uint CharmDamageDispVa = 0x0043C1F2;
    public const uint CharmDamageValueVa = 0x0043C1F3;
    public const uint CharmDamageSeriesVa = 0x0043C1FF;
    public const uint CharmHitVa = 0x0043B984;
    public const uint CharmHitDispVa = 0x0043B986;
    public const uint CharmHitValueVa = 0x0043B988;
    public const uint CharmHitSeriesVa = 0x0043B994;

    public StrategyBuiltInRuleInfo Read(byte[] image)
    {
        try
        {
            var fireTrigger = Offset(image, FireTriggerVa);
            var fireSeries = Offset(image, FireSeriesVa);
            var fireBonus = Offset(image, FireBonusVa);
            var charmDamage = Offset(image, CharmDamageVa);
            var charmDamageDisp = Offset(image, CharmDamageDispVa);
            var charmDamageValue = Offset(image, CharmDamageValueVa);
            var charmDamageSeries = Offset(image, CharmDamageSeriesVa);
            var charmHit = Offset(image, CharmHitVa);
            var charmHitDisp = Offset(image, CharmHitDispVa);
            var charmHitValue = Offset(image, CharmHitValueVa);
            var charmHitSeries = Offset(image, CharmHitSeriesVa);

            if (!Matches(image, fireTrigger, 0x84, 0xC0) && image[fireTrigger] != 0x3C)
                return Unsupported("火系触发类型站点不是 84 C0 或 3C xx。");
            if (image[fireTrigger + 2] != 0x75)
                return Unsupported("火系触发分支不是 JNE。");
            if (!Matches(image, fireSeries - 3, 0x80, 0x7D, 0xEC))
                return Unsupported("火系兵种系比较指令不是 80 7D EC xx。");
            if (!Matches(image, fireSeries + 1, 0x75, 0x04))
                return Unsupported("火系兵种系分支不是 JNE 04。");
            if (!Matches(image, fireBonus - 3, 0x83, 0x45, 0xF4))
                return Unsupported("火系加成指令不是 83 45 F4 xx。");

            if (!Matches(image, charmDamage, 0x80, 0x7D))
                return Unsupported("诱惑伤害判定不是 80 7D xx yy。");
            if (!Matches(image, charmDamage + 4, 0x75))
                return Unsupported("诱惑伤害判定分支不是 JNE。");
            if (!Matches(image, charmDamageSeries - 1, 0x3C))
                return Unsupported("诱惑伤害兵种系比较不是 3C xx。");
            if (!Matches(image, charmDamageSeries + 1, 0x75, 0x03))
                return Unsupported("诱惑伤害兵种系分支不是 JNE 03。");
            if (!Matches(image, charmDamageSeries + 3, 0xD1, 0x65, 0xFC))
                return Unsupported("诱惑伤害倍率不是 SHL [ebp-4],1。");

            if (!Matches(image, charmHit, 0x8B, 0x45))
                return Unsupported("诱惑命中判定不是 8B 45 xx。");
            if (!Matches(image, charmHitValue - 1, 0x3C))
                return Unsupported("诱惑命中判定不是 3C xx。");
            if (!Matches(image, charmHitValue + 1, 0x75, 0x17))
                return Unsupported("诱惑命中判定分支不是 JNE 17。");
            if (!Matches(image, charmHitSeries - 1, 0x3C))
                return Unsupported("诱惑命中兵种系比较不是 3C xx。");
            if (!Matches(image, charmHitSeries + 1, 0x75, 0x0B))
                return Unsupported("诱惑命中兵种系分支不是 JNE 0B。");
            if (!Matches(image, charmHitSeries + 6, 0x6B, 0xC9, 0x03, 0xD1, 0xE9))
                return Unsupported("诱惑命中倍率不是 IMUL 3 / SHR 1。");
            if (image[charmDamageSeries] != image[charmHitSeries])
                return Unsupported("诱惑伤害与命中的兵种系不一致，不能安全同步修改。");

            var charmDamageKind = image[charmDamageDisp] switch
            {
                0x10 => StrategyBuiltInMatchKind.StrategyId,
                0xE8 or 0xDC => StrategyBuiltInMatchKind.StrategyType,
                _ => (StrategyBuiltInMatchKind?)null
            };
            if (charmDamageKind == null)
                return Unsupported("诱惑伤害判定的操作数不是策略ID或策略类型。");
            if (image[charmHitDisp] is not (0x10 or 0xF8))
                return Unsupported("诱惑命中判定不是策略ID；当前版本不支持按类型命中。");

            var fireType = image[fireTrigger] == 0x84 ? (byte)0 : image[fireTrigger + 1];
            var series = image[fireSeries];
            var current = new StrategyBuiltInRuleSettings(
                FireEnabled: series != StrategyBuiltInRuleSettings.DisabledSeries,
                FireTriggerType: fireType,
                FireSeries: series == StrategyBuiltInRuleSettings.DisabledSeries ? (byte)0 : series,
                FireBonus: unchecked((sbyte)image[fireBonus]),
                CharmEnabled: image[charmDamageValue] != StrategyBuiltInRuleSettings.DisabledValue
                    && image[charmHitValue] != StrategyBuiltInRuleSettings.DisabledValue,
                CharmDamageValue: image[charmDamageValue],
                CharmHitValue: image[charmHitValue],
                CharmSeries: image[charmDamageSeries]);
            return new StrategyBuiltInRuleInfo(true, current, current, charmDamageKind.Value,
                StrategyBuiltInMatchKind.StrategyId, "引擎内置条目可编辑。");
        }
        catch (Exception ex)
        {
            return Unsupported("内置条目站点不可用：" + ex.Message);
        }
    }

    public StrategyJobModifierValidation Validate(StrategyBuiltInRuleSettings settings)
    {
        var issues = new List<StrategyJobModifierIssue>();
        if (settings.FireEnabled && settings.FireSeries > 39)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error, "火系内置条目的兵种系必须为 0..39。"));
        if (settings.FireBonus is < -100 or > 127)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error, "火系内置条目的加成必须为 -100..127。"));
        if (settings.CharmEnabled && settings.CharmDamageValue > 254)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error, "诱惑伤害判定值启用时必须为 0..254。"));
        if (settings.CharmEnabled && settings.CharmHitValue > 254)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error, "诱惑命中判定值启用时必须为 0..254。"));
        if (settings.CharmSeries > 39)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error, "诱惑内置条目的兵种系必须为 0..39。"));
        return new StrategyJobModifierValidation(issues);
    }

    public byte[] Apply(byte[] image, StrategyBuiltInRuleSettings settings)
    {
        var info = Read(image);
        if (!info.Supported)
            throw new InvalidDataException(info.Diagnostic);
        var error = Validate(settings).Issues
            .FirstOrDefault(issue => issue.Severity == StrategyJobModifierIssueSeverity.Error);
        if (error != null) throw new InvalidDataException(error.Message);

        var after = (byte[])image.Clone();
        var fireTrigger = Offset(after, FireTriggerVa);
        if (settings.FireTriggerType == 0 && after[fireTrigger] == 0x84)
        {
            // Keep the original TEST AL,AL form when type 0 is selected.
        }
        else
        {
            after[fireTrigger] = 0x3C;
            after[fireTrigger + 1] = settings.FireTriggerType;
        }
        after[Offset(after, FireSeriesVa)] = settings.FireEnabled
            ? settings.FireSeries
            : StrategyBuiltInRuleSettings.DisabledSeries;
        after[Offset(after, FireBonusVa)] = unchecked((byte)settings.FireBonus);
        after[Offset(after, CharmDamageValueVa)] = settings.CharmEnabled
            ? settings.CharmDamageValue
            : StrategyBuiltInRuleSettings.DisabledValue;
        after[Offset(after, CharmHitValueVa)] = settings.CharmEnabled
            ? settings.CharmHitValue
            : StrategyBuiltInRuleSettings.DisabledValue;
        var charmSeries = settings.CharmSeries;
        after[Offset(after, CharmDamageSeriesVa)] = charmSeries;
        after[Offset(after, CharmHitSeriesVa)] = charmSeries;
        return after;
    }

    private static StrategyBuiltInRuleInfo Unsupported(string diagnostic) => StrategyBuiltInRuleInfo.Unsupported(diagnostic);

    private static int Offset(byte[] image, uint va) => StrategyHookSectionService.VaToFileOffset(image, va);

    private static bool Matches(byte[] image, int offset, params byte[] expected)
    {
        if (offset < 0 || offset + expected.Length > image.Length) return false;
        return image.AsSpan(offset, expected.Length).SequenceEqual(expected);
    }
}
