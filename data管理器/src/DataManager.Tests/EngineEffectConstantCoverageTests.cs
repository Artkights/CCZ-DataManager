using System.Reflection;
using System.Text;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

/// <summary>
/// Full-disassembly coverage audit: every effect row of one base is classified by
/// its call status (static / derived / table / loop / none), and every collected
/// constant is tied back to the effect ids recorded in its evidence. The result is
/// written as CSV + Markdown so "全量反汇编并处理常量" can be re-checked per release.
/// </summary>
internal static class EngineEffectConstantCoverageTests
{
    private sealed record ConstantLink(string Id, string Kind, int[]? All,
        IReadOnlyDictionary<string, int[]>? ByVersion)
    {
        public int[] For(string version) =>
            ByVersion != null && ByVersion.TryGetValue(version, out var ids) ? ids : All ?? [];
    }

    // Effect ids are the same code slots across bases; 6.6 only renames some of them.
    // Formula entries have no single owning effect and are audited as 公式.
    private static readonly ConstantLink[] Links =
    [
        new("guided-attack-count", "特效", [24], null),
        new("furious-attack-targets", "特效", [23], null),
        new("active-combo-chance", "特效", [52], null),
        new("counter-damage-share", "特效", [72], null),
        new("breakthrough-quote", "特效", [97], null),
        new("breakthrough-damage-bonus", "特效", [97], null),
        new("drain-attack-cap", "特效", [20], null),
        new("iron-chain-directions", "特效", [96], null),
        new("heavy-strike-cap", "特效", [114], null),
        new("shield-counter-cap", "特效", [115], null),
        new("demon-stat-cap", "特效", [61], null),
        new("swift-attack-bonus", "特效", [117], null),
        new("swift-attack-offset", "特效", [117], null),
        new("physical-damage-base", "公式", null, null),
        new("strategy-damage-base", "公式", null, null),
        new("strategy-damage-divisor", "公式", null, null),
        new("absolute-strategy-damage-bonus", "特效", [214], null),
        new("strategy-damage-limit-cut", "特效", [216], null),
        new("absolute-damage-hp-threshold", "特效", [215], null),
        new("absolute-damage-bonus", "特效", [215], null),
        new("lone-brave-reduction-cap", "特效", [210], null),
        new("battle-veteran-damage-divisor", "特效", [203], null),
        new("wind-rush-damage-divisor", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [106], ["6.6"] = [195]
        }),
        new("convert-heal-mp-cost", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [193]
        }),
        new("continuous-attack-reduction-cap", "特效", null, new Dictionary<string, int[]>
        {
            ["6.6"] = [211]
        }),
        new("sharpen-attack-cap", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [194]
        }),
        new("strategy-hit-cap", "公式", null, null),
        new("chaos-yin-yang-hit-bonus", "特效", [39], null),
        new("advance-retreat-double-line", "特效", [220], null),
        new("effect-screen-directions", "特效", [89], null),
        new("screen-inner-directions", "特效", [89], null),
        new("pass-through-directions", "特效", [54], null),
        new("aura-range", "特效", [116], null),
        new("combo-damage-bonus", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [67], ["6.6"] = [158]
        }),
        new("adversity-reduction-cap", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [185], ["6.6"] = [178]
        }),
        new("sneak-attack-step", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [66], ["6.6"] = [176]
        }),
        new("fixed-damage-basis", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [176, 177], ["6.6"] = [165, 166]
        }),
        new("strategy-floor-bonus", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [212, 213]
        }),
        new("mp-strategy-bonus", "特效", [212], null),
        new("money-defense-cost", "特效", [107], null),
        new("money-absorb-multiplier", "特效", [21], null),
        new("dodge-damage-cap", "特效", [206], null),
        new("offense-defense-add-line", "特效", [209], null),
        new("offense-defense-effect-cap", "特效", [209], null),
        new("offense-defense-reduce-line", "特效", [209], null),
        new("exploit-weakness-threshold", "特效", null, new Dictionary<string, int[]>
        {
            ["6.5"] = [190], ["6.6"] = [188]
        }),
        new("fengfeng-floor-bonus", "特效", [111], null)
    ];

    // Every function entry that still contains arithmetic immediates must be reviewed once.
    // The key is "<version>|0x<entry>"; the value is the closed reason for not exposing a constant.
    private static readonly Dictionary<string, string> ReviewedDispositions = new(StringComparer.Ordinal)
    {
        ["6.6|0x0040399F"] = "属性/状态编号与循环计数（6 项能力、effect id 基址 6/8），非单一可调强度",
        ["6.6|0x004039F6"] = "状态编号与分支（4 项状态、effect id 基址 13/15）",
        ["6.6|0x0041A8B8"] = "目标数 1..2 与特效值 100 的判定线组合，不是单一强度",
        ["6.6|0x0041D90E"] = "地形编号 24 与循环计数 3",
        ["6.6|0x0042E5E9"] = "状态编号 62/64",
        ["6.6|0x0043B730"] = "命中流程结构偏移与单位/特效编号 33/210",
        ["6.6|0x0043CB00"] = "单位编号上限 250 与状态分界 2",
        ["6.6|0x0043CDDD"] = "单位字段偏移与每回合效果号基址（2/6/3）",
        ["6.6|0x0043D355"] = "单位编号分界 60（id ≥ 60 翻转阵营关系）",
        ["6.6|0x00442392"] = "字节表记录步长 12",
        ["6.6|0x0045B67C"] = "HP 百分比换算常数 100，无独立可调强度",
        ["6.6|0x0045D3A2"] = "等级/记录步长 64",
        ["6.6|0x004D2800"] = "单位类型判断 2",
        ["6.6|0x004E4300"] = "状态门槛 4",
        ["6.6|0x0043C72C"] = "命中/概率分支固定返回 100，不是可调强度",
        ["6.6|0x0043B600"] = "必中/命中/格挡分支固定返回 100%，与命中公式常量同源",
        ["6.6|0x00405DE1"] = "单位数组结构偏移 0x84/0x254",
        ["6.6|0x00478EAB"] = "4/5 是方向参数（十字/八方），不是强度",
        ["6.6|0x004E40E0"] = "连击率封顶 100%，与主动连击发动率同源",
        ["6.5|0x0040D0B3"] = "六项能力逐个钳到 3，需要六处同步，当前只支持两处成对",
        ["6.5|0x0041D90E"] = "地形编号 24 与循环计数 3",
        ["6.5|0x0043B600"] = "命中流程状态判断 2",
        ["6.5|0x0043B6F3"] = "命中流程结构偏移与单位/特效编号 73/210",
        ["6.5|0x0043CDF5"] = "单位字段偏移与每回合效果号基址（2/6）",
        ["6.5|0x0043D038"] = "状态/能力位掩码枚举（7/63/6/3）",
        ["6.5|0x0045288B"] = "地形/地块类型编号 37/10",
        ["6.5|0x0045B67C"] = "HP 百分比换算常数 100，无独立可调强度",
        ["6.5|0x004D2800"] = "单位类型判断 2",
        ["6.5|0x0043C72C"] = "命中/概率分支固定返回 100，不是可调强度",
        ["6.5|0x00440E3E"] = "反弹伤害的百分比换算分母 100，无独立可调强度",
        ["6.5|0x00405DE1"] = "单位数组结构偏移 0x84/0x254",
        ["6.5|0x00478EAB"] = "4/5 是方向参数（十字/八方），不是强度",
        ["6.5|0x004E40E0"] = "连击率封顶 100%，与主动连击发动率同源",
        ["6.5|0x004072BB"] = "辅助移动力的 min 上限 127，结构上限",
        ["6.4|0x0040CCCC"] = "地形编号 9/10",
        ["6.4|0x0040D1D0"] = "反击/连击标志判断 2",
        ["6.4|0x0041A430"] = "属性编号 4 与结构偏移 0x90",
        ["6.4|0x0041D90E"] = "地形编号 24 与循环计数 3",
        ["6.4|0x0041FF6E"] = "策略编号分界 128",
        ["6.4|0x00436DE1"] = "能力数量 5 与结构偏移 0x21/0x2C",
        ["6.4|0x0043B60B"] = "命中流程状态判断 2",
        ["6.4|0x0043CE15"] = "单位字段偏移与每回合效果号基址（2/6）",
        ["6.4|0x0045B67C"] = "HP 百分比换算常数 100，无独立可调强度",
        ["6.4|0x004D0000"] = "全局模式哨兵 3",
        ["6.4|0x0043C72C"] = "命中/概率分支固定返回 100，不是可调强度",
        ["6.4|0x00440E3E"] = "反弹伤害的百分比换算分母 100，无独立可调强度",
        ["6.4|0x00405DE1"] = "单位数组结构偏移 0x84/0x254",
        ["6.4|0x00478EAB"] = "4/5 是方向参数（十字/八方），不是强度",
        ["6.4|0x004D0041"] = "单位状态字段写入 10/1，与结构耦合，暂不开放"
    };

    public static void Run(string root, string? outputDirectory = null)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var report = new EngineEffectScanService().Scan(workspace);
        var version = report.EngineVersion;
        var values = new EngineConstantService().Read(Path.Combine(root, "Ekd5.exe"), version);
        var exePath = Path.Combine(root, "Ekd5.exe");
        var candidateCache = new Dictionary<(uint Entry, int EffectId), IReadOnlyList<EngineEffectImmediateCandidate>>();
        IReadOnlyList<EngineEffectImmediateCandidate> CandidatesFor(uint entry, int effectId)
        {
            if (candidateCache.TryGetValue((entry, effectId), out var cached)) return cached;
            var found = EngineEffectDisassemblyProbe.CollectImmediateCandidates(exePath, entry, effectId);
            candidateCache[(entry, effectId)] = found;
            return found;
        }
        var pendingReview = new List<string>();
        var linksById = Links.ToDictionary(link => link.Id, StringComparer.Ordinal);
        var formulaIds = FormulaConstantIds();
        var otherIds = EngineConstantService.Definitions
            .Where(definition => definition.Id == "shop-sell-price-numerator" ||
                definition.Evidence.Contains("engine-constants-catalog.json", StringComparison.Ordinal))
            .Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
        var unlinked = EngineConstantService.Definitions
            .Where(definition => !linksById.ContainsKey(definition.Id) && !formulaIds.Contains(definition.Id) &&
                                 !otherIds.Contains(definition.Id))
            .Select(definition => definition.Id).ToArray();
        TestSupport.Assert(unlinked.Length == 0,
            $"覆盖审计漏了常量页条目：links={linksById.Count} formula={formulaIds.Count} " +
            $"defs={EngineConstantService.Definitions.Count} missing={string.Join(",", unlinked)}");
        TestSupport.Assert(values.Count == EngineConstantService.Definitions.Count, "常量页条目数量不符。");

        var constantsByEffect = new Dictionary<int, List<string>>();
        foreach (var value in values)
        {
            if (!value.SignatureMatches || !linksById.TryGetValue(value.Definition.Id, out var link)) continue;
            foreach (var effectId in link.For(version))
            {
                if (!constantsByEffect.TryGetValue(effectId, out var list))
                    constantsByEffect[effectId] = list = [];
                list.Add(value.Definition.DisplayName);
            }
        }
        var knownIds = report.Effects.Select(row => row.EffectId).ToHashSet();
        var mappedIds = constantsByEffect.Keys.Order().ToArray();
        TestSupport.Assert(mappedIds.Length > 0, $"{version} 没有任何已收录常量关联到特效。");
        TestSupport.Assert(mappedIds.All(knownIds.Contains),
            $"{version} 常量映射指向了不存在的特效号：" +
            string.Join(",", mappedIds.Where(id => !knownIds.Contains(id))));
        var callless = mappedIds.Where(id => report.Effects.First(row => row.EffectId == id).TotalCallCount == 0)
            .ToArray();
        TestSupport.Assert(callless.Length == 0,
            $"{version} 常量映射到的特效没有调用点：" + string.Join(",", callless));

        var staticEffects = report.Effects.Count(row => row.CallSiteDetails.Count > 0);
        var calledEffects = report.Effects.Count(row => row.TotalCallCount > 0);
        var tableOnly = report.Effects.Count(row => row.TotalCallCount == 0 && row.AssignmentCovered);
        var textOnly = report.Effects.Count(row => row.TotalCallCount == 0 && !row.AssignmentCovered &&
                                                   (row.HasNameSlot || row.HasDescriptionSlot));
        var empty = report.Effects.Count - calledEffects - tableOnly - textOnly;
        var linkedEffects = constantsByEffect.Count;
        var linkedConstants = values.Where(value => value.SignatureMatches &&
                linksById.TryGetValue(value.Definition.Id, out var link) && link.For(version).Length > 0)
            .Select(value => value.Definition.Id).Distinct().Count();
        var formulaConstants = EngineConstantService.Definitions.Count(definition =>
            formulaIds.Contains(definition.Id) ||
            linksById.TryGetValue(definition.Id, out var link) && link.Kind == "公式");

        var name = Path.GetFileName(root);
        Console.WriteLine($"ENGINE_EFFECT_CONSTANT_COVERAGE_OK {version} {name} " +
                          $"effects={report.Effects.Count} static={staticEffects} called={calledEffects} " +
                          $"linkedEffects={linkedEffects} linkedConstants={linkedConstants} " +
                          $"formulaConstants={formulaConstants} tableOnly={tableOnly} " +
                          $"textOnly={textOnly} empty={empty}");

        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        Directory.CreateDirectory(outputDirectory);
        var csv = new StringBuilder();
        csv.AppendLine("基底,版本,特效号,特效名,调用状态,调用点数,函数入口,关联常量,复核结论,分配表行,文本槽");
        var md = new StringBuilder();
        md.AppendLine($"# 特效反汇编覆盖审计（{version} / {name}）");
        md.AppendLine();
        md.AppendLine($"- 引擎 SHA-256：`{report.ExeSha256}`");
        md.AppendLine($"- 特效行：{report.Effects.Count}");
        md.AppendLine($"- 有静态调用：{staticEffects}；有任何调用点：{calledEffects}");
        md.AppendLine($"- 已收录常量关联：{linkedEffects} 个特效、{linkedConstants} 项常量（另有 {formulaConstants} 项公式常量不绑定单一特效）");
        md.AppendLine($"- 无静态调用、走静态分配表：{tableOnly}；仅文本槽：{textOnly}；空置：{empty}");
        md.AppendLine();
        md.AppendLine("## 已收录常量的特效");
        md.AppendLine();
        md.AppendLine("| 特效号 | 特效名 | 调用点 | 常量 |");
        md.AppendLine("|---|---|---:|---|");
        foreach (var row in report.Effects.Where(row => constantsByEffect.ContainsKey(row.EffectId)))
            md.AppendLine($"| {row.EffectId} | {row.Name} | {row.TotalCallCount} | " +
                          $"{string.Join("、", constantsByEffect[row.EffectId])} |");
        md.AppendLine();
        md.AppendLine("## 有调用点但尚未收录固定常量的特效");
        md.AppendLine();
        md.AppendLine("| 函数入口 | 特效号 | 特效名 |");
        md.AppendLine("|---|---|---|");
        var pending = report.Effects
            .Where(row => row.TotalCallCount > 0 && !constantsByEffect.ContainsKey(row.EffectId))
            .Select(row => new { Entry = EntriesText(row), row.EffectId, row.Name })
            .GroupBy(item => item.Entry)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in pending)
            md.AppendLine($"| {group.Key} | {string.Join(",", group.Select(item => item.EffectId).Order())} | " +
                          $"{string.Join("、", group.Select(item => item.Name))} |");
        md.AppendLine();

        foreach (var row in report.Effects)
        {
            var related = constantsByEffect.TryGetValue(row.EffectId, out var names)
                ? string.Join("、", names)
                : string.Empty;
            var entries = EntriesText(row);
            var entryList = row.CallSiteDetails.Concat(row.DerivedCallDetails)
                .Concat(row.TableCallDetails).Concat(row.LoopCallDetails)
                .Select(call => call.FunctionEntry).Where(entry => entry.HasValue)
                .Select(entry => entry!.Value).Distinct().ToArray();
            var candidateInfo = entryList.Select(entry => (Entry: entry, Candidates: CandidatesFor(entry, row.EffectId))).ToArray();
            var withCandidates = candidateInfo.Where(item => item.Candidates.Count > 0).ToArray();
            var dispositionReasons = withCandidates
                .Select(item => ReviewedDispositions.TryGetValue($"{version}|0x{item.Entry:X8}", out var reason)
                    ? reason
                    : null)
                .Where(reason => reason != null).ToArray();
            if (related.Length == 0 && row.TotalCallCount > 0 && withCandidates.Length > 0)
            {
                var missing = withCandidates
                    .Where(item => !ReviewedDispositions.ContainsKey($"{version}|0x{item.Entry:X8}"))
                    .Select(item => $"0x{item.Entry:X8}=" + string.Join(";",
                        item.Candidates.Select(candidate => $"0x{candidate.Address:X8} {candidate.Mnemonic} {candidate.Value}")))
                    .ToArray();
                if (missing.Length > 0)
                    pendingReview.Add($"#{row.EffectId} {row.Name}（{string.Join("；", missing)}）");
            }
            var conclusion = related.Length > 0
                ? "已收录常量"
                : row.TotalCallCount > 0
                    ? withCandidates.Length == 0
                        ? "已复核：处理函数无算术立即数（纯判定/只读特效值）"
                        : dispositionReasons.Length == withCandidates.Length
                            ? "已复核：" + string.Join("；", dispositionReasons.Distinct())
                            : "待复核：发现候选立即数"
                    : row.AssignmentCovered
                        ? "无静态调用；静态分配表行可写特效值"
                        : row.HasNameSlot || row.HasDescriptionSlot
                            ? "无静态调用；仅名称/说明文本槽"
                            : "无调用点、无分配行、无文本槽（空置）";
            csv.AppendLine(string.Join(",",
                Csv(name), Csv(version), row.EffectId.ToString(), Csv(row.Name), Csv(CallStatus(row)),
                row.TotalCallCount.ToString(), Csv(entries.Length == 0 ? "—" : entries),
                Csv(related), Csv(conclusion),
                Csv(row.AssignmentRowOffset >= 0 ? $"0x{row.AssignmentRowOffset:X}" : "—"),
                Csv(row.HasNameSlot ? $"0x{row.NameSlotOffset:X}" : "—")));
        }
        TestSupport.Assert(pendingReview.Count == 0,
            $"{version} 仍有未复核的立即数候选：{string.Join(" | ", pendingReview)}");
        File.WriteAllText(Path.Combine(outputDirectory, $"特效反汇编覆盖-{name}.csv"), csv.ToString(),
            new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(outputDirectory, $"特效反汇编覆盖审计-{name}.md"), md.ToString(),
            new UTF8Encoding(true));
    }

    /// <summary>命中/暴击/连击公式常量由独立定义表提供，不绑定单一特效。</summary>
    private static HashSet<string> FormulaConstantIds()
    {
        var type = typeof(EngineConstantService).Assembly.GetType(
            "DataManager.Core.Scanner.BattleFormulaConstantDefinitions", throwOnError: true)!;
        var all = (System.Collections.IEnumerable)type
            .GetField("All", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return all.Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string CallStatus(EngineEffectScanRow row) =>
        row.CallSiteDetails.Count > 0 ? "Static" :
        row.DerivedCallDetails.Count > 0 ? "派生" :
        row.TableCallDetails.Count > 0 ? "跳表" :
        row.LoopCallDetails.Count > 0 ? "环内" :
        row.TotalCallCount > 0 ? "不可达" : "无";

    private static string EntriesText(EngineEffectScanRow row)
    {
        var entries = row.CallSiteDetails.Concat(row.DerivedCallDetails)
            .Concat(row.TableCallDetails).Concat(row.LoopCallDetails)
            .Select(call => call.FunctionEntry).Where(entry => entry.HasValue)
            .Select(entry => entry.GetValueOrDefault()).Distinct().Order().ToArray();
        return string.Join("/", entries.Select(entry => $"0x{entry:X8}"));
    }

    private static string Csv(string text) =>
        text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
