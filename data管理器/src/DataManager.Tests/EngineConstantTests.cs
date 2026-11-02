using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineConstantTests
{
    public static void Run(string sourceRoot)
    {
        var sourceHash = HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe"));
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(copy);
            var service = new EngineConstantService();
            var exe = Path.Combine(copy, "Ekd5.exe");
            var version = Path.GetFileName(sourceRoot) == "洗隋" ? "6.4"
                : Path.GetFileName(sourceRoot) == "复刻版6.6山贼" ? "6.6" : "6.5";
            var values = service.Read(exe, version);
            TestSupport.Assert(values.Count == EngineConstantService.Definitions.Count, "常量页条目数量不符。");
            bool OptionalOnly(EngineConstantValue value)
            {
                var layouts = value.Definition.Layouts.Where(layout => layout.AppliesTo(version)).ToArray();
                return layouts.Length > 0 && layouts.All(layout => layout.Optional);
            }
            TestSupport.Assert(values.All(value => value.SignatureMatches || OptionalOnly(value) ||
                    value.Definition.ApplicableBases is { } bases && !bases.Contains(version)),
                "常量签名校验失败：" + string.Join("；", values.Where(value => !value.SignatureMatches &&
                        !OptionalOnly(value) &&
                        (value.Definition.ApplicableBases is not { } declared || declared.Contains(version)))
                    .Select(value => value.Definition.DisplayName + "：" + value.Diagnostic)));
            TestSupport.Assert(values.Where(value => value.SignatureMatches).All(value => value.FileOffset >= 0),
                "常量文件偏移未定位。");
            var guided = values.Single(value => value.Definition.Id == "guided-attack-count");
            TestSupport.Assert(guided.FileOffset == 0x20740 && guided.NumericValue is >= 0 and <= 15,
                "引导攻击追加次数偏移或范围不符。");
            var furious = values.Single(value => value.Definition.Id == "furious-attack-targets");
            TestSupport.Assert(furious.NumericValue == 8 && furious.FileOffset ==
                (Path.GetFileName(sourceRoot) == "复刻版6.6山贼" ? 0x5013 : 0x5016),
                $"奋战攻击目标数布局不符：值={furious.NumericValue} 偏移=0x{furious.FileOffset:X}。");
            var activeCombo = values.Single(value => value.Definition.Id == "active-combo-chance");
            var is64Base = Path.GetFileName(sourceRoot) == "洗隋";
            TestSupport.Assert(activeCombo.NumericValue == 100 && activeCombo.FileOffset == (is64Base ? 0x50F8 : 0xB591C) &&
                activeCombo.SecondaryFileOffset == (is64Base ? -1 : 0xB5946),
                $"主动连击发动率布局不符：值={activeCombo.NumericValue} 偏移=0x{activeCombo.FileOffset:X}/0x{activeCombo.SecondaryFileOffset:X}。");
            var counter = values.Single(value => value.Definition.Id == "counter-damage-share");
            if (is64Base)
                TestSupport.Assert(!counter.SignatureMatches, "6.4 不应匹配普通反击伤害份数。");
            else
                TestSupport.Assert(counter.NumericValue == 3 && counter.FileOffset == 0xB4A16,
                    $"普通反击伤害份数布局不符：值={counter.NumericValue} 偏移=0x{counter.FileOffset:X}。");
            var quote = values.Single(value => value.Definition.Id == "breakthrough-quote");
            TestSupport.Assert(quote.FileOffset == 0xD24FF && quote.TextValue.Length > 0,
                "突破攻击台词槽读取失败。");
            var bonus = values.Single(value => value.Definition.Id == "breakthrough-damage-bonus");
            TestSupport.Assert(bonus.NumericValue == 40 && bonus.FileOffset == 0x104C1,
                $"突破攻击伤害加成布局不符：值={bonus.NumericValue} 偏移=0x{bonus.FileOffset:X}。");
            var drain = values.Single(value => value.Definition.Id == "drain-attack-cap");
            TestSupport.Assert(drain.NumericValue == 100 && drain.FileOffset == 0x5F9A &&
                drain.SecondaryFileOffset == 0x5F9E,
                $"吸血攻击吸取上限布局不符：值={drain.NumericValue} 偏移=0x{drain.FileOffset:X}/0x{drain.SecondaryFileOffset:X}。");
            var ironChain = values.Single(value => value.Definition.Id == "iron-chain-directions");
            TestSupport.Assert(ironChain.NumericValue == 4 && ironChain.FileOffset ==
                (Path.GetFileName(sourceRoot) == "复刻版6.6山贼" ? 0x11F7B : 0x11F7A),
                $"铁索横江邻接方向数布局不符：值={ironChain.NumericValue} 偏移=0x{ironChain.FileOffset:X}。");
            var is64 = Path.GetFileName(sourceRoot) == "洗隋";
            var heavy = values.Single(value => value.Definition.Id == "heavy-strike-cap");
            TestSupport.Assert(heavy.NumericValue == 100 && heavy.FileOffset == (is64 ? 0x21439 : 0x21436) &&
                heavy.SecondaryFileOffset == (is64 ? 0x2143D : 0x2143A),
                $"猛击伤害比例上限布局不符：值={heavy.NumericValue} 偏移=0x{heavy.FileOffset:X}/0x{heavy.SecondaryFileOffset:X}。");
            var shield = values.Single(value => value.Definition.Id == "shield-counter-cap");
            TestSupport.Assert(shield.NumericValue == 100 && shield.FileOffset == (is64 ? 0x214B5 : 0x2145F) &&
                shield.SecondaryFileOffset == (is64 ? 0x214B9 : 0x21463),
                $"盾反反弹比例上限布局不符：值={shield.NumericValue} 偏移=0x{shield.FileOffset:X}/0x{shield.SecondaryFileOffset:X}。");
            var demon = values.Single(value => value.Definition.Id == "demon-stat-cap");
            if (is64)
                TestSupport.Assert(!demon.SignatureMatches, "6.4 不应匹配神魔附体属性上限。");
            else
                TestSupport.Assert(demon.NumericValue == 9999 &&
                    demon.FileOffset == (version == "6.6" ? 0x3B922 : 0x3B917) &&
                    demon.SecondaryFileOffset == (version == "6.6" ? 0x3B929 : 0x3B91E),
                    $"神魔附体属性上限布局不符：值={demon.NumericValue} 偏移=0x{demon.FileOffset:X}/0x{demon.SecondaryFileOffset:X}。");
            var swift = values.Single(value => value.Definition.Id == "swift-attack-bonus");
            TestSupport.Assert(swift.NumericValue == 5 && swift.FileOffset ==
                (version == "6.6" ? 0xE2F5D : 0x3B188),
                $"疾风攻击每点增伤布局不符：值={swift.NumericValue} 偏移=0x{swift.FileOffset:X}。");
            if (version == "6.6")
            {
                TestSupport.Assert(swift.Nature == "特效值槽", "6.6 疾风攻击每点增伤应标记为特效值槽。");
                TestSupport.Assert(swift.Diagnostic.Contains("特效表指针", StringComparison.Ordinal),
                    "6.6 疾风攻击没有按特效表指针相对定位：" + swift.Diagnostic);
            }
            else
                TestSupport.Assert(swift.Nature == "代码固定值", "6.4/6.5 疾风攻击每点增伤应标记为代码固定值。");
            var swiftOffset = values.Single(value => value.Definition.Id == "swift-attack-offset");
            if (version == "6.6")
                TestSupport.Assert(swiftOffset.NumericValue == 12 && swiftOffset.FileOffset == 0x3B182,
                    $"疾风攻击补正值布局不符：值={swiftOffset.NumericValue} 偏移=0x{swiftOffset.FileOffset:X}。");
            else
                TestSupport.Assert(!swiftOffset.SignatureMatches, "只有 6.6 应匹配疾风攻击补正值。");
            var physicalBase = values.Single(value => value.Definition.Id == "physical-damage-base");
            TestSupport.Assert(physicalBase.NumericValue == 25 && physicalBase.FileOffset == 0x3B136,
                $"物理攻击基础伤害布局不符：值={physicalBase.NumericValue} 偏移=0x{physicalBase.FileOffset:X}。");
            var strategyBase = values.Single(value => value.Definition.Id == "strategy-damage-base");
            TestSupport.Assert(strategyBase.NumericValue == 25 && strategyBase.FileOffset ==
                (version == "6.6" ? 0x3B518 : 0x3B501),
                $"策略攻击基础伤害布局不符：值={strategyBase.NumericValue} 偏移=0x{strategyBase.FileOffset:X}。");
            var strategyDivisor = values.Single(value => value.Definition.Id == "strategy-damage-divisor");
            TestSupport.Assert(strategyDivisor.NumericValue == 3 && strategyDivisor.FileOffset == 0x3B526 &&
                strategyDivisor.SecondaryFileOffset == 0x3B539,
                $"策略伤害能力差除数布局不符：值={strategyDivisor.NumericValue} 偏移=0x{strategyDivisor.FileOffset:X}/0x{strategyDivisor.SecondaryFileOffset:X}。");
            var absoluteStrategy = values.Single(value => value.Definition.Id == "absolute-strategy-damage-bonus");
            var limitCut = values.Single(value => value.Definition.Id == "strategy-damage-limit-cut");
            var absoluteDamageHp = values.Single(value => value.Definition.Id == "absolute-damage-hp-threshold");
            var absoluteDamageBonus = values.Single(value => value.Definition.Id == "absolute-damage-bonus");
            var loneBrave = values.Single(value => value.Definition.Id == "lone-brave-reduction-cap");
            var veteranDivisor = values.Single(value => value.Definition.Id == "battle-veteran-damage-divisor");
            var windRushDivisor = values.Single(value => value.Definition.Id == "wind-rush-damage-divisor");
            var convertHealMp = values.Single(value => value.Definition.Id == "convert-heal-mp-cost");
            var sharpenAttack = values.Single(value => value.Definition.Id == "sharpen-attack-cap");
            var shopSell = values.Single(value => value.Definition.Id == "shop-sell-price-numerator");
            TestSupport.Assert(shopSell.NumericValue == 3 && shopSell.FileOffset == 0xA88D,
                $"商店卖出价格分子布局不符：值={shopSell.NumericValue} 偏移=0x{shopSell.FileOffset:X}。");
            var continuousCap = values.Single(value => value.Definition.Id == "continuous-attack-reduction-cap");
            if (version == "6.6")
                TestSupport.Assert(continuousCap.NumericValue == 100 && continuousCap.FileOffset == 0x412CA,
                    $"连续攻击减伤封顶布局不符：值={continuousCap.NumericValue} 偏移=0x{continuousCap.FileOffset:X}。");
            else
                TestSupport.Assert(!continuousCap.SignatureMatches, "只有 6.6 应匹配连续攻击减伤封顶。");
            var advanceRetreat = values.Single(value => value.Definition.Id == "advance-retreat-double-line");
            var strategyHitCap = values.Single(value => value.Definition.Id == "strategy-hit-cap");
            TestSupport.Assert(strategyHitCap.NumericValue == 100 && strategyHitCap.FileOffset == 0x3ADE7 &&
                strategyHitCap.SecondaryFileOffset == 0x3ADEB,
                $"策略命中/格挡率上限布局不符：值={strategyHitCap.NumericValue} 偏移=0x{strategyHitCap.FileOffset:X}/0x{strategyHitCap.SecondaryFileOffset:X}。");
            var offenseCap = values.Single(value => value.Definition.Id == "offense-defense-effect-cap");
            TestSupport.Assert(!offenseCap.Layout.Prefix.Contains((byte)0xE8) &&
                !offenseCap.Layout.Suffix.Contains((byte)0xE8),
                "攻守兼备减伤上限的签名仍依赖 call 位移，跨版本不稳。");
            if (version == "6.6")
                TestSupport.Assert(offenseCap.NumericValue == 100 && offenseCap.FileOffset == 0x4125A &&
                    offenseCap.SecondaryFileOffset == 0x4125E,
                    $"攻守兼备减伤效果值上限布局不符：值={offenseCap.NumericValue} 偏移=0x{offenseCap.FileOffset:X}/0x{offenseCap.SecondaryFileOffset:X}。");
            else
                TestSupport.Assert(!offenseCap.SignatureMatches, "只有 6.6 应匹配攻守兼备减伤效果值上限。");
            var chaosBonus = values.Single(value => value.Definition.Id == "chaos-yin-yang-hit-bonus");
            TestSupport.Assert(chaosBonus.NumericValue == 10 && chaosBonus.FileOffset == 0x3ADD6,
                $"逆乱阴阳命中加成布局不符：值={chaosBonus.NumericValue} 偏移=0x{chaosBonus.FileOffset:X}。");
            if (version == "6.6")
            {
                TestSupport.Assert(absoluteStrategy.NumericValue == 15 && absoluteStrategy.FileOffset == 0x33587,
                    $"绝对策略伤害追加比例布局不符：值={absoluteStrategy.NumericValue} 偏移=0x{absoluteStrategy.FileOffset:X}。");
                TestSupport.Assert(limitCut.NumericValue == 20 && limitCut.FileOffset == 0x33643,
                    $"限制策略伤害额外减伤布局不符：值={limitCut.NumericValue} 偏移=0x{limitCut.FileOffset:X}。");
                TestSupport.Assert(absoluteDamageHp.NumericValue == 50 && absoluteDamageHp.FileOffset == 0x335E2 &&
                    absoluteDamageBonus.NumericValue == 30 && absoluteDamageBonus.FileOffset == 0x335EA,
                    $"绝对伤害触发线/追加比例布局不符：{absoluteDamageHp.NumericValue}/0x{absoluteDamageHp.FileOffset:X}，" +
                    $"{absoluteDamageBonus.NumericValue}/0x{absoluteDamageBonus.FileOffset:X}。");
                TestSupport.Assert(loneBrave.NumericValue == 100 && loneBrave.FileOffset == 0x19D82 &&
                    loneBrave.SecondaryFileOffset == 0x19D86,
                    $"孤勇者减伤上限布局不符：值={loneBrave.NumericValue} 偏移=0x{loneBrave.FileOffset:X}/0x{loneBrave.SecondaryFileOffset:X}。");
                TestSupport.Assert(veteranDivisor.NumericValue == 67 && veteranDivisor.FileOffset == 0x19C5B,
                    $"越战越勇伤害除数布局不符：值={veteranDivisor.NumericValue} 偏移=0x{veteranDivisor.FileOffset:X}。");
                TestSupport.Assert(advanceRetreat.NumericValue == 50 && advanceRetreat.FileOffset == 0x3C7F2 &&
                    advanceRetreat.SecondaryFileOffset == 0x3C83B,
                    $"进退有据翻倍触发线布局不符：值={advanceRetreat.NumericValue} 偏移=0x{advanceRetreat.FileOffset:X}/0x{advanceRetreat.SecondaryFileOffset:X}。");
            }
            else
            {
                TestSupport.Assert(!absoluteStrategy.SignatureMatches && !limitCut.SignatureMatches &&
                    !absoluteDamageHp.SignatureMatches && !absoluteDamageBonus.SignatureMatches &&
                    !loneBrave.SignatureMatches && !veteranDivisor.SignatureMatches &&
                    !advanceRetreat.SignatureMatches,
                    "6.4/6.5 不应匹配 6.6 的绝对策略伤害/限制策略伤害/绝对伤害/孤勇者/越战越勇/进退有据常量。");
            }
            TestSupport.Assert(swift.Definition.Nature == "代码固定值" &&
                swift.Definition.Description.Contains("不是特效值", StringComparison.Ordinal),
                "疾风攻击每点增伤没有标明代码固定值/不是特效值。");
            TestSupport.Assert(swiftOffset.Definition.Nature == "代码固定值" &&
                swiftOffset.Definition.Description.Contains("不是特效值", StringComparison.Ordinal),
                "疾风攻击补正值没有标明代码固定值/不是特效值。");
            TestSupport.Assert(quote.Definition.Nature == "文本槽", "突破攻击台词的性质不是文本槽。");
            var screen = values.Single(value => value.Definition.Id == "effect-screen-directions");
            var inner = values.Single(value => value.Definition.Id == "screen-inner-directions");
            var pass = values.Single(value => value.Definition.Id == "pass-through-directions");
            var aura = values.Single(value => value.Definition.Id == "aura-range");
            TestSupport.Assert(inner.NumericValue == 4 && pass.NumericValue == 4,
                $"内层/穿越方向数不符：inner={inner.NumericValue} pass={pass.NumericValue}。");
            TestSupport.Assert(aura.NumericValue == (version == "6.6" ? 4 : 9) &&
                aura.FileOffset == 0x21551,
                $"周围能力范围布局不符：值={aura.NumericValue} 偏移=0x{aura.FileOffset:X}。");
            TestSupport.Assert(aura.Layout.Prefix.Length >= 7 &&
                aura.Layout.Prefix[0] == 0xFF && aura.Layout.Prefix[1] == 0x45 && aura.Layout.Prefix[2] == 0xF8 &&
                aura.Layout.SuffixMask is { Length: > 0 },
                "周围能力范围签名缺少循环头锚点，异引擎上可能误匹配到其他循环上界。");
            var comboBonus = values.Single(value => value.Definition.Id == "combo-damage-bonus");
            var adversity = values.Single(value => value.Definition.Id == "adversity-reduction-cap");
            var sneak = values.Single(value => value.Definition.Id == "sneak-attack-step");
            var fixedBasis = values.Single(value => value.Definition.Id == "fixed-damage-basis");
            var strategyBonus = values.Single(value => value.Definition.Id == "strategy-floor-bonus");
            var mpStrategy = values.Single(value => value.Definition.Id == "mp-strategy-bonus");
            var moneyDefense = values.Single(value => value.Definition.Id == "money-defense-cost");
            var moneyAbsorb = values.Single(value => value.Definition.Id == "money-absorb-multiplier");
            var dodgeCap = values.Single(value => value.Definition.Id == "dodge-damage-cap");
            var offenseAdd = values.Single(value => value.Definition.Id == "offense-defense-add-line");
            var offenseReduce = values.Single(value => value.Definition.Id == "offense-defense-reduce-line");
            var exploitWeakness = values.Single(value => value.Definition.Id == "exploit-weakness-threshold");
            var fengfeng = values.Single(value => value.Definition.Id == "fengfeng-floor-bonus");
            TestSupport.Assert(moneyAbsorb.NumericValue == (version == "6.4" ? 2 : 1) &&
                moneyAbsorb.FileOffset == 0x5FFF,
                $"吸收金钱获得倍率布局不符：值={moneyAbsorb.NumericValue} 偏移=0x{moneyAbsorb.FileOffset:X}。");
            if (version == "6.4")
            {
                TestSupport.Assert(!comboBonus.SignatureMatches && !adversity.SignatureMatches &&
                    !sneak.SignatureMatches && !fixedBasis.SignatureMatches &&
                    !strategyBonus.SignatureMatches && !mpStrategy.SignatureMatches &&
                    !moneyDefense.SignatureMatches && !dodgeCap.SignatureMatches &&
                    !offenseAdd.SignatureMatches && !offenseReduce.SignatureMatches &&
                    !exploitWeakness.SignatureMatches && !fengfeng.SignatureMatches,
                    "6.4 不应匹配 6.5/6.6 新增常量。");
            }
            else
            {
                TestSupport.Assert(comboBonus.NumericValue == 0 && comboBonus.FileOffset == 0xB499E,
                    $"连击固定增伤布局不符：值={comboBonus.NumericValue} 偏移=0x{comboBonus.FileOffset:X}。");
                TestSupport.Assert(adversity.NumericValue == 80 && adversity.FileOffset == 0x48493,
                    $"逆境减伤最大比例布局不符：值={adversity.NumericValue} 偏移=0x{adversity.FileOffset:X}。");
                TestSupport.Assert(sneak.NumericValue == 25 && sneak.FileOffset == 0xA3F67,
                    $"偷袭攻击每档增伤布局不符：值={sneak.NumericValue} 偏移=0x{sneak.FileOffset:X}。");
                TestSupport.Assert(fixedBasis.NumericValue == (version == "6.6" ? 15 : 10) &&
                    fixedBasis.FileOffset == 0x4838B && fixedBasis.SecondaryFileOffset == 0x483B7,
                    $"固定值基础加成布局不符：值={fixedBasis.NumericValue} 偏移=0x{fixedBasis.FileOffset:X}/0x{fixedBasis.SecondaryFileOffset:X}。");
                TestSupport.Assert(exploitWeakness.NumericValue == 3 &&
                    exploitWeakness.FileOffset == (version == "6.6" ? 0x3DE31 : 0x3DE40),
                    $"趁虚攻击虚弱阈值布局不符：值={exploitWeakness.NumericValue} 偏移=0x{exploitWeakness.FileOffset:X}。");
                TestSupport.Assert(fengfeng.NumericValue == (version == "6.6" ? 6 : 10) &&
                    fengfeng.FileOffset == 0x2366A,
                    $"神锋沥辉保底加成布局不符：值={fengfeng.NumericValue} 偏移=0x{fengfeng.FileOffset:X}。");
                TestSupport.Assert(fengfeng.Layout.PrefixMask!.Count(mask => mask == 0) >= 4 &&
                    fengfeng.Layout.SuffixMask!.Count(mask => mask == 0) >= 4,
                    "神锋沥辉保底加成的签名没有掩掉 call 位移。");
            }
            if (version == "6.5")
                TestSupport.Assert(strategyBonus.NumericValue == 20 && strategyBonus.FileOffset == 0x3C7EA &&
                    strategyBonus.SecondaryFileOffset == 0x3C83A,
                    $"策伤保底追加比例布局不符：值={strategyBonus.NumericValue} 偏移=0x{strategyBonus.FileOffset:X}/0x{strategyBonus.SecondaryFileOffset:X}。");
            else
                TestSupport.Assert(!strategyBonus.SignatureMatches, "只有 6.5 应匹配策伤保底追加比例。");
            if (version == "6.6")
                TestSupport.Assert(windRushDivisor.NumericValue == 10 && windRushDivisor.FileOffset == 0x19BF0,
                    $"风驰电掣伤害除数布局不符：值={windRushDivisor.NumericValue} 偏移=0x{windRushDivisor.FileOffset:X}。");
            else if (version == "6.5")
                TestSupport.Assert(windRushDivisor.NumericValue == 11 && windRushDivisor.FileOffset == 0x1991C,
                    $"风驰电掣伤害除数布局不符：值={windRushDivisor.NumericValue} 偏移=0x{windRushDivisor.FileOffset:X}。");
            else
                TestSupport.Assert(!windRushDivisor.SignatureMatches, "6.4 不应匹配风驰电掣伤害除数。");
            if (version == "6.5")
                TestSupport.Assert(convertHealMp.NumericValue == 2 && convertHealMp.FileOffset == 0xC4A8,
                    $"化力为疗MP消耗布局不符：值={convertHealMp.NumericValue} 偏移=0x{convertHealMp.FileOffset:X}。");
            else
                TestSupport.Assert(!convertHealMp.SignatureMatches, "只有 6.5 应匹配化力为疗MP消耗。");
            if (version == "6.5")
                TestSupport.Assert(sharpenAttack.NumericValue == 3 && sharpenAttack.FileOffset == 0xC4E1 &&
                    sharpenAttack.SecondaryFileOffset == 0xC4F7 &&
                    sharpenAttack.ExtraFileOffsets is { Count: 10 },
                    $"夺锐攻击能力上限布局不符：值={sharpenAttack.NumericValue} 偏移=0x{sharpenAttack.FileOffset:X}/" +
                    $"0x{sharpenAttack.SecondaryFileOffset:X} 附加={sharpenAttack.ExtraFileOffsets?.Count ?? 0}。");
            else
                TestSupport.Assert(!sharpenAttack.SignatureMatches, "只有 6.5 应匹配夺锐攻击能力上限。");
            if (version == "6.6")
            {
                TestSupport.Assert(mpStrategy.NumericValue == 10 && mpStrategy.FileOffset == 0x34543,
                    $"MP辅助策略基础加成布局不符：值={mpStrategy.NumericValue} 偏移=0x{mpStrategy.FileOffset:X}。");
                TestSupport.Assert(moneyDefense.NumericValue == 10 && moneyDefense.FileOffset == 0x206C3,
                    $"金钱防御消耗布局不符：值={moneyDefense.NumericValue} 偏移=0x{moneyDefense.FileOffset:X}。");
                TestSupport.Assert(dodgeCap.NumericValue == 10 && dodgeCap.FileOffset == 0x48551,
                    $"化险为夷上限布局不符：值={dodgeCap.NumericValue} 偏移=0x{dodgeCap.FileOffset:X}。");
                TestSupport.Assert(offenseAdd.NumericValue == 40 && offenseAdd.FileOffset == 0x4123A &&
                    offenseReduce.NumericValue == 60 && offenseReduce.FileOffset == 0x41284,
                    $"攻守兼备触发线布局不符：{offenseAdd.NumericValue}/0x{offenseAdd.FileOffset:X}，{offenseReduce.NumericValue}/0x{offenseReduce.FileOffset:X}。");
            }
            else
            {
                TestSupport.Assert(!mpStrategy.SignatureMatches, "只有 6.6 应匹配MP辅助策略基础加成。");
                TestSupport.Assert(!moneyDefense.SignatureMatches && !dodgeCap.SignatureMatches &&
                    !offenseAdd.SignatureMatches && !offenseReduce.SignatureMatches,
                    "只有 6.6 应匹配金钱防御/化险为夷/攻守兼备常量。");
            }
            foreach (var check in service.ProbeRelocations(exe, version)
                         .Where(item => item.Applicable && item.Relocate))
                TestSupport.Assert(check.Optional ? check.SignatureHits <= 1 : check.SignatureHits == 1,
                    $"{check.DefinitionId}（{check.Label}）签名不唯一：{check.SignatureHits} 处。");
            Console.WriteLine($"CONSTANT_BASELINE {Path.GetFileName(sourceRoot)} guided={guided.NumericValue} " +
                $"furious={furious.NumericValue} bonus={bonus.NumericValue} drain={drain.NumericValue} " +
                $"iron={ironChain.NumericValue} heavy={heavy.NumericValue} shield={shield.NumericValue} " +
                $"demon={demon.NumericValue?.ToString() ?? "—"} swift={swift.NumericValue?.ToString() ?? "—"} " +
                $"swiftOffset={swiftOffset.NumericValue?.ToString() ?? "—"} " +
                $"screen={screen.NumericValue} inner={inner.NumericValue} " +
                $"pass={pass.NumericValue} aura={aura.NumericValue} combo={activeCombo.NumericValue} " +
                $"counter={counter.NumericValue?.ToString() ?? "—"} quote={quote.TextValue} " +
                $"comboBonus={comboBonus.NumericValue?.ToString() ?? "—"} adversity={adversity.NumericValue?.ToString() ?? "—"} " +
                $"sneak={sneak.NumericValue?.ToString() ?? "—"} fixedBasis={fixedBasis.NumericValue?.ToString() ?? "—"} " +
                $"strategyBonus={strategyBonus.NumericValue?.ToString() ?? "—"} mpStrategy={mpStrategy.NumericValue?.ToString() ?? "—"} " +
                $"money={moneyDefense.NumericValue?.ToString() ?? "—"} dodge={dodgeCap.NumericValue?.ToString() ?? "—"} " +
                $"exploit={exploitWeakness.NumericValue?.ToString() ?? "—"} " +
                $"offAdd={offenseAdd.NumericValue?.ToString() ?? "—"} offReduce={offenseReduce.NumericValue?.ToString() ?? "—"}");

            var target = guided.NumericValue == 0 ? 1 : 0;
            var first = service.Commit(workspace, [new EngineConstantEdit(guided.Definition.Id, target, null)]);
            TestSupport.Assert(first.Success, "引导攻击次数写入失败：" + first.Message);
            var afterNumber = service.Read(exe, version).Single(value => value.Definition.Id == guided.Definition.Id);
            TestSupport.Assert(afterNumber.NumericValue == target, "引导攻击次数复读不符。");
            var bytes = File.ReadAllBytes(exe);
            TestSupport.Assert(bytes[0x20740] == target, "引导攻击次数文件字节不符。");

            var fourth = service.Commit(workspace, [new EngineConstantEdit(furious.Definition.Id, 4, null)]);
            TestSupport.Assert(fourth.Success, "奋战攻击目标数写入失败：" + fourth.Message);
            var afterFurious = service.Read(exe, version).Single(value => value.Definition.Id == furious.Definition.Id);
            TestSupport.Assert(afterFurious.NumericValue == 4 && File.ReadAllBytes(exe)[checked((int)furious.FileOffset)] == 4,
                "奋战攻击目标数复读不符。");

            var eighth = service.Commit(workspace, [new EngineConstantEdit(pass.Definition.Id, 8, null)]);
            TestSupport.Assert(eighth.Success, "穿越移动方向数写入失败：" + eighth.Message);
            var afterPass = service.Read(exe, version).Single(value => value.Definition.Id == pass.Definition.Id);
            TestSupport.Assert(afterPass.NumericValue == 8 && File.ReadAllBytes(exe)[checked((int)pass.FileOffset)] == 8,
                "穿越移动方向数复读不符。");

            var sixth = service.Commit(workspace, [new EngineConstantEdit(bonus.Definition.Id, 60, null)]);
            TestSupport.Assert(sixth.Success, "突破攻击伤害加成写入失败：" + sixth.Message);
            var afterBonus = service.Read(exe, version).Single(value => value.Definition.Id == bonus.Definition.Id);
            TestSupport.Assert(afterBonus.NumericValue == 60 &&
                File.ReadAllBytes(exe)[checked((int)bonus.FileOffset)] == 60,
                "突破攻击伤害加成复读不符。");

            var drainCommit = service.Commit(workspace, [new EngineConstantEdit(drain.Definition.Id, 80, null)]);
            TestSupport.Assert(drainCommit.Success, "吸血攻击吸取上限写入失败：" + drainCommit.Message);
            var afterDrain = service.Read(exe, version).Single(value => value.Definition.Id == drain.Definition.Id);
            var drainBytes = File.ReadAllBytes(exe);
            TestSupport.Assert(afterDrain.NumericValue == 80 &&
                drainBytes[checked((int)drain.FileOffset)] == 80 &&
                drainBytes[checked((int)drain.SecondaryFileOffset)] == 80,
                "吸血攻击吸取上限复读或双字节写入不符。");

            var ironCommit = service.Commit(workspace, [new EngineConstantEdit(ironChain.Definition.Id, 8, null)]);
            TestSupport.Assert(ironCommit.Success, "铁索横江方向数写入失败：" + ironCommit.Message);
            var afterIron = service.Read(exe, version).Single(value => value.Definition.Id == ironChain.Definition.Id);
            TestSupport.Assert(afterIron.NumericValue == 8 &&
                File.ReadAllBytes(exe)[checked((int)ironChain.FileOffset)] == 8,
                "铁索横江方向数复读不符。");

            var comboCommit = service.Commit(workspace, [new EngineConstantEdit(activeCombo.Definition.Id, 50, null)]);
            TestSupport.Assert(comboCommit.Success, "主动连击发动率写入失败：" + comboCommit.Message);
            var afterCombo = service.Read(exe, version).Single(value => value.Definition.Id == activeCombo.Definition.Id);
            var comboBytes = File.ReadAllBytes(exe);
            TestSupport.Assert(afterCombo.NumericValue == 50 &&
                comboBytes[checked((int)activeCombo.FileOffset)] == 50 &&
                (activeCombo.SecondaryFileOffset < 0 || comboBytes[checked((int)activeCombo.SecondaryFileOffset)] == 50),
                "主动连击发动率复读或双字节写入不符。");

            if (counter.SignatureMatches)
            {
                var counterCommit = service.Commit(workspace, [new EngineConstantEdit(counter.Definition.Id, 4, null)]);
                TestSupport.Assert(counterCommit.Success, "普通反击伤害份数写入失败：" + counterCommit.Message);
                var afterCounter = service.Read(exe, version).Single(value => value.Definition.Id == counter.Definition.Id);
                TestSupport.Assert(afterCounter.NumericValue == 4 &&
                    File.ReadAllBytes(exe)[checked((int)counter.FileOffset)] == 4,
                    "普通反击伤害份数复读不符。");
            }

            foreach (var pair in new[] { heavy, shield })
            {
                var pairCommit = service.Commit(workspace, [new EngineConstantEdit(pair.Definition.Id, 60, null)]);
                TestSupport.Assert(pairCommit.Success, pair.Definition.DisplayName + "写入失败：" + pairCommit.Message);
                var afterPair = service.Read(exe, version).Single(value => value.Definition.Id == pair.Definition.Id);
                var pairBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterPair.NumericValue == 60 &&
                    pairBytes[checked((int)pair.FileOffset)] == 60 &&
                    pairBytes[checked((int)pair.SecondaryFileOffset)] == 60,
                    pair.Definition.DisplayName + "复读或双字节写入不符。");
            }

            foreach (var (item, targetValue) in new[]
                     {
                         (comboBonus, 10), (adversity, 60), (sneak, 30),
                         (fixedBasis, 20), (strategyBonus, 25), (mpStrategy, 20),
                         (moneyDefense, 20), (dodgeCap, 20), (offenseAdd, 50), (offenseReduce, 70),
                         (exploitWeakness, 4), (fengfeng, 20), (moneyAbsorb, 3)
                     })
            {
                if (!item.SignatureMatches) continue;
                var commit = service.Commit(workspace, [new EngineConstantEdit(item.Definition.Id, targetValue, null)]);
                TestSupport.Assert(commit.Success, item.Definition.DisplayName + "写入失败：" + commit.Message);
                var afterItem = service.Read(exe, version).Single(value => value.Definition.Id == item.Definition.Id);
                var afterItemBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterItem.NumericValue == targetValue &&
                    afterItemBytes[checked((int)afterItem.FileOffset)] == targetValue &&
                    (afterItem.SecondaryFileOffset < 0 ||
                     afterItemBytes[checked((int)afterItem.SecondaryFileOffset)] == targetValue),
                    item.Definition.DisplayName + "复读或成对写入不符。");
            }

            if (demon.SignatureMatches)
            {
                var demonCommit = service.Commit(workspace, [new EngineConstantEdit(demon.Definition.Id, 12000, null)]);
                TestSupport.Assert(demonCommit.Success, "神魔附体属性上限写入失败：" + demonCommit.Message);
                var afterDemon = service.Read(exe, version).Single(value => value.Definition.Id == demon.Definition.Id);
                var demonBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterDemon.NumericValue == 12000 &&
                    BitConverter.ToInt32(demonBytes, checked((int)demon.FileOffset)) == 12000 &&
                    BitConverter.ToInt32(demonBytes, checked((int)demon.SecondaryFileOffset)) == 12000,
                    "神魔附体属性上限复读或四字节写入不符。");
            }
            if (swift.SignatureMatches)
            {
                var swiftCommit = service.Commit(workspace, [new EngineConstantEdit(swift.Definition.Id, 9, null)]);
                TestSupport.Assert(swiftCommit.Success, "疾风攻击每点增伤写入失败：" + swiftCommit.Message);
                var afterSwift = service.Read(exe, version).Single(value => value.Definition.Id == swift.Definition.Id);
                TestSupport.Assert(afterSwift.NumericValue == 9 &&
                    File.ReadAllBytes(exe)[checked((int)swift.FileOffset)] == 9,
                    "疾风攻击每点增伤复读不符。");
            }
            if (swiftOffset.SignatureMatches)
            {
                var offsetCommit = service.Commit(workspace,
                    [new EngineConstantEdit(swiftOffset.Definition.Id, 20, null)]);
                TestSupport.Assert(offsetCommit.Success, "疾风攻击补正值写入失败：" + offsetCommit.Message);
                var afterOffset = service.Read(exe, version).Single(value => value.Definition.Id == swiftOffset.Definition.Id);
                TestSupport.Assert(afterOffset.NumericValue == 20 &&
                    File.ReadAllBytes(exe)[checked((int)swiftOffset.FileOffset)] == 20,
                    "疾风攻击补正值复读不符。");
            }
            if (windRushDivisor.SignatureMatches)
            {
                var windCommit = service.Commit(workspace,
                    [new EngineConstantEdit(windRushDivisor.Definition.Id, 20, null)]);
                TestSupport.Assert(windCommit.Success, "风驰电掣伤害除数写入失败：" + windCommit.Message);
                var afterWind = service.Read(exe, version)
                    .Single(value => value.Definition.Id == windRushDivisor.Definition.Id);
                TestSupport.Assert(afterWind.NumericValue == 20 &&
                    BitConverter.ToInt32(File.ReadAllBytes(exe), checked((int)windRushDivisor.FileOffset)) == 20,
                    "风驰电掣伤害除数复读不符。");
            }
            if (convertHealMp.SignatureMatches)
            {
                var mpCommit = service.Commit(workspace,
                    [new EngineConstantEdit(convertHealMp.Definition.Id, 4, null)]);
                TestSupport.Assert(mpCommit.Success, "化力为疗MP消耗写入失败：" + mpCommit.Message);
                var afterMp = service.Read(exe, version)
                    .Single(value => value.Definition.Id == convertHealMp.Definition.Id);
                TestSupport.Assert(afterMp.NumericValue == 4 &&
                    File.ReadAllBytes(exe)[checked((int)convertHealMp.FileOffset)] == 4,
                    "化力为疗MP消耗复读不符。");
            }
            if (sharpenAttack.SignatureMatches)
            {
                var sharpenCommit = service.Commit(workspace,
                    [new EngineConstantEdit(sharpenAttack.Definition.Id, 5, null)]);
                TestSupport.Assert(sharpenCommit.Success, "夺锐攻击能力上限写入失败：" + sharpenCommit.Message);
                var afterSharpen = service.Read(exe, version)
                    .Single(value => value.Definition.Id == sharpenAttack.Definition.Id);
                var sharpenBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterSharpen.NumericValue == 5 &&
                    sharpenBytes[checked((int)sharpenAttack.FileOffset)] == 5 &&
                    sharpenBytes[checked((int)sharpenAttack.SecondaryFileOffset)] == 5 &&
                    sharpenAttack.ExtraFileOffsets!.All(offset => sharpenBytes[checked((int)offset)] == 5),
                    "夺锐攻击能力上限 12 处同步复读不符。");
                var extraOffset = sharpenAttack.ExtraFileOffsets![0];
                sharpenBytes[checked((int)extraOffset)] = 6;
                File.WriteAllBytes(exe, sharpenBytes);
                var inconsistent = service.Read(exe, version)
                    .Single(value => value.Definition.Id == sharpenAttack.Definition.Id);
                TestSupport.Assert(!inconsistent.SignatureMatches &&
                    inconsistent.Diagnostic.Contains("附加同步字段的值不一致", StringComparison.Ordinal),
                    "夺锐攻击附加字段不一致时未保持只读：" + inconsistent.Diagnostic);
                sharpenBytes[checked((int)extraOffset)] = 5;
                File.WriteAllBytes(exe, sharpenBytes);
            }
            var damageCommit = service.Commit(workspace,
            [
                new EngineConstantEdit(physicalBase.Definition.Id, 30, null),
                new EngineConstantEdit(strategyBase.Definition.Id, 30, null),
                new EngineConstantEdit(strategyDivisor.Definition.Id, 4, null)
            ]);
            TestSupport.Assert(damageCommit.Success, "伤害公式常量写入失败：" + damageCommit.Message);
            var afterDamage = service.Read(exe, version).ToDictionary(value => value.Definition.Id, StringComparer.Ordinal);
            var damageBytes = File.ReadAllBytes(exe);
            TestSupport.Assert(afterDamage[physicalBase.Definition.Id].NumericValue == 30 &&
                afterDamage[strategyBase.Definition.Id].NumericValue == 30 &&
                afterDamage[strategyDivisor.Definition.Id].NumericValue == 4,
                "伤害公式常量复读不符。");
            TestSupport.Assert(damageBytes[checked((int)physicalBase.FileOffset)] == 30 &&
                damageBytes[checked((int)strategyBase.FileOffset)] == 30 &&
                BitConverter.ToInt32(damageBytes, checked((int)strategyDivisor.FileOffset)) == 4 &&
                BitConverter.ToInt32(damageBytes, checked((int)strategyDivisor.SecondaryFileOffset)) == 4,
                "伤害公式常量物理字节不符。");
            if (version == "6.6")
            {
                var effectDamageCommit = service.Commit(workspace,
                [
                    new EngineConstantEdit(absoluteStrategy.Definition.Id, 25, null),
                    new EngineConstantEdit(limitCut.Definition.Id, 30, null),
                    new EngineConstantEdit(absoluteDamageHp.Definition.Id, 70, null),
                    new EngineConstantEdit(absoluteDamageBonus.Definition.Id, 20, null)
                ]);
                TestSupport.Assert(effectDamageCommit.Success, "6.6 特效伤害常量写入失败：" + effectDamageCommit.Message);
                var afterEffectDamage = service.Read(exe, version)
                    .ToDictionary(value => value.Definition.Id, StringComparer.Ordinal);
                TestSupport.Assert(afterEffectDamage[absoluteStrategy.Definition.Id].NumericValue == 25 &&
                    afterEffectDamage[limitCut.Definition.Id].NumericValue == 30 &&
                    afterEffectDamage[absoluteDamageHp.Definition.Id].NumericValue == 70 &&
                    afterEffectDamage[absoluteDamageBonus.Definition.Id].NumericValue == 20,
                    "6.6 特效伤害常量复读不符。");
                var loneBraveCommit = service.Commit(workspace,
                    [new EngineConstantEdit(loneBrave.Definition.Id, 80, null)]);
                TestSupport.Assert(loneBraveCommit.Success, "孤勇者减伤上限写入失败：" + loneBraveCommit.Message);
                var afterLoneBrave = service.Read(exe, version)
                    .Single(value => value.Definition.Id == loneBrave.Definition.Id);
                var loneBraveBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterLoneBrave.NumericValue == 80 &&
                    loneBraveBytes[checked((int)loneBrave.FileOffset)] == 80 &&
                    BitConverter.ToInt32(loneBraveBytes, checked((int)loneBrave.SecondaryFileOffset)) == 80,
                    "孤勇者减伤上限混合宽度复读不符。");
                var veteranCommit = service.Commit(workspace,
                    [new EngineConstantEdit(veteranDivisor.Definition.Id, 80, null)]);
                TestSupport.Assert(veteranCommit.Success, "越战越勇伤害除数写入失败：" + veteranCommit.Message);
                var afterVeteran = service.Read(exe, version)
                    .Single(value => value.Definition.Id == veteranDivisor.Definition.Id);
                TestSupport.Assert(afterVeteran.NumericValue == 80 &&
                    BitConverter.ToInt32(File.ReadAllBytes(exe), checked((int)veteranDivisor.FileOffset)) == 80,
                    "越战越勇伤害除数复读不符。");
                var advanceCommit = service.Commit(workspace,
                    [new EngineConstantEdit(advanceRetreat.Definition.Id, 70, null)]);
                TestSupport.Assert(advanceCommit.Success, "进退有据翻倍触发线写入失败：" + advanceCommit.Message);
                var afterAdvance = service.Read(exe, version)
                    .Single(value => value.Definition.Id == advanceRetreat.Definition.Id);
                var advanceBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterAdvance.NumericValue == 70 &&
                    advanceBytes[checked((int)advanceRetreat.FileOffset)] == 70 &&
                    advanceBytes[checked((int)advanceRetreat.SecondaryFileOffset)] == 70,
                    "进退有据翻倍触发线成对写入不符。");
                var offenseCapCommit = service.Commit(workspace,
                    [new EngineConstantEdit(offenseCap.Definition.Id, 80, null)]);
                TestSupport.Assert(offenseCapCommit.Success,
                    "攻守兼备减伤效果值上限写入失败：" + offenseCapCommit.Message);
                var afterOffenseCap = service.Read(exe, version)
                    .Single(value => value.Definition.Id == offenseCap.Definition.Id);
                var offenseCapBytes = File.ReadAllBytes(exe);
                TestSupport.Assert(afterOffenseCap.NumericValue == 80 &&
                    offenseCapBytes[checked((int)offenseCap.FileOffset)] == 80 &&
                    BitConverter.ToInt32(offenseCapBytes, checked((int)offenseCap.SecondaryFileOffset)) == 80,
                    "攻守兼备减伤效果值上限混合宽度复读不符。");
            }

            var hitCapCommit = service.Commit(workspace,
                [new EngineConstantEdit(strategyHitCap.Definition.Id, 80, null)]);
            TestSupport.Assert(hitCapCommit.Success, "策略命中/格挡率上限写入失败：" + hitCapCommit.Message);
            var afterHitCap = service.Read(exe, version)
                .Single(value => value.Definition.Id == strategyHitCap.Definition.Id);
            var hitCapBytes = File.ReadAllBytes(exe);
            TestSupport.Assert(afterHitCap.NumericValue == 80 &&
                hitCapBytes[checked((int)strategyHitCap.FileOffset)] == 80 &&
                hitCapBytes[checked((int)strategyHitCap.SecondaryFileOffset)] == 80,
                "策略命中/格挡率上限成对写入不符。");

            var chaosCommit = service.Commit(workspace,
                [new EngineConstantEdit(chaosBonus.Definition.Id, 15, null)]);
            TestSupport.Assert(chaosCommit.Success, "逆乱阴阳命中加成写入失败：" + chaosCommit.Message);
            var afterChaos = service.Read(exe, version)
                .Single(value => value.Definition.Id == chaosBonus.Definition.Id);
            TestSupport.Assert(afterChaos.NumericValue == 15 &&
                File.ReadAllBytes(exe)[checked((int)chaosBonus.FileOffset)] == 15,
                "逆乱阴阳命中加成复读不符。");

            const string quoteText = "测试台词";
            var second = service.Commit(workspace, [new EngineConstantEdit(quote.Definition.Id, null, quoteText)]);
            TestSupport.Assert(second.Success, "突破攻击台词写入失败：" + second.Message);
            var afterQuote = service.Read(exe, version).Single(value => value.Definition.Id == quote.Definition.Id);
            TestSupport.Assert(afterQuote.TextValue == quoteText, "突破攻击台词复读不符。");

            var longText = new string('字', 9);
            var rejected = service.Commit(workspace, [new EngineConstantEdit(quote.Definition.Id, null, longText)]);
            TestSupport.Assert(!rejected.Success, "超长台词没有被拒绝。");
            var outOfRange = service.Commit(workspace,
                [new EngineConstantEdit(guided.Definition.Id, 300, null)]);
            TestSupport.Assert(!outOfRange.Success, "越界次数没有被拒绝。");
            Console.WriteLine("ENGINE_CONSTANT_TESTS_OK guided/screen/aura/quote + write/readback + limits");
        }
        finally
        {
            Directory.Delete(copy, true);
            TestSupport.Assert(HashService.Sha256(Path.Combine(sourceRoot, "Ekd5.exe")) == sourceHash,
                "常量测试修改了真实基底。");
            Console.WriteLine("SOURCE_BASE_UNCHANGED " + sourceHash);
        }
    }
}
