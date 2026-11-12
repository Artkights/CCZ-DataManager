using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Reads effect decisions from the current project's Ekd5.exe. The four stack
/// arguments of the shared 6.x effect entry are recovered from the immediate
/// pushes in front of each direct call; no game process is required.
/// </summary>
public sealed class EngineEffectScanService
{
    public const uint CoreEntry = 0x004101D9;
    public const uint WrapperEntry = 0x0042518F;
    // Effects 85-89 use leaf stubs that push four constants and call the
    // dispatcher directly, skipping the core pre-checks. They are genuine
    // equipment consumers (e.g. 116 -> 88 mimic, 117 -> 89 shield).
    public const uint DispatcherEntry = 0x0041301E;
    public const uint ValueEntry = 0x00413009;
    private const int MaximumLookbackBytes = 96;
    private const int LeadingBytes = 16;

    public static IReadOnlyDictionary<uint, string> EntryNames { get; } = new Dictionary<uint, string>
    {
        [CoreEntry] = "核心判定",
        [WrapperEntry] = "双渠道包装",
        [DispatcherEntry] = "直接分派"
    };

    // 00413009 aggregates EAX/EDX under ECX; it takes no four stack arguments.
    public static IReadOnlyList<uint> KnownEntryTargets { get; } = [CoreEntry, WrapperEntry];

    public EngineEffectScanReport Scan(ProjectWorkspace workspace)
        => Scan(workspace, CancellationToken.None);

    public Task<EngineEffectScanReport> ScanAsync(ProjectWorkspace workspace,
        CancellationToken cancellationToken = default, IProgress<EngineEffectScanProgress>? progress = null)
        => Task.Run(() => Scan(workspace, cancellationToken, progress), cancellationToken);

