﻿using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Recovers the guard conditions that lead to an effect-entry call. The function
/// body is split into basic blocks along fall-through, conditional branches and
/// direct jumps; a shortest path from the function entry to the block holding the
/// call yields the branch decisions and the compare/test instruction feeding each
/// decision. This turns "call site 0x0043BCxx reaches the core check" into
/// "cmp eax,16h; jne ... was not taken before the call", which is the missing
/// branch-level semantics of the dispatchers and level queries.
/// </summary>
internal static class EngineEffectBranchConditionAnalyzer
{
    private const int MaximumBlocks = 4096;
    private const int MaximumInstructions = 60000;
    private const int MaximumConditions = 12;
    private const int MaximumSwitchEntries = 64;
    /// <summary>Distinct entry→call paths enumerated per call site.</summary>
    private const int MaximumPaths = 32;
    /// <summary>Blocks on one simple path; longer paths are dropped and flagged as truncated.</summary>
    private const int MaximumPathDepth = 64;
    /// <summary>Total block expansions per call site, so a dense CFG cannot stall the scan.</summary>
    private const int MaximumPathSteps = 50000;
    /// <summary>Distinct guard sets (DNF terms) kept per call site.</summary>
    private const int MaximumGroups = 32;

    public static IReadOnlyList<EngineEffectBranchPath> Analyze(EngineEffectBinaryImage image, uint entry,
        IReadOnlyList<EngineEffectDirectCall> calls, EngineEffectJumpIndex? jumpIndex = null,
        EngineEffectCallGraph? callGraph = null)
    {
        if (calls.Count == 0 || image.SectionOf(entry) == null) return [];
        var graph = Build(image, entry);
        if (graph.Blocks.Count == 0) return [];
        var result = new List<EngineEffectBranchPath>(calls.Count);
        foreach (var call in calls.OrderBy(item => item.Address))
        {
            var block = graph.InstructionBlocks.GetValueOrDefault(call.Address);
            var targetName = EngineEffectScanService.EntryNames.TryGetValue(call.Target, out var name)
                ? name
                : $"0x{call.Target:X8}";
            if (block == null)
            {
                var inbound = FindInboundJump(image, jumpIndex, call.Address);
                var inboundFunction = inbound != null && callGraph != null
                    ? callGraph.CallerEntryOf(inbound.Address)
                    : null;
                bool? inboundReachable = null;
                UpstreamPath? upstream = null;
                if (inboundFunction is { } jumpOwner)
                {
                    var reached = EngineEffectFunctionAnalyzer.ReachableAddresses(image, jumpOwner);
                    if (reached != null) inboundReachable = reached.Contains(inbound!.Address);
                    if (inbound is { } jump) upstream = ResolveUpstream(image, jump, jumpOwner);
                }
                result.Add(new EngineEffectBranchPath
                {
                    CallAddress = call.Address,
                    Target = call.Target,
                    TargetName = targetName,
                    Reachable = false,
                    InboundJumpAddress = inbound?.Address,
                    InboundJumpKind = inbound?.Kind ?? string.Empty,
                    InboundJumpFunction = inboundFunction,
                    InboundJumpReachable = inboundReachable,
                    UpstreamConditions = upstream?.Reachable == true ? upstream.Conditions : [],
                    UpstreamTruncated = upstream?.Truncated ?? false,
                    UpstreamSummary = upstream?.Summary ?? string.Empty,
                    UpstreamPathCount = upstream?.PathCount ?? 0,
                    UpstreamPathsTruncated = upstream?.PathsTruncated ?? false,
                    UpstreamCommonConditions = upstream?.Common ?? [],
                    UpstreamOptionalConditions = upstream?.Optional ?? [],
                    UpstreamMultiPathSummary = upstream?.MultiPathSummary ?? string.Empty,
                    UpstreamConditionGroups = upstream?.Groups ?? [],
                    UpstreamConditionGroupsTruncated = upstream?.GroupsTruncated ?? false,
                    UpstreamConditionDecisions = upstream?.Decisions.Items ?? [],
                    UpstreamConditionDecisionCombinationSpace = upstream?.Decisions.Space ?? 0,
                    UpstreamConditionDecisionSpaceComplete = upstream?.Decisions.Complete ?? false,
                    UpstreamConditionDecisionMissingCount = upstream?.Decisions.Missing ?? 0,
                    UpstreamConditionDecisionSummary = upstream?.Decisions.Summary ?? string.Empty,
                    UpstreamConditionDecisionNonFixedPoints = upstream?.Decisions.NonFixed ?? [],
                    UpstreamConditionDecisionRepeatedPoints = upstream?.Decisions.Repeated ?? [],
                    Summary = DescribeInbound(call.Address, inbound, inboundFunction, inboundReachable, upstream)
                });
                continue;
            }
            var path = FindPath(graph, entry, block.Start);
            if (path == null)
            {
                result.Add(new EngineEffectBranchPath
                {
                    CallAddress = call.Address,
                    Target = call.Target,
                    TargetName = targetName,
                    Reachable = false,
                    BlockCount = 0,
                    Summary = "函数入口沿顺序/分支/跳转表无法到达该调用块（代码洞或不可达碎片）"
                });
                continue;
            }
            var allConditions = CollectConditions(graph, path);
            var truncated = allConditions.Count > MaximumConditions;
            var conditions = truncated ? allConditions[^MaximumConditions..] : allConditions;
            var pathSet = EnumeratePaths(graph, entry, block.Start);
            var merged = MergePaths(graph, path, pathSet);
            result.Add(new EngineEffectBranchPath
            {
                CallAddress = call.Address,
                Target = call.Target,
                TargetName = targetName,
                Reachable = true,
                BlockCount = path.Count,
                Truncated = truncated,
                Conditions = conditions,
                PathCount = merged.PathCount,
                PathsTruncated = merged.Truncated,
                CommonConditions = merged.Common,
                OptionalConditions = merged.Optional,
                ConflictingConditionCount = merged.Conflicts,
                MultiPathSummary = DescribeMultiPath(merged, allConditions),
                ConditionGroups = merged.Groups,
                ConditionGroupsTruncated = merged.GroupsTruncated,
                ConditionGroupDuplicatePathCount = merged.GroupDuplicates,
                ConditionDecisions = merged.Decisions.Items,
                ConditionDecisionCombinationSpace = merged.Decisions.Space,
                ConditionDecisionSpaceComplete = merged.Decisions.Complete,
                ConditionDecisionMissingCount = merged.Decisions.Missing,
                ConditionDecisionSummary = merged.Decisions.Summary,
                ConditionDecisionNonFixedPoints = merged.Decisions.NonFixed,
                ConditionDecisionRepeatedPoints = merged.Decisions.Repeated,
                Summary = Describe(conditions, truncated)
            });
        }
        return result;
    }

    private static string Describe(IReadOnlyList<EngineEffectBranchCondition> conditions, bool truncated)
    {
        if (conditions.Count == 0) return "入口到调用点无分支判定（顺序执行）";
        var text = string.Join("；", conditions.Select(DescribeCondition));
        return truncated ? "…（仅显示最近 " + conditions.Count + " 个判定）" + text : text;
    }

    private static string DescribeCondition(EngineEffectBranchCondition condition)
        => (condition.Comparison is { Length: > 0 } compare ? compare + " → " : string.Empty) +
           condition.Instruction + (condition.Taken ? "（跳转）" : "（不跳转）");

    private static List<EngineEffectBranchCondition> CollectConditions(Graph graph, List<Block> path)
    {
        var conditions = new List<EngineEffectBranchCondition>();
        for (var index = 0; index + 1 < path.Count; index++)
        {
            var from = path[index];
            var to = path[index + 1];
            var edge = graph.EdgeByPair.GetValueOrDefault((from.Start, to.Start));
            if (edge?.Condition is { } condition) conditions.Add(condition);
        }
        return conditions;
    }

