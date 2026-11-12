﻿using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectScanTests
{
    public static void Run(string root, string? outputDirectory = null)
    {
        RunSuffixCatalogTests();
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var report = new EngineEffectScanService().Scan(workspace);
        foreach (var row in report.Effects.Where(row => row.SuffixByte is not null))
            TestSupport.Assert(row.SuffixLabel == EngineEffectSuffixLabels.Label(report.EngineVersion, row.SuffixByte!.Value),
                $"后缀标签未按基底生成：{row.EffectId} 0x{row.SuffixByte:X2} {row.SuffixLabel}");
        var suffixSamples = report.EngineVersion switch
        {
            "6.4" => new (int Id, string Fragment)[] { (83, "不处理"), (84, "不处理") },
            "6.5" => new (int Id, string Fragment)[] { (128, "位掩码"), (93, "策略名"), (84, "属性名") },
            "6.6" => new (int Id, string Fragment)[]
            {
                (59, "次"), (149, "范围"), (155, "阵型"), (159, "地形"),
                (51, "攻击/策略/ALL"), (108, "不处理")
            },
            _ => []
        };
        foreach (var sample in suffixSamples)
        {
            var row = report.Effects.FirstOrDefault(item => item.EffectId == sample.Id);
            TestSupport.Assert(row is { HasNameSlot: true } &&
                               row.SuffixLabel.Contains(sample.Fragment, StringComparison.Ordinal),
                $"{report.EngineVersion} 特效 {sample.Id} 后缀标签不符：{row?.SuffixLabel}");
        }
        var invalidSuffix = (byte)(EngineEffectSuffixLabels.MaximumSuffix(report.EngineVersion) + 1);
        var suffixRow = report.Effects.FirstOrDefault(item => item.HasNameSlot && item.SuffixByte != invalidSuffix);
        if (suffixRow != null && invalidSuffix <= byte.MaxValue)
        {
            var suffixEdit = new EngineEffectSlotService().PreviewSuffix(workspace, suffixRow.EffectId, invalidSuffix);
            TestSupport.Assert(suffixEdit.ValidationError != null &&
                               suffixEdit.ValidationError.Contains(report.EngineVersion, StringComparison.Ordinal),
                $"{report.EngineVersion} 未拒绝超范围后缀 0x{invalidSuffix:X2}：{suffixEdit.ValidationError}");
        }
        WriteBaselineDiagnostics(report, root, outputDirectory);
        foreach (var line in report.Diagnostics.Where(line =>
                     line.StartsWith("指令边界图", StringComparison.Ordinal) ||
                     line.StartsWith("静态分配表", StringComparison.Ordinal) ||
                     line.StartsWith("内嵌特效名称池", StringComparison.Ordinal) ||
                     line.StartsWith("特效号 255", StringComparison.Ordinal) ||
                     line.StartsWith("通用分派链", StringComparison.Ordinal)))
            Console.WriteLine("DIAG " + line);
        TestSupport.Assert(report.NameCount is 180 or 255, $"名称行数异常：{report.NameCount}");
        TestSupport.Assert(report.Effects.Count >= report.NameCount, "名称行未完整生成。");
        TestSupport.Assert(report.ExeSha256.Length == 64, "缺少 Ekd5.exe SHA-256。");
        TestSupport.Assert(report.CallSiteCount ==
                          report.ResolvedCallSiteCount + report.DynamicCallSiteCount + report.UnresolvedCallSiteCount,
            "调用点分类计数不一致。");
        var expected = report.EngineVersion switch
        {
            "6.4" => (Total: 99, Static: 90, Dynamic: 4, Unresolved: 5, Internal: 0, Effective: 99,
                Core: 97, Wrapper: 0, Value: 5, Dispatcher: 2),
            "6.5" => (Total: 183, Static: 165, Dynamic: 8, Unresolved: 10, Internal: 1, Effective: 182,
                Core: 159, Wrapper: 22, Value: 5, Dispatcher: 2),
            "6.6" => (Total: 201, Static: 169, Dynamic: 15, Unresolved: 17, Internal: 1, Effective: 200,
                Core: 143, Wrapper: 56, Value: 7, Dispatcher: 2),
            _ => (Total: -1, Static: -1, Dynamic: -1, Unresolved: -1, Internal: -1, Effective: -1,
                Core: -1, Wrapper: -1, Value: -1, Dispatcher: -1)
        };
        if (expected.Total >= 0)
        {
            TestSupport.Assert(report.CallSiteCount == expected.Total, $"调用点总数不符：{report.CallSiteCount}/{expected.Total}");
            TestSupport.Assert(report.ResolvedCallSiteCount == expected.Static, $"静态调用数不符：{report.ResolvedCallSiteCount}");
            TestSupport.Assert(report.DynamicCallSiteCount == expected.Dynamic, $"动态调用数不符：{report.DynamicCallSiteCount}");
            TestSupport.Assert(report.UnresolvedCallSiteCount == expected.Unresolved, $"未解析调用数不符：{report.UnresolvedCallSiteCount}");
            TestSupport.Assert(report.InternalCallSiteCount == expected.Internal, $"引擎内部调用数不符：{report.InternalCallSiteCount}");
            TestSupport.Assert(report.EffectiveCallSiteCount == expected.Effective, $"有效调用数不符：{report.EffectiveCallSiteCount}");
            var stats = report.EntryStatistics.ToDictionary(item => item.Target);
            TestSupport.Assert(stats[EngineEffectScanService.CoreEntry].Total == expected.Core, "核心判定调用数不符。");
            TestSupport.Assert(stats[EngineEffectScanService.WrapperEntry].Total == expected.Wrapper, "双渠道包装调用数不符。");
            TestSupport.Assert(stats[EngineEffectScanService.DispatcherEntry].Total == expected.Dispatcher,
                "直接分派桩调用数不符。");
            TestSupport.Assert(stats[EngineEffectScanService.DispatcherEntry].Static == expected.Dispatcher,
                "直接分派桩应全部为静态常量参数。");
            TestSupport.Assert(!stats.ContainsKey(EngineEffectScanService.ValueEntry), "寄存器聚合器不应按四参数入口统计。");
            var internalAddresses = report.InternalCalls.Select(call => call.CallAddress).OrderBy(value => value).ToArray();
            var expectedInternal = report.EngineVersion switch
            {
                "6.4" => Array.Empty<uint>(),
                _ => new uint[] { 0x004251AB }
            };
            TestSupport.Assert(internalAddresses.SequenceEqual(expectedInternal.OrderBy(value => value)),
                "引擎内部调用地址集合不符：" + string.Join(",", internalAddresses.Select(value => value.ToString("X8"))));
        }
        TestSupport.Assert(report.EntryPoint != 0, "未解析 PE 入口点。");
        TestSupport.Assert(report.EntryPoint == 0x004817E0, $"PE 入口点异常：0x{report.EntryPoint:X8}");
        TestSupport.Assert(report.UnalignedCallAddresses.Count == report.UnalignedCallCount &&
                           report.UnalignedCallAddresses.SequenceEqual(
                               report.UnalignedCallAddresses.OrderBy(address => address)),
            "未对齐 E8 字节统计不一致。");
        TestSupport.Assert(report.UnalignedCallCount == report.EngineVersion switch
        {
            "6.4" => 84,
            "6.5" => 79,
            "6.6" => 61,
            _ => -1
        }, $"未对齐 E8 字节数量异常：{report.UnalignedCallCount}");
        var expectedAnchors = report.EngineVersion switch
        {
            "6.4" => (Instructions: 143812, Anchors: 510),
            "6.5" => (Instructions: 142291, Anchors: 435),
            "6.6" => (Instructions: 139244, Anchors: 385),
            _ => (Instructions: 0, Anchors: 0)
        };
        if (expectedAnchors.Anchors > 0)
        {
            TestSupport.Assert(report.InstructionCount == expectedAnchors.Instructions,
                $"指令边界图指令数不符：{report.InstructionCount}/{expectedAnchors.Instructions}");
            TestSupport.Assert(report.IndexedJumpAnchorCount == expectedAnchors.Anchors,
                $"索引跳转锚点数不符：{report.IndexedJumpAnchorCount}/{expectedAnchors.Anchors}");
        }
        var genericDispatch = report.Diagnostics.FirstOrDefault(line =>
            line.Contains("通用分派链", StringComparison.Ordinal));
        var expectedDispatch = report.EngineVersion switch
        {
            "6.4" => (Table: 0x00508998u, Handler: 0x0050E400u),
            "6.5" => (Table: 0x00507800u, Handler: 0x0050E000u),
            "6.6" => (Table: 0x00511000u, Handler: 0x0050E000u),
            _ => (Table: 0u, Handler: 0u)
        };
        TestSupport.Assert(genericDispatch != null &&
                           genericDispatch.Contains($"0x{expectedDispatch.Table:X8}", StringComparison.Ordinal) &&
                           genericDispatch.Contains($"0x{expectedDispatch.Handler:X8}", StringComparison.Ordinal),
            $"通用分派链未识别：{genericDispatch ?? "—"}");
        var expectedAssignment = report.EngineVersion switch
        {
            "6.4" => (Rows: 180, Size: 8, Persons: 3, Jobs: 1, Shared: true, NonEmpty: 75, PersonRows: 55,
                JobRows: 28, Table: 0x00508998u, Offset: 0xDA198L),
            "6.5" => (Rows: 255, Size: 8, Persons: 3, Jobs: 1, Shared: true, NonEmpty: 157, PersonRows: 145,
                JobRows: 48, Table: 0x00507800u, Offset: 0xD9000L),
            "6.6" => (Rows: 255, Size: 16, Persons: 4, Jobs: 2, Shared: false, NonEmpty: 111, PersonRows: 0,
                JobRows: 111, Table: 0x00511000u, Offset: 0xE2800L),
            _ => (Rows: 0, Size: 0, Persons: 0, Jobs: 0, Shared: false, NonEmpty: 0, PersonRows: 0, JobRows: 0,
                Table: 0u, Offset: 0L)
        };
        if (expectedAssignment.Rows > 0)
        {
            var table = report.AssignmentTable;
            TestSupport.Assert(table is { Verified: true } &&
                               table.PointerAddress == 0x00500C3B &&
                               table.TableAddress == expectedAssignment.Table &&
                               table.TableFileOffset == expectedAssignment.Offset &&
                               table.RowCount == expectedAssignment.Rows &&
                               table.RowSize == expectedAssignment.Size &&
                               table.PersonSlotCount == expectedAssignment.Persons &&
                               table.JobSlotCount == expectedAssignment.Jobs &&
                               table.SharedValue == expectedAssignment.Shared &&
                               table.NonEmptyRowCount == expectedAssignment.NonEmpty &&
                               table.PersonRowCount == expectedAssignment.PersonRows &&
                               table.JobRowCount == expectedAssignment.JobRows,
                $"静态分配表几何或行统计不符：{table?.RowCount}×{table?.RowSize}，" +
                $"非空 {table?.NonEmptyRowCount}（人物 {table?.PersonRowCount} / 兵种 {table?.JobRowCount}）");
            TestSupport.Assert(report.Effects.Count(row => row.AssignmentCovered) == expectedAssignment.NonEmpty,
                "静态分配表覆盖的特效行数与表内非空行数不一致。");
            TestSupport.Assert(report.Effects.Where(row => row.AssignmentCovered)
                    .All(row => row.AssignmentRowOffset >= 0), "静态分配表覆盖行缺少文件偏移。");
        }
        var expectedNamePool = report.EngineVersion switch
        {
            "6.4" => (Address: 0x004FF560u, Offset: 0xD0D60L, Rows: 180),
            "6.5" => (Address: 0x004CC600u, Offset: 0x9E800L, Rows: 255),
            "6.6" => (Address: 0x004CC600u, Offset: 0x9E800L, Rows: 255),
            _ => (Address: 0u, Offset: 0L, Rows: 0)
        };
        if (expectedNamePool.Rows > 0)
        {
            var namePool = report.EmbeddedNamePool;
            TestSupport.Assert(namePool is { Verified: true, SlotSize: 16, IsWritable: false } &&
                               namePool.TableAddress == expectedNamePool.Address &&
                               namePool.TableFileOffset == expectedNamePool.Offset &&
                               namePool.RowCount == expectedNamePool.Rows,
                $"内嵌特效名称池未定位或几何不符：0x{namePool?.TableAddress:X8}/+0x{namePool?.TableFileOffset:X}");
        }
        var entryRoot = report.RootInfos.FirstOrDefault(info => info.Address == report.EntryPoint);
        TestSupport.Assert(entryRoot is { IsEntryPoint: true }, "E8 根分类缺少 PE 入口。");
        TestSupport.Assert(report.RootInfos.Any(info => info.DataReferenceCount > 0 ||
                                                       info.CodeReferenceCount > 0 ||
                                                       info.InboundJumpCount > 0 ||
                                                       info.InteriorEntryCount > 0),
            "E8 根分类没有识别任何数据/代码/跳转/函数体内入口。");
        TestSupport.Assert(report.DeadEffectStubCount == report.RootInfos.Count(info => info.IsDeadEffectStub),
            "死代码特效桩计数与根分类不一致。");
        TestSupport.Assert(report.RootInfos.Where(info => info.IsDeadEffectStub)
                .All(info => info.ReferenceCount == 0 && info.InboundJumpCount == 0 &&
                             info.InteriorEntryCount == 0 && info.ReachableAlternatives.Count > 0 &&
                             info.Evidence.Contains("死代码依据", StringComparison.Ordinal)),
            "死代码特效桩缺少无入边或同特效可达桩证据。");
        TestSupport.Assert(report.RootInfos.All(info => info.FunctionSize > 0 && info.EntryBytes.Length > 0),
            "E8 根没有生成函数画像。");
        TestSupport.Assert(report.RootInfos.Where(info => info.IsNoStaticNonEffectRoot)
                .All(info => info.EffectIds.Count == 0 && info.EffectCoreCallCount == 0 &&
                             info.ReferenceCount == 0 && info.InboundJumpCount == 0 &&
                             info.InteriorEntryCount == 0 &&
                             info.Evidence.Contains("不是特效消费函数", StringComparison.Ordinal)),
            "非特效无入边根缺少普通代码证据。");
        TestSupport.Assert(report.RootInfos.Where(info => info.IsDeadEffectStub)
                .All(info => info.EffectCoreCallCount > 0 && info.Evidence.Contains("核心判定", StringComparison.Ordinal)),
            "死代码特效桩没有调用核心判定的证据。");
        var upstreamRoots = report.RootInfos.Where(info => !info.IsEntryPoint && !info.IsExported &&
                                                           info.ReferenceCount == 0 && info.InboundJumpCount == 0 &&
                                                           info.InteriorEntryCount == 0 && info.EffectIds.Count == 0 &&
                                                           !info.CallsEffectCore && info.ReachesEffectIndirectly).ToArray();
        TestSupport.Assert(upstreamRoots.All(info => info.EffectReachPath.Count == info.EffectReachDepth + 1 &&
                                                     info.EffectReachPath[0] == info.Address &&
                                                     info.EffectReachPath[^1] == info.EffectReachViaTarget &&
                                                     info.Evidence.Contains("上游消费者", StringComparison.Ordinal)),
            "无静态入边的特效链上游根缺少最短 E8 路径证据。");
        TestSupport.Assert(upstreamRoots.Length == report.EngineVersion switch
        {
            "6.5" => 1,
            "6.6" => 3,
            _ => 0
        }, $"间接进入特效链的无入边根数量异常：{upstreamRoots.Length}");
        if (report.EngineVersion == "6.4")
        {
            TestSupport.Assert(report.DeadEffectStubCount == 0, "6.4 不应出现未被引用的特效桩。");
        }
        else if (report.EngineVersion == "6.5")
        {
            var deadStubs = report.RootInfos.Where(info => info.IsDeadEffectStub)
                .ToDictionary(info => info.Address);
            TestSupport.Assert(deadStubs.Count == 4, $"6.5 死代码特效桩数量异常：{deadStubs.Count}");
            TestSupport.Assert(deadStubs.ContainsKey(0x00405CB9) && deadStubs.ContainsKey(0x004060D2) &&
                               deadStubs.ContainsKey(0x004063FB) && deadStubs.ContainsKey(0x0040CCE2),
                "6.5 死代码特效桩地址集合不符。");
            TestSupport.Assert(deadStubs[0x00405CB9].ReachableAlternatives.Any(address => address == 0x004E40E0),
                "6.5 特效 52 死代码桩没有指向可达桩 0x004E40E0。");
            TestSupport.Assert(deadStubs[0x00405CB9].Kind.Contains("52 主动连击", StringComparison.Ordinal),
                "6.5 死代码桩缺少特效名称。");
            var unreferenced = report.RootInfos.FirstOrDefault(info => info.Address == 0x004404B2);
            TestSupport.Assert(unreferenced is { IsDeadEffectStub: false },
                "6.5 无特效的 0x004404B2 不应标记为死代码特效桩。");
            TestSupport.Assert(unreferenced is
                               {
                                   IsNoStaticNonEffectRoot: false, EffectCoreCallCount: 0,
                                   FunctionSize: 0x86D, FunctionInstructionCount: 850,
                                   PrecedingPaddingBytes: 64, EndsWithReturn: true,
                                   EffectReachDepth: 2, EffectReachViaEntry: 0x0043BC62,
                                   EffectReachViaTarget: 0x004101D9
                               } &&
                               unreferenced.EffectReachPath.SequenceEqual(new[]
                                   { 0x004404B2u, 0x0043BC62u, 0x004101D9u }) &&
                               unreferenced.Evidence.Contains("上游消费者", StringComparison.Ordinal),
                "6.5 0x004404B2 根画像不符：应是无入边但经 0x0043BC62 间接进入核心判定的上游函数。");
            var dispatchRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x0043BC62);
            TestSupport.Assert(dispatchRole is { Role: "特效判定分发器（4 个分支）" } &&
                               dispatchRole.EffectIds.SequenceEqual(new[] { 22, 25, 34, 35 }) &&
                               dispatchRole.Evidence.Contains("0x004101D9", StringComparison.Ordinal),
                "6.5 0x0043BC62 应识别为覆盖 22/25/34/35 的核心判定分发器。");
            var upstreamRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x004404B2);
            TestSupport.Assert(upstreamRole is { Role: "上游消费者（深度 2）" },
                "6.5 0x004404B2 应识别为深度 2 的上游消费者。");
        }
        else if (report.EngineVersion == "6.6")
        {
            var deadStubs = report.RootInfos.Where(info => info.IsDeadEffectStub)
                .ToDictionary(info => info.Address);
            TestSupport.Assert(deadStubs.Count == 2, $"6.6 死代码特效桩数量异常：{deadStubs.Count}");
            TestSupport.Assert(deadStubs.TryGetValue(0x00405CB9, out var dead52) &&
                               dead52.ReachableAlternatives.Any(address => address == 0x004E40E0),
                "6.6 特效 52 死代码桩没有指向可达桩 0x004E40E0。");
            var noStaticRoots = report.RootInfos.Where(info => info.IsNoStaticNonEffectRoot)
                .ToDictionary(info => info.Address);
            TestSupport.Assert(noStaticRoots.Count == 0,
                $"6.6 无静态入边非特效根数量异常：{noStaticRoots.Count}");
            var accessor = report.RootInfos.FirstOrDefault(info => info.Address == 0x004C0400);
            TestSupport.Assert(accessor is
                               {
                                   IsNoStaticNonEffectRoot: false, EffectCoreCallCount: 0,
                                   FunctionSize: 0x1E, FunctionInstructionCount: 13,
                                   EffectReachDepth: 3, EffectReachViaEntry: 0x004E30E1,
                                   EffectReachViaTarget: 0x004101D9
                               } &&
                               accessor.EffectReachPath.SequenceEqual(new[]
                                   { 0x004C0400u, 0x004064F1u, 0x004E30E1u, 0x004101D9u }) &&
                               accessor.Evidence.Contains("上游消费者", StringComparison.Ordinal),
                "6.6 0x004C0400 根画像不符：应为 0x1E 字节且经 0x004064F1 进入特效链的上游函数。");
            var dispatchRoot = report.RootInfos.FirstOrDefault(info => info.Address == 0x004207B2);
            TestSupport.Assert(dispatchRoot is
                               {
                                   IsNoStaticNonEffectRoot: false, EffectCoreCallCount: 0,
                                   FunctionSize: 0x1A0, FunctionInstructionCount: 121,
                                   EffectReachDepth: 4, EffectReachViaEntry: 0x004E30E1,
                                   EffectReachViaTarget: 0x004101D9
                               } &&
                               dispatchRoot.EffectReachPath.SequenceEqual(new[]
                                   { 0x004207B2u, 0x0045321Au, 0x004064F1u, 0x004E30E1u, 0x004101D9u }),
                "6.6 0x004207B2 事件分派根应经 4 层进入核心判定。");
            var predicateRoot = report.RootInfos.FirstOrDefault(info => info.Address == 0x0047EF3D);
            TestSupport.Assert(predicateRoot is
                               {
                                   IsNoStaticNonEffectRoot: false, EffectCoreCallCount: 0,
                                   FunctionSize: 0x22, FunctionInstructionCount: 14,
                                   PrecedingPaddingBytes: 31,
                                   EffectReachDepth: 2, EffectReachViaEntry: 0x0041CDDC,
                                   EffectReachViaTarget: 0x004101D9
                               } &&
                               predicateRoot.EffectReachPath.SequenceEqual(new[]
                                   { 0x0047EF3Du, 0x0041CDDCu, 0x004101D9u }),
                "6.6 0x0047EF3D 谓词根应经 2 层进入核心判定。");
            var queryRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x004E30E1);
            TestSupport.Assert(queryRole is { Role: "特效等级查询" } &&
                               queryRole.EffectIds.SequenceEqual(new[] { 181 }) &&
                               queryRole.Evidence.Contains("关联特效 181 一剑一念", StringComparison.Ordinal),
                "6.6 0x004E30E1 应识别为特效 181 的等级查询。");
            var stubRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x0041CDDC);
            TestSupport.Assert(stubRole is { Role: "特效判定桩" } &&
                               stubRole.EffectIds.SequenceEqual(new[] { 47 }),
                "6.6 0x0041CDDC 应识别为特效 47 的判定桩。");
            var consumerRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x0045321A);
            TestSupport.Assert(consumerRole is { Role: "上游消费者（深度 3）" } &&
                               consumerRole.Evidence.Contains("0x004064F1", StringComparison.Ordinal),
                "6.6 0x0045321A 应识别为经 0x004064F1 的深度 3 上游消费者。");
            var accessorRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x004064F1);
            TestSupport.Assert(accessorRole is { Role: "上游消费者（深度 2）" },
                "6.6 0x004064F1 应识别为深度 2 的上游消费者。");
            var rootRole = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x004C0400);
            TestSupport.Assert(rootRole is { Role: "上游消费者（深度 3）" },
                "6.6 0x004C0400 应识别为深度 3 的上游消费者。");
        }
        TestSupport.Assert(report.FunctionRoles.Count > 0 &&
                           report.FunctionRoles.All(role => role.Entry != 0 && role.Role.Length > 0 &&
                                                            role.Evidence.Length > 0),
            "函数角色列表为空或存在空角色。");
        var expectedIndirect = report.EngineVersion switch
        {
            // 索引跳转表锚点让两处“伪成员调用”落入真实指令内部而被剔除：
            // 6.4 0x0047A5E1（[eax-0x6F6F6F70]）、6.6 0x0046C2B0（[eax+0x04032D90]）。
            "6.4" => 2362,
            "6.5" => 2309,
            "6.6" => 2371,
            _ => 0
        };
        TestSupport.Assert(report.IndirectCallCount == expectedIndirect,
            $"间接调用总数不符：{report.IndirectCallCount}/{expectedIndirect}");
        TestSupport.Assert(report.IndirectCalls.Count == report.IndirectCallCount,
            "间接调用明细条数与统计总数不一致。");
        TestSupport.Assert(report.IndirectImportCount + report.IndirectRegisterCount + report.IndirectMemberCount +
                           report.IndirectTableCount + report.IndirectCodeTargetCount == report.IndirectCallCount,
            "间接调用分类计数之和与总数不一致。");
        TestSupport.Assert(report.IndirectEffectEntryCount == 0,
            $"间接调用不得进入特效入口，实际命中 {report.IndirectEffectEntryCount} 处。");
        TestSupport.Assert(report.IndirectCalls.All(call => !call.TargetsEffectEntry && call.Evidence.Length > 0 &&
                                                          call.Kind != "特效入口"),
            "存在命中特效入口的间接调用，或缺少证据。");
        TestSupport.Assert(report.EntryPointerCount == 0, $"特效入口绝对指针数量异常：{report.EntryPointerCount}");
        TestSupport.Assert(report.EntryJumpCount == 0, $"直达特效入口的跳转数量异常：{report.EntryJumpCount}");
        var codeTargets = report.IndirectCalls.Where(call => call.Kind == "代码目标").ToArray();
        TestSupport.Assert(codeTargets.Length == 1 &&
                           codeTargets[0] is { Address: 0x00481995, ResolvedTarget: 0x00481650, CallerEntry: 0x0048196D },
            "唯一的间接代码目标应是 0x0048196D 内的 CRT 回调 0x00481650。");
        var memberCalls = report.IndirectCalls.Where(call => call.Kind == "成员").ToArray();
        TestSupport.Assert(memberCalls.All(call => call.MemberExpression.Length > 1 &&
                                                   call.MemberExpression[0] == '[' &&
                                                   call.MemberExpression[^1] == ']' &&
                                                   call.Evidence.Contains("成员调用", StringComparison.Ordinal)),
            "成员间接调用缺少 [reg+disp] 表达式或证据。");
        var tableCalls = report.IndirectCalls.Where(call => call.Kind == "表").ToArray();
        TestSupport.Assert(tableCalls.All(call => call.MemberExpression.Length > 1 &&
                                                  call.Evidence.Contains("索引", StringComparison.Ordinal)),
            "表间接调用缺少带索引的内存表达式。");
        TestSupport.Assert(tableCalls.Count(call => call.TableAddress != null) > 0 &&
                           tableCalls.Where(call => call.TableAddress != null)
                               .All(call => call.Evidence.Contains("偏移表", StringComparison.Ordinal)),
            "无基址的静态偏移表调用没有标注表地址。");
        var expectedBranch = report.EngineVersion switch
        {
            "6.4" => (Paths: 97, Conditions: 148, Unreachable: 0),
            "6.5" => (Paths: 181, Conditions: 252, Unreachable: 4),
            "6.6" => (Paths: 199, Conditions: 159, Unreachable: 2),
            _ => (Paths: 0, Conditions: 0, Unreachable: 0)
        };
        TestSupport.Assert(report.BranchPathCount == expectedBranch.Paths,
            $"判定路径总数不符：{report.BranchPathCount}/{expectedBranch.Paths}");
        TestSupport.Assert(report.BranchConditionCount == expectedBranch.Conditions,
            $"分支条件总数不符：{report.BranchConditionCount}/{expectedBranch.Conditions}");
        TestSupport.Assert(report.BranchPathUnreachableCount == expectedBranch.Unreachable,
            $"不可回溯判定路径数不符：{report.BranchPathUnreachableCount}/{expectedBranch.Unreachable}");
        var allBranchPaths = report.FunctionRoles.SelectMany(role => role.BranchPaths).ToArray();
        TestSupport.Assert(allBranchPaths.Length == report.BranchPathCount &&
                           allBranchPaths.All(path => path.Summary.Length > 0 && path.Target != 0 &&
                                                      path.TargetName.Length > 0),
            "判定路径明细与统计不一致或缺少目标/摘要。");
        TestSupport.Assert(allBranchPaths.Where(path => path.Reachable)
                .All(path => path.Conditions.All(condition => condition.Address != 0 &&
                                                              condition.Instruction.Length > 0)),
            "可达判定路径中存在缺少地址或指令文本的分支条件。");
        var notTaken = allBranchPaths.SelectMany(path => path.Conditions)
            .Where(condition => !condition.Taken).ToArray();
        TestSupport.Assert(notTaken.Length > 0 &&
                           notTaken.All(condition => condition.Target != 0) &&
                           allBranchPaths.Where(path => path.Conditions.Any(condition => !condition.Taken))
                               .All(path => path.Summary.Contains("（不跳转）", StringComparison.Ordinal)),
            "不跳转一侧的守卫没有被记录为条件，或摘要缺少“（不跳转）”标注。");
        TestSupport.Assert(report.BranchPathUpstreamResolvedCount ==
                           allBranchPaths.Count(path => path.InboundJumpAddress != null &&
                                                        path.InboundJumpReachable != false &&
                                                        path.UpstreamSummary.Length > 0) &&
                           report.BranchPathUpstreamConditionCount ==
                           allBranchPaths.Sum(path => path.UpstreamConditions.Count),
            "上游条件回溯统计不一致。");
        TestSupport.Assert(allBranchPaths.Where(path => path.UpstreamConditions.Count > 0)
                .All(path => path.InboundJumpFunction != null &&
                             path.UpstreamSummary.Length > 0 &&
                             path.Summary.Contains("上游条件", StringComparison.Ordinal) &&
                             path.UpstreamConditions.All(condition => condition.Address != 0 &&
                                                                     condition.Instruction.Length > 0)),
            "已回溯上游守卫的调用点缺少发起函数、上游摘要或指令文本。");
        var reachableBranchPaths = allBranchPaths.Where(path => path.Reachable).ToArray();
        TestSupport.Assert(reachableBranchPaths.All(path => path.PathCount >= 1 &&
                                                            path.MultiPathSummary.Length > 0) &&
                           allBranchPaths.Where(path => !path.Reachable).All(path => path.PathCount == 0),
            "多路径枚举计数与可达性不一致。");
        TestSupport.Assert(report.BranchMultiPathCount ==
                           reachableBranchPaths.Count(path => path.PathCount > 1) &&
                           report.BranchCommonConditionCount ==
                           allBranchPaths.Sum(path => path.CommonConditions.Count) &&
                           report.BranchOptionalConditionCount ==
                           allBranchPaths.Sum(path => path.OptionalConditions.Count) &&
                           report.BranchMultiPathTruncatedCount ==
                           allBranchPaths.Count(path => path.PathsTruncated) &&
                           report.BranchConflictingConditionCount ==
                           allBranchPaths.Sum(path => path.ConflictingConditionCount) &&
                           report.BranchUpstreamCommonConditionCount ==
                           allBranchPaths.Sum(path => path.UpstreamCommonConditions.Count) &&
                           report.BranchUpstreamOptionalConditionCount ==
                           allBranchPaths.Sum(path => path.UpstreamOptionalConditions.Count),
            "多路径守卫统计与明细不一致。");
        TestSupport.Assert(report.BranchConditionGroupCount ==
                           allBranchPaths.Sum(path => path.ConditionGroups.Count) &&
                           report.BranchConditionGroupDuplicatePathCount ==
                           allBranchPaths.Sum(path => path.ConditionGroupDuplicatePathCount) &&
                           report.BranchConditionGroupLimitCount ==
                           allBranchPaths.Count(path => path.ConditionGroupsTruncated) &&
                           report.BranchUpstreamConditionGroupCount ==
                           allBranchPaths.Sum(path => path.UpstreamConditionGroups.Count),
            "条件组（DNF）统计与明细不一致。");
        var expectedGroups = report.EngineVersion switch
        {
            "6.4" => (Groups: 453, Duplicates: 0, Limit: 0, Upstream: 0),
            "6.5" => (Groups: 773, Duplicates: 0, Limit: 0, Upstream: 45),
            "6.6" => (Groups: 481, Duplicates: 0, Limit: 0, Upstream: 0),
            _ => (Groups: 0, Duplicates: 0, Limit: 0, Upstream: 0)
        };
        TestSupport.Assert(report.BranchConditionGroupCount == expectedGroups.Groups &&
                           report.BranchConditionGroupDuplicatePathCount == expectedGroups.Duplicates &&
                           report.BranchConditionGroupLimitCount == expectedGroups.Limit &&
                           report.BranchUpstreamConditionGroupCount == expectedGroups.Upstream,
            $"条件组基线不符：{report.BranchConditionGroupCount}/{report.BranchConditionGroupDuplicatePathCount}/" +
            $"{report.BranchConditionGroupLimitCount}/{report.BranchUpstreamConditionGroupCount}，应为 " +
            $"{expectedGroups.Groups}/{expectedGroups.Duplicates}/{expectedGroups.Limit}/{expectedGroups.Upstream}");
        var expectedDecisions = report.EngineVersion switch
        {
            "6.4" => (Decisions: 47, Complete: 3, Missing: 0, Upstream: 0,
                NonFixed: 71, Repeated: 0, UpNonFixed: 0, UpRepeated: 0),
            "6.5" => (Decisions: 118, Complete: 25, Missing: 0, Upstream: 5,
                NonFixed: 69, Repeated: 0, UpNonFixed: 8, UpRepeated: 1),
            "6.6" => (Decisions: 54, Complete: 5, Missing: 0, Upstream: 0,
                NonFixed: 56, Repeated: 0, UpNonFixed: 0, UpRepeated: 0),
            _ => (Decisions: 0, Complete: 0, Missing: 0, Upstream: 0,
                NonFixed: 0, Repeated: 0, UpNonFixed: 0, UpRepeated: 0)
        };
        TestSupport.Assert(report.BranchConditionDecisionCount == expectedDecisions.Decisions &&
                           report.BranchConditionDecisionCompleteCount == expectedDecisions.Complete &&
                           report.BranchConditionDecisionMissingCount == expectedDecisions.Missing &&
                           report.BranchUpstreamConditionDecisionCount == expectedDecisions.Upstream &&
                           report.BranchConditionDecisionNonFixedCount == expectedDecisions.NonFixed &&
                           report.BranchConditionDecisionRepeatedCount == expectedDecisions.Repeated &&
                           report.BranchUpstreamConditionDecisionNonFixedCount == expectedDecisions.UpNonFixed &&
                           report.BranchUpstreamConditionDecisionRepeatedCount == expectedDecisions.UpRepeated,
            $"决策点基线不符：{report.BranchConditionDecisionCount}/{report.BranchConditionDecisionCompleteCount}/" +
            $"{report.BranchConditionDecisionMissingCount}/{report.BranchUpstreamConditionDecisionCount}/" +
            $"{report.BranchConditionDecisionNonFixedCount}/{report.BranchConditionDecisionRepeatedCount}/" +
            $"{report.BranchUpstreamConditionDecisionNonFixedCount}/" +
            $"{report.BranchUpstreamConditionDecisionRepeatedCount}，应为 " +
            $"{expectedDecisions.Decisions}/{expectedDecisions.Complete}/{expectedDecisions.Missing}/" +
            $"{expectedDecisions.Upstream}/{expectedDecisions.NonFixed}/{expectedDecisions.Repeated}/" +
            $"{expectedDecisions.UpNonFixed}/{expectedDecisions.UpRepeated}");
        TestSupport.Assert(report.BranchConditionDecisionCount ==
                           allBranchPaths.Sum(path => path.ConditionDecisions.Count) &&
                           report.BranchConditionDecisionCompleteCount ==
                           allBranchPaths.Count(path => path.ConditionDecisionSpaceComplete) &&
                           report.BranchConditionDecisionMissingCount ==
                           allBranchPaths.Sum(path => path.ConditionDecisionMissingCount) &&
                           report.BranchUpstreamConditionDecisionCount ==
                           allBranchPaths.Sum(path => path.UpstreamConditionDecisions.Count),
            "决策点统计与明细不一致。");
        TestSupport.Assert(allBranchPaths.All(path =>
                path.PathCount <= 1
                    ? path.ConditionDecisions.Count == 0 && path.ConditionDecisionCombinationSpace == 0 &&
                      !path.ConditionDecisionSpaceComplete && path.ConditionDecisionMissingCount == 0
                    : path.ConditionDecisionCombinationSpace ==
                      (path.ConditionDecisions.Count == 0 ? 0 : 1 << path.ConditionDecisions.Count) &&
                      path.ConditionDecisions.Select(decision => decision.Address).Distinct().Count() ==
                      path.ConditionDecisions.Count &&
                      path.ConditionDecisions.All(decision => decision.Instruction.Length > 0 &&
                                                              decision.TakenPaths > 0 &&
                                                              decision.NotTakenPaths > 0 &&
                                                              decision.TakenPaths + decision.NotTakenPaths ==
                                                              path.PathCount) &&
                      (path.ConditionDecisionSpaceComplete
                          ? path.ConditionGroups.Count == path.ConditionDecisionCombinationSpace &&
                            path.ConditionDecisionMissingCount == 0
                          : path.ConditionDecisionMissingCount == 0 ||
                            path.ConditionGroups.Count + path.ConditionDecisionMissingCount ==
                            path.ConditionDecisionCombinationSpace)) &&
                           allBranchPaths.All(path =>
                               path.UpstreamConditionDecisionCombinationSpace ==
                               (path.UpstreamConditionDecisions.Count == 0
                                   ? 0
                                   : 1 << path.UpstreamConditionDecisions.Count)) &&
                           allBranchPaths.Where(path => path.ConditionDecisions.Count > 0)
                               .All(path => path.ConditionDecisionSummary.Length > 0),
            "决策点与组合空间不完整：2^k 不成立、方向计数与路径数不符，或组合覆盖语义错误。");
        TestSupport.Assert(report.BranchConditionDecisionNonFixedCount ==
                           allBranchPaths.Sum(path => path.ConditionDecisionNonFixedPoints.Count) &&
                           report.BranchConditionDecisionRepeatedCount ==
                           allBranchPaths.Sum(path => path.ConditionDecisionRepeatedPoints.Count) &&
                           report.BranchUpstreamConditionDecisionNonFixedCount ==
                           allBranchPaths.Sum(path => path.UpstreamConditionDecisionNonFixedPoints.Count) &&
                           report.BranchUpstreamConditionDecisionRepeatedCount ==
                           allBranchPaths.Sum(path => path.UpstreamConditionDecisionRepeatedPoints.Count),
            "非固定/重复求值分支点统计与明细不一致。");
        TestSupport.Assert(allBranchPaths.All(path =>
                path.ConditionDecisionNonFixedPoints.All(point =>
                    point.PresentPaths > 0 && point.PresentPaths < point.PathCount &&
                    point.PathCount == path.PathCount && !point.Repeated && point.Instruction.Length > 0 &&
                    !path.ConditionDecisions.Any(decision => decision.Address == point.Address)) &&
                path.ConditionDecisionNonFixedPoints.Select(point => point.Address).Distinct().Count() ==
                path.ConditionDecisionNonFixedPoints.Count &&
                path.ConditionDecisionRepeatedPoints.All(point =>
                    point.Repeated && point.PresentPaths > 0 && point.PathCount == path.PathCount &&
                    point.Instruction.Length > 0 &&
                    !path.ConditionDecisions.Any(decision => decision.Address == point.Address)) &&
                path.ConditionDecisionRepeatedPoints.Select(point => point.Address).Distinct().Count() ==
                path.ConditionDecisionRepeatedPoints.Count &&
                path.UpstreamConditionDecisionNonFixedPoints.All(point =>
                    point.PresentPaths > 0 && point.PresentPaths <= point.PathCount && !point.Repeated) &&
                path.UpstreamConditionDecisionRepeatedPoints.All(point => point.Repeated)),
            "分支点偏离明细不完整：非固定点必须只出现在部分路径，重复求值点必须被排除在决策点之外。");
        TestSupport.Assert(allBranchPaths.Where(path => path.ConditionDecisionSpaceComplete)
                .All(path => path.ConditionDecisionNonFixedPoints.Count == 0 &&
                             path.ConditionDecisionRepeatedPoints.Count == 0) &&
            allBranchPaths.Where(path => path.ConditionDecisionNonFixedPoints.Count > 0 ||
                                         path.ConditionDecisionRepeatedPoints.Count > 0)
                .All(path => path.ConditionDecisionSummary.Contains("非固定分支点") ||
                             path.ConditionDecisionSummary.Contains("重复求值分支点")),
            "完整组合站点不得含偏离点；含偏离点的摘要必须点名。");
        TestSupport.Assert(reachableBranchPaths.All(path => path.ConditionGroups.Count >= 1 &&
                                                            path.ConditionGroups.Sum(group => group.PathCount) ==
                                                            path.PathCount &&
                                                            path.ConditionGroups.All(group => group.Conditions
                                                                .All(condition => path.CommonConditions.Any(item =>
                                                                    item.Address == condition.Address &&
                                                                    item.Taken == condition.Taken) ||
                                                                                  path.OptionalConditions.Any(item =>
                                                                                      item.Address == condition.Address &&
                                                                                      item.Taken == condition.Taken)))) &&
                           reachableBranchPaths.Where(path => path.PathCount > 1 &&
                                                              path.ConditionGroups.Count > 1)
                               .Any(path => path.ConditionGroups.Any(group =>
                                   group.ExclusiveGroupIndexes.Count > 0)),
            "可达调用点的条件组不完整：组内守卫不属于公共/特有守卫，" +
            "或没有标注任何互斥方向。");
        TestSupport.Assert(reachableBranchPaths.All(path =>
                path.CommonConditions.All(common => path.Truncated || path.Conditions.Any(condition =>
                    condition.Address == common.Address && condition.Taken == common.Taken)) &&
                path.CommonConditions.All(common => !path.OptionalConditions.Any(optional =>
                    optional.Address == common.Address && optional.Taken == common.Taken)) &&
                !path.OptionalConditions.Any(optional => path.CommonConditions.Any(common =>
                    common.Address == optional.Address && common.Taken == optional.Taken))),
            "公共守卫不是最短路径的子集（未截断时），或与路径特有守卫重叠。");
        TestSupport.Assert(reachableBranchPaths.All(path => path.PathCount != 1 || path.Truncated ||
                                                             path.OptionalConditions.Count == 0 &&
                                                             path.CommonConditions.Count == path.Conditions.Count),
            "单路径调用点不应出现路径特有守卫。");
        var expectedMulti = report.EngineVersion switch
        {
            "6.4" => (Multi: 18, Common: 100, Optional: 223, Truncated: 11, Conflicts: 105),
            "6.5" => (Multi: 45, Common: 138, Optional: 359, Truncated: 12, Conflicts: 172),
            "6.6" => (Multi: 25, Common: 105, Optional: 204, Truncated: 7, Conflicts: 94),
            _ => (Multi: 0, Common: 0, Optional: 0, Truncated: 0, Conflicts: 0)
        };
        TestSupport.Assert(report.BranchMultiPathCount == expectedMulti.Multi &&
                           report.BranchCommonConditionCount == expectedMulti.Common &&
                           report.BranchOptionalConditionCount == expectedMulti.Optional &&
                           report.BranchMultiPathTruncatedCount == expectedMulti.Truncated &&
                           report.BranchConflictingConditionCount == expectedMulti.Conflicts,
            $"多路径守卫基线不符：{report.BranchMultiPathCount}/{report.BranchCommonConditionCount}/" +
            $"{report.BranchOptionalConditionCount}/{report.BranchMultiPathTruncatedCount}/" +
            $"{report.BranchConflictingConditionCount}，应为 {expectedMulti.Multi}/{expectedMulti.Common}/" +
            $"{expectedMulti.Optional}/{expectedMulti.Truncated}/{expectedMulti.Conflicts}");
        TestSupport.Assert(reachableBranchPaths.Where(path => path.PathsTruncated)
                .All(path => path.MultiPathSummary.Contains("上限", StringComparison.Ordinal)),
            "被截断的路径枚举没有标注上限说明。");
        TestSupport.Assert(reachableBranchPaths.Any(path => path.PathCount > 1 &&
                                                           path.OptionalConditions.Any(optional =>
                                                               !path.Conditions.Any(condition =>
                                                                   condition.Address == optional.Address &&
                                                                   condition.Taken == optional.Taken))),
            "多路径合并没有发现任何最短路径之外的守卫。");
        if (report.EngineVersion == "6.5")
        {
            TestSupport.Assert(allBranchPaths.Where(path => path.InboundJumpReachable == true)
                    .All(path => path.UpstreamConditions.Count > 0) &&
                                   allBranchPaths.Any(path =>
                                       path.UpstreamConditions.Any(condition => !condition.Taken)),
                "6.5 跨函数跳入的调用点没有回溯上游守卫（含不跳转一侧）。");
        }
        else if (report.EngineVersion == "6.6")
        {
            TestSupport.Assert(allBranchPaths.Where(path => path.InboundJumpAddress != null &&
                                                            path.InboundJumpReachable == false)
                    .All(path => path.UpstreamConditions.Count == 0 &&
                                 path.Summary.Contains("上游条件不可回溯", StringComparison.Ordinal)),
                "6.6 死代码跳转块不应伪造上游守卫。");
        }
        if (report.EngineVersion == "6.5")
        {
            var dispatcher = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x0043BC62);
            TestSupport.Assert(dispatcher is { BranchPaths.Count: 4 },
                "6.5 0x0043BC62 应有 4 条判定路径。");
            var deepest = dispatcher!.BranchPaths.OrderBy(path => path.BlockCount).Last();
            TestSupport.Assert(deepest is { Reachable: true } && deepest.Conditions.Count >= 6 &&
                               deepest.Conditions.Any(condition =>
                                   condition.Comparison is { } comparison &&
                                   comparison.Contains("cmp eax,64h", StringComparison.Ordinal)) &&
                               deepest.Conditions.Any(condition => condition.Comparison == "test eax,eax"),
                "6.5 0x0043BC62 最深判定路径应包含 cmp eax,64h 与 test eax,eax 守卫。");
            TestSupport.Assert(deepest.Summary.Contains("cmp al,0FFh", StringComparison.Ordinal),
                "6.5 0x0043BC62 判定路径缺少入口 0xFF 守卫。");
            var entryGuard = deepest.Conditions.FirstOrDefault(condition =>
                condition.Comparison is { } comparison && comparison.Contains("cmp al,0FFh", StringComparison.Ordinal));
            TestSupport.Assert(entryGuard is { Operand: "al", OperandKind: "对象字段" } &&
                               entryGuard.OperandOrigin.Contains("movzx eax", StringComparison.Ordinal) &&
                               entryGuard.OperandOrigin.Contains("[ecx+4]", StringComparison.Ordinal) &&
                               entryGuard.OperandOrigin.Contains("栈参数 1", StringComparison.Ordinal),
                "6.5 0x0043BC62 的 cmp al,0FFh 没有回溯到对象字段 [ecx+4] ← 栈参数。" +
                $"（实际 {entryGuard?.Comparison}/{entryGuard?.Operand}/{entryGuard?.OperandKind}/" +
                $"{entryGuard?.OperandOrigin}）");
            TestSupport.Assert(deepest.Conditions.Where(condition => condition.Comparison != null)
                    .All(condition => condition.Operand.Length > 0 && condition.OperandKind.Length > 0 &&
                                      condition.OperandOrigin.Length > 0),
                "6.5 0x0043BC62 判定条件缺少操作数来源。");
        }
        else if (report.EngineVersion == "6.6")
        {
            var query = report.FunctionRoles.FirstOrDefault(role => role.Entry == 0x004E30E1);
            TestSupport.Assert(query is { BranchPaths: [{ Reachable: true, Conditions.Count: 0 } path] } &&
                               path.Summary.Contains("顺序执行", StringComparison.Ordinal),
                "6.6 0x004E30E1 等级查询应是无分支判定的顺序路径。");
        }
        TestSupport.Assert(report.BranchPathJumpEnteredCount ==
                           allBranchPaths.Count(path => !path.Reachable && path.InboundJumpAddress != null),
            "入边跳转判定路径统计不一致。");
        TestSupport.Assert(allBranchPaths.Where(path => path.InboundJumpAddress is not null)
                .All(path => !path.Reachable && path.InboundJumpKind.Length > 0 &&
                             path.Summary.Contains("跳转进入", StringComparison.Ordinal)) &&
                           allBranchPaths.Where(path => path.InboundJumpFunction is not null)
                               .All(path => path.InboundJumpReachable != null),
            "入边跳转路径缺少跳转来源或可达性说明。");
        TestSupport.Assert(allBranchPaths.Where(path => !path.Reachable && path.InboundJumpAddress == null)
                .All(path => path.Summary.Contains("疑似死代码", StringComparison.Ordinal)),
            "无入边跳转的不可回溯路径没有标注孤立代码片段。");
        TestSupport.Assert(report.TriggerSummaryCount >= allBranchPaths.Length &&
                           report.TriggerSummaries.Count == report.TriggerSummaryCount,
            "触发条件摘要未覆盖判定路径及展开调用实例。");
        TestSupport.Assert(report.TriggerConditionCount == report.TriggerSummaries.Sum(item => item.ConditionCount) &&
                           report.TriggerSequentialCount == report.TriggerSummaries.Count(item =>
                               item.Reachable && item.ConditionCount == 0) &&
                           report.TriggerUnreachableCount == report.TriggerSummaries.Count(item => !item.Reachable),
            "触发条件摘要统计与判定路径不一致。");
        TestSupport.Assert(report.TriggerSummaries.All(item => item.Summary.Length > 0 &&
                                                              item.ParameterSummary.Length > 0 &&
                                                              item.TargetName.Length > 0 &&
                                                              item.ConditionSummary.Length > 0),
            "触发条件摘要缺少目标、参数或条件文本。");
        var allCallsForTrigger = report.Effects.SelectMany(row => row.CallSiteDetails)
            .Concat(report.UnassignedCalls).Concat(report.InternalCalls)
            .Concat(report.DerivedCalls).Concat(report.TableCalls).Concat(report.LoopCalls).ToArray();
        TestSupport.Assert(report.TriggerSummaries.All(item =>
                allCallsForTrigger.Any(call => call.Identity == item.Identity &&
                                               call.TriggerSummary == item.Summary)),
            "触发条件摘要没有回填到对应调用点。");
        TestSupport.Assert(report.TriggerSummaries.Where(item => item.EffectId != null)
                .All(item => report.Effects.Any(row => row.EffectId == item.EffectId &&
                                                       row.Name == item.EffectName)),
            "触发条件摘要的效果号/名称与名称行不一致。");
        if (report.EngineVersion == "6.5")
        {
            var dispatcherTriggers = report.TriggerSummaries
                .Where(item => item.FunctionEntry == 0x0043BC62).ToArray();
            TestSupport.Assert(dispatcherTriggers.Length == 4 &&
                               dispatcherTriggers.Any(item => item.EffectId != null &&
                                                              item.ConditionCount >= 6 &&
                                                              item.Summary.Contains("cmp al,0FFh",
                                                                  StringComparison.Ordinal)),
                "6.5 0x0043BC62 的触发条件摘要没有把守卫条件与特效号合并。");
        }
        else if (report.EngineVersion == "6.6")
        {
            var buildTriggers = report.TriggerSummaries
                .Where(item => item.FunctionEntry == 0x004D2800).ToArray();
            TestSupport.Assert(buildTriggers.Length == 5 &&
                               buildTriggers.All(item => item.ParameterSummary.Contains("值=",
                                   StringComparison.Ordinal)),
                "6.6 0x004D2800 的五条判定路径没有生成带参数的触发条件摘要。");
        }
        else if (report.EngineVersion == "6.4")
        {
            TestSupport.Assert(report.TriggerSummaries.Any(item => item.FunctionEntry == 0x0043C022 &&
                                                                   item.ConditionCount == 12),
                "6.4 0x0043C022 的最深 12 条件路径没有进入触发条件摘要。");
        }
        var rootInfo4207 = report.RootInfos.FirstOrDefault(info => info.Address == 0x004207B2);
        if (report.EngineVersion == "6.4" && rootInfo4207 != null)
            TestSupport.Assert(rootInfo4207.InteriorEntryCount > 0 &&
                               rootInfo4207.Evidence.Contains("函数体内入口", StringComparison.Ordinal),
                "6.4 0x004207B2 根没有标注函数体内 E8 入口。");
        var dynamicTable = FindCall(report, report.EngineVersion == "6.6" ? 0x004039D5u : 0x004039D6u);
        TestSupport.Assert(dynamicTable is { Pattern: EngineEffectCallPattern.ComputedStub },
            "双渠道表查找调用没有识别为计算桩。");
        TestSupport.Assert(dynamicTable!.ItemPush.SourceKind == EngineEffectArgumentSource.Memory,
            "双渠道表查找调用的装备号不是内存来源。");
        TestSupport.Assert(dynamicTable.PersonalPush.SourceKind == EngineEffectArgumentSource.Computed &&
                          dynamicTable.PersonalPush.DefinitionChain.Any(item => item.Text.Contains("add", StringComparison.OrdinalIgnoreCase)),
            "双渠道表查找调用的个人号缺少加法定义链。");
        TestSupport.Assert(dynamicTable.CallerPaths.Count > 0, "表查找调用没有 E8 调用链路径。");
        TestSupport.Assert(dynamicTable.ConsumerRoots.Count > 0, "表查找调用没有到达 E8 根。");
        TestSupport.Assert(dynamicTable.CallerPaths.Any(path => path.RootEntry != null &&
                                                               path.Summary.Contains("E8 根", StringComparison.Ordinal)),
            "表查找调用缺少带 E8 根的路径摘要。");
        TestSupport.Assert(dynamicTable.CallerPaths.Any(path => path.RootIsEntryPoint &&
                                                               path.RootEntry == report.EntryPoint),
            "表查找调用的 E8 根没有标注 PE 入口。");
        var helper = FindCall(report, 0x00412AA3);
        if (report.EngineVersion == "6.4")
            TestSupport.Assert(helper is { Pattern: EngineEffectCallPattern.WrapperPassThrough } &&
                              helper.Pushes.Count == 4 &&
                              helper.Pushes.All(push => push.SourceKind == EngineEffectArgumentSource.StackParameter),
                "6.4 双渠道包装内部调用没有从函数体重建四个栈参数。");
        var wrapperInternal = FindCall(report, 0x004251AB);
        if (report.EngineVersion is "6.5" or "6.6")
            TestSupport.Assert(wrapperInternal is { Pattern: EngineEffectCallPattern.EngineInternal } &&
                              wrapperInternal.InternalReason?.Contains("双渠道包装", StringComparison.Ordinal) == true,
                "双渠道包装内部调用没有识别。");
        var computedPersonal = FindCall(report, 0x0041CDFF);
        TestSupport.Assert(computedPersonal is not null && computedPersonal.ItemPush.WritePlan.IsWritable &&
                          computedPersonal.PersonalPush.SourceKind == EngineEffectArgumentSource.Computed &&
                          computedPersonal.PersonalPush.DefinitionChain.Any(item => item.Text.Contains("add", StringComparison.OrdinalIgnoreCase)),
            "计算型个人号调用没有保留定义链。");
        if (report.EngineVersion == "6.4")
        {
            var safeComputed = FindCall(report, 0x0043D037);
            TestSupport.Assert(safeComputed is not null, "6.4 安全计算立即数调用不存在。");
            TestSupport.Assert(safeComputed!.ItemPush.ResolvedValue == 0x66 &&
                              safeComputed.ItemPush.WritePlan.IsWritable &&
                              safeComputed.ItemPush.WritePlan.ImmediateLength == 1 &&
                              safeComputed.ItemPush.WritePlan.Definition?.Address == 0x0043D02C,
                "6.4 安全计算立即数没有生成 mov cl 写回计划。");
            TestSupport.Assert(safeComputed!.PersonalPush.WritePlan.IsWritable == false,
                "6.4 计算型个人号不应可写。");
        }
        var blockedComputed = report.UnassignedCalls.FirstOrDefault(call =>
            call.PersonalPush.SourceKind == EngineEffectArgumentSource.Computed &&
            !call.PersonalPush.WritePlan.IsWritable);
        TestSupport.Assert(blockedComputed != null && !string.IsNullOrWhiteSpace(blockedComputed.PersonalPush.WritePlan.BlockReason),
            "计算型参数缺少只读原因。");
        TestSupport.Assert(report.DerivedCallCount == report.DerivedCalls.Count, "派生调用计数不一致。");
        TestSupport.Assert(report.TableCallCount == report.TableCalls.Count, "表驱动调用计数不一致。");
        TestSupport.Assert(report.LoopCallCount == report.LoopCalls.Count, "循环展开调用计数不一致。");
        foreach (var loopCall in report.LoopCalls)
        {
            TestSupport.Assert((loopCall.Pattern is EngineEffectCallPattern.LoopDerived
                                   or EngineEffectCallPattern.NestedDerived) &&
                              loopCall.Status == EngineEffectCallStatus.Static &&
                              loopCall.LoopBase != null && loopCall.LoopLimit != null,
                $"循环/二级包装展开调用 0x{loopCall.CallAddress:X8} 分类异常。");
            TestSupport.Assert(!loopCall.ValuePush.WritePlan.IsWritable &&
                              !loopCall.StackingPush.WritePlan.IsWritable &&
                              !loopCall.PersonalPush.WritePlan.IsWritable,

                $"循环展开调用 0x{loopCall.CallAddress:X8} 的计算/模式参数不应可写。");
        }
        if (report.EngineVersion == "6.4")
        {
            TestSupport.Assert(report.LoopCallCount == 17, $"6.4 循环/二级包装展开数不是 17：{report.LoopCallCount}");
            TestSupport.Assert(report.LoopCalls.Select(call => call.PersonalEffectId).Distinct().OrderBy(value => value)
                    .SequenceEqual(new int?[] { 73, 74, 75, 76, 77, 78, 79, 80 }),
                "6.4 二级包装个人号不是 73–80。");
            var firstNested = report.LoopCalls.First(call => call.Pattern == EngineEffectCallPattern.NestedDerived);
            TestSupport.Assert(firstNested.LoopBase == 0x49 && firstNested.ItemEffectId == 108 &&
                              firstNested.ValueMode == 0 && firstNested.StackingMode == 3,
                "6.4 二级包装基址/装备/值/叠加不符。");
        }
        if (report.EngineVersion == "6.5")
        {
            TestSupport.Assert(report.LoopCallCount == 5, $"6.5 循环展开数不是 5：{report.LoopCallCount}");
            TestSupport.Assert(report.LoopCalls.Select(call => call.PersonalEffectId)
                    .SequenceEqual(new int?[] { 165, 166, 167, 168, 169 }),
                "6.5 循环展开个人号不是 165–169。");
            var firstLoop = report.LoopCalls.First(call => call.LoopIndex == 0);
            TestSupport.Assert(firstLoop.LoopBase == 0xA5 && firstLoop.LoopLimit == 5 &&
                              firstLoop.ItemEffectId == 0x54 && firstLoop.ValueMode == 0 && firstLoop.StackingMode == 1,
                "6.5 循环展开基址/装备/值/叠加不符。");
        }
        if (report.EngineVersion == "6.6")
        {
            TestSupport.Assert(report.LoopCallCount == 5, $"6.6 循环展开数不是 5：{report.LoopCallCount}");
            TestSupport.Assert(report.LoopCalls.Select(call => call.PersonalEffectId)
                    .SequenceEqual(new int?[] { 160, 161, 162, 163, 164 }),
                "6.6 循环展开个人号不是 160–164。");
            var firstLoop66 = report.LoopCalls.First(call => call.LoopIndex == 0);
            TestSupport.Assert(firstLoop66.LoopBase == 0xA0 && firstLoop66.LoopLimit == 5 &&
                              firstLoop66.ItemEffectId == 0x27 && firstLoop66.ValueMode == 0 && firstLoop66.StackingMode == 1,
                "6.6 循环展开基址/装备/值/叠加不符。");
            TestSupport.Assert(report.UnreachableCallCount == 2,
                $"6.6 不可达调用数不是 2：{report.UnreachableCallCount}");
            TestSupport.Assert(report.UnreachableOnlyEffectIds.SequenceEqual(new[] { 64, 219 }),
                "6.6 仅死代码调用的特效不是 64/219：" + string.Join(",", report.UnreachableOnlyEffectIds));
            TestSupport.Assert(FindCall(report, 0x0042E581) is { FunctionEntry: 0x0042E568 },
                "6.6 特效 177 调用点 0x0042E581 应归入非常规序言函数入口 0x0042E568。");
            TestSupport.Assert(FindCall(report, 0x0043D363) is { FunctionEntry: 0x0043D355 },
                "6.6 特效 222 调用点 0x0043D363 应归入代码洞入口 0x0043D355，而不是幻影跳转目标 0x0043D357。");
            TestSupport.Assert(report.RootInfos.All(info => info.Address != 0x0043D357 && info.Address != 0x0043D331),
                "6.6 幻影跳转目标的 0x0043D357/0x0043D331 不应再作为 E8 根出现。");
            TestSupport.Assert(FindCall(report, 0x0045D3C6) is { IsReachable: false, PersonalEffectId: 64 },
                "6.6 0x0045D3C6 没有标记为特效 64 的不可达调用。");
            TestSupport.Assert(FindCall(report, 0x0042E581) is { IsReachable: true, PersonalEffectId: 177 },
                "6.6 0x0042E581 应归入可达的特效 177 调用（函数入口 0x0042E568）。");
            TestSupport.Assert(FindCall(report, 0x004E4393) is { IsReachable: false, PersonalEffectId: 219 },
                "6.6 0x004E4393 没有标记为特效 219 的不可达调用。");
            TestSupport.Assert(report.Effects.First(row => row.EffectId == 64) is
                    { HasReachableCall: false, OnlyUnreachableCalls: true } &&
                              report.Effects.First(row => row.EffectId == 219) is
                    { HasReachableCall: false, OnlyUnreachableCalls: true } &&
                              report.Effects.First(row => row.EffectId == 177) is
                    { HasReachableCall: true, OnlyUnreachableCalls: false },
                "6.6 特效 64/219 没有标记为仅死代码调用，或特效 177 仍被误判为死代码。");
        }
        TestSupport.Assert(report.TableCalls.Count == 13, $"表驱动调用数不是 13：{report.TableCalls.Count}");
        var tableIds = report.TableCalls.Select(call => call.PersonalEffectId).OrderBy(value => value).ToArray();
        TestSupport.Assert(tableIds.SequenceEqual(new int?[]
            { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16 }),
            "表驱动个人号集合不符：" + string.Join(",", tableIds));
        foreach (var tableCall in report.TableCalls)
        {
            TestSupport.Assert(tableCall.Pattern == EngineEffectCallPattern.TableDerived &&
                              tableCall.Status == EngineEffectCallStatus.Static &&
                              tableCall.TableIndex != null && tableCall.TableAddress != null,
                $"表驱动调用 0x{tableCall.CallAddress:X8} 分类异常。");
            TestSupport.Assert(!tableCall.ValuePush.WritePlan.IsWritable &&
                              !tableCall.StackingPush.WritePlan.IsWritable &&
                              tableCall.ItemPush.WritePlan.IsWritable &&
                              !tableCall.PersonalPush.WritePlan.IsWritable,
                $"表驱动调用 0x{tableCall.CallAddress:X8} 只有表项装备号应可写。");
        }
        if (report.EngineVersion == "6.5")
        {
            var table6 = report.TableCalls.First(call =>
                call.TableAddress == 0x00437E19 && call.TableIndex == 0);
            TestSupport.Assert(table6.ItemEffectId == 0x47 && table6.PersonalEffectId == 6,
                "6.5 表 0x437E19[0] 不是装备 0x47 / 个人 6。");
            var table15 = report.TableCalls.First(call =>
                call.TableAddress == 0x00437E1F && call.TableIndex == 2);
            TestSupport.Assert(table15.ItemEffectId == 0x2B && table15.PersonalEffectId == 15 &&
                              table15.ValueMode == 0,
                "6.5 表 0x437E1F[2] 不是装备 0x2B / 个人 15 / 值 0。");
            var recovery = report.TableCalls.First(call =>
                call.TableAddress == 0x0043CFCD && call.TableIndex == 0);
            TestSupport.Assert(recovery.PersonalEffectId == 3 && recovery.ItemEffectId == 0,
                "6.5 回合恢复表 0x43CFCD[0] 不是个人 3 / 装备 0。");
        }
        TestSupport.Assert(report.ResolvedDerivedCallCount ==
                          report.DerivedCalls.Count(call => call.PersonalEffectId != null), "派生调用个人号计数不一致。");
        foreach (var derivedCall in report.DerivedCalls)
        {
            TestSupport.Assert(derivedCall.Pattern == EngineEffectCallPattern.WrapperDerived,
                $"派生调用 0x{derivedCall.CallAddress:X8} 类型异常。");
            TestSupport.Assert(derivedCall.WrapperStubEntry != null && derivedCall.WrapperStubCallAddress != null,
                $"派生调用 0x{derivedCall.CallAddress:X8} 缺少桩来源。");
            TestSupport.Assert(derivedCall.CallInstruction.RawBytes.Length == 5 &&
                              derivedCall.CallInstruction.RawBytes[0] == 0xE8,
                $"派生调用 0x{derivedCall.CallAddress:X8} 不是 E8 调用。");
            TestSupport.Assert(derivedCall.CallInstruction.Target == derivedCall.WrapperStubEntry,
                $"派生调用 0x{derivedCall.CallAddress:X8} 调用目标与桩不一致。");
        }
        if (report.EngineVersion == "6.4")
        {
            var cave = FindCall(report, 0x004D0023);
            TestSupport.Assert(cave is
                {
                    Status: EngineEffectCallStatus.Static, PersonalEffectId: 159, ItemEffectId: 82,
                    StackingMode: 1, ValueMode: 0
                }, "6.4 代码洞跳转调用没有解析为特效 159。");
            TestSupport.Assert(cave!.RedirectSourceAddress == 0x004CFFFD,
                "6.4 代码洞调用没有记录跳转来源 0x004CFFFD。");
            TestSupport.Assert(cave.PersonalPush.WritePlan.IsWritable &&
                              cave.PersonalPush.WritePlan.ImmediateFileOffset == 661495,
                "6.4 代码洞调用的个人号没有落在跳转前 push 的立即数上。");
            var caveDerived = report.DerivedCalls.FirstOrDefault(call => call.CallAddress == 0x0041A447);
            TestSupport.Assert(caveDerived is { IsReachable: true, ReachCaveEntry: 0x0041A430, ReachJumpAddress: 0x00407D11 } &&
                              caveDerived.Reachability.Contains("代码洞", StringComparison.Ordinal),
                "6.4 0x0041A447 派生调用没有标记为由 0x00407D11 跳转进入代码洞 0x0041A430。");
            var caveStatic = FindCall(report, 0x0041A5B8);
            TestSupport.Assert(caveStatic is { IsReachable: true, ReachCaveEntry: 0x0041A5A0, ReachJumpAddress: 0x0043C648 },
                "6.4 0x0041A5B8 没有标记为由 0x0043C648 跳转进入代码洞 0x0041A5A0。");
            var caveStatic2 = FindCall(report, 0x0045EE95);
            TestSupport.Assert(caveStatic2 is { IsReachable: true, ReachCaveEntry: 0x0045EE80, ReachJumpAddress: 0x0040C81C },
                "6.4 0x0045EE95 没有标记为由 0x0040C81C 跳转进入代码洞 0x0045EE80。");
            var caveStatic3 = FindCall(report, 0x0045EFD5);
            TestSupport.Assert(caveStatic3 is { IsReachable: true, ReachCaveEntry: 0x0045EFC0, ReachJumpAddress: 0x00405D01 },
                "6.4 0x0045EFD5 没有标记为由 0x00405D01 跳转进入代码洞 0x0045EFC0。");
            TestSupport.Assert(report.UnreachableCallCount == 0,
                $"6.4 不可达调用数不是 0：{report.UnreachableCallCount}");
            TestSupport.Assert(report.CaveEntryCallCount == 3,
                $"6.4 代码洞进入的调用数不是 3：{report.CaveEntryCallCount}");
            TestSupport.Assert(report.DerivedCalls.Count == 5, "6.4 双渠道包装派生调用数不是 5。");
            var wrapper = report.Wrappers.FirstOrDefault(info => info.Entry == 0x00412A87);
            TestSupport.Assert(wrapper is { DerivedCallCount: 5, StaticDerivedCallCount: 3 } &&
                              wrapper.CoreCallAddresses.SequenceEqual(new[] { 0x00412AA3u }),
                "6.4 包装桩汇总不是 0x00412A87/5/3。");
            TestSupport.Assert(report.DerivedCalls.Count(call => call.Status == EngineEffectCallStatus.Static) == 3,
                "6.4 双渠道包装静态派生调用数不是 3。");
            TestSupport.Assert(report.DerivedOnlyEffectIds.SequenceEqual(
                    new[]
                    {
                        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 61, 69, 73, 74, 75, 76, 77, 78, 79, 80,
                        144, 145, 146, 147, 148, 152
                    }),
                "6.4 仅派生特效集合不符：" + string.Join(",", report.DerivedOnlyEffectIds));
            var rangeCall = report.DerivedCalls.FirstOrDefault(call => call.CallAddress == 0x0041A447);
            TestSupport.Assert(rangeCall is { PersonalEffectId: null } &&
                               rangeCall.EffectRange.SequenceEqual(new[] { 144, 145, 146, 147, 148 }) &&
                               rangeCall.PersonalPush is { MinimumValue: 144, MaximumValue: 148 } &&
                               !rangeCall.PersonalPush.WritePlan.IsWritable,
                "6.4 代码洞范围分派 0x0041A447 未解析为 144–148 只读范围。");
            foreach (var id in new[] { 144, 145, 146, 147, 148 })
                TestSupport.Assert(report.Effects.FirstOrDefault(row => row.EffectId == id) is
                                   { DerivedCallDetails.Count: 1 },
                    $"6.4 范围分派未让特效 {id} 获得调用点。");
            foreach (var id in new[] { 61, 69, 152 })
            {
                var derived = report.DerivedCalls.FirstOrDefault(call => call.PersonalEffectId == id);
                TestSupport.Assert(derived is { Status: EngineEffectCallStatus.Static } &&
                                  derived.PersonalPush.WritePlan.IsWritable,
                    $"6.4 特效 {id} 的派生调用没有解析为可写静态调用。");
            }
        }
        if (report.EngineVersion == "6.5")
        {
            var log196 = report.DerivedCalls.FirstOrDefault(call =>
                call.CallAddress == 0x004E6020 && call.WrapperStubCallAddress == 0x004E0010);
            TestSupport.Assert(log196 is
                {
                    PersonalEffectId: 196, ItemEffectId: 255, StackingMode: 1, ValueMode: 0,
                    Status: EngineEffectCallStatus.Static
                }, "0x004E6020 → 0x004E0010 派生调用没有解析出特效 196。");
            TestSupport.Assert(log196!.PersonalPush.WritePlan.IsWritable,
                "派生调用特效 196 的个人号来自调用者直接常量，应为可写。");
            TestSupport.Assert(log196.WrapperStubEntry == 0x004E0000 &&
                              log196.Callees.Count(call => call.Target == EngineEffectScanService.CoreEntry) == 2,
                "0x004E0000 桩函数体应显示两次核心判定调用。");
            var wrapper = report.Wrappers.FirstOrDefault(info => info.Entry == 0x004E0000);
            TestSupport.Assert(wrapper is { DerivedCallCount: 2, StaticDerivedCallCount: 2 } &&
                              wrapper.CoreCallAddresses.SequenceEqual(new[] { 0x004E0010u, 0x004E0025u }),
                "6.5 包装桩 0x004E0000 汇总不是 2/2/两次核心判定。");
            var log195 = report.DerivedCalls.FirstOrDefault(call =>
                call.CallAddress == 0x004E4034 && call.WrapperStubCallAddress == 0x004EE108);
            TestSupport.Assert(log195 is
                {
                    PersonalEffectId: 195, ItemEffectId: 0, StackingMode: 1, ValueMode: 1,
                    Status: EngineEffectCallStatus.Static
                }, "0x004E4034 → 0x004EE108 派生调用没有解析出特效 195。");
            var log210 = report.DerivedCalls.FirstOrDefault(call =>
                call.CallAddress == 0x004E4174 && call.WrapperStubCallAddress == 0x004EEA10);
            TestSupport.Assert(log210 is
                {
                    PersonalEffectId: 210, ItemEffectId: 35, StackingMode: 1, ValueMode: 0,
                    Status: EngineEffectCallStatus.Static
                }, "0x004E4174 → 0x004EEA10 派生调用没有解析出特效 210。");
            var log107 = report.DerivedCalls.FirstOrDefault(call =>
                call.CallAddress == 0x004E41A0 && call.WrapperStubCallAddress == 0x004EEA10);
            TestSupport.Assert(log107 is
                {
                    PersonalEffectId: 107, ItemEffectId: 255, StackingMode: 1, ValueMode: 0,
                    Status: EngineEffectCallStatus.Static
                }, "0x004E41A0 → 0x004EEA10 派生调用没有解析出特效 107。");
        }
        var first = report.Effects.FirstOrDefault(row => row.EffectId == 0);
        TestSupport.Assert(first is { HasNameSlot: true }, "ID 0 没有名称槽。");
        TestSupport.Assert(first!.SuffixByte is { } firstSuffix &&
                           (EngineEffectSuffixLabels.Supports(report.EngineVersion, firstSuffix) ||
                            first.SuffixLabel.Contains("不处理", StringComparison.Ordinal)),
            $"ID 0 后缀超出范围或标签不符：{first.SuffixByte} {first.SuffixLabel}");
        TestSupport.Assert(first.DescriptionByteCount is >= 0 and <= 199, "ID 0 说明字节数异常。");
        foreach (var row in report.Effects.Where(row => row.HasCalls))
            foreach (var call in row.CallSiteDetails)
            {
                TestSupport.Assert(call.Status == EngineEffectCallStatus.Static, "已归属调用点不是静态。");
                TestSupport.Assert(call.ValuePush.RawBytes.Length is 2 or 5, $"调用点 0x{call.CallAddress:X8} 的第 1 个 push 长度异常。");
                TestSupport.Assert(call.PersonalPush.RawBytes.Length is 2 or 5, $"调用点 0x{call.CallAddress:X8} 的第 4 个 push 长度异常。");
                TestSupport.Assert(call.CallInstruction.RawBytes.Length == 5 && call.CallInstruction.RawBytes[0] == 0xE8,
                    $"调用点 0x{call.CallAddress:X8} 不是 E8 rel32。");
                TestSupport.Assert(call.CallInstruction.FileOffset > 0, "调用点缺少文件偏移。");
                TestSupport.Assert(call.EntryTarget is EngineEffectScanService.CoreEntry
                        or EngineEffectScanService.WrapperEntry or EngineEffectScanService.DispatcherEntry,
                    "调用点判定入口异常。");
            }

        Console.WriteLine($"ENGINE_SCAN {Path.GetFileName(root)}：版本 {report.EngineVersion}；名称 {report.NameCount}；" +
                          $"特效 {report.Effects.Count}；调用点 {report.CallSiteCount}（静态 {report.ResolvedCallSiteCount}，" +
                          $"动态 {report.DynamicCallSiteCount}，未解析 {report.UnresolvedCallSiteCount}；" +
                          $"内部 {report.InternalCallSiteCount}，有效 {report.EffectiveCallSiteCount}；" +
                          $"派生 {report.DerivedCallCount}，派生独有特效 {report.DerivedOnlyEffectIds.Count}，" +
                          $"无调用 {report.UnreferencedEffectIds.Count}）；" +
                          $"说明槽 {(report.ImsgSha256.Length == 64 ? "可读" : "缺失")}");
        foreach (var statistic in report.EntryStatistics)
            Console.WriteLine($"  STAT {statistic.Name} 0x{statistic.Target:X8}：总数 {statistic.Total}，静态 {statistic.Static}，" +
                              $"动态 {statistic.Dynamic}，未解析 {statistic.Unresolved}，内部 {statistic.Internal}");
        foreach (var call in report.InternalCalls)
            Console.WriteLine($"  INTERNAL 0x{call.CallAddress:X8} {call.Pattern} {call.InternalReason}");
        foreach (var loopCall in report.LoopCalls)
            Console.WriteLine($"  LOOP 0x{loopCall.CallAddress:X8} 索引 {loopCall.LoopIndex} " +
                              $"基址 0x{loopCall.LoopBase:X2} 上限 {loopCall.LoopLimit} " +
                              $"个人 {loopCall.PersonalEffectId} 装备 {loopCall.ItemEffectId}");
        foreach (var tableCall in report.TableCalls)
            Console.WriteLine($"  TABLE 0x{tableCall.CallAddress:X8} 索引 {tableCall.TableIndex} " +
                              $"表 0x{tableCall.TableAddress:X8} 个人 {tableCall.PersonalEffectId} " +
                              $"装备 {tableCall.ItemEffectId} 值 {tableCall.ValueMode} 叠加 {tableCall.StackingMode}");
        foreach (var wrapper in report.Wrappers)
            Console.WriteLine($"  WRAPPER {wrapper.Name} 0x{wrapper.Entry:X8} 派生 {wrapper.DerivedCallCount} " +
                              $"静态 {wrapper.StaticDerivedCallCount} 桩内 " +
                              string.Join("/", wrapper.CoreCallAddresses.Select(address => $"0x{address:X8}")));
        foreach (var call in report.DerivedCalls)
            Console.WriteLine($"  DERIVED 0x{call.CallAddress:X8} → 桩内 0x{call.WrapperStubCallAddress:X8} " +
                              $"桩函数 0x{call.WrapperStubEntry:X8} 值 {call.ValueMode} 叠加 {call.StackingMode} " +
                              $"装备 {call.ItemEffectId} 个人 {call.PersonalEffectId} {call.Status} " +
                              $"个人可写 {call.PersonalPush.WritePlan.IsWritable}");
        foreach (var call in report.UnassignedCalls)
        {
            Console.WriteLine($"  UNASSIGNED 0x{call.CallAddress:X8} {call.Pattern} {call.Status} " +
                              $"fn={FormatAddress(call.FunctionEntry)} " +
                              $"值 {call.ValueMode} 叠加 {call.StackingMode} 装备 {call.ItemEffectId} 个人 {call.PersonalEffectId}");
            PrintArgument("值", call.ValuePush);
            PrintArgument("叠加", call.StackingPush);
            PrintArgument("装备", call.ItemPush);
            PrintArgument("个人", call.PersonalPush);
        }
        foreach (var address in new uint[] { 0x004039D6, 0x004039D5, 0x00412AA3, 0x004251AB, 0x0041CDFF,
                     0x0043D037, 0x0043D031, 0x00413053, 0x0041305A, 0x0041023E, 0x004102AD })
        {
            var call = report.Effects.SelectMany(row => row.CallSiteDetails)
                .Concat(report.UnassignedCalls).Concat(report.InternalCalls)
                .FirstOrDefault(item => item.CallAddress == address);
            if (call == null) continue;
            Console.WriteLine($"  FOCUS 0x{address:X8} {call.Pattern} {call.Status} fn={FormatAddress(call.FunctionEntry)} " +
                              $"值 {call.ValueMode} 叠加 {call.StackingMode} 装备 {call.ItemEffectId} 个人 {call.PersonalEffectId}");
            PrintArgument("  装备定义", call.ItemPush);
        }
        Console.WriteLine($"ID 0：名称「{first.Name}」后缀 {first.SuffixLabel}；说明 {first.DescriptionByteCount} 字节「{Short(first.Description)}」；" +
                          $"名称容量 {first.NameCapacity}；调用点 {first.CallSiteDetails.Count}");
        foreach (var call in first.CallSiteDetails)
            Console.WriteLine($"  0x{call.CallAddress:X8} 入口 {call.EntryName} 函数 {call.FunctionEntry:X8}；" +
                              $"值 {call.ValueMode} 叠加 {call.StackingMode} 装备 {call.ItemEffectId} 个人 {call.PersonalEffectId}");

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            var lines = new List<string>
            {
                $"版本,{report.EngineVersion}", $"名称行,{report.NameCount}", $"特效,{report.Effects.Count}",
                $"调用点,{report.CallSiteCount}", $"静态,{report.ResolvedCallSiteCount}",
                $"动态,{report.DynamicCallSiteCount}", $"未解析,{report.UnresolvedCallSiteCount}",
                $"Ekd5.exe SHA-256,{report.ExeSha256}", $"Imsg.e5 SHA-256,{report.ImsgSha256}"
            };
            lines.AddRange(report.Effects.Where(row => row.HasCalls).Select(row =>
                $"{row.EffectId},{Escape(row.Name)},{row.SuffixByte},{row.CallSiteDetails.Count}," +
                $"{string.Join('/', row.StackingModes)},{string.Join('/', row.ValueModes)},{string.Join('/', row.ItemEffectIds)}"));
            File.WriteAllLines(Path.Combine(outputDirectory, "引擎扫描-" + Path.GetFileName(root) + ".csv"), lines);
        }
    }

    private static string Short(string value)
        => value.Length <= 18 ? value : value[..18] + "…";

    private static void RunSuffixCatalogTests()
    {
        TestSupport.Assert(EngineEffectSuffixLabels.MaximumSuffix("6.4") == 0x05, "6.4 后缀上限不是 0x05。");
        TestSupport.Assert(EngineEffectSuffixLabels.MaximumSuffix("6.5") == 0x0A, "6.5 后缀上限不是 0x0A。");
        TestSupport.Assert(EngineEffectSuffixLabels.MaximumSuffix("6.6") == 0x0F, "6.6 后缀上限不是 0x0F。");
        TestSupport.Assert(!EngineEffectSuffixLabels.Supports("6.4", 0x06) &&
                           EngineEffectSuffixLabels.Label("6.4", 0x06).Contains("不处理", StringComparison.Ordinal),
            "6.4 0x06 应显示为选择器不处理。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.5", 0x06).Contains("位掩码", StringComparison.Ordinal),
            "6.5 0x06 应为位掩码组合。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.6", 0x06).Contains("次", StringComparison.Ordinal),
            "6.6 0x06 应为次数后缀。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.6", 0x0C).Contains("范围", StringComparison.Ordinal),
            "6.6 0x0C 应为范围/形状名。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.6", 0x0D).Contains("阵型", StringComparison.Ordinal),
            "6.6 0x0D 应为阵型/范围名。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.6", 0x0E).Contains("地形", StringComparison.Ordinal),
            "6.6 0x0E 应为地形名。");
        TestSupport.Assert(EngineEffectSuffixLabels.Name("6.6", 0x0F).Contains("攻击/策略/ALL", StringComparison.Ordinal),
            "6.6 0x0F 应为攻击/策略/ALL。");
        TestSupport.Assert(!EngineEffectSuffixLabels.Supports("6.6", 0x10) &&
                           EngineEffectSuffixLabels.Name("6.6", 0x12).Contains("不处理", StringComparison.Ordinal),
            "6.6 0x10+ 应显示为选择器不处理。");
    }

    private static void WriteBaselineDiagnostics(EngineEffectScanReport report, string root, string? outputDirectory)
    {
        var metrics = typeof(EngineEffectScanReport).GetProperties()
            .Where(property => property.PropertyType == typeof(int))
            .ToDictionary(property => property.Name, property => (int)property.GetValue(report)!);
        Console.WriteLine("BASELINE_METRICS " + System.Text.Json.JsonSerializer.Serialize(metrics));
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        Directory.CreateDirectory(outputDirectory);
        // Reconstruct only the excluded register aggregator's CFG paths. This
        // audits the delta without restoring it as a four-stack-argument entry
        // or changing the scanner's production entry catalogue.
        var assembly = typeof(EngineEffectScanService).Assembly;
        var imageType = assembly.GetType("DataManager.Core.Scanner.EngineEffectBinaryImage", throwOnError: true)!;
        var jumpType = assembly.GetType("DataManager.Core.Scanner.EngineEffectJumpIndex", throwOnError: true)!;
        var graphType = assembly.GetType("DataManager.Core.Scanner.EngineEffectCallGraph", throwOnError: true)!;
        var analyzerType = assembly.GetType("DataManager.Core.Scanner.EngineEffectBranchConditionAnalyzer", throwOnError: true)!;
        var image = imageType.GetMethod("Read")!.Invoke(null, [File.ReadAllBytes(report.ExePath), CancellationToken.None])!;
        var jumps = jumpType.GetMethod("Build")!.Invoke(null, [image])!;
        var graph = graphType.GetMethod("Build")!.Invoke(null, [image, jumps])!;
        var calls = ((System.Collections.IEnumerable)graphType.GetProperty("Calls")!.GetValue(graph)!).Cast<object>().ToArray();
        var excluded = calls.Where(call => (uint)call.GetType().GetProperty("Target")!.GetValue(call)! == EngineEffectScanService.ValueEntry).ToArray();
        var removedPaths = new List<EngineEffectBranchPath>();
        foreach (var group in excluded.GroupBy(call => (uint?)call.GetType().GetProperty("CallerEntry")!.GetValue(call)))
        {
            if (group.Key is not { } entry) continue;
            var typedCalls = Array.CreateInstance(group.First().GetType(), group.Count());
            var index = 0;
            foreach (var call in group) typedCalls.SetValue(call, index++);
            removedPaths.AddRange((IReadOnlyList<EngineEffectBranchPath>)analyzerType.GetMethod("Analyze")!
                .Invoke(null, [image, entry, typedCalls, jumps, graph])!);
        }
        var removedMetrics = PathMetrics(removedPaths);
        VerifyAggregatorBaselineDelta(report, metrics, removedMetrics, excluded.Select(call =>
            (uint)call.GetType().GetProperty("Address")!.GetValue(call)!).ToArray());
        Console.WriteLine("EXCLUDED_AGGREGATOR_METRICS " + System.Text.Json.JsonSerializer.Serialize(removedMetrics));
        var diagnostic = new
        {
            Sample = Path.GetFileName(root), report.EngineVersion, report.ExeSha256, Metrics = metrics,
            ExcludedAggregatorMetrics = removedMetrics,
            ExcludedCalls = excluded.Select(call => new
            {
                Address = $"0x{(uint)call.GetType().GetProperty("Address")!.GetValue(call)!:X8}",
                Caller = $"0x{(uint?)call.GetType().GetProperty("CallerEntry")!.GetValue(call):X8}",
                Target = "0x00413009"
            }),
            ExcludedPaths = removedPaths,
            Paths = report.FunctionRoles.SelectMany(role => role.BranchPaths.Select(path => new { role.Entry, Path = path }))
        };
        File.WriteAllText(Path.Combine(outputDirectory, "扫描基线诊断-" + Path.GetFileName(root) + ".json"),
            System.Text.Json.JsonSerializer.Serialize(diagnostic, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void VerifyAggregatorBaselineDelta(EngineEffectScanReport report,
        IReadOnlyDictionary<string, int> current, IReadOnlyDictionary<string, int> removed, IReadOnlyList<uint> excludedCalls)
    {
        var expectedCalls = report.EngineVersion == "6.6"
            ? new uint[] { 0x00402D6F, 0x00402DCF, 0x0041023E, 0x004102AD, 0x0041305A, 0x004198FE, 0x00419943 }
            : new uint[] { 0x00402D73, 0x00402DD5, 0x0041023E, 0x004102AD, 0x00413053 };
        TestSupport.Assert(excludedCalls.Order().SequenceEqual(expectedCalls), "被排除的寄存器聚合器调用地址集合不符。");
        TestSupport.Assert(report.FunctionRoles.SelectMany(role => role.BranchPaths).All(path => path.Target != EngineEffectScanService.ValueEntry),
            "寄存器聚合器仍出现在四参数特效判定路径中。");
        // These are the pre-fix values. Every changed counter must be explained
        // exactly by the independently reconstructed aggregator call paths.
        // 6.6 的 BranchConditionGroupCount 由 539 调为 540：非常规序言入口
        // 0x0042E568（特效 177）修正后，其调用点多枚举出一组条件，聚合器排除量不变。
        var old = report.EngineVersion switch
        {
            "6.4" => new[] { 102, 165, 472, 54, 85, 0, 22, 110, 253, 11, 114 },
            "6.5" => new[] { 186, 269, 792, 125, 83, 0, 49, 148, 389, 12, 181 },
            "6.6" => new[] { 206, 177, 540, 58, 70, 9, 30, 117, 250, 8, 117 },
            _ => []
        };
        var keys = new[] { "BranchPathCount", "BranchConditionCount", "BranchConditionGroupCount", "BranchConditionDecisionCount",
            "BranchConditionDecisionNonFixedCount", "BranchConditionDecisionRepeatedCount", "BranchMultiPathCount",
            "BranchCommonConditionCount", "BranchOptionalConditionCount", "BranchMultiPathTruncatedCount", "BranchConflictingConditionCount" };
        for (var index = 0; index < old.Length; index++)
            TestSupport.Assert(current[keys[index]] + removed[keys[index]] == old[index],
                $"{keys[index]} 的基线变化并非仅来自排除聚合器：当前{current[keys[index]]}+排除{removed[keys[index]]}!=旧值{old[index]}。");
    }

    private static IReadOnlyDictionary<string, int> PathMetrics(IReadOnlyList<EngineEffectBranchPath> paths)
        => new Dictionary<string, int>
        {
            ["BranchPathCount"] = paths.Count,
            ["BranchConditionCount"] = paths.Sum(path => path.Conditions.Count),
            ["BranchPathUnreachableCount"] = paths.Count(path => !path.Reachable),
            ["BranchConditionGroupCount"] = paths.Sum(path => path.ConditionGroups.Count),
            ["BranchConditionGroupDuplicatePathCount"] = paths.Sum(path => path.ConditionGroupDuplicatePathCount),
            ["BranchConditionGroupLimitCount"] = paths.Count(path => path.ConditionGroupsTruncated),
            ["BranchUpstreamConditionGroupCount"] = paths.Sum(path => path.UpstreamConditionGroups.Count),
            ["BranchConditionDecisionCount"] = paths.Sum(path => path.ConditionDecisions.Count),
            ["BranchConditionDecisionCompleteCount"] = paths.Count(path => path.ConditionDecisionSpaceComplete),
            ["BranchConditionDecisionMissingCount"] = paths.Sum(path => path.ConditionDecisionMissingCount),
            ["BranchUpstreamConditionDecisionCount"] = paths.Sum(path => path.UpstreamConditionDecisions.Count),
            ["BranchConditionDecisionNonFixedCount"] = paths.Sum(path => path.ConditionDecisionNonFixedPoints.Count),
            ["BranchConditionDecisionRepeatedCount"] = paths.Sum(path => path.ConditionDecisionRepeatedPoints.Count),
            ["BranchUpstreamConditionDecisionNonFixedCount"] = paths.Sum(path => path.UpstreamConditionDecisionNonFixedPoints.Count),
            ["BranchUpstreamConditionDecisionRepeatedCount"] = paths.Sum(path => path.UpstreamConditionDecisionRepeatedPoints.Count),
            ["BranchMultiPathCount"] = paths.Count(path => path.Reachable && path.PathCount > 1),
            ["BranchCommonConditionCount"] = paths.Sum(path => path.CommonConditions.Count),
            ["BranchOptionalConditionCount"] = paths.Sum(path => path.OptionalConditions.Count),
            ["BranchMultiPathTruncatedCount"] = paths.Count(path => path.PathsTruncated),
            ["BranchConflictingConditionCount"] = paths.Sum(path => path.ConflictingConditionCount)
        };

    private static EngineEffectCallSite? FindCall(EngineEffectScanReport report, uint address)
        => report.Effects.SelectMany(row => row.CallSiteDetails)
            .Concat(report.UnassignedCalls)
            .Concat(report.InternalCalls)
            .FirstOrDefault(call => call.CallAddress == address);

    private static string FormatAddress(uint? address) => address is { } value ? $"0x{value:X8}" : "—";

    private static void PrintArgument(string label, EngineEffectPushArgument argument)
        => Console.WriteLine($"    {label}：值 {argument.Value?.ToString() ?? "—"} 解析 {argument.ResolvedValue?.ToString() ?? "—"} " +
                             $"来源 {argument.SourceKind} 可写 {argument.WritePlan.IsWritable} " +
                             $"原因 {argument.WritePlan.BlockReason ?? "—"} 定义 {FormatAddress(argument.WritePlan.Definition?.Address)} " +
                             $"链 {string.Join(" | ", argument.DefinitionChain.Select(item => item.Text))}");

    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