    public EngineEffectScanReport Scan(ProjectWorkspace workspace, CancellationToken cancellationToken,
        IProgress<EngineEffectScanProgress>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        void Stage(string name, int completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EngineEffectScanProgress(name, completed, 9, timer.ElapsedMilliseconds));
        }
        Stage("读取引擎快照", 0);
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.RootPath)) throw new InvalidOperationException("尚未打开项目。");
        var layout = EngineEffectLayout.Resolve(workspace);
        if (!File.Exists(layout.ExePath)) throw new FileNotFoundException("当前项目缺少 Ekd5.exe。", layout.ExePath);

        var bytes = File.ReadAllBytes(layout.ExePath);
        var image = EngineEffectBinaryImage.Read(bytes, cancellationToken);
        Stage("解析静态分配表", 1);
        var assignmentLayout = EngineEffectAssignmentService.ProbeJobLayout(image);
        var assignmentRows = ReadAssignmentRows(assignmentLayout, bytes);
        var assignmentTable = DescribeAssignmentTable(assignmentLayout, assignmentRows);
        var namePoolProbe = new EffectNamePoolProbe().Inspect(bytes, Math.Max(1, layout.NameCount));
        var namePool = namePoolProbe.Success && namePoolProbe.FileOffset is { } poolOffset &&
                       namePoolProbe.VirtualAddress is { } poolAddress
            ? new EngineEffectNamePoolInfo
            {
                Verified = true,
                TableAddress = checked((uint)poolAddress),
                TableFileOffset = poolOffset,
                RowCount = layout.NameCount,
                SlotSize = 16,
                IsWritable = false,
                Evidence = namePoolProbe.Message
            }
            : null;
        Stage("构建指令边界与跳转索引", 2);
        var jumpIndex = EngineEffectJumpIndex.Build(image);
        Stage("构建调用图", 3);
        var callGraph = EngineEffectCallGraph.Build(image, jumpIndex);
        var imsgBytes = File.Exists(layout.ImsgPath) ? File.ReadAllBytes(layout.ImsgPath) : null;
        var diagnostics = new List<string>();
        diagnostics.Add($"指令边界图 {jumpIndex.InstructionCount} 条指令（索引跳转锚点 {jumpIndex.IndexedJumpAnchorCount} 个，" +
                        "只作为解码起点，不产生跳转边）。");
        if (imsgBytes == null) diagnostics.Add("未找到 Imsg.e5，特效说明只读。");
        else if (!layout.HasImsg) diagnostics.Add("Imsg.e5 不足以覆盖特效说明槽，说明只读。");
        if (IsSlotRangeInvalid(bytes, layout)) diagnostics.Add("名称槽超出 Ekd5.exe，名称只读。");

        var calls = new List<EngineEffectCallSite>();
        var seen = new HashSet<uint>();
        Stage("解析特效参数与证据", 4);
        foreach (var rawCall in image.Context.RawCalls)
        {
            image.Context.CheckCancellation();
            var callAddress = rawCall.Address;
            var target = rawCall.Target;
            if (!IsEffectEntryTarget(image, callAddress, target) || !seen.Add(callAddress)) continue;
            var functionEntry = callGraph.CallerEntryOf(callAddress);
            IReadOnlyList<EngineEffectPushArgument>? pushes = null;
            uint? redirectSource = null;
            var decoded = EngineEffectDecoder.DecodeBacktrack(image, callAddress, MaximumLookbackBytes);
            var backtrackPushes = decoded?.Where(item => item.IsPush).TakeLast(4).ToArray() ?? [];
            if (backtrackPushes.Length >= 4)
            {
                pushes = backtrackPushes
                    .Select(item => EngineEffectArgumentAnalyzer.Analyze(image, item)).ToArray();
            }
            if (pushes == null)
            {
                var bodyPushes = EngineEffectFunctionAnalyzer.FunctionPushArguments(image, functionEntry, callAddress);
                if (bodyPushes.Count >= 4) pushes = bodyPushes;
            }
            if (pushes == null)
            {
                var jump = EngineEffectDecoder.DecodeJumpSource(image, callAddress, out var jumpAddress);
                var jumpPushes = jump?.Where(item => item.IsPush).TakeLast(4).ToArray() ?? [];
                if (jumpPushes.Length >= 4)
                {
                    pushes = jumpPushes
                        .Select(item => EngineEffectArgumentAnalyzer.Analyze(image, item)).ToArray();
                    redirectSource = jumpAddress;
                }
            }
            pushes ??= [];
            var owner = EngineEffectFunctionAnalyzer.InternalOwner(functionEntry);
            var stubPushes = EngineEffectFunctionAnalyzer.StubPushes(image, functionEntry);
            calls.Add(CreateCallSite(image, callGraph, jumpIndex, callAddress, target, pushes, functionEntry,
                owner, stubPushes, redirectSource));
        }

        Stage("分析包装、表驱动与循环", 5);
        foreach (var call in calls) EngineEffectArgumentAnalyzer.ValidateBranchTargets(image, call);
        EngineEffectArgumentAnalyzer.MarkSharedDefinitions(calls);
        var derivedCalls = EngineEffectWrapperAnalyzer.Analyze(image, callGraph, jumpIndex, calls);
        var tableCalls = EngineEffectTableAnalyzer.Analyze(image, calls);
        var loopCalls = EngineEffectLoopAnalyzer.Analyze(image, callGraph, jumpIndex, derivedCalls);
        ApplyEffectRanges(derivedCalls, layout.NameCount);

        var internalCalls = calls.Where(call => call.Pattern == EngineEffectCallPattern.EngineInternal)
            .OrderBy(call => call.CallAddress).ToArray();
        var effectiveCalls = calls.Where(call => call.Pattern != EngineEffectCallPattern.EngineInternal).ToArray();
        var staticCalls = effectiveCalls.Where(call => call.Status == EngineEffectCallStatus.Static &&
                                                       call.PersonalEffectId is >= 0 and <= byte.MaxValue).ToArray();
        var unassignedCalls = effectiveCalls.Where(call => call.Status != EngineEffectCallStatus.Static)
            .OrderBy(call => call.CallAddress).ToArray();
        var callsById = staticCalls.GroupBy(call => call.PersonalEffectId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var derivedById = ExpandDerivedById(derivedCalls, layout.NameCount);
        var tableById = tableCalls.Where(call => call.PersonalEffectId is >= 0 and <= byte.MaxValue)
            .GroupBy(call => call.PersonalEffectId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var loopById = loopCalls.Where(call => call.PersonalEffectId is >= 0 and <= byte.MaxValue)
            .GroupBy(call => call.PersonalEffectId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var ids = new SortedSet<int>(Enumerable.Range(0, Math.Max(0, layout.NameCount)));
        foreach (var id in callsById.Keys) ids.Add(id);
        foreach (var id in derivedById.Keys)
            if (id < layout.NameCount) ids.Add(id);
        foreach (var id in tableById.Keys)
            if (id < layout.NameCount) ids.Add(id);
        foreach (var id in loopById.Keys)
            if (id < layout.NameCount) ids.Add(id);
        var rows = new List<EngineEffectScanRow>(ids.Count);
        foreach (var id in ids)
        {
            callsById.TryGetValue(id, out var effectCalls);
            derivedById.TryGetValue(id, out var rowDerivedCalls);
            tableById.TryGetValue(id, out var rowTableCalls);
            loopById.TryGetValue(id, out var rowLoopCalls);
            var allCalls = (effectCalls ?? []).Concat(rowDerivedCalls ?? [])
                .Concat(rowTableCalls ?? []).Concat(rowLoopCalls ?? []).ToArray();
            var nameSlot = ReadNameSlot(bytes, layout, id);
            var descriptionSlot = ReadDescriptionSlot(imsgBytes, layout, id);
            var suffix = nameSlot != null ? nameSlot[EngineEffectLayout.NameSuffixOffset] : (byte?)null;
            EffectAssignmentRow? assignment = assignmentRows.TryGetValue(id, out var found) ? found : null;
            rows.Add(new EngineEffectScanRow
            {
                EffectId = id,
                Name = nameSlot == null ? string.Empty : EngineEffectSlotRules.DecodeName(nameSlot),
                SuffixByte = suffix,
                SuffixLabel = suffix is { } value ? EngineEffectSuffixLabels.Label(layout.Version, value) : string.Empty,
                Description = descriptionSlot == null ? string.Empty : EngineEffectSlotRules.DecodeDescription(descriptionSlot),
                DescriptionByteCount = descriptionSlot == null ? 0 : EngineEffectSlotRules.DescriptionByteCount(descriptionSlot),
                HasNameSlot = nameSlot != null,
                HasDescriptionSlot = descriptionSlot != null,
                NameSlotOffset = nameSlot == null ? -1 : layout.NameOffset + id * (long)EngineEffectLayout.NameSlotSize,
                DescriptionSlotOffset = descriptionSlot == null ? -1 : layout.DescriptionOffset + id * (long)layout.DescriptionStride,
                NameCapacity = nameSlot == null ? 0 : EngineEffectSlotRules.NameCapacity(nameSlot),
                CallSiteDetails = effectCalls == null ? [] : effectCalls.OrderBy(call => call.CallAddress).ToArray(),
                DerivedCallDetails = rowDerivedCalls == null
                    ? []
                    : rowDerivedCalls.OrderBy(call => call.CallAddress).ThenBy(call => call.WrapperStubCallAddress).ToArray(),
                TableCallDetails = rowTableCalls == null
                    ? []
                    : rowTableCalls.OrderBy(call => call.CallAddress).ThenBy(call => call.TableIndex).ToArray(),
                LoopCallDetails = rowLoopCalls == null
                    ? []
                    : rowLoopCalls.OrderBy(call => call.CallAddress).ThenBy(call => call.LoopIndex).ToArray(),
                HasReachableCall = allCalls.Any(call => call.IsReachable != false),
                OnlyUnreachableCalls = allCalls.Length > 0 && allCalls.All(call => call.IsReachable == false),
                StackingModes = Calls(allCalls, call => call.StackingMode),
                ValueModes = Calls(allCalls, call => call.ValueMode),
                ItemEffectIds = Calls(allCalls, call => call.ItemEffectId),
                Entries = allCalls.Select(call => call.EntryName).Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                AssignmentPersonIds = assignment?.PersonIds ?? [],
                AssignmentPersonValues = assignment?.PersonValues ?? [],
                AssignmentJobIds = assignment?.JobIds ?? [],
                AssignmentJobValues = assignment?.JobValues ?? [],
                AssignmentRowOffset = assignment?.Offset ?? -1
            });
        }

        ApplyConsumerSummaries(calls, callGraph, rows);
        var callsByAddress = calls.ToDictionary(call => call.CallAddress);
        foreach (var tableCall in tableCalls)
            if (callsByAddress.TryGetValue(tableCall.CallAddress, out var baseCall))
                tableCall.ConsumerSummary = baseCall.ConsumerSummary;
        var wrappers = derivedCalls.Where(call => call.WrapperStubEntry is not null)
            .GroupBy(call => call.WrapperStubEntry!.Value)
            .Select(group => new EngineEffectWrapperInfo
            {
                Entry = group.Key,
                Name = EntryNames.TryGetValue(group.Key, out var name) ? name : $"发现包装 0x{group.Key:X8}",
                DerivedCallCount = group.Count(),
                StaticDerivedCallCount = group.Count(call => call.Status == EngineEffectCallStatus.Static),
                CoreCallAddresses = group.Select(call => call.WrapperStubCallAddress!.Value)
                    .Distinct().OrderBy(address => address).ToArray()
            })
            .OrderBy(info => info.Entry)
            .ToArray();
        var derivedOnlyIds = derivedById.Keys.Concat(tableById.Keys).Concat(loopById.Keys).Distinct()
            .Where(id => id < layout.NameCount && !callsById.ContainsKey(id))
            .OrderBy(id => id).ToArray();
        var calledIds = rows.Where(row => row.HasCalls).Select(row => row.EffectId).ToHashSet();
        var unreferencedIds = Enumerable.Range(0, Math.Max(0, layout.NameCount))
            .Where(id => !calledIds.Contains(id)).ToArray();
        var unreachableCalls = calls.Concat(derivedCalls).Concat(tableCalls).Concat(loopCalls)
            .Where(call => call.IsReachable == false)
            .OrderBy(call => call.CallAddress).ToArray();
        var caveCalls = calls.Where(call => call.ReachCaveEntry != null)
            .OrderBy(call => call.CallAddress).ToArray();
        var unreachableOnlyIds = rows.Where(row => row.OnlyUnreachableCalls)
            .Select(row => row.EffectId).OrderBy(id => id).ToArray();

        var statistics = new[] { CoreEntry, WrapperEntry, DispatcherEntry }.Select(target => new EngineEffectEntryStatistics
        {
            Target = target,
            Name = EntryNames[target],
            Total = calls.Count(call => call.EntryTarget == target),
            Static = staticCalls.Count(call => call.EntryTarget == target),
            Dynamic = calls.Count(call => call.EntryTarget == target && call.Status == EngineEffectCallStatus.Dynamic),
            Unresolved = calls.Count(call => call.EntryTarget == target && call.Status == EngineEffectCallStatus.Unresolved),
            Internal = internalCalls.Count(call => call.EntryTarget == target)
        }).ToArray();

        var sharedDefinitions = calls.SelectMany(call => call.Pushes)
            .Count(push => push.WritePlan.IsSharedDefinition);
        if (calls.Count == 0) diagnostics.Add("未找到 6.x 四参数调用点；详情页只显示名称行。");
        diagnostics.Add($"引擎内部调用 {internalCalls.Length} 个已单列；未归属调用 {unassignedCalls.Length} 个；" +
                        $"派生调用 {derivedCalls.Count} 个（个人号已解析 " +
                        $"{derivedCalls.Count(call => call.PersonalEffectId != null)} 个）；共享定义 {sharedDefinitions} 个。");
        diagnostics.Add($"派生/表驱动独有特效 {derivedOnlyIds.Length} 个；名称槽无调用 {unreferencedIds.Length} 个；" +
                        $"表驱动调用 {tableCalls.Count} 个；循环展开 {loopCalls.Count} 个；" +
                        $"不可达调用 {unreachableCalls.Length} 个；代码洞进入 {caveCalls.Length} 个。");
        var rangeCalls = derivedCalls.Where(call => call.EffectRange.Count > 0).ToArray();
        if (rangeCalls.Length > 0)
            diagnostics.Add($"范围分派 {rangeCalls.Length} 个（覆盖特效 {rangeCalls.Sum(call => call.EffectRange.Count)} 个）：" +
                            string.Join("；", rangeCalls.Select(call =>
                                $"0x{call.CallAddress:X8} → {call.EffectRange[0]}–{call.EffectRange[^1]}" +
                                $"（{call.PersonalPush.RangeReason}）")));
        var genericDispatch = DescribeGenericDispatch(image);
        if (genericDispatch != null) diagnostics.Add(genericDispatch);
        if (assignmentTable != null)
            diagnostics.Add($"静态分配表 0x{assignmentTable.TableAddress:X8}（[0x{assignmentTable.PointerAddress:X8}]，" +
                            $"文件+0x{assignmentTable.TableFileOffset:X}，{assignmentTable.RowCount}×{assignmentTable.RowSize} 字节，" +
                            $"行号=特效号，{assignmentTable.PersonSlotCount} 人物槽/{assignmentTable.JobSlotCount} 兵种槽，" +
                            $"N {(assignmentTable.SharedValue ? "整行共享" : "每槽独立")}）：非空 " +
                            $"{assignmentTable.NonEmptyRowCount} 行（人物 {assignmentTable.PersonRowCount} / " +
                            $"兵种 {assignmentTable.JobRowCount}）。");
        diagnostics.Add(namePool != null
            ? $"内嵌特效名称池 0x{namePool.TableAddress:X8}（文件+0x{namePool.TableFileOffset:X}，" +
              $"{namePool.RowCount}×{namePool.SlotSize} 字节，严格 GBK，共享只读）：{namePool.Evidence}"
            : "内嵌特效名称池未定位（两个消费者不一致或映射不完整），名称只按 Imsg.e5 读取。");
        diagnostics.Add($"后缀类型码：{layout.Version} 支持 0x00..0x{EngineEffectSuffixLabels.MaximumSuffix(layout.Version):X2}；" +
                        "超出范围的尾码在引擎中不产生后缀。");
        if (callsById.TryGetValue(byte.MaxValue, out var sentinelCalls))
            diagnostics.Add($"特效号 255 作为“无特效”哨兵被 {sentinelCalls.Length} 个静态调用点引用" +
                            "（没有名称槽与分配行，不是可分配特效）。");
        if (unreachableCalls.Length > 0)
            diagnostics.Add("不可达调用点：" + string.Join("；", unreachableCalls.Select(call =>
                $"0x{call.CallAddress:X8}（函数 0x{call.FunctionEntry:X8}）")));
        if (caveCalls.Length > 0)
            diagnostics.Add("代码洞进入的调用点：" + string.Join("；", caveCalls.Select(call =>
                $"0x{call.CallAddress:X8} ← 跳转 0x{call.ReachJumpAddress:X8} 进入 0x{call.ReachCaveEntry:X8}")));
        if (unreachableOnlyIds.Length > 0)
            diagnostics.Add("仅死代码调用的特效：" + string.Join("；", unreachableOnlyIds.Select(id =>
                $"{id}（{rows.First(row => row.EffectId == id).Name}）")));
        if (wrappers.Length > 0)
            diagnostics.Add("包装桩：" + string.Join("；", wrappers.Select(info =>
                $"{info.Name} 0x{info.Entry:X8}（派生 {info.DerivedCallCount}，静态 {info.StaticDerivedCallCount}，" +
                $"桩内 {string.Join("/", info.CoreCallAddresses.Select(address => $"0x{address:X8}"))}）")));
        diagnostics.Add(string.Join("；", statistics.Select(item =>
            $"{item.Name} 0x{item.Target:X8}：{item.Total}（静态 {item.Static}，动态 {item.Dynamic}，" +
            $"未解析 {item.Unresolved}，内部 {item.Internal}）")));
        diagnostics.Add("常量页（已确认语义）：" + string.Join("；", EngineConstantService.Definitions.Select(definition =>
            $"{definition.DisplayName} {definition.LocationText}")));

        var rootAddresses = calls.SelectMany(call => call.ConsumerRoots)
            .Concat(derivedCalls.SelectMany(call => call.ConsumerRoots))
            .Concat(tableCalls.SelectMany(call => call.ConsumerRoots))
            .Concat(loopCalls.SelectMany(call => call.ConsumerRoots))
            .Distinct().OrderBy(address => address).ToArray();
        var effectIdsByFunction = new Dictionary<uint, List<int>>();
        foreach (var row in rows)
        {
            foreach (var call in row.CallSiteDetails.Concat(row.DerivedCallDetails)
                         .Concat(row.TableCallDetails).Concat(row.LoopCallDetails))
            {
                if (call.FunctionEntry is not { } function) continue;
                if (!effectIdsByFunction.TryGetValue(function, out var list))
                {
                    list = [];
                    effectIdsByFunction[function] = list;
                }
                if (!list.Contains(row.EffectId)) list.Add(row.EffectId);
            }
        }
        var effectIdMap = effectIdsByFunction.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(id => id).ToArray());
        var reachableFunctionsByEffect = new Dictionary<int, List<uint>>();
        foreach (var row in rows)
        {
            foreach (var call in row.CallSiteDetails.Concat(row.DerivedCallDetails)
                         .Concat(row.TableCallDetails).Concat(row.LoopCallDetails))
            {
                if (call.FunctionEntry is not { } function || !call.ReachesProcessEntry) continue;
                if (!reachableFunctionsByEffect.TryGetValue(row.EffectId, out var list))
                {
                    list = [];
                    reachableFunctionsByEffect[row.EffectId] = list;
                }
                if (!list.Contains(function)) list.Add(function);
            }
        }
        var reachableMap = reachableFunctionsByEffect.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<uint>)pair.Value.OrderBy(address => address).ToArray());
        var effectNames = rows.ToDictionary(row => row.EffectId, row => row.Name);
        var effectEntries = new HashSet<uint>(KnownEntryTargets);
        Stage("分析根入口与引用", 6);
        var rootInfos = EngineEffectRootClassifier.Classify(image, jumpIndex, callGraph, effectIdMap,
            reachableMap, effectNames, effectEntries, rootAddresses);
        var roleFunctions = calls.Select(call => call.FunctionEntry)
            .Concat(derivedCalls.Select(call => call.FunctionEntry))
            .Concat(tableCalls.Select(call => call.FunctionEntry))
            .Concat(loopCalls.Select(call => call.FunctionEntry))
            .Concat(ChainFunctionEntries(calls))
            .Concat(ChainFunctionEntries(derivedCalls))
            .Concat(ChainFunctionEntries(tableCalls))
            .Concat(ChainFunctionEntries(loopCalls))
            .Concat(rootInfos.Select(info => (uint?)info.Address))
            .Where(entry => entry != null).Select(entry => entry!.Value);
        Stage("分析控制流与判定条件", 7);
        var functionRoles = EngineEffectFunctionRoleAnalyzer.Analyze(image, callGraph, jumpIndex, effectIdMap,
            effectNames, roleFunctions);
        if (functionRoles.Count > 0)
        {
            diagnostics.Add($"函数角色 {functionRoles.Count} 个：" + string.Join("，",
                functionRoles.GroupBy(role => role.Role.Split('（')[0])
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => $"{group.Key} {group.Count()}")));
        }
        var branchPaths = functionRoles.SelectMany(role => role.BranchPaths).ToArray();
        if (branchPaths.Length > 0)
        {
            var jumpEntered = branchPaths.Count(path => !path.Reachable && path.InboundJumpAddress != null);
            var upstreamResolved = branchPaths.Count(path => path.InboundJumpAddress != null &&
                                                             path.InboundJumpReachable != false &&
                                                             path.UpstreamSummary.Length > 0);
            diagnostics.Add($"判定条件 {branchPaths.Length} 条路径：" +
                            $"分支条件 {branchPaths.Sum(path => path.Conditions.Count)} 个" +
                            $"（不跳转一侧 {branchPaths.Sum(path => path.Conditions.Count(condition => !condition.Taken))} 个），" +
                            $"无判定（顺序执行）{branchPaths.Count(path => path.Reachable && path.Conditions.Count == 0)} 条，" +
                            $"不可静态回溯 {branchPaths.Count(path => !path.Reachable)} 条" +
                            (jumpEntered > 0
                                ? $"（其中经入边跳转进入 {jumpEntered} 条，已回溯上游条件 {upstreamResolved} 条 / " +
                                  $"{branchPaths.Sum(path => path.UpstreamConditions.Count)} 个守卫）"
                                : string.Empty));
            var multiPath = branchPaths.Where(path => path.Reachable && path.PathCount > 1).ToArray();
            if (multiPath.Length > 0)
                diagnostics.Add($"多路径守卫合并 {multiPath.Length} 个调用点：" +
                                $"公共守卫 {multiPath.Sum(path => path.CommonConditions.Count)} 个" +
                                $"（全部可达调用点含单路径合计 " +
                                $"{branchPaths.Where(path => path.Reachable).Sum(path => path.CommonConditions.Count)} 个），" +
                                $"路径特有守卫 {multiPath.Sum(path => path.OptionalConditions.Count)} 个" +
                                $"（其中不在最短路径上 {multiPath.Sum(path =>
                                    path.OptionalConditions.Count(condition =>
                                        !path.Conditions.Any(item => item.Address == condition.Address &&
                                                                     item.Taken == condition.Taken)))} 个），" +
                                $"互斥判定 {multiPath.Sum(path => path.ConflictingConditionCount)} 处，" +
                                $"枚举截断 {multiPath.Count(path => path.PathsTruncated)} 个");
            var truncatedSites = branchPaths.Where(path => path.PathsTruncated)
                .OrderBy(path => path.CallAddress).ToArray();
            if (truncatedSites.Length > 0)
                diagnostics.Add("枚举截断调用点：" + string.Join("，", truncatedSites.Select(path =>
                    $"0x{path.CallAddress:X8}（{path.PathCount} 条）")));
            var reachablePaths = branchPaths.Where(path => path.Reachable).ToArray();
            if (reachablePaths.Length > 0)
                diagnostics.Add($"条件组（DNF）{reachablePaths.Sum(path => path.ConditionGroups.Count)} 组：" +
                                $"等价路径合并 {reachablePaths.Sum(path => path.ConditionGroupDuplicatePathCount)} 条，" +
                                $"多组调用点 {reachablePaths.Count(path => path.ConditionGroups.Count > 1)} 个，" +
                                $"含互斥方向 {reachablePaths.Count(path => path.ConditionGroups.Any(group => group.ExclusiveGroupIndexes.Count > 0))} 个，" +
                                $"条件组达上限 {reachablePaths.Count(path => path.ConditionGroupsTruncated)} 个");
            var upstreamGroups = branchPaths.Sum(path => path.UpstreamConditionGroups.Count);
            if (upstreamGroups > 0)
                diagnostics.Add($"上游条件组 {upstreamGroups} 组：" +
                                $"多组跳转路径 {branchPaths.Count(path => path.UpstreamConditionGroups.Count > 1)} 个");
            var decisionSites = reachablePaths.Where(path => path.ConditionDecisions.Count > 0).ToArray();
            if (decisionSites.Length > 0)
                diagnostics.Add($"决策点与组合覆盖 {decisionSites.Length} 个站点：" +
                                $"二向分支点 {decisionSites.Sum(path => path.ConditionDecisions.Count)} 个，" +
                                $"完整组合 {decisionSites.Count(path => path.ConditionDecisionSpaceComplete)} 个，" +
                                $"未枚举组合 {decisionSites.Sum(path => path.ConditionDecisionMissingCount)} 种");
            var upstreamDecisionSites = branchPaths
                .Where(path => path.UpstreamConditionDecisions.Count > 0).ToArray();
            if (upstreamDecisionSites.Length > 0)
                diagnostics.Add($"上游决策点 {upstreamDecisionSites.Length} 个站点：" +
                                $"二向分支点 {upstreamDecisionSites.Sum(path => path.UpstreamConditionDecisions.Count)} 个，" +
                                $"完整组合 {upstreamDecisionSites.Count(path => path.UpstreamConditionDecisionSpaceComplete)} 个，" +
                                $"未枚举组合 {upstreamDecisionSites.Sum(path => path.UpstreamConditionDecisionMissingCount)} 种");
        }
        var triggerInstances = calls.Concat(derivedCalls).Concat(tableCalls).Concat(loopCalls)
            .DistinctBy(call => call.Identity).ToArray();
        var triggerSummaries = EngineEffectTriggerAnalyzer.Build(functionRoles, triggerInstances, effectNames);
        var triggerCalls = triggerInstances.ToDictionary(call => call.Identity);
        foreach (var trigger in triggerSummaries)
        {
            if (triggerCalls.TryGetValue(trigger.Identity, out var triggerCall))
                triggerCall.TriggerSummary = trigger.Summary;
        }
        if (triggerSummaries.Count > 0)
        {
            diagnostics.Add($"触发条件摘要 {triggerSummaries.Count} 条：" +
                            $"判定守卫 {triggerSummaries.Sum(item => item.ConditionCount)} 个，" +
                            $"顺序执行 {triggerSummaries.Count(item => item.Reachable && item.ConditionCount == 0)} 条，" +
                            $"经入边跳转 {triggerSummaries.Count(item => !item.Reachable && item.InboundJumpAddress != null)} 条，" +
                            $"其中已回溯上游守卫 {triggerSummaries.Count(item => item.UpstreamConditions.Count > 0)} 条，" +
                            $"不可回溯 {triggerSummaries.Count(item => !item.Reachable)} 条");
        }
        Stage("审计间接调用并汇总", 8);
        var indirectAudit = EngineEffectIndirectCallAnalyzer.Analyze(image, callGraph, KnownEntryTargets);
        var indirectCalls = indirectAudit.Calls;
        diagnostics.Add($"间接调用审计 {indirectCalls.Count} 处：" +
                        $"导入表 {indirectCalls.Count(call => call.Kind == "导入表")}，" +
                        $"寄存器 {indirectCalls.Count(call => call.Kind == "寄存器")}，" +
                        $"成员 {indirectCalls.Count(call => call.Kind == "成员")}，" +
                        $"表 {indirectCalls.Count(call => call.Kind == "表")}，" +
                        $"代码目标 {indirectCalls.Count(call => call.Kind == "代码目标")}，" +
                        $"命中特效入口 {indirectCalls.Count(call => call.TargetsEffectEntry)}；" +
                        $"特效入口绝对指针 {indirectAudit.EntryPointerCount} 处，" +
                        $"直接跳转 {indirectAudit.EntryJumpCount} 处");
        var deadStubs = rootInfos.Where(info => info.IsDeadEffectStub).ToArray();
        var noStaticRoots = rootInfos.Where(info => info.IsNoStaticNonEffectRoot).ToArray();
        var upstreamRoots = rootInfos.Where(info => info.ReachesEffectIndirectly &&
                                                    !info.IsEntryPoint && !info.IsExported &&
                                                    info.ReferenceCount == 0 && info.InboundJumpCount == 0 &&
                                                    info.InteriorEntryCount == 0 && info.EffectIds.Count == 0 &&
                                                    !info.CallsEffectCore).ToArray();
        diagnostics.Add($"E8 根 {rootInfos.Count} 个：PE 入口 {rootInfos.Count(info => info.IsEntryPoint)}，" +
                        $"特效桩 {rootInfos.Count(info => info.EffectIds.Count > 0)}，" +
                        $"导出函数 {rootInfos.Count(info => info.IsExported)}，" +
                        $"数据引用 {rootInfos.Count(info => info.DataReferenceCount > 0)}，" +
                        $"代码引用 {rootInfos.Count(info => info.CodeReferenceCount > 0)}，" +
                        $"跳转进入 {rootInfos.Count(info => info.InboundJumpCount > 0 && !info.IsEntryPoint)}，" +
                        $"函数体内入口 {rootInfos.Count(info => info.InteriorEntryCount > 0 && !info.IsEntryPoint)}，" +
                        $"死代码特效桩 {deadStubs.Length}，" +
                        $"无特效且无静态入边 {noStaticRoots.Length}，" +
                        $"间接参与特效链 {upstreamRoots.Length}。");
        if (upstreamRoots.Length > 0)
            diagnostics.Add("无静态入边（经下游间接参与特效链）：" + string.Join("；", upstreamRoots.Select(info =>
                $"0x{info.Address:X8}（深度 {info.EffectReachDepth}，" +
                $"{string.Join(" → ", info.EffectReachPath.Select(value => $"0x{value:X8}"))}，" +
                $"展开 {info.EffectReachExpandedFunctions} 个目标，E8 调用 {info.DirectCallCount}，" +
                $"核心判定 {info.EffectCoreCallCount}）")));
        if (deadStubs.Length > 0)
            diagnostics.Add("死代码特效桩：" + string.Join("；", deadStubs.Select(info =>
                $"0x{info.Address:X8}（特效 {string.Join("/", info.EffectIds)}，同特效可达桩 " +
                $"{string.Join("/", info.ReachableAlternatives.Select(address => $"0x{address:X8}"))}）")));
        if (noStaticRoots.Length > 0)
            diagnostics.Add("无静态入边（非特效普通代码）：" + string.Join("；", noStaticRoots.Select(info =>
                $"0x{info.Address:X8}（0x{info.FunctionSize:X} 字节，E8 调用 {info.DirectCallCount}，" +
                $"间接调用 {info.RegisterCallCount + info.ExternalCallCount}，" +
                $"核心判定 {info.EffectCoreCallCount}，前置填充 {info.PrecedingPaddingBytes} 字节）")));
        var unalignedCalls = callGraph.UnalignedCalls;
        if (unalignedCalls.Count > 0)
            diagnostics.Add($"未对齐 E8 字节（已在调用链与调用者计数中排除）{unalignedCalls.Count} 处：" +
                            string.Join("；", unalignedCalls.Take(16).Select(call =>
                                $"0x{call.Address:X8} → 0x{call.Target:X8}")) +
                            (unalignedCalls.Count > 16 ? "…" : string.Empty));

        var report = new EngineEffectScanReport
        {
            ExePath = layout.ExePath,
            ImsgPath = layout.ImsgPath,
            EngineVersion = layout.Version,
            ExeSha256 = HashService.Sha256(bytes),
            ImsgSha256 = imsgBytes == null ? string.Empty : HashService.Sha256(imsgBytes),
            EntryPoint = callGraph.EntryPoint,
            NameCount = layout.NameCount,
            CallSiteCount = calls.Count,
            EffectiveCallSiteCount = effectiveCalls.Length,
            InternalCallSiteCount = internalCalls.Length,
            ResolvedCallSiteCount = staticCalls.Length,
            DynamicCallSiteCount = calls.Count(call => call.Status == EngineEffectCallStatus.Dynamic),
            UnresolvedCallSiteCount = calls.Count(call => call.Status == EngineEffectCallStatus.Unresolved),
            DerivedCallCount = derivedCalls.Count,
            ResolvedDerivedCallCount = derivedCalls.Count(call => call.PersonalEffectId != null),
            TableCallCount = tableCalls.Count,
            LoopCallCount = loopCalls.Count,
            UnreachableCallCount = unreachableCalls.Length,
            CaveEntryCallCount = caveCalls.Length,
            DeadEffectStubCount = deadStubs.Length,
            InstructionCount = jumpIndex.InstructionCount,
            IndexedJumpAnchorCount = jumpIndex.IndexedJumpAnchorCount,
            UnalignedCallCount = unalignedCalls.Count,
            UnalignedCallAddresses = unalignedCalls.Select(call => call.Address)
                .OrderBy(address => address).ToArray(),
            DerivedOnlyEffectIds = derivedOnlyIds,
            UnreachableOnlyEffectIds = unreachableOnlyIds,
            UnreferencedEffectIds = unreferencedIds,
            Wrappers = wrappers,
            EntryStatistics = statistics,
            RootInfos = rootInfos,
            FunctionRoles = functionRoles,
            IndirectCallCount = indirectCalls.Count,
            IndirectImportCount = indirectCalls.Count(call => call.Kind == "导入表"),
            IndirectRegisterCount = indirectCalls.Count(call => call.Kind == "寄存器"),
            IndirectMemberCount = indirectCalls.Count(call => call.Kind == "成员"),
            IndirectTableCount = indirectCalls.Count(call => call.Kind == "表"),
            IndirectCodeTargetCount = indirectCalls.Count(call => call.Kind == "代码目标"),
            IndirectEffectEntryCount = indirectCalls.Count(call => call.TargetsEffectEntry),
            EntryPointerCount = indirectAudit.EntryPointerCount,
            EntryJumpCount = indirectAudit.EntryJumpCount,
            IndirectCalls = indirectCalls,
            BranchPathCount = branchPaths.Length,
            BranchConditionCount = branchPaths.Sum(path => path.Conditions.Count),
            BranchPathUnreachableCount = branchPaths.Count(path => !path.Reachable),
            BranchPathJumpEnteredCount = branchPaths.Count(path =>
                !path.Reachable && path.InboundJumpAddress != null),
            BranchPathUpstreamResolvedCount = branchPaths.Count(path =>
                path.InboundJumpAddress != null && path.InboundJumpReachable != false &&
                path.UpstreamSummary.Length > 0),
            BranchPathUpstreamConditionCount = branchPaths.Sum(path => path.UpstreamConditions.Count),
            BranchMultiPathCount = branchPaths.Count(path => path.Reachable && path.PathCount > 1),
            BranchCommonConditionCount = branchPaths.Sum(path => path.CommonConditions.Count),
            BranchOptionalConditionCount = branchPaths.Sum(path => path.OptionalConditions.Count),
            BranchMultiPathTruncatedCount = branchPaths.Count(path => path.PathsTruncated),
            BranchConflictingConditionCount = branchPaths.Sum(path => path.ConflictingConditionCount),
            BranchConditionGroupCount = branchPaths.Sum(path => path.ConditionGroups.Count),
            BranchConditionGroupDuplicatePathCount = branchPaths.Sum(path => path.ConditionGroupDuplicatePathCount),
            BranchConditionGroupLimitCount = branchPaths.Count(path => path.ConditionGroupsTruncated),
            BranchUpstreamConditionGroupCount = branchPaths.Sum(path => path.UpstreamConditionGroups.Count),
            BranchUpstreamCommonConditionCount = branchPaths.Sum(path => path.UpstreamCommonConditions.Count),
            BranchUpstreamOptionalConditionCount = branchPaths.Sum(path => path.UpstreamOptionalConditions.Count),
            BranchConditionDecisionCount = branchPaths.Sum(path => path.ConditionDecisions.Count),
            BranchConditionDecisionCompleteCount = branchPaths.Count(path => path.ConditionDecisionSpaceComplete),
            BranchConditionDecisionMissingCount = branchPaths.Sum(path => path.ConditionDecisionMissingCount),
            BranchConditionDecisionNonFixedCount = branchPaths.Sum(path => path.ConditionDecisionNonFixedPoints.Count),
            BranchConditionDecisionRepeatedCount = branchPaths.Sum(path => path.ConditionDecisionRepeatedPoints.Count),
            BranchUpstreamConditionDecisionCount = branchPaths.Sum(path => path.UpstreamConditionDecisions.Count),
            BranchUpstreamConditionDecisionNonFixedCount =
                branchPaths.Sum(path => path.UpstreamConditionDecisionNonFixedPoints.Count),
            BranchUpstreamConditionDecisionRepeatedCount =
                branchPaths.Sum(path => path.UpstreamConditionDecisionRepeatedPoints.Count),
            TriggerSummaryCount = triggerSummaries.Count,
            TriggerConditionCount = triggerSummaries.Sum(item => item.ConditionCount),
            TriggerSequentialCount = triggerSummaries.Count(item => item.Reachable && item.ConditionCount == 0),
            TriggerUnreachableCount = triggerSummaries.Count(item => !item.Reachable),
            TriggerJumpEnteredCount = triggerSummaries.Count(item =>
                !item.Reachable && item.InboundJumpAddress != null),
            TriggerSummaries = triggerSummaries,
            AssignmentTable = assignmentTable,
            EmbeddedNamePool = namePool,
            Effects = rows,
            UnassignedCalls = unassignedCalls,
            InternalCalls = internalCalls,
            DerivedCalls = derivedCalls,
            TableCalls = tableCalls,
            LoopCalls = loopCalls,
            Diagnostics = diagnostics
        };
        Stage("扫描完成", 9);
        return report;
    }

    private static IEnumerable<uint?> ChainFunctionEntries(IEnumerable<EngineEffectCallSite> calls)
        => calls.SelectMany(call => call.CallerPaths)
            .SelectMany(path => path.Nodes)
            .Select(node => node.FunctionEntry == 0 ? (uint?)null : node.FunctionEntry);

    /// <summary>
    /// Finds the shared dispatch chain <c>mov edx,[global]; call lookup</c> followed
    /// by <c>mov edx,table; call handler</c>. The table is populated at runtime, so
    /// effects covered only by it never get a per-effect call site; naming the
    /// chain keeps those effects explainable instead of "unreferenced".
    /// </summary>
    private static string? DescribeGenericDispatch(EngineEffectBinaryImage image)
    {
        foreach (var section in image.ExecutableSections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 16) continue;
            var span = image.Bytes.AsSpan(start, length);
            for (var index = 0; index + 7 <= length; index++)
            {
                if (span[index] != 0x8B || span[index + 1] != 0x15) continue;   // mov edx,[disp32]
                if (span[index + 6] != 0xE8) continue;                          // call
                var pointerCell = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index + 2, 4));
                if (image.VirtualAddressToFileOffset(pointerCell) is not { } cellOffset) continue;
                if (cellOffset + 4 > image.Bytes.LongLength) continue;
                var table = BinaryPrimitives.ReadUInt32LittleEndian(
                    image.Bytes.AsSpan(checked((int)cellOffset), 4));
                if (image.SectionOf(table) is not { IsExecutable: true }) continue;
                var limit = Math.Min(length - 6, index + 0x80);
                for (var cursor = index + 11; cursor <= limit; cursor++)
                {
                    if (span[cursor] != 0xBA || span[cursor + 5] != 0xE8) continue;   // mov edx,imm32; call
                    var handler = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(cursor + 1, 4));
                    if (image.SectionOf(handler) is not { IsExecutable: true }) continue;
                    return $"通用分派链：0x{(section.VirtualAddress + (uint)index):X8} " +
                           $"mov edx,[0x{pointerCell:X8}] → 表 0x{table:X8}；" +
                           $"0x{(section.VirtualAddress + (uint)cursor):X8} mov edx,0x{handler:X8} → 处理器表" +
                           $"（文件内未初始化，运行时由效果分配数据填充）";
                }
            }
        }
        return null;
    }

    private readonly record struct EffectAssignmentRow(long Offset, int[] PersonIds, int[] PersonValues,
        int[] JobIds, int[] JobValues);

    /// <summary>
    /// Decodes every non-empty row of the per-effect assignment table the generic dispatch reads.
    /// Empty tokens are 0x400 (person) and 0xFF (job); rows carry shared or per-slot N values.
    /// </summary>
    private static Dictionary<int, EffectAssignmentRow> ReadAssignmentRows(EffectAssignmentLayout layout, byte[] bytes)
    {
        var result = new Dictionary<int, EffectAssignmentRow>();
        if (!layout.Verified || layout.RowSize <= 0) return result;
        for (var id = 0; id < layout.RowCount; id++)
        {
            var row = layout.GetRowOffset(id);
            if (row < 0 || row + layout.RowSize > bytes.LongLength) continue;
            var offset = checked((int)row);
            var persons = new List<int>();
            var personValues = new List<int>();
            for (var slot = 0; slot < layout.PersonIdOffsets.Count; slot++)
            {
                var person = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(offset + layout.PersonIdOffsets[slot], 2));
                if (person >= 1024) continue;
                persons.Add(person);
                personValues.Add(bytes[offset + layout.PersonValueOffsets[slot]]);
            }
            var jobs = new List<int>();
            var jobValues = new List<int>();
            for (var slot = 0; slot < layout.JobIdOffsets.Count; slot++)
            {
                var job = bytes[offset + layout.JobIdOffsets[slot]];
                if (job == 255) continue;
                jobs.Add(job);
                jobValues.Add(bytes[offset + layout.JobValueOffsets[slot]]);
            }
            if (persons.Count == 0 && jobs.Count == 0) continue;
            result[id] = new EffectAssignmentRow(row, persons.ToArray(), personValues.ToArray(),
                jobs.ToArray(), jobValues.ToArray());
        }
        return result;
    }

    private static EngineEffectAssignmentTableInfo? DescribeAssignmentTable(EffectAssignmentLayout layout,
        Dictionary<int, EffectAssignmentRow> rows)
    {
        if (!layout.Verified) return null;
        return new EngineEffectAssignmentTableInfo
        {
            Verified = true,
            PointerAddress = layout.PointerAddress,
            TableAddress = layout.TableAddress,
            TableFileOffset = layout.TableOffset,
            RowCount = layout.RowCount,
            RowSize = layout.RowSize,
            PersonSlotCount = layout.PersonIdOffsets.Count,
            JobSlotCount = layout.JobIdOffsets.Count,
            SharedValue = layout.SharedValue,
            NonEmptyRowCount = rows.Count,
            PersonRowCount = rows.Values.Count(row => row.PersonIds.Length > 0),
            JobRowCount = rows.Values.Count(row => row.JobIds.Length > 0),
            Evidence = layout.Evidence
        };
    }

    /// <summary>
    /// A derived call whose personal id is a guarded stack parameter plus a
    /// constant (e.g. [ebp+8]+0x90 with 0≤[ebp+8]≤4) covers a contiguous range.
    /// Recording the range lets every covered effect get a call point instead of
    /// being reported as "no static call site".
    /// </summary>
    private static void ApplyEffectRanges(IReadOnlyList<EngineEffectCallSite> calls, int nameCount)
    {
        foreach (var call in calls)
        {
            if (call.PersonalEffectId != null) continue;
            if (call.PersonalPush.MinimumValue is not { } minimum ||
                call.PersonalPush.MaximumValue is not { } maximum) continue;
            var from = Math.Max(0, minimum);
            var to = Math.Min(nameCount - 1, maximum);
            if (to < from || to - from > byte.MaxValue) continue;
            call.EffectRange = Enumerable.Range(from, to - from + 1).ToArray();
            call.RangeText = $"{call.PersonalPush.RangeReason}；覆盖特效 {from}–{to}";
        }
    }

    /// <summary>
    /// Accepts the two classic four-argument entries and, additionally, leaf
    /// stubs that push four constants and call 0x0041301E directly. Wrappers
    /// and queries that do more work around the dispatch are rejected so their
    /// runtime-computed arguments never look like writable equipment fields.
    /// </summary>
    private static bool IsEffectEntryTarget(EngineEffectBinaryImage image, uint callAddress, uint target)
    {
        if (target == CoreEntry || target == WrapperEntry) return true;
        if (target != DispatcherEntry) return false;
        if (EngineEffectFunctionAnalyzer.FindFunctionEntry(image, callAddress) is not { } entry) return false;
        if (entry == CoreEntry || entry == WrapperEntry) return false;
        return IsPureDispatcherStub(image, entry, callAddress);
    }

    private static bool IsPureDispatcherStub(EngineEffectBinaryImage image, uint entry, uint callAddress)
    {
        if (entry >= callAddress) return false;
        var length = (int)Math.Min(0x200u, callAddress - entry + 0x20);
        var decoded = EngineEffectDecoder.DecodeForward(image, entry, length);
        if (decoded.Count == 0) return false;
        var argumentPushes = 0;
        var reachedCall = false;
        foreach (var item in decoded)
        {
            var instruction = item.Instruction;
            if (item.Address < callAddress)
            {
                if (instruction.FlowControl == FlowControl.Call || instruction.Mnemonic == Mnemonic.Ret) return false;
                if (instruction.Mnemonic == Mnemonic.Push && instruction.Op0Kind == OpKind.Register &&
                    instruction.Op0Register == Register.EBP) continue;
                if (instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Register &&
                    instruction.Op0Register == Register.EBP && instruction.Op1Kind == OpKind.Register &&
                    instruction.Op1Register == Register.ESP) continue;
                if (EngineEffectDecoder.IsArgumentPush(item))
                {
                    argumentPushes++;
                    continue;
                }
                return false;
            }
            if (item.Address == callAddress)
            {
                reachedCall = true;
                continue;
            }
            if (!reachedCall) continue;
            if (instruction.Mnemonic == Mnemonic.Pop && instruction.Op0Kind == OpKind.Register &&
                instruction.Op0Register == Register.EBP) continue;
            if (instruction.Mnemonic != Mnemonic.Ret) return false;
            return argumentPushes == 4;
        }
        return false;
    }

    private static IReadOnlyDictionary<int, EngineEffectCallSite[]> ExpandDerivedById(
        IReadOnlyList<EngineEffectCallSite> calls, int nameCount)
    {
        var map = new Dictionary<int, List<EngineEffectCallSite>>();
        foreach (var call in calls)
        {
            if (call.PersonalEffectId is >= 0 and <= byte.MaxValue)
            {
                Add(call.PersonalEffectId!.Value, call);
                continue;
            }
            foreach (var id in call.EffectRange)
                if (id >= 0 && id < nameCount)
                    Add(id, call);
        }
        return map.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        void Add(int id, EngineEffectCallSite call)
        {
            if (!map.TryGetValue(id, out var list))
            {
                list = [];
                map[id] = list;
            }
            list.Add(call);
        }
    }

    private static EngineEffectCallSite CreateCallSite(EngineEffectBinaryImage image, EngineEffectCallGraph callGraph,
        EngineEffectJumpIndex jumpIndex, uint callAddress, uint target, IReadOnlyList<EngineEffectPushArgument> pushes,
        uint? functionEntry, uint? internalOwner, IReadOnlyList<EngineEffectPushArgument> stubPushes,
        uint? redirectSource)
    {
        var status = Classify(pushes);
        var callFileOffset = image.VirtualAddressToFileOffset(callAddress) ?? -1;
        var rawCall = callFileOffset >= 0 ? image.ReadBytes(callFileOffset, 5) ?? [] : [];
        EngineEffectPushArgument Push(int index) => index < pushes.Count ? pushes[index] : new EngineEffectPushArgument();
        var internalCall = internalOwner != null;
        var chain = functionEntry is { } stubEntry ? callGraph.ChainOf(stubEntry, 3) : [];
        var paths = functionEntry is { } pathEntry ? callGraph.PathsOf(pathEntry) : [];
        var roots = EngineEffectCallGraph.RootsOf(paths);
        var callerText = EngineEffectCallGraph.Describe(chain);
        var tableConsumer = pushes.Any(push => push.SourceKind == EngineEffectArgumentSource.Memory &&
                                               push.DefinitionChain.Any(item =>
                                                   item.Text.Contains("movzx", StringComparison.OrdinalIgnoreCase)));
        var evidence = EngineEffectFunctionAnalyzer.BuildEvidence(image, callAddress, target, functionEntry,
            pushes, stubPushes);
        var entryCallerCount = functionEntry is { } entryFunction ? callGraph.CallersOf(entryFunction).Count : 0;
        var afterReturn = EngineEffectFunctionAnalyzer.IsAfterReturn(image, functionEntry, callAddress);
        var (reachable, jumpSource, caveEntry) =
            EngineEffectFunctionAnalyzer.ResolveReachability(image, functionEntry, callAddress, jumpIndex,
                prologueEntry: EngineEffectFunctionAnalyzer.FindFunctionEntry(image, callAddress));
        var reachability = EngineEffectFunctionAnalyzer.DescribeReachability(reachable, afterReturn,
            entryCallerCount, functionEntry, jumpSource, caveEntry);
        return new EngineEffectCallSite
        {
            CallAddress = callAddress,
            FunctionEntry = functionEntry,
            FunctionEntryDistance = functionEntry is { } entry ? checked((int)(callAddress - entry)) : 0,
            IsReachable = reachable,
            Reachability = reachability,
            ReachJumpAddress = jumpSource,
            ReachCaveEntry = caveEntry,
            EntryName = EntryNames[target],
            EntryTarget = target,
            Status = status,
            Pattern = EngineEffectFunctionAnalyzer.Pattern(internalCall, pushes),
            InternalReason = internalOwner is { } owner
                ? EngineEffectFunctionAnalyzer.InternalReason(owner, functionEntry)
                : null,
            Pushes = pushes,
            StubPushes = stubPushes,
            ValuePush = Push(0),
            StackingPush = Push(1),
            ItemPush = Push(2),
            PersonalPush = Push(3),
            ValueMode = ToByte(EffectiveValue(Push(0))),
            StackingMode = ToByte(EffectiveValue(Push(1))),
            ItemEffectId = EngineEffectByteContract.Decode(EffectiveValue(Push(2))),
            PersonalEffectId = EngineEffectByteContract.Decode(EffectiveValue(Push(3))),
            CallInstruction = new EngineEffectCallInstruction
            {
                Address = callAddress,
                FileOffset = callFileOffset,
                Target = target,
                RawBytes = rawCall
            },
            LeadingBytesHex = HexBefore(image, callAddress, LeadingBytes),
            DisassemblyText = evidence +
                               (redirectSource is { } source
                                   ? Environment.NewLine + $"跳转来源 0x{source:X8}：参数 push 位于跳转前代码块（代码洞）。"
                                   : string.Empty) +
                               Environment.NewLine + "可达性：" + reachability +
                               Environment.NewLine + "调用链：" + callerText,
            CallerChain = chain,
            CallerChainText = callerText,
            CallerPaths = paths,
            ConsumerRoots = roots,
            FirstCallerEntry = chain.FirstOrDefault()?.CallerEntry,
            CallerCount = functionEntry is { } function ? callGraph.CallersOf(function).Count : 0,
            IsTableConsumer = tableConsumer,
            Callees = EngineEffectFunctionAnalyzer.Callees(image, functionEntry),
            RedirectSourceAddress = redirectSource
        };
    }

    private static void ApplyConsumerSummaries(IReadOnlyList<EngineEffectCallSite> calls,
        EngineEffectCallGraph callGraph, IReadOnlyList<EngineEffectScanRow> rows)
    {
        var names = rows.ToDictionary(row => row.EffectId, row => row.Name);
        var stubs = new Dictionary<uint, EngineEffectCallSite>();
        foreach (var call in calls)
        {
            if (call.FunctionEntry is not { } entry) continue;
            if (!stubs.TryGetValue(entry, out var existing) ||
                existing.Status != EngineEffectCallStatus.Static)
            {
                stubs[entry] = call;
            }
        }

        foreach (var call in calls)
        {
            if (call.IsReachable == false)
            {
                call.ConsumerSummary = call.Reachability;
                continue;
            }
            if (call.ReachCaveEntry != null)
            {
                call.ConsumerSummary = call.Reachability + "；静态 E8 调用图无法跨代码洞继续上溯";
                continue;
            }
            if (call.FirstCallerEntry is not { } caller)
            {
                call.ConsumerSummary = call.IsTableConsumer
                    ? "表驱动消费（未发现直接调用者，可能由跳转表分派）"
                    : "未发现直接调用者（可能由跳转表或数据字段间接分派）";
                continue;
            }

            string role;
            if (EntryNames.ContainsKey(caller))
            {
                role = $"{EntryNames[caller]}函数体内（入口 0x{caller:X8}）";
            }
            else if (stubs.TryGetValue(caller, out var stub) && stub.Status == EngineEffectCallStatus.Static &&
                     stub.PersonalEffectId is { } id)
            {
                var name = names.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text)
                    ? $" {text}"
                    : string.Empty;
                role = stub.CallAddress == call.CallAddress
                    ? $"自身桩函数 0x{caller:X8}"
                    : $"特效 {id}{name} 的调用桩 0x{caller:X8}（桩内调用点 0x{stub.CallAddress:X8}）";
            }
            else if (stubs.TryGetValue(caller, out var other))
            {
                role = $"调用桩 0x{caller:X8}（桩内调用点 0x{other.CallAddress:X8}，{StatusText(other.Status)}）";
            }
            else if (call.IsTableConsumer)
            {
                role = $"表驱动消费函数 0x{caller:X8}（参数来自内存表/循环）";
            }
            else
            {
                role = $"引擎消费函数 0x{caller:X8}";
            }

            var callerCount = callGraph.CallersOf(caller).Count;
            call.ConsumerSummary = callerCount > 1
                ? $"{role}，该函数共 {callerCount} 处调用"
                : role;
        }
    }

    private static string StatusText(EngineEffectCallStatus status) => status switch
    {
        EngineEffectCallStatus.Static => "静态",
        EngineEffectCallStatus.Dynamic => "动态",
        _ => "未解析"
    };

    private static EngineEffectCallStatus Classify(IReadOnlyList<EngineEffectPushArgument> pushes)
    {
        if (pushes.Count >= 4 && pushes[3].SourceKind == EngineEffectArgumentSource.Immediate &&
            EngineEffectByteContract.Decode(pushes[3].Value) is not null) return EngineEffectCallStatus.Static;
        if (pushes.Count >= 3 && pushes[2].SourceKind == EngineEffectArgumentSource.Immediate &&
            EngineEffectByteContract.Decode(pushes[2].Value) is not null) return EngineEffectCallStatus.Dynamic;
        return EngineEffectCallStatus.Unresolved;
    }

    private static uint? EffectiveValue(EngineEffectPushArgument push)
        => push.Value ?? (push.ResolvedValue is { } resolved and >= 0 ? (uint)resolved : null);

    private static IReadOnlyList<int> Calls(EngineEffectCallSite[]? calls, Func<EngineEffectCallSite, int?> selector)
        => calls == null
            ? []
            : calls.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).Distinct().OrderBy(value => value).ToArray();

    private static int? ToByte(int? value)
        => value is >= 0 and <= byte.MaxValue ? value : null;

    private static int? ToByte(uint? value)
        => value is <= byte.MaxValue ? (int)value!.Value : null;

    private static bool IsSlotRangeInvalid(byte[] bytes, EngineEffectLayout layout)
        => layout.NameOffset < 0 || layout.NameOffset + (long)layout.NameCount * EngineEffectLayout.NameSlotSize > bytes.LongLength;

    private static byte[]? ReadNameSlot(byte[] bytes, EngineEffectLayout layout, int id)
    {
        if (id < 0 || id >= layout.NameCount) return null;
        var offset = layout.NameOffset + id * (long)EngineEffectLayout.NameSlotSize;
        if (offset < 0 || offset + EngineEffectLayout.NameSlotSize > bytes.LongLength) return null;
        return bytes.AsSpan(checked((int)offset), EngineEffectLayout.NameSlotSize).ToArray();
    }

    private static byte[]? ReadDescriptionSlot(byte[]? bytes, EngineEffectLayout layout, int id)
    {
        if (bytes == null || !layout.HasImsg || id < 0 || id >= layout.NameCount) return null;
        var offset = layout.DescriptionOffset + id * (long)layout.DescriptionStride;
        if (offset < 0 || offset + EngineEffectLayout.DescriptionSlotSize > bytes.LongLength) return null;
        return bytes.AsSpan(checked((int)offset), EngineEffectLayout.DescriptionSlotSize).ToArray();
    }

    private static string HexBefore(EngineEffectBinaryImage image, uint address, int length)
    {
        var offset = image.VirtualAddressToFileOffset(address);
        if (offset is not { } fileOffset) return string.Empty;
        var available = (int)Math.Min(length, fileOffset);
        var bytes = image.ReadBytes(fileOffset - available, available);
        return bytes == null ? string.Empty : EngineEffectDecoder.Hex(bytes);
    }
}