    /// <summary>
    /// Enumerates the simple entry→target paths of the CFG, bounded by path count,
    /// block depth and expansion count. The shortest path alone hides guards that
    /// only exist on a longer route (an early loop test, a table switch arm), so the
    /// whole set is needed before guards can be split into common and path-specific.
    /// </summary>
    private static PathSet EnumeratePaths(Graph graph, uint entry, uint target)
    {
        var key = (entry, target);
        if (graph.Paths.TryGetValue(key, out var cached)) return cached;
        var result = EnumeratePathsCore(graph, entry, target);
        graph.Paths.Add(key, result);
        return result;
    }

    private static PathSet EnumeratePathsCore(Graph graph, uint entry, uint target)
    {
        var result = new List<List<Block>>();
        if (!graph.Blocks.ContainsKey(entry) || !graph.Blocks.ContainsKey(target))
            return new PathSet(result, false);
        var stack = new List<uint>();
        var onPath = new HashSet<uint>();
        var steps = 0;
        var truncated = false;

        void Visit(uint current)
        {
            if ((steps & 0x3FF) == 0) graph.Context.CheckCancellation();
            if (result.Count >= MaximumPaths || steps++ > MaximumPathSteps)
            {
                truncated = true;
                return;
            }
            if (!onPath.Add(current)) return;
            stack.Add(current);
            if (current == target)
            {
                result.Add(stack.Select(address => graph.Blocks[address]).ToList());
            }
            else if (stack.Count >= MaximumPathDepth)
            {
                truncated = true;
            }
            else
            {
                foreach (var edge in graph.Successors.GetValueOrDefault(current) ?? [])
                {
                    if (graph.Blocks.ContainsKey(edge.To)) Visit(edge.To);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            onPath.Remove(current);
        }

        Visit(entry);
        return new PathSet(result, truncated);
    }

    private static MergedPaths MergePaths(Graph graph, List<Block> shortest, PathSet set)
    {
        var paths = new List<List<Block>> { shortest };
        var truncated = set.Truncated;
        foreach (var candidate in set.Paths)
        {
            if (paths.Any(existing => SamePath(existing, candidate))) continue;
            if (paths.Count >= MaximumPaths)
            {
                truncated = true;
                break;
            }
            paths.Add(candidate);
        }
        var conditions = paths.Select(path => (IReadOnlyList<EngineEffectBranchCondition>)CollectConditions(graph, path))
            .ToList();
        var merged = MergeConditions(conditions);
        var groups = BuildGroups(conditions);
        return new MergedPaths(paths.Count, truncated || merged.Truncated, merged.Common, merged.Optional,
            merged.Conflicts, groups.Groups, groups.Truncated, groups.Duplicates, groups.Decisions);
    }

    /// <summary>
    /// Splits the guards of all enumerated paths into the intersection (holds on every
    /// path) and the difference (holds on some paths only), and counts branch addresses
    /// that are taken on one path but not on another.
    /// </summary>
    private static MergedPaths MergeConditions(
        IReadOnlyList<IReadOnlyList<EngineEffectBranchCondition>> paths)
    {
        var counts = new Dictionary<ConditionKey, int>();
        var firstSeen = new Dictionary<ConditionKey, (EngineEffectBranchCondition Condition, int Order)>();
        var directions = new Dictionary<uint, HashSet<bool>>();
        var order = 0;
        foreach (var path in paths)
        foreach (var condition in path)
        {
            var key = new ConditionKey(condition.Address, condition.Taken);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (!firstSeen.ContainsKey(key)) firstSeen[key] = (condition, order++);
            if (!directions.TryGetValue(condition.Address, out var taken))
                directions[condition.Address] = taken = [];
            taken.Add(condition.Taken);
        }
        var common = new List<EngineEffectBranchCondition>();
        var optional = new List<EngineEffectBranchCondition>();
        foreach (var entry in firstSeen.OrderBy(pair => pair.Value.Order))
        {
            if (counts[entry.Key] >= paths.Count) common.Add(entry.Value.Condition);
            else optional.Add(entry.Value.Condition);
        }
        var conflicts = directions.Values.Count(taken => taken.Count > 1);
        return new MergedPaths(paths.Count, false, common, optional, conflicts, [], false, 0,
            new DecisionSet([], 0, false, 0, string.Empty, [], []));
    }

    /// <summary>
    /// Turns the guard list of each enumerated path into a DNF: one group per distinct
    /// conjunction, keeping the first path's execution order. Two paths are equivalent
    /// when they test the same branch addresses with the same directions, so their
    /// group is merged and only <see cref="EngineEffectBranchConditionGroup.PathCount"/>
    /// grows. Groups that disagree on a shared branch are recorded as mutually
    /// exclusive, which is what tells "either the 0xFF entry path or the count path".
    /// </summary>
    private static GroupSet BuildGroups(IReadOnlyList<IReadOnlyList<EngineEffectBranchCondition>> paths)
    {
        var order = new List<string>();
        var firstConditions = new Dictionary<string, IReadOnlyList<EngineEffectBranchCondition>>();
        var counts = new Dictionary<string, int>();
        var truncated = false;
        foreach (var path in paths)
        {
            var key = ConditionSetKey(path);
            if (!firstConditions.ContainsKey(key))
            {
                if (order.Count >= MaximumGroups)
                {
                    truncated = true;
                    continue;
                }
                order.Add(key);
                firstConditions[key] = path;
            }
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var duplicates = order.Sum(key => counts[key] - 1);
        var drafts = order.Select((key, index) => new GroupDraft(index + 1, firstConditions[key], counts[key])).ToArray();
        var exclusions = new List<(int Index, int Other, uint Address)>[drafts.Length];
        for (var i = 0; i < drafts.Length; i++) exclusions[i] = [];
        for (var i = 0; i < drafts.Length; i++)
        {
            for (var j = i + 1; j < drafts.Length; j++)
            {
                foreach (var address in SharedOppositeAddresses(drafts[i].Conditions, drafts[j].Conditions))
                {
                    exclusions[i].Add((drafts[i].Index, drafts[j].Index, address));
                    exclusions[j].Add((drafts[j].Index, drafts[i].Index, address));
                }
            }
        }
        var groups = drafts.Select((draft, index) => new EngineEffectBranchConditionGroup
        {
            Index = draft.Index,
            Conditions = draft.Conditions,
            PathCount = draft.PathCount,
            ExclusiveGroupIndexes = exclusions[index].Select(item => item.Other).Distinct().OrderBy(item => item).ToArray(),
            ConditionText = DescribeConditions(draft.Conditions),
            Summary = DescribeGroup(draft, exclusions[index])
        }).ToArray();
        return new GroupSet(groups, truncated, duplicates, BuildDecisions(paths, groups.Length, truncated));
    }

    /// <summary>
    /// Finds the free boolean variables behind a large DNF. A branch address qualifies
    /// when every enumerated path tests it exactly once and both directions occur; those
    /// points generate the terms, so 2^k is the full combination space and the enumerated
    /// group count is the covered part of it. Addresses that only some paths test are
    /// reported as non-fixed, and addresses tested more than once inside a single path
    /// (loop re-entry) are reported as repeated, because in both cases a single 2^k
    /// boolean model no longer holds.
    /// </summary>
    private static DecisionSet BuildDecisions(
        IReadOnlyList<IReadOnlyList<EngineEffectBranchCondition>> paths, int groupCount, bool groupsTruncated)
    {
        if (paths.Count <= 1) return new DecisionSet([], 0, false, 0, string.Empty, [], []);
        var presence = new Dictionary<uint, int>();
        var takenCounts = new Dictionary<uint, int>();
        var notTakenCounts = new Dictionary<uint, int>();
        var firstSeen = new Dictionary<uint, (EngineEffectBranchCondition Condition, int Order)>();
        var repeated = new HashSet<uint>();
        var order = 0;
        foreach (var path in paths)
        {
            var seen = new Dictionary<uint, int>();
            foreach (var condition in path)
            {
                var count = seen.GetValueOrDefault(condition.Address) + 1;
                seen[condition.Address] = count;
                if (count > 1) repeated.Add(condition.Address);
                if (count == 1)
                    presence[condition.Address] = presence.GetValueOrDefault(condition.Address) + 1;
                if (!firstSeen.ContainsKey(condition.Address)) firstSeen[condition.Address] = (condition, order++);
                var counts = condition.Taken ? takenCounts : notTakenCounts;
                counts[condition.Address] = counts.GetValueOrDefault(condition.Address) + 1;
            }
        }
        var decisions = new List<EngineEffectConditionDecision>();
        var nonFixed = new List<EngineEffectConditionDeviation>();
        var repeatedAddresses = new List<EngineEffectConditionDeviation>();
        foreach (var entry in firstSeen.OrderBy(pair => pair.Value.Order))
        {
            if (repeated.Contains(entry.Key))
            {
                repeatedAddresses.Add(DescribeDeviation(entry.Value.Condition, presence.GetValueOrDefault(entry.Key),
                    paths.Count, takenCounts.GetValueOrDefault(entry.Key), notTakenCounts.GetValueOrDefault(entry.Key), true));
                continue;
            }
            if (presence[entry.Key] < paths.Count)
            {
                nonFixed.Add(DescribeDeviation(entry.Value.Condition, presence[entry.Key], paths.Count,
                    takenCounts.GetValueOrDefault(entry.Key), notTakenCounts.GetValueOrDefault(entry.Key), false));
                continue;
            }
            var taken = takenCounts.GetValueOrDefault(entry.Key);
            var notTaken = notTakenCounts.GetValueOrDefault(entry.Key);
            if (taken == 0 || notTaken == 0) continue;
            decisions.Add(new EngineEffectConditionDecision
            {
                Address = entry.Value.Condition.Address,
                Instruction = DescribeCondition(entry.Value.Condition),
                Comparison = entry.Value.Condition.Comparison,
                TakenPaths = taken,
                NotTakenPaths = notTaken
            });
        }
        var space = decisions.Count == 0 ? 0 : decisions.Count >= 24 ? int.MaxValue : 1 << decisions.Count;
        var complete = decisions.Count > 0 && nonFixed.Count == 0 && repeatedAddresses.Count == 0 &&
                       !groupsTruncated && space != int.MaxValue && groupCount == space;
        var missing = complete || nonFixed.Count > 0 || repeatedAddresses.Count > 0 ||
                      space == int.MaxValue || groupCount >= space
            ? 0
            : space - groupCount;
        var summary = DescribeDecisions(decisions, nonFixed, repeatedAddresses, space, groupCount,
            complete, missing, groupsTruncated);
        return new DecisionSet(decisions, space, complete, missing, summary, nonFixed, repeatedAddresses);
    }

    /// <summary>
    /// Names the branch address with its presence statistics so a non-fixed or loop
    /// guard can be inspected instead of only counted. Lists are capped because the
    /// full set is exported as JSON; the cap keeps the evidence panel readable.
    /// </summary>
    private static EngineEffectConditionDeviation DescribeDeviation(EngineEffectBranchCondition condition,
        int presentPaths, int pathCount, int takenPaths, int notTakenPaths, bool repeated) =>
        new()
        {
            Address = condition.Address,
            Instruction = condition.Instruction,
            Comparison = condition.Comparison,
            PresentPaths = presentPaths,
            PathCount = pathCount,
            TakenPaths = takenPaths,
            NotTakenPaths = notTakenPaths,
            Repeated = repeated
        };

    private const int MaximumDeviationListed = 6;

    private static string DescribeDeviationList(IReadOnlyList<EngineEffectConditionDeviation> deviations)
    {
        var listed = string.Join("、", deviations.Take(MaximumDeviationListed).Select(item =>
            $"0x{item.Address:X8}（出现 {item.PresentPaths}/{item.PathCount} 条路径" +
            (item.Repeated ? $"，判定 {item.TakenPaths + item.NotTakenPaths} 次" : string.Empty) + "）"));
        return deviations.Count > MaximumDeviationListed
            ? listed + $"…（其余 {deviations.Count - MaximumDeviationListed} 个见导出证据）"
            : listed;
    }

    private static string DescribeDecisions(IReadOnlyList<EngineEffectConditionDecision> decisions,
        IReadOnlyList<EngineEffectConditionDeviation> nonFixed,
        IReadOnlyList<EngineEffectConditionDeviation> repeated, int space, int groupCount, bool complete,
        int missing, bool groupsTruncated)
    {
        var nonFixedCount = nonFixed.Count;
        var repeatedCount = repeated.Count;
        if (decisions.Count == 0 && nonFixedCount == 0 && repeatedCount == 0)
            return "无二向分支点：各路径的守卫方向一致，条件组差异来自固定分支点集合";
        var text = new System.Text.StringBuilder();
        if (decisions.Count > 0)
        {
            text.Append($"二向分支点 {decisions.Count} 个：" + string.Join("、", decisions.Select(item =>
                $"0x{item.Address:X8}（跳转 {item.TakenPaths} 条 / 不跳转 {item.NotTakenPaths} 条）")));
        }
        if (nonFixedCount > 0)
        {
            if (text.Length > 0) text.Append("；");
            text.Append($"非固定分支点 {nonFixedCount} 个（只在部分路径出现，无法按 2^k 完整展开）：" +
                        DescribeDeviationList(nonFixed));
        }
        if (repeatedCount > 0)
        {
            if (text.Length > 0) text.Append("；");
            text.Append($"重复求值分支点 {repeatedCount} 个（同一路径内多次判定，循环回边使 2^k 不适用）：" +
                        DescribeDeviationList(repeated));
        }
        if (decisions.Count > 0)
        {
            text.Append($"；组合空间 2^{decisions.Count}={space}，已枚举 {groupCount} 组");
            if (complete)
                text.Append("（完整）");
            else if (missing > 0)
                text.Append($"（仍有 {missing} 种组合未枚举" +
                            (groupsTruncated ? "，条件组达上限" : string.Empty) + "）");
            else if (nonFixedCount == 0 && space != int.MaxValue && groupCount > space)
                text.Append("（已枚举组数超过组合空间，存在额外非固定守卫）");
        }
        return text.ToString();
    }

    private static string ConditionSetKey(IReadOnlyList<EngineEffectBranchCondition> conditions)
        => string.Join("|", conditions
            .Select(condition => $"{condition.Address:X8}:{(condition.Taken ? 1 : 0)}")
            .OrderBy(text => text, StringComparer.Ordinal));

    private static IEnumerable<uint> SharedOppositeAddresses(
        IReadOnlyList<EngineEffectBranchCondition> left, IReadOnlyList<EngineEffectBranchCondition> right)
    {
        var directions = left
            .GroupBy(condition => condition.Address)
            .ToDictionary(group => group.Key, group => group.First().Taken);
        foreach (var condition in right)
            if (directions.TryGetValue(condition.Address, out var taken) && taken != condition.Taken)
                yield return condition.Address;
    }

    private static string DescribeConditions(IReadOnlyList<EngineEffectBranchCondition> conditions)
    {
        if (conditions.Count == 0) return "无判定（顺序执行）";
        return string.Join("；", conditions.Select(DescribeCondition));
    }

    private static string DescribeGroup(GroupDraft draft,
        IReadOnlyList<(int Index, int Other, uint Address)> exclusions)
    {
        var text = $"组 {draft.Index}（{draft.PathCount} 条路径）：{DescribeConditions(draft.Conditions)}";
        if (exclusions.Count == 0) return text;
        var detail = exclusions
            .GroupBy(item => item.Other)
            .OrderBy(group => group.Key)
            .Select(group => $"组 {group.Key} 在 " +
                             string.Join("、", group.Select(item => $"0x{item.Address:X8}")) + " 方向相反")
            .ToArray();
        return text + "；互斥：" + string.Join("；", detail);
    }

    private readonly record struct GroupDraft(int Index,
        IReadOnlyList<EngineEffectBranchCondition> Conditions, int PathCount);

    private sealed record GroupSet(IReadOnlyList<EngineEffectBranchConditionGroup> Groups,
        bool Truncated, int Duplicates, DecisionSet Decisions);

    private sealed record DecisionSet(IReadOnlyList<EngineEffectConditionDecision> Items,
        int Space, bool Complete, int Missing, string Summary,
        IReadOnlyList<EngineEffectConditionDeviation> NonFixed,
        IReadOnlyList<EngineEffectConditionDeviation> Repeated);

    private static bool SamePath(IReadOnlyList<Block> left, IReadOnlyList<Block> right)
        => left.Count == right.Count && left.Zip(right).All(pair => pair.First.Start == pair.Second.Start);

    private static string DescribeMultiPath(MergedPaths merged,
        IReadOnlyList<EngineEffectBranchCondition> shortest)
    {
        if (merged.PathCount <= 1)
            return merged.Truncated
                ? "路径枚举达上限，仅确认 1 条入口→调用点路径"
                : "仅 1 条入口→调用点路径，无额外分支可合并";
        var shortestKeys = shortest
            .Select(condition => new ConditionKey(condition.Address, condition.Taken)).ToHashSet();
        var outside = merged.Optional.Count(condition =>
            !shortestKeys.Contains(new ConditionKey(condition.Address, condition.Taken)));
        var text = $"共 {merged.PathCount} 条入口→调用点路径" +
                   (merged.Truncated ? "（枚举受上限截断，可能仍有未枚举路径）" : string.Empty) +
                   $"；公共守卫 {merged.Common.Count} 个，路径特有守卫 {merged.Optional.Count} 个" +
                   (outside > 0 ? $"，其中 {outside} 个不在最短路径上" : string.Empty);
        if (merged.Groups.Count > 0)
            text += $"；条件组 {merged.Groups.Count} 组" +
                    (merged.GroupDuplicates > 0 ? $"（合并 {merged.GroupDuplicates} 条等价路径）" : string.Empty) +
                    (merged.GroupsTruncated ? "（条件组达上限，仍有未分组路径）" : string.Empty);
        if (merged.Conflicts > 0)
            text += $"；互斥判定 {merged.Conflicts} 处（同一跳转在不同路径上方向相反）";
        return text;
    }

    private readonly record struct ConditionKey(uint Address, bool Taken);

    private sealed record PathSet(IReadOnlyList<List<Block>> Paths, bool Truncated);

    private sealed record MergedPaths(int PathCount, bool Truncated,
        IReadOnlyList<EngineEffectBranchCondition> Common,
        IReadOnlyList<EngineEffectBranchCondition> Optional, int Conflicts,
        IReadOnlyList<EngineEffectBranchConditionGroup> Groups, bool GroupsTruncated,
        int GroupDuplicates, DecisionSet Decisions);

    /// <summary>
    /// Recovers the guards in the function that jumps into an out-of-block call site.
    /// The chain runs from that function's entry to the jumping block and ends with
    /// the conditional jump itself (taken), so a call entered from a dispatcher keeps
    /// the dispatcher's cmp/test decisions instead of stopping at "unreachable".
    /// </summary>
    private static UpstreamPath? ResolveUpstream(EngineEffectBinaryImage image, EngineEffectJump jump, uint owner)
    {
        if (image.SectionOf(owner) == null) return null;
        var graph = Build(image, owner);
        if (graph.Blocks.Count == 0) return null;
        var block = graph.InstructionBlocks.GetValueOrDefault(jump.Address);
        if (block == null)
            return new UpstreamPath(owner, false, [], false, $"跳转指令 0x{jump.Address:X8} 不在函数基本块内",
                0, false, [], [], string.Empty, [], false, new DecisionSet([], 0, false, 0, string.Empty, [], []));
        var path = FindPath(graph, owner, block.Start);
        if (path == null)
            return new UpstreamPath(owner, false, [], false, $"跳转块 0x{block.Start:X8} 从函数入口不可达",
                0, false, [], [], string.Empty, [], false, new DecisionSet([], 0, false, 0, string.Empty, [], []));
        var allConditions = CollectConditions(graph, path);
        if (block.Conditional) allConditions.Add(MakeCondition(block, true));
        var truncated = allConditions.Count > MaximumConditions;
        var conditions = truncated ? allConditions[^MaximumConditions..] : allConditions;
        var pathSet = EnumeratePaths(graph, owner, block.Start);
        var paths = new List<List<Block>> { path };
        var pathsTruncated = pathSet.Truncated;
        foreach (var candidate in pathSet.Paths)
        {
            if (paths.Any(existing => SamePath(existing, candidate))) continue;
            if (paths.Count >= MaximumPaths)
            {
                pathsTruncated = true;
                break;
            }
            paths.Add(candidate);
        }
        var pathConditions = paths.Select(item =>
        {
            var list = (IReadOnlyList<EngineEffectBranchCondition>)CollectConditions(graph, item);
            return block.Conditional ? list.Append(MakeCondition(block, true)).ToArray() : list;
        }).ToList();
        var merged = MergeConditions(pathConditions) with
        {
            Truncated = truncated || pathsTruncated
        };
        var groups = BuildGroups(pathConditions);
        return new UpstreamPath(owner, true, conditions, truncated, Describe(conditions, truncated),
            merged.PathCount, merged.Truncated, merged.Common, merged.Optional,
            DescribeMultiPath(merged with { Groups = groups.Groups, GroupsTruncated = groups.Truncated,
                GroupDuplicates = groups.Duplicates, Decisions = groups.Decisions }, allConditions),
            groups.Groups, groups.Truncated, groups.Decisions);
    }

    private static string DescribeInbound(uint callAddress, EngineEffectJump? inbound, uint? function,
        bool? reachable, UpstreamPath? upstream)
    {
        if (inbound is not { } jump)
            return "调用点不在函数基本块内，且没有指向该地址或其前 16 字节的直接跳转" +
                   "（ret 之后的孤立代码片段，疑似死代码），无法静态回溯判定条件";
        var text = $"调用点不在函数基本块内；经 0x{jump.Address:X8} {jump.Kind} 跳转进入 0x{jump.Target:X8}";
        if (function is { } owner)
        {
            text += reachable == false
                ? $"（跳转发起函数 0x{owner:X8}，但跳转块从函数入口不可达，疑似死代码）"
                : $"（跳转发起函数 0x{owner:X8}）";
            if (upstream is { Reachable: true } chain)
            {
                text += $"；上游条件（函数 0x{owner:X8}）：{chain.Summary}";
                if (chain.PathCount > 1)
                    text += $"；上游路径合并：{chain.MultiPathSummary}";
            }
            else if (upstream is { } blocked)
                text += $"；上游条件不可回溯（{blocked.Summary}）";
        }
        else
        {
            text += "（跳转发起函数未知）";
        }
        if (jump.Target != callAddress)
            text += $"；跳转目标 0x{jump.Target:X8} 线性直达调用点，无额外判定";
        return text;
    }

    private sealed record UpstreamPath(uint Function, bool Reachable,
        IReadOnlyList<EngineEffectBranchCondition> Conditions, bool Truncated, string Summary,
        int PathCount, bool PathsTruncated,
        IReadOnlyList<EngineEffectBranchCondition> Common,
        IReadOnlyList<EngineEffectBranchCondition> Optional, string MultiPathSummary,
        IReadOnlyList<EngineEffectBranchConditionGroup> Groups, bool GroupsTruncated,
        DecisionSet Decisions);

    /// <summary>
    /// Finds the direct jump that enters an out-of-block call site. The index only
    /// keeps jumps whose opcode is an instruction boundary, so an immediate byte
    /// that happens to look like a jump cannot fake an inbound edge. A jump may
    /// also land a few bytes early, provided a linear decode runs exactly into
    /// the call address.
    /// </summary>
    private static EngineEffectJump? FindInboundJump(EngineEffectBinaryImage image, EngineEffectJumpIndex? index,
        uint callAddress)
    {
        if (index == null) return null;
        for (var delta = 0; delta <= 16; delta++)
        {
            var target = delta == 0 ? callAddress : callAddress - (uint)delta;
            var sources = index.SourcesOf(target);
            if (sources.Count == 0) continue;
            if (delta > 0 && !LinearEndsAt(image, target, callAddress)) continue;
            return sources.Where(jump => jump.Address < callAddress)
                       .OrderByDescending(jump => jump.Address).FirstOrDefault() ??
                   sources[0];
        }
        return null;
    }

    private static bool LinearEndsAt(EngineEffectBinaryImage image, uint start, uint end)
    {
        if (end <= start) return false;
        var decoded = EngineEffectDecoder.DecodeForward(image, start, checked((int)(end - start) + 1));
        return decoded.Any(item => item.Address == end);
    }

    private static List<Block>? FindPath(Graph graph, uint entry, uint target)
    {
        if (!graph.Blocks.ContainsKey(entry) || !graph.Blocks.ContainsKey(target)) return null;
        if (entry == target) return [graph.Blocks[entry]];
        if (!graph.Parents.TryGetValue(entry, out var parents))
        {
            parents = new Dictionary<uint, uint> { [entry] = entry };
            var pending = new Queue<uint>();
            pending.Enqueue(entry);
            while (pending.Count > 0)
            {
                graph.Context.CheckCancellation();
                var current = pending.Dequeue();
                foreach (var edge in graph.Successors.GetValueOrDefault(current) ?? [])
                {
                    if (parents.ContainsKey(edge.To) || !graph.Blocks.ContainsKey(edge.To)) continue;
                    parents[edge.To] = current;
                    pending.Enqueue(edge.To);
                }
            }
            graph.Parents.Add(entry, parents);
        }
        if (!parents.ContainsKey(target)) return null;
        var path = new List<Block>();
        var address = target;
        while (true)
        {
            path.Add(graph.Blocks[address]);
            var parent = parents[address];
            if (parent == address) break;
            address = parent;
        }
        path.Reverse();
        return path;
    }

    private static Graph Build(EngineEffectBinaryImage image, uint entry)
        => image.Context.GetOrBuild(EngineEffectAnalysisKind.ControlFlowGraph, entry, MaximumInstructions,
            () => BuildCore(image, entry));

    private static Graph BuildCore(EngineEffectBinaryImage image, uint entry)
    {
        var blocks = new Dictionary<uint, Block>();
        var pending = new Stack<uint>();
        var planned = new HashSet<uint> { entry };
        pending.Push(entry);
        var budget = MaximumInstructions;
        while (pending.Count > 0 && blocks.Count < MaximumBlocks && budget > 0)
        {
            var start = pending.Pop();
            if (blocks.ContainsKey(start) || image.SectionOf(start) == null) continue;
            var block = new Block { Start = start };
            var address = start;
            var terminated = false;
            while (!terminated && budget-- > 0)
            {
                var decoded = EngineEffectDecoder.DecodeOne(image, address);
                if (decoded == null || decoded.Address != address) break;
                block.Instructions.Add(decoded);
                var next = address + (uint)decoded.Length;
                block.End = next;
                var instruction = decoded.Instruction;
                switch (instruction.FlowControl)
                {
                    case FlowControl.ConditionalBranch:
                        block.Conditional = true;
                        block.BranchTarget = BranchTarget(instruction);
                        block.FallThrough = next;
                        terminated = true;
                        break;
                    case FlowControl.UnconditionalBranch:
                        block.BranchTarget = BranchTarget(instruction);
                        if (block.BranchTarget == null) block.Indirect = true;
                        terminated = true;
                        break;
                    case FlowControl.IndirectBranch:
                        block.Indirect = true;
                        ReadSwitchTargets(image, instruction, block.SwitchTargets);
                        terminated = true;
                        break;
                    case FlowControl.Return:
                    case FlowControl.Interrupt:
                    case FlowControl.Exception:
                        terminated = true;
                        break;
                    default:
                        address = next;
                        // A branch target that is only queued is still a block start:
                        // stopping here keeps the decoder from emitting the target
                        // instruction twice in two overlapping blocks.
                        if (blocks.ContainsKey(next) || planned.Contains(next))
                        {
                            block.FallThrough = next;
                            terminated = true;
                        }
                        break;
                }
            }
            if (block.Instructions.Count == 0) continue;
            if (!terminated && block.FallThrough == null && !block.Conditional && !block.Indirect &&
                block.BranchTarget == null)
                block.FallThrough = block.End;
            blocks[start] = block;
            if (block.Conditional)
            {
                if (block.BranchTarget is { } branch)
                {
                    pending.Push(branch);
                    planned.Add(branch);
                }
                if (block.FallThrough is { } fall)
                {
                    pending.Push(fall);
                    planned.Add(fall);
                }
            }
            else if (block.Indirect)
            {
                foreach (var switchTarget in block.SwitchTargets)
                {
                    pending.Push(switchTarget);
                    planned.Add(switchTarget);
                }
            }
            else if (block.BranchTarget is { } jump)
            {
                pending.Push(jump);
                planned.Add(jump);
            }
            else if (block.FallThrough is { } continuation)
            {
                pending.Push(continuation);
                planned.Add(continuation);
            }
        }

        // Provenance needs the complete block map: a register tested in one block
        // may be defined in a predecessor block, so conditions are resolved after
        // all blocks exist instead of while the first block is still being decoded.
        var predecessorsByStart = new Dictionary<uint, List<Block>>();
        var effects = new CallEffects(image);
        foreach (var block in blocks.Values)
        {
            void Link(uint? target)
            {
                if (target is not { } address || !blocks.ContainsKey(address)) return;
                if (!predecessorsByStart.TryGetValue(address, out var list))
                    predecessorsByStart[address] = list = [];
                if (!list.Contains(block)) list.Add(block);
            }

            if (block.Conditional)
            {
                Link(block.BranchTarget);
                Link(block.FallThrough);
            }
            else if (block.Indirect)
            {
                foreach (var switchTarget in block.SwitchTargets) Link(switchTarget);
            }
            else
            {
                Link(block.BranchTarget);
                Link(block.FallThrough);
            }
        }
        foreach (var block in blocks.Values.Where(block => block.Conditional))
        {
            (block.Comparison, block.Operand) = DescribeComparison(block, predecessorsByStart, effects);
            if (block.Comparison == null)
                (block.Comparison, block.Operand) = DescribeInheritedComparison(block, predecessorsByStart,
                    effects);
        }

        var edges = new List<Edge>();
        var successors = new Dictionary<uint, List<Edge>>();
        var predecessors = new Dictionary<uint, List<Edge>>();
        foreach (var block in blocks.Values)
        {
            void Add(uint to, EngineEffectBranchCondition? condition)
            {
                if (!blocks.ContainsKey(to)) return;
                var edge = new Edge(block.Start, to, condition);
                edges.Add(edge);
                if (!successors.TryGetValue(block.Start, out var list))
                    successors[block.Start] = list = new List<Edge>();
                list.Add(edge);
                if (!predecessors.TryGetValue(to, out var incoming))
                    predecessors[to] = incoming = new List<Edge>();
                incoming.Add(edge);
            }

            if (block.Conditional)
            {
                if (block.BranchTarget is { } taken)
                    Add(taken, MakeCondition(block, true));
                if (block.FallThrough is { } fall)
                    Add(fall, MakeCondition(block, false));
            }
            else if (block.Indirect)
            {
                foreach (var switchTarget in block.SwitchTargets) Add(switchTarget, null);
            }
            else if (block.BranchTarget is { } jump)
            {
                Add(jump, null);
            }
            else if (block.FallThrough is { } fall)
            {
                Add(fall, null);
            }
        }
        return new Graph(blocks, edges, successors, predecessors, image.Context);
    }

    private static EngineEffectBranchCondition MakeCondition(Block block, bool taken)
    {
        var terminator = block.Instructions[^1];
        return new EngineEffectBranchCondition
        {
            Address = terminator.Address,
            Instruction = terminator.Text,
            Comparison = block.Comparison,
            Operand = block.Operand.Operand,
            OperandKind = block.Operand.Kind,
            OperandOrigin = block.Operand.Origin,
            Taken = taken,
            Target = taken ? block.BranchTarget ?? 0 : block.FallThrough ?? 0
        };
    }

    /// <summary>
    /// Finds the cmp/test that feeds the branch and explains where the tested
    /// operand comes from. The operand is resolved inside the same basic block:
    /// stack slots, globals, object fields, constants, call return values and
    /// simple register chains (mov/movzx/movsx/lea plus zeroing and arithmetic)
    /// are folded into one provenance string, so <c>cmp al,0FFh</c> reads as
    /// "al ← movzx eax,byte [ecx+4] ← 对象字段 ← 栈参数 1".
    /// </summary>
    private static (string? Comparison, OperandInfo Operand) DescribeComparison(Block block,
        Dictionary<uint, List<Block>> predecessors, CallEffects effects)
    {
        var instructions = block.Instructions;
        var context = new BlockContext(block.Start, predecessors, effects);
        for (var index = instructions.Count - 2; index >= 0; index--)
        {
            var instruction = instructions[index].Instruction;
            if (instruction.Mnemonic is Mnemonic.Cmp or Mnemonic.Test)
                return (instructions[index].Text, DescribeOperand(instructions, index, context));
            if (instruction.FlowControl != FlowControl.Next) break;
        }
        return (null, OperandInfo.Empty);
    }

    /// <summary>
    /// Handles a block that starts at a shared conditional jump: the flags were set by
    /// the block's predecessors, so the comparison is inherited when every predecessor
    /// agrees on the same cmp/test. A predecessor that ends in a branch contributes its
    /// own comparison; a predecessor that falls through must end with the cmp/test
    /// itself, otherwise the flags are unknown and the condition stays empty.
    /// </summary>
    private static (string? Comparison, OperandInfo Operand) DescribeInheritedComparison(Block block,
        Dictionary<uint, List<Block>> predecessors, CallEffects effects)
    {
        var candidates = new List<(string Comparison, OperandInfo Operand)>();
        foreach (var predecessor in PredecessorsOf(block.Start, predecessors))
        {
            var instructions = predecessor.Instructions;
            if (instructions.Count == 0) continue;
            if (predecessor.Conditional)
            {
                if (predecessor.Comparison is { Length: > 0 } comparison)
                    candidates.Add((comparison, predecessor.Operand));
                continue;
            }
            var last = instructions[^1].Instruction;
            if (last.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test)) continue;
            var operand = DescribeOperand(instructions, instructions.Count - 1,
                new BlockContext(predecessor.Start, predecessors, effects));
            candidates.Add((instructions[^1].Text, operand));
        }
        if (candidates.Count == 0) return (null, OperandInfo.Empty);
        var distinct = candidates.Select(item => (item.Comparison, item.Operand.Operand,
            item.Operand.Kind, item.Operand.Origin)).Distinct().ToArray();
        if (distinct.Length != 1) return (null, OperandInfo.Empty);
        return (candidates[0].Comparison, candidates[0].Operand);
    }

    /// <summary>
    /// Resolves a register that has no definition in its own block by walking the CFG
    /// predecessors. Up to four incoming edges are traced; a single shared definition
    /// is reported with its block address, conflicting definitions are reported as
    /// "多上游块定义不一致" instead of picking one silently.
    /// </summary>
    private static OperandInfo? ResolveAcrossBlocks(BlockContext context, Register register, int depth)
    {
        var full = FullRegister(register);
        if (full == Register.None) return null;
        var traces = new List<(uint Address, OperandInfo Info)>();
        foreach (var predecessor in PredecessorsOf(context.Start, context.Predecessors).Take(4))
        {
            var visited = new HashSet<uint> { context.Start };
            var trace = TraceRegister(predecessor, full, register, context.Predecessors, context.Calls,
                visited, depth);
            if (trace != null) traces.Add((predecessor.Start, trace.Value));
        }
        if (traces.Count == 0) return null;
        var distinct = traces.Select(item => item.Info).Distinct().ToArray();
        if (distinct.Length == 1)
            return new OperandInfo(RegisterName(register), distinct[0].Kind,
                $"上游块 0x{traces[0].Address:X8}：{Slot(distinct[0])}");
        return new OperandInfo(RegisterName(register), "未定",
            "多上游块定义不一致：" + string.Join("；", traces.OrderBy(item => item.Address)
                .Select(item => $"0x{item.Address:X8}：{Slot(item.Info)}")));
    }

    private static IReadOnlyList<Block> PredecessorsOf(uint blockStart,
        Dictionary<uint, List<Block>> predecessors)
        => predecessors.TryGetValue(blockStart, out var list)
            ? list.OrderBy(block => block.Start).ToArray()
            : [];

    private static string Slot(OperandInfo info) => info.Origin.Length > 0 ? info.Origin : info.Kind;

    /// <summary>
    /// Looks backwards through one block for a write to the register and, when the
    /// block does not define it, continues into the block's predecessors (depth 3).
    /// </summary>
    private static OperandInfo? TraceRegister(Block block, Register full, Register display,
        Dictionary<uint, List<Block>> predecessors, CallEffects calls, HashSet<uint> visited, int depth)
    {
        if (depth > 3 || !visited.Add(block.Start)) return null;
        var instructions = block.Instructions;
        var context = new BlockContext(block.Start, predecessors, calls);
        for (var index = instructions.Count - 1; index >= 0; index--)
        {
            var decoded = instructions[index];
            var instruction = decoded.Instruction;
            if (instruction.Mnemonic == Mnemonic.Call)
            {
                var effect = DescribeCallEffect(instruction, decoded.Text, full, display, context);
                if (effect != null) return effect;
                continue;
            }
            if (!WritesRegister(instruction, full)) continue;
            return DescribeWrite(instructions, index, full, depth + 1, context);
        }
        foreach (var predecessor in PredecessorsOf(block.Start, predecessors).Take(4))
        {
            var trace = TraceRegister(predecessor, full, display, predecessors, calls, visited, depth + 1);
            if (trace != null) return trace;
        }
        return null;
    }

    /// <summary>
    /// Decides what a call does to one register. A direct call into the scanned image
    /// is resolved by reading the callee: a register the callee writes is treated as
    /// the call's return register, a register it never writes keeps its older value
    /// and the backward scan continues. Indirect or external calls fall back to the
    /// usual convention (EAX return value, ECX/EDX clobbered).
    /// </summary>
    private static OperandInfo? DescribeCallEffect(Instruction instruction, string text, Register full,
        Register display, BlockContext? context)
    {
        if (full is not (Register.EAX or Register.ECX or Register.EDX)) return null;
        var name = RegisterName(display);
        if (context != null && IsDirectCall(instruction, out var callee) &&
            context.Calls.VolatileWrites(callee) is { } writes)
        {
            if ((writes.Mask & CallEffects.BitOf(full)) == 0) return null;
            if (full == Register.EAX)
                return new OperandInfo(name, "调用返回值", $"{text}（返回值）");
            // ECX/EDX 只在被调函数确有写入时才能说“写回”；含嵌套调用时只能保守，
            // 这时如实标为未定，避免把“可能被写”说成确定语义。
            return writes.Exact
                ? new OperandInfo(name, "调用返回值", $"{text}（调用写回 {RegisterName(full)}）")
                : new OperandInfo(name, "未定", $"{text} 之后 {name} 可能被写（被调函数含嵌套调用）");
        }
        if (full == Register.EAX)
            return new OperandInfo(name, "调用返回值", $"{text}（返回值）");
        return new OperandInfo(name, "未定", $"{text} 之后 {name} 被调用破坏");
    }

    private static bool IsDirectCall(Instruction instruction, out uint target)
    {
        target = 0;
        if (instruction.Mnemonic != Mnemonic.Call) return false;
        if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            return false;
        target = unchecked((uint)instruction.NearBranchTarget);
        return true;
    }

    private static OperandInfo DescribeOperand(IReadOnlyList<DecodedInstruction> instructions, int compareIndex,
        BlockContext? context)
    {
        var instruction = instructions[compareIndex].Instruction;
        var count = Math.Min(instruction.OpCount, 2);
        for (var op = 0; op < count; op++)
        {
            var kind = instruction.GetOpKind(op);
            if (kind == OpKind.Register)
            {
                var register = instruction.GetOpRegister(op);
                return DescribeRegister(instructions, compareIndex, register, 0, context) with
                {
                    Operand = RegisterName(register)
                };
            }
            if (kind != OpKind.Memory) continue;
            var (memoryKind, memoryDetail) = DescribeMemory(instructions, compareIndex, instruction, 0, context);
            return new OperandInfo(MemoryExpression(instruction), memoryKind, memoryDetail);
        }
        return OperandInfo.Empty;
    }

    private static OperandInfo DescribeRegister(IReadOnlyList<DecodedInstruction> instructions, int fromIndex,
        Register register, int depth, BlockContext? context)
    {
        var name = RegisterName(register);
        var full = FullRegister(register);
        if (full == Register.None) return new OperandInfo(name, "未定", string.Empty);
        for (var index = fromIndex - 1; index >= 0; index--)
        {
            var decoded = instructions[index];
            var instruction = decoded.Instruction;
            if (instruction.Mnemonic == Mnemonic.Call)
            {
                // EAX carries the return value; ECX/EDX are volatile. The callee is
                // scanned for writes so helpers that return a pointer in ECX (common
                // in this engine) are not misreported as "clobbered". Callee-saved
                // registers (EBX/ESI/EDI/EBP) keep their previous definition.
                var effect = DescribeCallEffect(instruction, decoded.Text, full, register, context);
                if (effect is { } callInfo) return callInfo;
                continue;
            }
            if (!WritesRegister(instruction, full)) continue;
            return DescribeWrite(instructions, index, full, depth, context);
        }
        if (context != null && depth <= 3 && ResolveAcrossBlocks(context, register, depth) is { } resolved)
            return resolved with { Operand = name };
        return new OperandInfo(name, "未定", $"块内未见写入 {name}（可能来自上游块或函数入口）");
    }

    private static OperandInfo DescribeWrite(IReadOnlyList<DecodedInstruction> instructions, int index,
        Register full, int depth, BlockContext? context)
    {
        var decoded = instructions[index];
        var instruction = decoded.Instruction;
        var name = RegisterName(full);
        if (depth > 3) return new OperandInfo(name, "寄存器计算", decoded.Text);
        if (instruction.Mnemonic is Mnemonic.Xor or Mnemonic.Sub && instruction.OpCount >= 2 &&
            instruction.GetOpKind(1) == OpKind.Register &&
            FullRegister(instruction.GetOpRegister(1)) == full)
            return new OperandInfo(name, "常量", $"{decoded.Text}（常量 0）");
        if (instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd)
        {
            if (instruction.OpCount < 2) return new OperandInfo(name, "寄存器计算", decoded.Text);
            var sourceKind = instruction.GetOpKind(1);
            if (IsImmediate(sourceKind))
                return new OperandInfo(name, "常量", $"{decoded.Text}（常量 0x{ImmediateValue(instruction):X}）");
            if (sourceKind == OpKind.Register)
            {
                var source = instruction.GetOpRegister(1);
                var chain = DescribeRegister(instructions, index, source, depth + 1, context);
                return chain with
                {
                    Operand = name,
                    Origin = $"{decoded.Text} ← {chain.Operand}" +
                             (chain.Origin.Length > 0 ? $" ← {chain.Origin}" : string.Empty)
                };
            }
            if (sourceKind == OpKind.Memory)
            {
                var (memoryKind, memoryDetail) = DescribeMemory(instructions, index, instruction, depth + 1,
                    context);
                return new OperandInfo(name, memoryKind, $"{decoded.Text} ← {memoryDetail}");
            }
        }
        if (instruction.Mnemonic == Mnemonic.Lea && instruction.OpCount >= 2 &&
            instruction.GetOpKind(1) == OpKind.Memory)
        {
            var (_, addressDetail) = DescribeMemory(instructions, index, instruction, depth + 1, context);
            return new OperandInfo(name, "寄存器计算", $"{decoded.Text} ← 地址 {addressDetail}");
        }
        if (instruction.Mnemonic == Mnemonic.Pop)
            return new OperandInfo(name, "栈局部", $"{decoded.Text}（栈弹出值）");
        if (instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub or Mnemonic.And or Mnemonic.Or or Mnemonic.Shl
            or Mnemonic.Shr or Mnemonic.Sar or Mnemonic.Imul or Mnemonic.Neg or Mnemonic.Inc or Mnemonic.Dec
            or Mnemonic.Xchg)
            return new OperandInfo(name, "寄存器计算", decoded.Text);
        return new OperandInfo(name, "寄存器计算", decoded.Text);
    }

    /// <summary>Classifies a memory operand and, for object/array fields, folds the base register's origin.</summary>
    private static (string Kind, string Detail) DescribeMemory(IReadOnlyList<DecodedInstruction> instructions,
        int index, Instruction instruction, int depth, BlockContext? context)
    {
        var expression = MemoryExpression(instruction);
        var baseRegister = instruction.MemoryBase;
        var indexRegister = instruction.MemoryIndex;
        var displacement = Displacement(instruction);
        if (baseRegister is Register.EBP or Register.RBP && indexRegister == Register.None)
        {
            if (displacement >= 8 && (displacement - 8) % 4 == 0)
                return ("栈参数", $"栈参数 {(displacement - 8) / 4 + 1} {expression}");
            if (displacement < 0)
                return ("栈局部", $"栈局部 {displacement} {expression}");
        }
        if (baseRegister == Register.None && indexRegister == Register.None)
            return ("全局变量", $"全局变量 0x{unchecked((uint)displacement):X8} {expression}");
        if (baseRegister is Register.ESP or Register.RSP)
            return ("栈局部", $"栈顶相对 {expression}");
        var kind = indexRegister == Register.None ? "对象字段" : "对象数组";
        if (baseRegister == Register.None || depth > 3)
            return (kind, $"{kind} {expression}");
        var origin = DescribeRegister(instructions, index, baseRegister, depth + 1, context);
        var detail = $"{kind} {expression} ← {RegisterName(baseRegister)}";
        if (origin.Origin.Length > 0) detail += $" ← {origin.Origin}";
        return (kind, detail);
    }

    private static string MemoryExpression(Instruction instruction)
    {
        var text = string.Empty;
        var baseName = RegisterName(instruction.MemoryBase);
        if (baseName.Length > 0) text = baseName;
        if (instruction.MemoryIndex != Register.None)
        {
            var indexName = RegisterName(instruction.MemoryIndex);
            if (instruction.MemoryIndexScale != 1) indexName += "*" + instruction.MemoryIndexScale;
            text += (text.Length > 0 ? "+" : string.Empty) + indexName;
        }
        var displacement = Displacement(instruction);
        if (displacement < 0)
            text += "-" + DisplacementText(-displacement);
        else if (displacement > 0 || text.Length == 0)
            text += (text.Length > 0 ? "+" : string.Empty) + DisplacementText(displacement);
        return $"[{text}]";
    }

    /// <summary>Iced-style displacement text: 4, 0Ch, 10h, 500C48h.</summary>
    private static string DisplacementText(int displacement)
    {
        if (displacement < 0) return "-" + DisplacementText(-displacement);
        var hex = displacement.ToString("X");
        if (hex.Length == 1 && displacement <= 9) return hex;
        if (char.IsLetter(hex[0])) hex = "0" + hex;
        return hex + "h";
    }

    private static int Displacement(Instruction instruction)
        => unchecked((int)(uint)instruction.MemoryDisplacement64);

    private static string RegisterName(Register register)
        => register == Register.None ? string.Empty : register.ToString().ToLowerInvariant();

    private static Register FullRegister(Register register)
        => register == Register.None ? Register.None : RegisterExtensions.GetFullRegister32(register);

    private static bool IsImmediate(OpKind kind)
        => kind is OpKind.Immediate8 or OpKind.Immediate8_2nd or OpKind.Immediate16 or OpKind.Immediate32
            or OpKind.Immediate64 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
            or OpKind.Immediate32to64;

    private static uint ImmediateValue(Instruction instruction)
        => instruction.OpCount >= 2 ? unchecked((uint)instruction.GetImmediate(1)) : 0;

    private static bool WritesRegister(Instruction instruction, Register full)
    {
        if (full == Register.None || instruction.OpCount == 0) return false;
        // Implicit writes of the one-operand forms: mul/div write EDX:EAX, cdq/cwd
        // write EDX/EAX and string loads write EAX.
        if (instruction.Mnemonic is Mnemonic.Mul or Mnemonic.Div or Mnemonic.Idiv or Mnemonic.Cdq or Mnemonic.Cwd
            or Mnemonic.Lodsb or Mnemonic.Lodsw or Mnemonic.Lodsd or Mnemonic.Popad)
        {
            if (full == Register.EAX) return true;
            if (full == Register.EDX && instruction.Mnemonic is not (Mnemonic.Lodsb or Mnemonic.Lodsw
                    or Mnemonic.Lodsd or Mnemonic.Popad)) return true;
        }
        if (instruction.Mnemonic == Mnemonic.Imul && instruction.OpCount == 1 &&
            full is Register.EAX or Register.EDX)
            return true;
        if (instruction.GetOpKind(0) != OpKind.Register) return false;
        if (FullRegister(instruction.GetOpRegister(0)) != full) return false;
        return instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd
            or Mnemonic.Lea or Mnemonic.Xor or Mnemonic.Sub or Mnemonic.Add or Mnemonic.And or Mnemonic.Or
            or Mnemonic.Shl or Mnemonic.Shr or Mnemonic.Sar or Mnemonic.Imul or Mnemonic.Neg or Mnemonic.Inc
            or Mnemonic.Dec or Mnemonic.Pop or Mnemonic.Xchg;
    }

    /// <summary>Basic block plus its CFG predecessors, so provenance can continue upstream.</summary>
    private sealed record BlockContext(uint Start, Dictionary<uint, List<Block>> Predecessors, CallEffects Calls);

    /// <summary>
    /// Summarises which volatile registers a direct callee writes, so provenance can
    /// tell "ECX holds the helper's pointer result" from "ECX was destroyed". The
    /// callee body is walked along reachable instructions; a callee that itself calls
    /// other code is treated conservatively as clobbering all volatiles.
    /// </summary>
    private sealed class CallEffects
    {
        private const int MaximumCalleeInstructions = 4000;
        private readonly EngineEffectBinaryImage _image;
        private readonly Dictionary<uint, CallOutcome> _cache = [];

        public CallEffects(EngineEffectBinaryImage image) => _image = image;

        public static int BitOf(Register register) => register switch
        {
            Register.EAX => 1,
            Register.ECX => 2,
            Register.EDX => 4,
            _ => 0
        };

        /// <summary>被调函数对易失寄存器的写入摘要；Exact 为假表示只做过保守估计。</summary>
        public readonly record struct CallOutcome(int Mask, bool Exact);

        /// <summary>Null when the target is outside the image, so callers keep the convention fallback.</summary>
        public CallOutcome? VolatileWrites(uint target)
        {
            if (_image.SectionOf(target) is not { IsExecutable: true }) return null;
            if (_cache.TryGetValue(target, out var cached)) return cached;
            var outcome = _image.Context.GetOrBuild(EngineEffectAnalysisKind.CallEffects, target,
                MaximumCalleeInstructions, () => Scan(target));
            _cache[target] = outcome;
            return outcome;
        }

        private CallOutcome Scan(uint target)
        {
            var reached = EngineEffectFunctionAnalyzer.ReachableAddresses(_image, target,
                MaximumCalleeInstructions);
            if (reached == null || reached.Count >= MaximumCalleeInstructions)
                return new CallOutcome(1 | 2 | 4, false);
            var mask = 0;
            var exact = true;
            foreach (var address in reached)
            {
                var decoded = EngineEffectDecoder.DecodeOne(_image, address);
                if (decoded == null || decoded.Address != address) continue;
                var instruction = decoded.Instruction;
                if (instruction.Mnemonic == Mnemonic.Call)
                {
                    mask |= 1 | 2 | 4;
                    exact = false;
                    continue;
                }
                if (WritesRegister(instruction, Register.EAX)) mask |= 1;
                if (WritesRegister(instruction, Register.ECX)) mask |= 2;
                if (WritesRegister(instruction, Register.EDX)) mask |= 4;
            }
            return new CallOutcome(mask, exact);
        }
    }

    private readonly record struct OperandInfo(string Operand, string Kind, string Origin)
    {
        public static OperandInfo Empty => new(string.Empty, string.Empty, string.Empty);
    }

    private static void ReadSwitchTargets(EngineEffectBinaryImage image, Instruction instruction, List<uint> targets)
    {
        if (instruction.Op0Kind != OpKind.Memory) return;
        if (instruction.MemoryBase != Register.None || instruction.MemoryIndex == Register.None) return;
        var table = unchecked((uint)instruction.MemoryDisplacement64);
        if (table == 0 || image.VirtualAddressToFileOffset(table) is not { } offset) return;
        var bytes = image.Bytes;
        for (var index = 0; index < MaximumSwitchEntries; index++)
        {
            var entryOffset = offset + index * 4L;
            if (entryOffset + 4 > bytes.LongLength) break;
            var target = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(checked((int)entryOffset), 4));
            if (image.SectionOf(target) is not { IsExecutable: true }) break;
            targets.Add(target);
        }
    }

    private static uint? BranchTarget(Instruction instruction)
        => instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
            ? unchecked((uint)instruction.NearBranchTarget)
            : null;

    private sealed class Block
    {
        public uint Start { get; init; }
        public uint End { get; set; }
        public List<DecodedInstruction> Instructions { get; } = [];
        public bool Conditional { get; set; }
        public bool Indirect { get; set; }
        public uint? BranchTarget { get; set; }
        public uint? FallThrough { get; set; }
        public string? Comparison { get; set; }
        public OperandInfo Operand { get; set; } = OperandInfo.Empty;
        public List<uint> SwitchTargets { get; } = [];
    }

    private sealed record Edge(uint From, uint To, EngineEffectBranchCondition? Condition);

    private sealed class Graph
    {
        public Dictionary<uint, Block> Blocks { get; }
        public Dictionary<uint, List<Edge>> Successors { get; }
        public Dictionary<uint, List<Edge>> Predecessors { get; }
        public EngineEffectScanContext Context { get; }
        public Dictionary<uint, Block> InstructionBlocks { get; } = [];
        public Dictionary<(uint From, uint To), Edge> EdgeByPair { get; } = [];
        public Dictionary<uint, Dictionary<uint, uint>> Parents { get; } = [];
        public Dictionary<(uint Entry, uint Target), PathSet> Paths { get; } = [];

        public Graph(Dictionary<uint, Block> blocks, List<Edge> edges,
            Dictionary<uint, List<Edge>> successors, Dictionary<uint, List<Edge>> predecessors,
            EngineEffectScanContext context)
        {
            Blocks = blocks; Successors = successors; Predecessors = predecessors; Context = context;
            // Preserve FirstOrDefault's first block/edge when overlapping blocks or duplicate edges exist.
            foreach (var block in blocks.Values)
                foreach (var instruction in block.Instructions) InstructionBlocks.TryAdd(instruction.Address, block);
            foreach (var edge in edges) EdgeByPair.TryAdd((edge.From, edge.To), edge);
        }
    }
}
