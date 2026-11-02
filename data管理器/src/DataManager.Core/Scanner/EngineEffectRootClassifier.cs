using System.Buffers.Binary;
using System.Globalization;
using DataManager.Core.Models;

namespace DataManager.Core.Scanner;

/// <summary>
/// Explains E8 roots: the process entry, PE exports, inbound direct jumps and
/// function pointers stored in non-executable sections.  A root without any of
/// these is a genuine no-caller gap.
/// </summary>
internal static class EngineEffectRootClassifier
{
    private const int MaximumStoredReferences = 8;
    private const int MaximumFunctionSpan = 0x1000;

    public static IReadOnlyList<EngineEffectRootInfo> Classify(EngineEffectBinaryImage image,
        EngineEffectJumpIndex jumpIndex, EngineEffectCallGraph callGraph,
        IReadOnlyDictionary<uint, IReadOnlyList<int>> effectIdsByFunction,
        IReadOnlyDictionary<int, IReadOnlyList<uint>> reachableFunctionsByEffect,
        IReadOnlyDictionary<int, string> effectNames, IReadOnlySet<uint> effectEntries,
        IEnumerable<uint> roots)
    {
        var addresses = roots.Distinct().OrderBy(address => address).ToArray();
        if (addresses.Length == 0) return [];
        var wanted = new HashSet<uint>(addresses);
        var dataCounts = addresses.ToDictionary(address => address, _ => 0);
        var codeCounts = addresses.ToDictionary(address => address, _ => 0);
        var stored = addresses.ToDictionary(address => address, _ => new List<EngineEffectRootReference>());
        var targetEntries = new Dictionary<uint, uint?>();
        uint? EntryOf(uint target)
        {
            if (targetEntries.TryGetValue(target, out var cached)) return cached;
            var entry = callGraph.CallerEntryOf(target);
            targetEntries[target] = entry;
            return entry;
        }

        foreach (var section in image.Sections)
        {
            var start = checked((int)section.RawPointer);
            var length = checked((int)Math.Min(section.RawSize, image.Bytes.LongLength - start));
            if (length < 4) continue;
            var span = image.Bytes.AsSpan(start, length);
            for (var index = 0; index <= length - 4; index++)
            {
                if ((index & 0xFFFF) == 0) image.Context.CheckCancellation();
                var value = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4));
                if (!wanted.Contains(value)) continue;
                if (section.IsExecutable) codeCounts[value]++;
                else dataCounts[value]++;
                if (stored[value].Count >= MaximumStoredReferences) continue;
                stored[value].Add(new EngineEffectRootReference
                {
                    Section = section.Name,
                    IsExecutable = section.IsExecutable,
                    FileOffset = section.RawPointer + index,
                    Address = section.VirtualAddress + (uint)index
                });
            }
        }

        var result = new List<EngineEffectRootInfo>(addresses.Length);
        foreach (var address in addresses)
        {
            var isEntry = address == image.EntryPoint;
            var isExport = image.ExportedFunctions.Contains(address);
            var count = dataCounts[address] + codeCounts[address];
            var jumps = jumpIndex.SourcesOf(address);
            var interior = callGraph.Calls
                .Where(call => call.Target > address && call.Target < address + MaximumFunctionSpan &&
                               EntryOf(call.Target) == address)
                .OrderBy(call => call.Target).ThenBy(call => call.Address).ToArray();
            var labels = new List<string>(7);
            if (isEntry) labels.Add("PE 入口");
            if (isExport) labels.Add("导出函数");
            var hasInbound = isEntry || isExport || count > 0 || jumps.Count > 0 || interior.Length > 0;
            var profile = EngineEffectFunctionProfiler.Profile(image, address);
            var reach = callGraph.ReachEffect(address, effectEntries);
            var reachableAlternatives = Array.Empty<uint>();
            var boundEffectIds = effectIdsByFunction.TryGetValue(address, out var effectIds) ? effectIds : [];
            var hasEffectBinding = boundEffectIds.Count > 0;
            if (hasEffectBinding)
            {
                var names = boundEffectIds.Take(3).Select(id =>
                    effectNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                        ? $"{id} {name}" : id.ToString(CultureInfo.InvariantCulture));
                labels.Add("特效桩：" + string.Join("/", names) + (boundEffectIds.Count > 3 ? "…" : string.Empty));
                if (!hasInbound)
                {
                    reachableAlternatives = boundEffectIds
                        .SelectMany(id => reachableFunctionsByEffect.TryGetValue(id, out var functions)
                            ? functions : [])
                        .Where(function => function != address).Distinct().OrderBy(value => value).ToArray();
                    if (reachableAlternatives.Length > 0)
                    {
                        labels.Add("死代码特效桩（同特效另有可达桩 " +
                                   string.Join("/", reachableAlternatives.Take(3).Select(value => $"0x{value:X8}")) +
                                   (reachableAlternatives.Length > 3 ? "…" : string.Empty) + "）");
                    }
                }
            }
            if (dataCounts[address] > 0)
                labels.Add($"数据引用 {dataCounts[address].ToString(CultureInfo.InvariantCulture)} 处");
            if (codeCounts[address] > 0)
                labels.Add($"代码引用 {codeCounts[address].ToString(CultureInfo.InvariantCulture)} 处");
            if (jumps.Count > 0) labels.Add($"跳转进入 {jumps.Count.ToString(CultureInfo.InvariantCulture)} 处");
            if (interior.Length > 0)
                labels.Add($"函数体内 E8 入口 {interior.Length.ToString(CultureInfo.InvariantCulture)} 处");
            if (labels.Count == 0)
            {
                if (hasEffectBinding)
                    labels.Add("无静态入边（特效桩，无同特效可达桩）");
                else if (profile.CallsCore)
                    labels.Add("无静态入边（调用核心判定，疑似运行时入口）");
                else if (reach.Found)
                    labels.Add($"无静态入边（经下游间接参与特效链，深度 {reach.Depth}）");
                else
                    labels.Add("无静态入边（非特效代码，未调用核心判定）");
            }
            var noStaticNonEffect = !hasInbound && !hasEffectBinding && !profile.CallsCore && !reach.Found;

            var evidence = new System.Text.StringBuilder();
            evidence.Append($"0x{address:X8}：{string.Join("；", labels)}");
            var first = stored[address].FirstOrDefault();
            if (first != null)
            {
                evidence.Append($"；首处{(first.IsExecutable ? "代码" : "数据")}引用 " +
                                $"{first.Section} 文件偏移 0x{first.FileOffset:X}，地址 0x{first.Address:X8}");
            }
            if (jumps.Count > 0)
            {
                evidence.Append($"；首处跳转 0x{jumps[0].Address:X8}（{jumps[0].Kind}）");
            }
            if (interior.Length > 0)
            {
                evidence.Append($"；首处函数体内入口 0x{interior[0].Address:X8} → 0x{interior[0].Target:X8}");
            }
            if (reachableAlternatives.Length > 0)
            {
                evidence.Append("；死代码依据：本函数无引用/跳转/体内入口，同特效可达桩 " +
                                string.Join("/", reachableAlternatives.Take(MaximumStoredReferences)
                                    .Select(value => $"0x{value:X8}")));
            }
            if (profile.SizeBytes > 0)
            {
                evidence.Append($"；函数 0x{address:X8}–0x{address + (uint)profile.SizeBytes:X8}" +
                                $"（0x{profile.SizeBytes:X} 字节，{profile.FunctionInstructionCount} 条指令，" +
                                $"{(profile.EndsWithReturn ? "以 ret 结束" : "未见 ret")}）");
                evidence.Append($"；E8 调用 {profile.DirectCallCount} 处（核心判定 {profile.EffectCoreCallCount} 处）");
                evidence.Append($"；间接调用 {profile.RegisterCallCount + profile.ExternalCallCount} 处" +
                                $"（寄存器 {profile.RegisterCallCount}/内存 {profile.ExternalCallCount}）");
                evidence.Append($"；入口字节 {profile.EntryBytes}");
                if (profile.PrecedingPaddingBytes > 0)
                    evidence.Append($"；前置填充 {profile.PrecedingPaddingBytes} 字节");
                if (profile.CalleeTargets.Count > 0)
                    evidence.Append("；E8 目标 " + string.Join("/", profile.CalleeTargets.Take(4)
                        .Select(value => $"0x{value:X8}")));
            }
            if (reach.Found)
            {
                var entryName = EngineEffectScanService.EntryNames.TryGetValue(reach.ViaTarget, out var known)
                    ? known : "特效入口";
                evidence.Append($"；特效可达：深度 {reach.Depth}" +
                                (reach.IsIndirect ? "（经中间函数）" : "（直接调用）") +
                                $"，0x{reach.ViaEntry:X8} → 0x{reach.ViaTarget:X8}（{entryName}）" +
                                $"；路径 {string.Join(" → ", reach.Path.Select(value => $"0x{value:X8}"))}");
            }
            else if (reach.Capped)
            {
                evidence.Append($"；特效可达：展开 {reach.ExpandedFunctionCount} 个目标后达到上限，未发现特效入口");
            }
            else
            {
                evidence.Append("；特效可达：沿 E8 直接边未发现核心判定/双渠道包装/取值路径");
            }
            if (noStaticNonEffect)
            {
                evidence.Append("；结论：未调用核心判定/双渠道包装/取值路径，属于普通引擎代码，" +
                                "不是特效消费函数；运行时入口未知");
            }
            else if (!hasInbound && !hasEffectBinding && profile.CallsCore)
            {
                evidence.Append("；结论：函数内含核心判定调用但无静态入边，疑似运行时计算入口");
            }
            else if (!hasInbound && !hasEffectBinding && reach.IsIndirect)
            {
                evidence.Append($"；结论：本身不直接调用特效入口，但经下游函数（深度 {reach.Depth}，" +
                                $"0x{reach.ViaEntry:X8} → 0x{reach.ViaTarget:X8}）进入特效链，" +
                                "属于特效链上游消费者；静态入边仍缺失，运行时入口未知");
            }

            result.Add(new EngineEffectRootInfo
            {
                Address = address,
                IsEntryPoint = isEntry,
                IsExported = isExport,
                DataReferenceCount = dataCounts[address],
                CodeReferenceCount = codeCounts[address],
                InboundJumpCount = jumps.Count,
                InboundJumpSources = jumps.Take(MaximumStoredReferences).Select(jump => jump.Address).ToArray(),
                InteriorEntryCount = interior.Length,
                InteriorEntryTargets = interior.Take(MaximumStoredReferences).Select(call => call.Target).ToArray(),
                InteriorEntryCalls = interior.Take(MaximumStoredReferences).Select(call => call.Address).ToArray(),
                EffectIds = effectIdsByFunction.TryGetValue(address, out var ids) ? ids : [],
                IsDeadEffectStub = reachableAlternatives.Length > 0,
                ReachableAlternatives = reachableAlternatives,
                FunctionSize = profile.SizeBytes,
                FunctionInstructionCount = profile.FunctionInstructionCount,
                EndsWithReturn = profile.EndsWithReturn,
                DirectCallCount = profile.DirectCallCount,
                ExternalCallCount = profile.ExternalCallCount,
                RegisterCallCount = profile.RegisterCallCount,
                EffectCoreCallCount = profile.EffectCoreCallCount,
                CalleeTargets = profile.CalleeTargets,
                EntryBytes = profile.EntryBytes,
                PrecedingPaddingBytes = profile.PrecedingPaddingBytes,
                IsNoStaticNonEffectRoot = noStaticNonEffect,
                EffectReachDepth = reach.Depth,
                EffectReachViaEntry = reach.ViaEntry,
                EffectReachViaTarget = reach.ViaTarget,
                EffectReachExpandedFunctions = reach.ExpandedFunctionCount,
                EffectReachPath = reach.Path,
                DataReferences = stored[address],
                Kind = string.Join("；", labels),
                Evidence = evidence.ToString()
            });
        }
        return result;
    }
}
