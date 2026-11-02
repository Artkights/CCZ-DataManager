using System.Globalization;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>
/// Merges the recovered branch path of each direct effect-entry call with the four
/// pushed stack arguments. The branch path says which cmp/test decisions must hold
/// to arrive at the call; the pushes say which value/stacking/item/personal numbers
/// the call supplies. Together they give a per-effect trigger summary such as
/// "effect 12: guard cmp eax,64h not taken; push value=1 stacking=0 item=-- personal=12".
/// </summary>
internal static class EngineEffectTriggerAnalyzer
{
    public static IReadOnlyList<EngineEffectTriggerSummary> Build(
        IReadOnlyList<EngineEffectFunctionRole> roles, IReadOnlyList<EngineEffectCallSite> calls,
        IReadOnlyDictionary<int, string> effectNames)
    {
        if (roles.Count == 0 || calls.Count == 0) return [];
        var byAddress = calls.DistinctBy(call => call.Identity).ToLookup(call => call.CallAddress);
        var result = new List<EngineEffectTriggerSummary>();
        foreach (var role in roles)
        {
            foreach (var path in role.BranchPaths)
            {
                foreach (var call in byAddress[path.CallAddress])
                {
                var effectId = call.Status == EngineEffectCallStatus.Static ? call.PersonalEffectId : null;
                var effectName = effectId is { } id && effectNames.TryGetValue(id, out var name)
                    ? name
                    : string.Empty;
                var personalMatches = effectId is { } expected && call.PersonalEffectId == expected;
                var parameterSummary = DescribeParameters(call);
                result.Add(new EngineEffectTriggerSummary
                {
                    Identity = call.Identity,
                    EffectId = effectId,
                    EffectName = effectName,
                    FunctionEntry = role.Entry,
                    FunctionRole = role.Role,
                    CallAddress = call.CallAddress,
                    Target = path.Target,
                    TargetName = path.TargetName,
                    Reachable = path.Reachable,
                    Truncated = path.Truncated,
                    ConditionCount = path.Conditions.Count,
                    PathCount = path.PathCount,
                    OptionalConditions = path.OptionalConditions,
                    MultiPathSummary = path.MultiPathSummary,
                    ValueMode = call.ValueMode,
                    StackingMode = call.StackingMode,
                    ItemEffectId = call.ItemEffectId,
                    PersonalEffectId = call.PersonalEffectId,
                    PersonalMatchesEffect = personalMatches,
                    ParameterSummary = parameterSummary,
                    ConditionSummary = path.MultiPathSummary.Length > 0
                        ? path.Summary + "；" + path.MultiPathSummary
                        : path.Summary,
                    Summary = DescribeTrigger(effectId, effectName, role, path, parameterSummary),
                    InboundJumpAddress = path.InboundJumpAddress,
                    InboundJumpKind = path.InboundJumpKind,
                    InboundJumpReachable = path.InboundJumpReachable,
                    Conditions = path.Conditions,
                    UpstreamConditions = path.UpstreamConditions,
                    UpstreamOptionalConditions = path.UpstreamOptionalConditions
                });
                }
            }
        }
        return result.OrderBy(item => item.EffectId ?? int.MaxValue)
            .ThenBy(item => item.CallAddress).ToArray();
    }

    private static string DescribeTrigger(int? effectId, string effectName, EngineEffectFunctionRole role,
        EngineEffectBranchPath path, string parameterSummary)
    {
        var effect = effectId is { } id
            ? $"特效 {id}" + (effectName.Length > 0 ? $" {effectName}" : string.Empty)
            : "未归属调用";
        var multiPath = path.PathCount > 1 ? $"；多路径守卫 {path.MultiPathSummary}" : string.Empty;
        return $"{effect}：函数 0x{role.Entry:X8}（{role.Role}）调用点 0x{path.CallAddress:X8} → " +
               $"{path.TargetName}；参数 {parameterSummary}；触发条件 {path.Summary}{multiPath}";
    }

    private static string DescribeParameters(EngineEffectCallSite call)
    {
        var parts = new[]
        {
            Argument("值", call.ValueMode, call.ValuePush),
            Argument("叠加", call.StackingMode, call.StackingPush),
            Argument("装备", call.ItemEffectId, call.ItemPush),
            Argument("个人", call.PersonalEffectId, call.PersonalPush)
        };
        return string.Join("，", parts);
    }

    private static string Argument(string label, int? value, EngineEffectPushArgument argument)
    {
        var text = label + "=" + (value?.ToString(CultureInfo.InvariantCulture) ?? "未定");
        var source = argument.SourceKind switch
        {
            EngineEffectArgumentSource.Immediate => argument.Value is { } immediate
                ? $" 立即数 0x{immediate:X}"
                : " 立即数",
            EngineEffectArgumentSource.Memory => " 内存",
            EngineEffectArgumentSource.Register => " 寄存器",
            EngineEffectArgumentSource.StackParameter => " 栈参数",
            EngineEffectArgumentSource.Computed => " 计算",
            _ => string.Empty
        };
        var writable = argument.WritePlan.IsWritable ? "（可写）" : "（只读）";
        return text + source + writable;
    }
}
