using System.Security.Cryptography;
using System.Text;
using DataManager.Core.Domain;
using DataManager.Core.Editing;

namespace DataManager.Core.Scanner;

/// <summary>
/// Reads and writes the user-defined strategy modifier rules that live in the
/// .strh section. Validation is kept here so the UI and tests share one contract.
/// </summary>
public sealed class StrategyJobModifierService
{
    private readonly StrategyHookSectionService _sections = new();
    private readonly StrategyBuiltInRuleService _builtIn = new();

    public StrategyJobModifierSnapshot Read(ProjectWorkspace workspace)
    {
        var path = Path.Combine(workspace.RootPath, "Ekd5.exe");
        if (!File.Exists(path))
            return new StrategyJobModifierSnapshot([], false, VersionOf(workspace), ProfileId(workspace),
                VariantId(workspace), StrategyHookSectionService.DamageHookVa,
                StrategyHookSectionService.HitHookVa, 0, "找不到 Ekd5.exe。",
                StrategyBuiltInRuleInfo.Unsupported("找不到 Ekd5.exe。"));
        var image = File.ReadAllBytes(path);
        try
        {
            var builtIn = _builtIn.Read(image);
            var state = _sections.Read(image);
            return state == null
                ? new StrategyJobModifierSnapshot([], false, VersionOf(workspace), ProfileId(workspace),
                    VariantId(workspace), StrategyHookSectionService.DamageHookVa,
                    StrategyHookSectionService.HitHookVa, 0, "尚未安装策略 Hook。", builtIn)
                : new StrategyJobModifierSnapshot(state.Rules, state.HookInstalled, VersionOf(workspace),
                    ProfileId(workspace), VariantId(workspace), StrategyHookSectionService.DamageHookVa,
                    StrategyHookSectionService.HitHookVa, state.SectionVa, state.Diagnostic, builtIn);
        }
        catch (Exception ex)
        {
            return new StrategyJobModifierSnapshot([], false, VersionOf(workspace), ProfileId(workspace),
                VariantId(workspace), StrategyHookSectionService.DamageHookVa,
                StrategyHookSectionService.HitHookVa, 0, "读取策略专项失败：" + ex.Message,
                StrategyBuiltInRuleInfo.Unsupported("读取策略专项失败：" + ex.Message));
        }
    }

    public IReadOnlyList<StrategyJobModifierRule> LoadRules(ProjectWorkspace workspace) => Read(workspace).Rules;

    public StrategyJobModifierValidation Validate(IReadOnlyList<StrategyJobModifierRule> rules)
    {
        var issues = new List<StrategyJobModifierIssue>();
        if (rules.Count > StrategyJobModifierRule.Capacity)
            issues.Add(new(StrategyJobModifierIssueSeverity.Error,
                $"规则数量 {rules.Count} 超过上限 {StrategyJobModifierRule.Capacity}。"));
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (!Enum.IsDefined(rule.Side))
                issues.Add(new(StrategyJobModifierIssueSeverity.Error, $"第 {index + 1} 条规则的“作用侧”无效。", index));
            if (!Enum.IsDefined(rule.MatchKind))
                issues.Add(new(StrategyJobModifierIssueSeverity.Error, $"第 {index + 1} 条规则的“条件”无效。", index));
            if (rule.Series > 39)
                issues.Add(new(StrategyJobModifierIssueSeverity.Error, $"第 {index + 1} 条规则的兵种系必须为 0..39。", index));
            if (rule.DamagePercent is < -100 or > 127)
                issues.Add(new(StrategyJobModifierIssueSeverity.Error, $"第 {index + 1} 条规则的伤害加成为 -100..127。", index));
            if (rule.HitPercent is < -100 or > 100)
                issues.Add(new(StrategyJobModifierIssueSeverity.Error, $"第 {index + 1} 条规则的命中加成为 -100..100。", index));
            var noteBytes = Encoding.UTF8.GetByteCount(rule.Note ?? string.Empty);
            if (noteBytes > StrategyJobModifierRule.MaxNoteLength)
                issues.Add(new(StrategyJobModifierIssueSeverity.Error,
                    $"第 {index + 1} 条规则的备注为 {noteBytes} 字节，上限 {StrategyJobModifierRule.MaxNoteLength} 字节（UTF-8）。", index));
            if (!rule.Enabled) continue;
            if (active.TryGetValue(rule.Key, out var previous))
                issues.Add(new(StrategyJobModifierIssueSeverity.Error,
                    $"第 {index + 1} 条规则与第 {previous + 1} 条启用规则的条件/兵种系重复。", index));
            else
                active[rule.Key] = index;
        }
        return new StrategyJobModifierValidation(issues);
    }

    public WorkspaceBinaryCandidate BuildCandidate(ProjectWorkspace workspace,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook)
    {
        var snapshot = Read(workspace);
        return BuildCandidate(workspace, rules, installHook,
            snapshot.BuiltIn.Supported ? snapshot.BuiltIn.Current : null);
    }

    public WorkspaceBinaryCandidate BuildCandidate(ProjectWorkspace workspace,
        IReadOnlyList<StrategyJobModifierRule> rules, bool installHook,
        StrategyBuiltInRuleSettings? builtIn)
    {
        var validation = Validate(rules);
        var error = validation.Issues.FirstOrDefault(issue => issue.Severity == StrategyJobModifierIssueSeverity.Error);
        if (error != null) throw new InvalidDataException(error.Message);
        if (builtIn != null)
        {
            var builtInError = _builtIn.Validate(builtIn).Issues
                .FirstOrDefault(issue => issue.Severity == StrategyJobModifierIssueSeverity.Error);
            if (builtInError != null) throw new InvalidDataException(builtInError.Message);
        }
        var path = Path.Combine(workspace.RootPath, "Ekd5.exe");
        if (!File.Exists(path)) throw new InvalidDataException("找不到 Ekd5.exe。");
        var before = File.ReadAllBytes(path);
        var hook = _sections.Build(path, before, rules, installHook);
        var after = builtIn == null ? hook.AfterBytes : _builtIn.Apply(hook.AfterBytes, builtIn);
        return new WorkspaceBinaryCandidate(path, before, after, "策略专项");
    }

    public byte[] BuildImage(byte[] before, IReadOnlyList<StrategyJobModifierRule> rules, bool installHook)
        => _sections.Build("Ekd5.exe", before, rules, installHook).AfterBytes;

    public byte[] BuildImage(byte[] before, IReadOnlyList<StrategyJobModifierRule> rules, bool installHook,
        StrategyBuiltInRuleSettings? builtIn)
    {
        var hook = _sections.Build("Ekd5.exe", before, rules, installHook);
        return builtIn == null ? hook.AfterBytes : _builtIn.Apply(hook.AfterBytes, builtIn);
    }

    public StrategyJobModifierValidation ValidateBuiltIn(StrategyBuiltInRuleSettings settings)
        => _builtIn.Validate(settings);

    private static string VersionOf(ProjectWorkspace workspace)
        => workspace.ActiveProfile.Profile.Version ?? workspace.ActiveProfile.ProfileId;

    private static string ProfileId(ProjectWorkspace workspace) => workspace.ActiveProfile.ProfileId ?? "unknown";

    private static string VariantId(ProjectWorkspace workspace) => workspace.ActiveProfile.VariantId ?? "default";
}
