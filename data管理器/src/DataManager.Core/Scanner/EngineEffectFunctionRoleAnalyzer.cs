using System.Text;
using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Labels functions that appear in an effect call chain by structure: direct
/// effect entry stubs, multi-branch dispatchers, return-value queries and
/// upstream consumers that only reach the effect chain through downstream calls.
/// </summary>
internal static class EngineEffectFunctionRoleAnalyzer
{
    private const int QueryWindowBytes = 32;
    private const int StubInstructionLimit = 12;
    private const int MaximumEvidenceCalls = 6;

    public static IReadOnlyList<EngineEffectFunctionRole> Analyze(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph, EngineEffectJumpIndex jumpIndex,
        IReadOnlyDictionary<uint, IReadOnlyList<int>> effectIdsByFunction,
        IReadOnlyDictionary<int, string> effectNames, IEnumerable<uint> functionEntries)
    {
        var entries = functionEntries.Where(entry => image.SectionOf(entry) != null)
            .Distinct().OrderBy(entry => entry).ToArray();
        if (entries.Length == 0) return [];
        var wanted = new HashSet<uint>(entries);
        var directByEntry = callGraph.Calls
            .Where(call => call.CallerEntry is { } caller && wanted.Contains(caller) &&
                           EngineEffectScanService.KnownEntryTargets.Contains(call.Target))
            .GroupBy(call => call.CallerEntry!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(call => call.Address).ToArray());
        var effectEntries = new HashSet<uint>(EngineEffectScanService.KnownEntryTargets);
        var result = new List<EngineEffectFunctionRole>(entries.Length);
        foreach (var entry in entries)
        {
            var effectIds = effectIdsByFunction.TryGetValue(entry, out var ids) ? ids : [];
            var calls = directByEntry.GetValueOrDefault(entry) ?? [];
            var profile = EngineEffectFunctionProfiler.Profile(image, entry);
            var branchPaths = EngineEffectBranchConditionAnalyzer.Analyze(image, entry, calls, jumpIndex, callGraph);
            var evidence = new StringBuilder();
            string role;
            if (EngineEffectScanService.EntryNames.TryGetValue(entry, out var entryName))
            {
                role = $"特效入口（{entryName}）";
                evidence.Append($"共享特效入口 0x{entry:X8}，函数 0x{profile.SizeBytes:X} 字节");
            }
            else if (calls.Length >= 2)
            {
                role = $"特效判定分发器（{calls.Length} 个分支）";
                evidence.Append($"函数体内直接调用特效入口 {calls.Length} 处：" +
                                DescribeCalls(calls, effectNames));
            }
            else if (calls.Length == 1)
            {
                var call = calls[0];
                if (IsLevelQuery(image, call.Address))
                {
                    role = "特效等级查询";
                    evidence.Append($"调用 0x{call.Address:X8} → 0x{call.Target:X8}（{EntryName(call.Target)}）后，" +
                                    "对返回值 test eax,eax 并多次 inc eax，返回等级/次数");
                }
                else if (profile.FunctionInstructionCount <= StubInstructionLimit && profile.DirectCallCount == 1)
                {
                    role = "特效判定桩";
                    evidence.Append($"单点调用 0x{call.Address:X8} → 0x{call.Target:X8}（{EntryName(call.Target)}），" +
                                    $"函数 0x{profile.SizeBytes:X} 字节 / {profile.FunctionInstructionCount} 条指令");
                }
                else
                {
                    role = "特效调用封装";
                    evidence.Append($"函数体内调用 0x{call.Address:X8} → 0x{call.Target:X8}（{EntryName(call.Target)}），" +
                                    $"函数 0x{profile.SizeBytes:X} 字节 / {profile.FunctionInstructionCount} 条指令");
                }
            }
            else
            {
                var reach = callGraph.ReachEffect(entry, effectEntries);
                if (reach.Found && reach.Depth > 1)
                {
                    role = $"上游消费者（深度 {reach.Depth}）";
                    evidence.Append($"本身不直接调用特效入口，经 0x{reach.ViaEntry:X8} → 0x{reach.ViaTarget:X8}" +
                                    $"（{EntryName(reach.ViaTarget)}）进入特效链；路径 " +
                                    string.Join(" → ", reach.Path.Select(value => $"0x{value:X8}")));
                }
                else
                {
                    role = "普通引擎函数";
                    evidence.Append($"函数 0x{profile.SizeBytes:X} 字节 / {profile.FunctionInstructionCount} 条指令，" +
                                    "沿 E8 直接边未进入特效链");
                }
            }

            if (effectIds.Count > 0)
            {
                evidence.Append("；关联特效 " + string.Join("/", effectIds.Select(id =>
                    effectNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                        ? $"{id} {name}" : id.ToString())));
            }
            if (branchPaths.Count > 0)
            {
                var conditionCount = branchPaths.Sum(path => path.Conditions.Count);
                var unreachable = branchPaths.Count(path => !path.Reachable);
                var jumpEntered = branchPaths.Count(path => !path.Reachable && path.InboundJumpAddress != null);
                evidence.Append($"；判定路径 {branchPaths.Count} 条，分支条件 {conditionCount} 个" +
                                (unreachable > 0
                                    ? $"（不可静态回溯 {unreachable} 条" +
                                      (jumpEntered > 0 ? $"，其中经入边跳转进入 {jumpEntered} 条" : string.Empty) + "）"
                                    : string.Empty));
            }
            result.Add(new EngineEffectFunctionRole
            {
                Entry = entry,
                Role = role,
                EffectIds = effectIds,
                Evidence = evidence.ToString(),
                BranchPaths = branchPaths
            });
        }
        return result;
    }

    private static string DescribeCalls(IReadOnlyList<EngineEffectDirectCall> calls,
        IReadOnlyDictionary<int, string> effectNames)
        => string.Join("；", calls.Take(MaximumEvidenceCalls).Select(call =>
            $"0x{call.Address:X8} → 0x{call.Target:X8}（{EntryName(call.Target)}）")) +
           (calls.Count > MaximumEvidenceCalls ? "…" : string.Empty);

    private static string EntryName(uint target)
        => EngineEffectScanService.EntryNames.TryGetValue(target, out var name) ? name : $"0x{target:X8}";

    /// <summary>
    /// Detects the shape "call entry; test eax,eax; ...; inc eax" used by
    /// per-unit level/count accessors. The flag byte compared afterwards is the
    /// unit field, so the return value is a level in 0..3 rather than a boolean.
    /// </summary>
    private static bool IsLevelQuery(EngineEffectBinaryImage image, uint callAddress)
    {
        var decoded = EngineEffectDecoder.DecodeForward(image, callAddress + 5, QueryWindowBytes);
        var sawTestEax = false;
        var sawIncEax = false;
        foreach (var item in decoded)
        {
            var instruction = item.Instruction;
            if (instruction.Mnemonic == Mnemonic.Test && instruction.OpCount >= 2 &&
                IsRegister(instruction, 0, Register.EAX) && IsRegister(instruction, 1, Register.EAX))
                sawTestEax = true;
            if (instruction.Mnemonic == Mnemonic.Inc && instruction.OpCount >= 1 &&
                IsRegister(instruction, 0, Register.EAX))
                sawIncEax = true;
            if (instruction.Mnemonic == Mnemonic.Ret) break;
        }
        return sawTestEax && sawIncEax;
    }

    private static bool IsRegister(in Instruction instruction, int index, Register register)
        => instruction.GetOpKind(index) == OpKind.Register && instruction.GetOpRegister(index) == register;
}
