using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectEvidenceTests
{
    public static void Run(string root, string outputDirectory)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var report = new EngineEffectScanService().Scan(workspace);
        var references = new EngineEffectReferenceService().Build(workspace);
        var referencedEffects = references
            .Where(pair => pair.Value.PersonAssignments.Count > 0)
            .OrderBy(pair => pair.Key)
            .ToArray();
        var assignedIds = referencedEffects.Select(pair => pair.Key).ToHashSet();
        var itemSelectors = ReadItemSelectors(workspace, report);
        var itemSelectorIds = itemSelectors.Keys.ToHashSet();
        var specialItemSelectors = ReadItemSelectors(workspace, report, specialType: true);
        var specialItemSelectorIds = specialItemSelectors.Keys.ToHashSet();
        var genericChain = report.Diagnostics.FirstOrDefault(line =>
            line.Contains("通用分派链", StringComparison.Ordinal)) ?? string.Empty;
        var rowsById = report.Effects.ToDictionary(row => row.EffectId);
        bool TablePerson(int id) => rowsById.TryGetValue(id, out var row) && row.HasPersonAssignment;
        bool TableJob(int id) => rowsById.TryGetValue(id, out var row) && row.HasJobAssignment;
        bool TableCovered(int id) => TablePerson(id) || TableJob(id);
        Directory.CreateDirectory(outputDirectory);
        var name = Path.GetFileName(root);
        var jsonPath = Path.Combine(outputDirectory, $"引擎反汇编-{name}.json");
        var csvPath = Path.Combine(outputDirectory, $"引擎反汇编-{name}.csv");
        var pathCsvPath = Path.Combine(outputDirectory, $"引擎调用链-{name}.csv");
        var referenceCsvPath = Path.Combine(outputDirectory, $"引擎引用-{name}.csv");
        var roleCsvPath = Path.Combine(outputDirectory, $"引擎函数角色-{name}.csv");

        static object Deviation(EngineEffectConditionDeviation point) => new
        {
            Address = $"0x{point.Address:X8}",
            point.Instruction,
            point.Comparison,
            point.PresentPaths,
            point.PathCount,
            point.TakenPaths,
            point.NotTakenPaths,
            point.Repeated
        };

        var payload = new
        {
            report.EngineVersion,
            report.ExePath,
            report.ExeSha256,
            report.ImsgSha256,
            EntryPoint = $"0x{report.EntryPoint:X8}",
            report.NameCount,
            Diagnostics = report.Diagnostics,
            Counts = new
            {
                report.CallSiteCount,
                report.EffectiveCallSiteCount,
                report.InternalCallSiteCount,
                report.ResolvedCallSiteCount,
                report.DynamicCallSiteCount,
                report.UnresolvedCallSiteCount,
                report.DerivedCallCount,
                report.ResolvedDerivedCallCount,
                report.TableCallCount,
                report.LoopCallCount,
                report.UnreachableCallCount,
                report.CaveEntryCallCount,
                report.DeadEffectStubCount,
                report.UnalignedCallCount,
                report.InstructionCount,
                report.IndexedJumpAnchorCount,
                DerivedOnly = report.DerivedOnlyEffectIds.Count,
                Unreferenced = report.UnreferencedEffectIds.Count,
                UnreferencedAssigned = report.UnreferencedEffectIds.Count(id => assignedIds.Contains(id)),
                UnreferencedTableCovered = report.UnreferencedEffectIds.Count(id => TableCovered(id)),
                UnreferencedTablePerson = report.UnreferencedEffectIds.Count(id => TablePerson(id)),
                UnreferencedTableJob = report.UnreferencedEffectIds.Count(id => TableJob(id)),
                UnreferencedItemSelector = report.UnreferencedEffectIds.Count(id =>
                    !assignedIds.Contains(id) && itemSelectorIds.Contains(id)),
                UnreferencedSpecialTypeItem = report.UnreferencedEffectIds.Count(id =>
                    !assignedIds.Contains(id) && !itemSelectorIds.Contains(id) && specialItemSelectorIds.Contains(id)),
                UnreferencedUnused = report.UnreferencedEffectIds.Count(id =>
                    !assignedIds.Contains(id) && !itemSelectorIds.Contains(id)),
                UnreferencedResidual = report.UnreferencedEffectIds.Count(id =>
                    !assignedIds.Contains(id) && !itemSelectorIds.Contains(id) &&
                    !specialItemSelectorIds.Contains(id) && !TableCovered(id)),
                UnreferencedUnusedNamed = report.Effects.Count(row =>
                    report.UnreferencedEffectIds.Contains(row.EffectId) && !string.IsNullOrWhiteSpace(row.Name) &&
                    !assignedIds.Contains(row.EffectId) && !itemSelectorIds.Contains(row.EffectId)),
                UnreferencedUnusedEmpty = report.Effects.Count(row =>
                    report.UnreferencedEffectIds.Contains(row.EffectId) && string.IsNullOrWhiteSpace(row.Name) &&
                    !assignedIds.Contains(row.EffectId) && !itemSelectorIds.Contains(row.EffectId)),
                Unassigned = report.UnassignedCalls.Count,
                Triggers = report.TriggerSummaryCount,
                ReferencedEffects = referencedEffects.Length,
                PersonAssignments = referencedEffects.Sum(pair => pair.Value.PersonAssignments.Count)
            },
            report.DerivedOnlyEffectIds,
            report.UnreachableOnlyEffectIds,
            report.UnreferencedEffectIds,
            UnreferencedAssignedEffectIds = report.UnreferencedEffectIds.Where(assignedIds.Contains).ToArray(),
            UnreferencedItemSelectorEffectIds = report.UnreferencedEffectIds
                .Where(id => !assignedIds.Contains(id) && itemSelectorIds.Contains(id)).ToArray(),
            UnreferencedSpecialTypeItemEffectIds = report.UnreferencedEffectIds
                .Where(id => !assignedIds.Contains(id) && !itemSelectorIds.Contains(id) &&
                             specialItemSelectorIds.Contains(id)).ToArray(),
            UnreferencedUnusedEffectIds = report.UnreferencedEffectIds
                .Where(id => !assignedIds.Contains(id) && !itemSelectorIds.Contains(id)).ToArray(),
            GenericDispatchChain = genericChain,
            AssignmentTable = report.AssignmentTable is { } assignmentTableInfo
                ? new
                {
                    assignmentTableInfo.PointerAddress, assignmentTableInfo.TableAddress,
                    assignmentTableInfo.TableFileOffset, assignmentTableInfo.RowCount,
                    assignmentTableInfo.RowSize, assignmentTableInfo.PersonSlotCount,
                    assignmentTableInfo.JobSlotCount, assignmentTableInfo.SharedValue,
                    assignmentTableInfo.NonEmptyRowCount, assignmentTableInfo.PersonRowCount,
                    assignmentTableInfo.JobRowCount, assignmentTableInfo.Evidence
                }
                : null,
            EmbeddedNamePool = report.EmbeddedNamePool is { } namePoolInfo
                ? new
                {
                    namePoolInfo.TableAddress, namePoolInfo.TableFileOffset, namePoolInfo.RowCount,
                    namePoolInfo.SlotSize, namePoolInfo.IsWritable, namePoolInfo.Evidence
                }
                : null,
            UnreferencedTableCoveredEffectIds = report.UnreferencedEffectIds.Where(TableCovered).ToArray(),
            UnreferencedTablePersonEffectIds = report.UnreferencedEffectIds.Where(TablePerson).ToArray(),
            UnreferencedTableJobEffectIds = report.UnreferencedEffectIds.Where(TableJob).ToArray(),
            UnreferencedResidualEffectIds = report.UnreferencedEffectIds
                .Where(id => !assignedIds.Contains(id) && !itemSelectorIds.Contains(id) &&
                             !specialItemSelectorIds.Contains(id) && !TableCovered(id))
                .ToArray(),
            UnreferencedAssignedDispatch = report.UnreferencedEffectIds.Where(assignedIds.Contains)
                .Select(id => new { EffectId = id, Coverage = "通用分派覆盖（只读）", Chain = genericChain })
                .ToArray(),
            UnreferencedItemSelectorDispatch = report.UnreferencedEffectIds
                .Where(id => !assignedIds.Contains(id) && itemSelectorIds.Contains(id))
                .Select(id => new
                {
                    EffectId = id, Coverage = "装备 selector 引用（运行期通用分派，只读）",
                    Items = itemSelectors[id].Select(item => $"{item.Id}:{item.Name}").ToArray(),
                    Chain = genericChain
                })
                .ToArray(),
            UnreferencedItemSelectorOwners = itemSelectors
                .Where(pair => report.UnreferencedEffectIds.Contains(pair.Key) && !assignedIds.Contains(pair.Key))
                .OrderBy(pair => pair.Key)
                .Select(pair => new
                {
                    EffectId = pair.Key,
                    Count = pair.Value.Count,
                    Items = pair.Value.Select(item => new
                    {
                        item.Id, item.Name, item.Selector, SelectorOffset = $"0x{item.SelectorFileOffset:X}",
                        item.Category, item.Value
                    }).ToArray()
                })
                .ToArray(),
            UnreferencedSpecialTypeItemOwners = specialItemSelectors
                .Where(pair => report.UnreferencedEffectIds.Contains(pair.Key) && !assignedIds.Contains(pair.Key) &&
                               !itemSelectorIds.Contains(pair.Key))
                .OrderBy(pair => pair.Key)
                .Select(pair => new
                {
                    EffectId = pair.Key,
                    Count = pair.Value.Count,
                    Items = pair.Value.Select(item => new
                    {
                        item.Id, item.Name, item.Selector, item.TypeValue, item.NeedsSpecialType,
                        SelectorOffset = $"0x{item.SelectorFileOffset:X}"
                    }).ToArray()
                })
                .ToArray(),
            UnreferencedEffects = report.Effects
                .Where(row => report.UnreferencedEffectIds.Contains(row.EffectId))
                .Select(row => new
                {
                    row.EffectId, row.Name, row.SuffixLabel, row.HasNameSlot, row.HasDescriptionSlot,
                    row.DescriptionByteCount,
                    AssignmentRow = row.HasAssignmentRow ? $"0x{row.AssignmentRowOffset:X}" : null,
                    row.AssignmentPersonIds, row.AssignmentPersonValues,
                    row.AssignmentJobIds, row.AssignmentJobValues, row.AssignmentCovered,
                    Dispatch = assignedIds.Contains(row.EffectId) ? "通用分派覆盖（Effect.e5 人物槽，只读）"
                        : row.AssignmentCovered ? "静态分配表覆盖（人物/兵种行，只读）"
                        : itemSelectorIds.Contains(row.EffectId) ? "装备 selector 引用（运行期通用分派，只读）"
                        : specialItemSelectorIds.Contains(row.EffectId)
                            ? "特殊类型宝物 selector 引用（运行期通用分派，只读）"
                        : string.IsNullOrWhiteSpace(row.Name) ? "空名称槽（保留）"
                        : "已定义但当前数据未引用（名称/说明可写）"
                })
                .ToArray(),
            Wrappers = report.Wrappers.Select(item => new
            {
                Entry = $"0x{item.Entry:X8}", item.Name, item.DerivedCallCount, item.StaticDerivedCallCount,
                CoreCallAddresses = item.CoreCallAddresses.Select(address => $"0x{address:X8}").ToArray()
            }).ToArray(),
            EntryStatistics = report.EntryStatistics.Select(item => new
            {
                Target = $"0x{item.Target:X8}", item.Name, item.Total, item.Static, item.Dynamic,
                item.Unresolved, item.Internal
            }).ToArray(),
            RootInfos = report.RootInfos.Select(info => new
            {
                Address = $"0x{info.Address:X8}",
                info.IsEntryPoint,
                info.IsExported,
                info.DataReferenceCount,
                info.CodeReferenceCount,
                info.ReferenceCount,
                info.InboundJumpCount,
                InboundJumpSources = info.InboundJumpSources.Select(address => $"0x{address:X8}").ToArray(),
                info.InteriorEntryCount,
                InteriorEntryTargets = info.InteriorEntryTargets.Select(address => $"0x{address:X8}").ToArray(),
                InteriorEntryCalls = info.InteriorEntryCalls.Select(address => $"0x{address:X8}").ToArray(),
                info.EffectIds,
                info.IsDeadEffectStub,
                ReachableAlternatives = info.ReachableAlternatives.Select(address => $"0x{address:X8}").ToArray(),
                info.FunctionSize,
                info.FunctionInstructionCount,
                info.EndsWithReturn,
                info.DirectCallCount,
                info.ExternalCallCount,
                info.RegisterCallCount,
                info.EffectCoreCallCount,
                CalleeTargets = info.CalleeTargets.Select(address => $"0x{address:X8}").ToArray(),
                info.EntryBytes,
                info.PrecedingPaddingBytes,
                info.IsNoStaticNonEffectRoot,
                info.EffectReachDepth,
                EffectReachViaEntry = $"0x{info.EffectReachViaEntry:X8}",
                EffectReachViaTarget = $"0x{info.EffectReachViaTarget:X8}",
                info.EffectReachExpandedFunctions,
                EffectReachPath = info.EffectReachPath.Select(address => $"0x{address:X8}").ToArray(),
                info.ReachesEffectEntry,
                info.ReachesEffectIndirectly,
                info.Kind,
                info.Evidence,
                DataReferences = info.DataReferences.Select(reference => new
                {
                    reference.Section,
                    reference.IsExecutable,
                    reference.FileOffset,
                    Address = reference.Address is { } address ? $"0x{address:X8}" : null
                }).ToArray()
            }).ToArray(),
            FunctionRoles = report.FunctionRoles.Select(role => new
            {
                Entry = $"0x{role.Entry:X8}",
                role.Role,
                role.EffectIds,
                role.Evidence,
                BranchPaths = role.BranchPaths.Select(path => new
                {
                    CallAddress = $"0x{path.CallAddress:X8}",
                    Target = $"0x{path.Target:X8}",
                    path.TargetName,
                    path.Reachable,
                    path.BlockCount,
                    path.Truncated,
                    path.Summary,
                    InboundJumpAddress = path.InboundJumpAddress is { } jumpAddress ? $"0x{jumpAddress:X8}" : null,
                    path.InboundJumpKind,
                    path.InboundJumpReachable,
                    InboundJumpFunction = path.InboundJumpFunction is { } jumpFunction
                        ? $"0x{jumpFunction:X8}"
                        : null,
                    path.UpstreamSummary,
                    path.UpstreamTruncated,
                    path.PathCount,
                    path.PathsTruncated,
                    path.ConflictingConditionCount,
                    path.MultiPathSummary,
                    path.UpstreamPathCount,
                    path.UpstreamPathsTruncated,
                    path.UpstreamMultiPathSummary,
                    Conditions = path.Conditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    CommonConditions = path.CommonConditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    OptionalConditions = path.OptionalConditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    ConditionGroups = path.ConditionGroups.Select(group => new
                    {
                        group.Index,
                        group.PathCount,
                        group.ConditionText,
                        group.Summary,
                        group.ExclusiveGroupIndexes,
                        Conditions = group.Conditions.Select(condition => new
                        {
                            Address = $"0x{condition.Address:X8}",
                            condition.Instruction,
                            condition.Comparison,
                            condition.Operand,
                            condition.OperandKind,
                            condition.OperandOrigin,
                            condition.Taken,
                            Target = $"0x{condition.Target:X8}"
                        }).ToArray()
                    }).ToArray(),
                    path.ConditionGroupsTruncated,
                    path.ConditionGroupDuplicatePathCount,
                    ConditionDecisions = path.ConditionDecisions.Select(decision => new
                    {
                        Address = $"0x{decision.Address:X8}",
                        decision.Instruction,
                        decision.Comparison,
                        decision.TakenPaths,
                        decision.NotTakenPaths
                    }).ToArray(),
                    path.ConditionDecisionCombinationSpace,
                    path.ConditionDecisionSpaceComplete,
                    path.ConditionDecisionMissingCount,
                    path.ConditionDecisionSummary,
                    ConditionDecisionNonFixedPoints =
                        path.ConditionDecisionNonFixedPoints.Select(Deviation).ToArray(),
                    ConditionDecisionRepeatedPoints =
                        path.ConditionDecisionRepeatedPoints.Select(Deviation).ToArray(),
                    UpstreamConditions = path.UpstreamConditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    UpstreamCommonConditions = path.UpstreamCommonConditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    UpstreamOptionalConditions = path.UpstreamOptionalConditions.Select(condition => new
                    {
                        Address = $"0x{condition.Address:X8}",
                        condition.Instruction,
                        condition.Comparison,
                        condition.Operand,
                        condition.OperandKind,
                        condition.OperandOrigin,
                        condition.Taken,
                        Target = $"0x{condition.Target:X8}"
                    }).ToArray(),
                    UpstreamConditionGroups = path.UpstreamConditionGroups.Select(group => new
                    {
                        group.Index,
                        group.PathCount,
                        group.ConditionText,
                        group.Summary,
                        group.ExclusiveGroupIndexes,
                        Conditions = group.Conditions.Select(condition => new
                        {
                            Address = $"0x{condition.Address:X8}",
                            condition.Instruction,
                            condition.Comparison,
                            condition.Operand,
                            condition.OperandKind,
                            condition.OperandOrigin,
                            condition.Taken,
                            Target = $"0x{condition.Target:X8}"
                        }).ToArray()
                    }).ToArray(),
                    path.UpstreamConditionGroupsTruncated,
                    UpstreamConditionDecisions = path.UpstreamConditionDecisions.Select(decision => new
                    {
                        Address = $"0x{decision.Address:X8}",
                        decision.Instruction,
                        decision.Comparison,
                        decision.TakenPaths,
                        decision.NotTakenPaths
                    }).ToArray(),
                    path.UpstreamConditionDecisionCombinationSpace,
                    path.UpstreamConditionDecisionSpaceComplete,
                    path.UpstreamConditionDecisionMissingCount,
                    path.UpstreamConditionDecisionSummary,
                    UpstreamConditionDecisionNonFixedPoints =
                        path.UpstreamConditionDecisionNonFixedPoints.Select(Deviation).ToArray(),
                    UpstreamConditionDecisionRepeatedPoints =
                        path.UpstreamConditionDecisionRepeatedPoints.Select(Deviation).ToArray()
                }).ToArray()
            }).ToArray(),
            BranchConditions = new
            {
                report.BranchPathCount,
                report.BranchConditionCount,
                report.BranchPathUnreachableCount,
                report.BranchPathJumpEnteredCount,
                report.BranchPathUpstreamResolvedCount,
                report.BranchPathUpstreamConditionCount,
                report.BranchMultiPathCount,
                report.BranchCommonConditionCount,
                report.BranchOptionalConditionCount,
                report.BranchMultiPathTruncatedCount,
                report.BranchConflictingConditionCount,
                report.BranchConditionGroupCount,
                report.BranchConditionGroupDuplicatePathCount,
                report.BranchConditionGroupLimitCount,
                report.BranchUpstreamConditionGroupCount,
                report.BranchUpstreamCommonConditionCount,
                report.BranchUpstreamOptionalConditionCount,
                report.BranchConditionDecisionCount,
                report.BranchConditionDecisionCompleteCount,
                report.BranchConditionDecisionMissingCount,
                report.BranchConditionDecisionNonFixedCount,
                report.BranchConditionDecisionRepeatedCount,
                report.BranchUpstreamConditionDecisionCount,
                report.BranchUpstreamConditionDecisionNonFixedCount,
                report.BranchUpstreamConditionDecisionRepeatedCount
            },
            TriggerSummary = new
            {
                report.TriggerSummaryCount,
                report.TriggerConditionCount,
                report.TriggerSequentialCount,
                report.TriggerUnreachableCount,
                report.TriggerJumpEnteredCount
            },
            Triggers = report.TriggerSummaries.Select(trigger => new
            {
                trigger.EffectId,
                trigger.EffectName,
                FunctionEntry = $"0x{trigger.FunctionEntry:X8}",
                trigger.FunctionRole,
                CallAddress = $"0x{trigger.CallAddress:X8}",
                Target = $"0x{trigger.Target:X8}",
                trigger.TargetName,
                trigger.Reachable,
                trigger.Truncated,
                trigger.ConditionCount,
                trigger.PathCount,
                trigger.MultiPathSummary,
                trigger.ValueMode,
                trigger.StackingMode,
                trigger.ItemEffectId,
                trigger.PersonalEffectId,
                trigger.PersonalMatchesEffect,
                trigger.ParameterSummary,
                trigger.ConditionSummary,
                trigger.Summary,
                InboundJumpAddress = trigger.InboundJumpAddress is { } jumpAddress
                    ? $"0x{jumpAddress:X8}"
                    : null,
                trigger.InboundJumpKind,
                trigger.InboundJumpReachable,
                Conditions = trigger.Conditions.Select(condition => new
                {
                    Address = $"0x{condition.Address:X8}",
                    condition.Instruction,
                    condition.Comparison,
                    condition.Operand,
                    condition.OperandKind,
                    condition.OperandOrigin,
                    condition.Taken,
                    Target = $"0x{condition.Target:X8}"
                }).ToArray(),
                OptionalConditions = trigger.OptionalConditions.Select(condition => new
                {
                    Address = $"0x{condition.Address:X8}",
                    condition.Instruction,
                    condition.Comparison,
                    condition.Operand,
                    condition.OperandKind,
                    condition.OperandOrigin,
                    condition.Taken,
                    Target = $"0x{condition.Target:X8}"
                }).ToArray(),
                UpstreamConditions = trigger.UpstreamConditions.Select(condition => new
                {
                    Address = $"0x{condition.Address:X8}",
                    condition.Instruction,
                    condition.Comparison,
                    condition.Operand,
                    condition.OperandKind,
                    condition.OperandOrigin,
                    condition.Taken,
                    Target = $"0x{condition.Target:X8}"
                }).ToArray(),
                UpstreamOptionalConditions = trigger.UpstreamOptionalConditions.Select(condition => new
                {
                    Address = $"0x{condition.Address:X8}",
                    condition.Instruction,
                    condition.Comparison,
                    condition.Operand,
                    condition.OperandKind,
                    condition.OperandOrigin,
                    condition.Taken,
                    Target = $"0x{condition.Target:X8}"
                }).ToArray()
            }).ToArray(),
            Indirect = new
            {
                report.IndirectCallCount,
                report.IndirectImportCount,
                report.IndirectRegisterCount,
                report.IndirectMemberCount,
                report.IndirectTableCount,
                report.IndirectCodeTargetCount,
                report.IndirectEffectEntryCount,
                report.EntryPointerCount,
                report.EntryJumpCount,
                Calls = report.IndirectCalls.Select(call => new
                {
                    Address = $"0x{call.Address:X8}",
                    CallerEntry = call.CallerEntry is { } caller ? $"0x{caller:X8}" : null,
                    call.Instruction,
                    call.Kind,
                    ResolvedTarget = call.ResolvedTarget is { } target ? $"0x{target:X8}" : null,
                    call.TargetsEffectEntry,
                    call.MemberExpression,
                    TableAddress = call.TableAddress is { } tableAddress ? $"0x{tableAddress:X8}" : null,
                    CandidateTargets = call.CandidateTargets.Select(candidate => $"0x{candidate:X8}").ToArray(),
                    call.Evidence
                }).ToArray()
            },
            Effects = report.Effects.Where(row => row.HasCalls).Select(row => new
            {
                row.EffectId, row.Name, row.SuffixByte,
                References = references.TryGetValue(row.EffectId, out var reference)
                    ? new
                    {
                        PersonAssignments = reference.PersonAssignments.Select(item => new
                        {
                            item.PersonId, item.PersonName, item.Slot, item.Value,
                            item.Experience, item.RemainingTurns
                        }).ToArray(),
                        Growth = new
                        {
                            reference.Growth.Available, reference.Growth.FileName, reference.Growth.Enabled,
                            reference.Growth.InitialValue, reference.Growth.GrowthLimit,
                            reference.Growth.TemporaryTurns, reference.Growth.GrowthBookExperience,
                            reference.Growth.AttackExperience, reference.Growth.HitExperience,
                            reference.Growth.KillExperience, reference.Growth.IsWritable,
                            reference.Growth.Evidence
                        }
                    }
                    : null,
                Calls = row.CallSiteDetails.Select(Project).ToArray(),
                DerivedCalls = row.DerivedCallDetails.Select(Project).ToArray(),
                TableCalls = row.TableCallDetails.Select(Project).ToArray(),
                LoopCalls = row.LoopCallDetails.Select(Project).ToArray()
            }).ToArray(),
            Unassigned = report.UnassignedCalls.Select(Project).ToArray(),
            Internal = report.InternalCalls.Select(Project).ToArray(),
            Derived = report.DerivedCalls.Select(Project).ToArray(),
            Table = report.TableCalls.Select(Project).ToArray(),
            Loop = report.LoopCalls.Select(Project).ToArray()
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));

        var lines = new List<string>
        {
            "分类,调用点,入口,函数入口,桩类型,状态,效果值,叠加,装备号,个人号,内部原因,值链,叠加链,装备链,个人链,值可写,叠加可写,装备可写,个人可写,链路径数,E8根数,根函数,根类型,可达PE入口"
        };
        var rootKinds = report.RootInfos.ToDictionary(info => info.Address);
        void AddRow(string category, EngineEffectCallSite call)
        {
            lines.Add(string.Join(",",
                Escape(category), Escape($"0x{call.CallAddress:X8}"), Escape(call.EntryName),
                Escape(call.FunctionEntry is { } entry ? $"0x{entry:X8}" : "—"), Escape(call.Pattern.ToString()),
                Escape(call.Status.ToString()), Value(call.ValueMode), Value(call.StackingMode),
                Value(call.ItemEffectId), Value(call.PersonalEffectId), Escape(call.InternalReason ?? string.Empty),
                Escape(Chain(call.ValuePush)), Escape(Chain(call.StackingPush)), Escape(Chain(call.ItemPush)),
                Escape(Chain(call.PersonalPush)), Bool(call.ValuePush.WritePlan.IsWritable),
                Bool(call.StackingPush.WritePlan.IsWritable), Bool(call.ItemPush.WritePlan.IsWritable),
                Bool(call.PersonalPush.WritePlan.IsWritable),
                call.CallerPathCount.ToString(CultureInfo.InvariantCulture),
                call.ConsumerRootCount.ToString(CultureInfo.InvariantCulture),
                Escape(string.Join(" | ", call.ConsumerRoots.Select(address => $"0x{address:X8}"))),
                Escape(string.Join(" | ", call.ConsumerRoots.Select(address =>
                    rootKinds.TryGetValue(address, out var info)
                        ? $"0x{address:X8} {info.Kind}"
                        : $"0x{address:X8}"))),
                Bool(call.ReachesProcessEntry)));
        }
        foreach (var row in report.Effects)
            foreach (var call in row.CallSiteDetails) AddRow($"特效 {row.EffectId}", call);
        foreach (var call in report.UnassignedCalls) AddRow("未归属", call);
        foreach (var call in report.InternalCalls) AddRow("引擎内部", call);
        foreach (var call in report.DerivedCalls)
            AddRow(call.PersonalEffectId is { } id ? $"派生 特效 {id}"
                : call.EffectRange.Count > 0 ? $"派生 范围 {call.EffectRange[0]}-{call.EffectRange[^1]}"
                : "派生 未解析", call);
        foreach (var call in report.TableCalls)
            AddRow(call.PersonalEffectId is { } id ? $"表驱动 特效 {id}" : "表驱动 未解析", call);
        foreach (var call in report.LoopCalls)
            AddRow(call.PersonalEffectId is { } id ? $"循环展开 特效 {id}" : "循环展开 未解析", call);
        File.WriteAllLines(csvPath, lines);

        var pathLines = new List<string>
        {
            "分类,调用点,路径序号,节点序号,调用者函数,调用地址,被调函数,深度,根函数,根为PE入口,状态"
        };
        var pathSeen = new HashSet<uint>();
        void AddPathRows(string category, EngineEffectCallSite call)
        {
            if (!pathSeen.Add(call.CallAddress)) return;
            for (var index = 0; index < call.CallerPaths.Count; index++)
            {
                var path = call.CallerPaths[index];
                var status = PathStatus(path);
                var root = path.RootEntry is { } rootEntry ? $"0x{rootEntry:X8}" : "—";
                if (path.Nodes.Count == 0)
                {
                    pathLines.Add(string.Join(",",
                        Escape(category), Escape($"0x{call.CallAddress:X8}"),
                        (index + 1).ToString(CultureInfo.InvariantCulture), "0",
                        Escape(string.Empty), Escape(string.Empty), Escape(string.Empty), "0",
                        Escape(root), Bool(path.RootIsEntryPoint), Escape(status)));
                    continue;
                }
                var nodeIndex = 0;
                foreach (var node in path.Nodes)
                {
                    nodeIndex++;
                    pathLines.Add(string.Join(",",
                        Escape(category), Escape($"0x{call.CallAddress:X8}"),
                        (index + 1).ToString(CultureInfo.InvariantCulture),
                        nodeIndex.ToString(CultureInfo.InvariantCulture),
                        Escape(node.FunctionEntry == 0 ? string.Empty : $"0x{node.FunctionEntry:X8}"),
                        Escape($"0x{node.CallAddress:X8}"),
                        Escape($"0x{node.ChildTarget:X8}"),
                        node.Depth.ToString(CultureInfo.InvariantCulture),
                        Escape(root), Bool(path.RootIsEntryPoint), Escape(status)));
                }
            }
        }
        foreach (var row in report.Effects)
            foreach (var call in row.CallSiteDetails) AddPathRows($"特效 {row.EffectId}", call);
        foreach (var call in report.UnassignedCalls) AddPathRows("未归属", call);
        foreach (var call in report.InternalCalls) AddPathRows("引擎内部", call);
        foreach (var call in report.DerivedCalls)
            AddPathRows(call.PersonalEffectId is { } id ? $"派生 特效 {id}" : "派生 未解析", call);
        foreach (var call in report.TableCalls)
            AddPathRows(call.PersonalEffectId is { } id ? $"表驱动 特效 {id}" : "表驱动 未解析", call);
        foreach (var call in report.LoopCalls)
            AddPathRows(call.PersonalEffectId is { } id ? $"循环展开 特效 {id}" : "循环展开 未解析", call);
        File.WriteAllLines(pathCsvPath, pathLines);

        var names = report.Effects.ToDictionary(row => row.EffectId, row => row.Name);
        var referenceLines = new List<string>
        {
            "特效ID,特效名称,人物ID,人物名称,槽号,效果值,经验,剩余回合,成长启用,初始值,成长上限,临时回合,成长书经验,攻击经验,受击经验,击杀经验,成长只读,成长来源"
        };
        foreach (var (effectId, summary) in referencedEffects)
        {
            var growth = summary.Growth;
            foreach (var item in summary.PersonAssignments)
                referenceLines.Add(string.Join(",",
                    effectId.ToString(CultureInfo.InvariantCulture),
                    Escape(names.GetValueOrDefault(effectId, string.Empty)),
                    item.PersonId.ToString(CultureInfo.InvariantCulture), Escape(item.PersonName),
                    item.Slot.ToString(CultureInfo.InvariantCulture),
                    item.Value.ToString(CultureInfo.InvariantCulture),
                    item.Experience.ToString(CultureInfo.InvariantCulture),
                    item.RemainingTurns.ToString(CultureInfo.InvariantCulture),
                    Bool(growth.Enabled), growth.InitialValue.ToString(CultureInfo.InvariantCulture),
                    growth.GrowthLimit.ToString(CultureInfo.InvariantCulture),
                    growth.TemporaryTurns.ToString(CultureInfo.InvariantCulture),
                    growth.GrowthBookExperience.ToString(CultureInfo.InvariantCulture),
                    growth.AttackExperience.ToString(CultureInfo.InvariantCulture),
                    growth.HitExperience.ToString(CultureInfo.InvariantCulture),
                    growth.KillExperience.ToString(CultureInfo.InvariantCulture),
                    Bool(!growth.IsWritable), Escape(growth.Evidence)));
        }
        File.WriteAllLines(referenceCsvPath, referenceLines);
        var roleLines = new List<string> { "函数入口,角色,关联特效,证据" };
        foreach (var role in report.FunctionRoles)
            roleLines.Add(string.Join(",",
                Escape($"0x{role.Entry:X8}"), Escape(role.Role),
                Escape(string.Join(" | ", role.EffectIds.Select(id =>
                    names.TryGetValue(id, out var label) && !string.IsNullOrWhiteSpace(label)
                        ? $"{id} {label}" : id.ToString(CultureInfo.InvariantCulture)))),
                Escape(role.Evidence)));
        File.WriteAllLines(roleCsvPath, roleLines);
        Console.WriteLine($"ENGINE_EFFECT_ROLE_OK {report.EngineVersion} {name} " +
                          $"{report.FunctionRoles.Count} -> {roleCsvPath}");
        var indirectCsvPath = Path.Combine(outputDirectory, $"引擎间接调用-{name}.csv");
        var indirectLines = new List<string>
        {
            "调用地址,调用者函数,指令,类型,解析目标,命中特效入口,成员表达式,表地址,候选目标,证据"
        };
        foreach (var call in report.IndirectCalls)
            indirectLines.Add(string.Join(",",
                Escape($"0x{call.Address:X8}"),
                Escape(call.CallerEntry is { } entry ? $"0x{entry:X8}" : "—"),
                Escape(call.Instruction), Escape(call.Kind),
                Escape(call.ResolvedTarget is { } target ? $"0x{target:X8}" : "—"),
                Bool(call.TargetsEffectEntry), Escape(call.MemberExpression),
                Escape(call.TableAddress is { } table ? $"0x{table:X8}" : "—"),
                Escape(string.Join(" | ", call.CandidateTargets.Select(candidate => $"0x{candidate:X8}"))),
                Escape(call.Evidence)));
        File.WriteAllLines(indirectCsvPath, indirectLines);
        var indirectRows = File.ReadAllLines(indirectCsvPath);
        TestSupport.Assert(indirectRows.Length == report.IndirectCallCount + 1 &&
                           indirectRows[0] == "调用地址,调用者函数,指令,类型,解析目标,命中特效入口,成员表达式,表地址,候选目标,证据" &&
                           indirectRows.Skip(1).All(row => row.Contains(",false,", StringComparison.Ordinal) ||
                                                            row.Contains(",True,", StringComparison.Ordinal)),
            "间接调用 CSV 表头、行数或命中标记不符。");
        Console.WriteLine($"ENGINE_EFFECT_INDIRECT_OK {report.EngineVersion} {name} " +
                          $"{report.IndirectCallCount}/{report.IndirectEffectEntryCount} -> {indirectCsvPath}");
        var branchCsvPath = Path.Combine(outputDirectory, $"引擎判定条件-{name}.csv");
        var branchLines = new List<string>
        {
            "函数入口,函数角色,调用点,调用目标,可达,基本块数,条件数,上游条件数,路径数,枚举截断,公共守卫数," +
            "路径特有守卫数,互斥判定,判定路径,多路径合并,上游条件,上游路径数,上游公共守卫数,上游路径特有守卫数," +
            "上游路径合并,条件来源,上游条件来源,条件组数,条件组合并路径,条件组达上限,条件组," +
            "上游条件组数,上游条件组达上限,上游条件组,决策点,组合空间,未枚举组合,决策摘要,上游决策摘要"
        };
        foreach (var role in report.FunctionRoles)
        foreach (var path in role.BranchPaths)
            branchLines.Add(string.Join(",",
                Escape($"0x{role.Entry:X8}"), Escape(role.Role),
                Escape($"0x{path.CallAddress:X8}"),
                Escape($"0x{path.Target:X8}（{path.TargetName}）"),
                Bool(path.Reachable), path.BlockCount.ToString(CultureInfo.InvariantCulture),
                path.Conditions.Count.ToString(CultureInfo.InvariantCulture),
                path.UpstreamConditions.Count.ToString(CultureInfo.InvariantCulture),
                path.PathCount.ToString(CultureInfo.InvariantCulture), Bool(path.PathsTruncated),
                path.CommonConditions.Count.ToString(CultureInfo.InvariantCulture),
                path.OptionalConditions.Count.ToString(CultureInfo.InvariantCulture),
                path.ConflictingConditionCount.ToString(CultureInfo.InvariantCulture),
                Escape(path.Summary), Escape(path.MultiPathSummary), Escape(path.UpstreamSummary),
                path.UpstreamPathCount.ToString(CultureInfo.InvariantCulture),
                path.UpstreamCommonConditions.Count.ToString(CultureInfo.InvariantCulture),
                path.UpstreamOptionalConditions.Count.ToString(CultureInfo.InvariantCulture),
                Escape(path.UpstreamMultiPathSummary),
                Escape(ConditionSources(path.Conditions)), Escape(ConditionSources(path.UpstreamConditions)),
                path.ConditionGroups.Count.ToString(CultureInfo.InvariantCulture),
                path.ConditionGroupDuplicatePathCount.ToString(CultureInfo.InvariantCulture),
                Bool(path.ConditionGroupsTruncated),
                Escape(string.Join(" | ", path.ConditionGroups.Select(group => group.Summary))),
                path.UpstreamConditionGroups.Count.ToString(CultureInfo.InvariantCulture),
                Bool(path.UpstreamConditionGroupsTruncated),
                Escape(string.Join(" | ", path.UpstreamConditionGroups.Select(group => group.Summary))),
                path.ConditionDecisions.Count.ToString(CultureInfo.InvariantCulture),
                path.ConditionDecisionCombinationSpace.ToString(CultureInfo.InvariantCulture),
                path.ConditionDecisionMissingCount.ToString(CultureInfo.InvariantCulture),
                Escape(path.ConditionDecisionSummary),
                Escape(path.UpstreamConditionDecisionSummary)));
        File.WriteAllLines(branchCsvPath, branchLines);
        TestSupport.Assert(branchLines.Count == report.BranchPathCount + 1 &&
                           branchLines[0] == "函数入口,函数角色,调用点,调用目标,可达,基本块数,条件数,上游条件数," +
                                             "路径数,枚举截断,公共守卫数,路径特有守卫数,互斥判定,判定路径,多路径合并," +
                                             "上游条件,上游路径数,上游公共守卫数,上游路径特有守卫数,上游路径合并," +
                                             "条件来源,上游条件来源,条件组数,条件组合并路径,条件组达上限,条件组," +
                                             "上游条件组数,上游条件组达上限,上游条件组,决策点,组合空间,未枚举组合," +
                                             "决策摘要,上游决策摘要",
            "判定条件 CSV 行数或表头与报告统计不一致。");
        TestSupport.Assert(report.FunctionRoles.SelectMany(role => role.BranchPaths)
                .All(path => path.Reachable ? path.PathCount >= 1 : path.PathCount == 0) &&
                           report.FunctionRoles.SelectMany(role => role.BranchPaths)
                               .All(path => !path.Reachable || path.MultiPathSummary.Length > 0) &&
                           report.FunctionRoles.SelectMany(role => role.BranchPaths)
                               .Where(path => path.PathCount > 1)
                               .All(path => path.MultiPathSummary.Contains("公共守卫", StringComparison.Ordinal)),
            "多路径枚举缺失：可达路径应有至少 1 条路径并给出合并摘要。");
        TestSupport.Assert(report.FunctionRoles.SelectMany(role => role.BranchPaths)
                .Where(path => path.Reachable)
                .All(path =>
                {
                    var groups = path.ConditionGroups;
                    if (groups.Count == 0) return false;
                    if (groups.Sum(group => group.PathCount) != path.PathCount) return false;
                    if (path.ConditionGroupDuplicatePathCount != path.PathCount - groups.Count) return false;
                    var keys = groups.Select(group => string.Join("|", group.Conditions
                            .Select(condition => $"{condition.Address:X8}:{condition.Taken}")
                            .OrderBy(text => text, StringComparer.Ordinal)))
                        .ToArray();
                    if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) return false;
                    var union = groups.SelectMany(group => group.Conditions)
                        .Select(condition => (condition.Address, condition.Taken)).ToHashSet();
                    var expected = path.CommonConditions.Concat(path.OptionalConditions)
                        .Select(condition => (condition.Address, condition.Taken)).ToHashSet();
                    return union.SetEquals(expected) &&
                           groups.Select((group, index) => group.Index == index + 1).All(ok => ok) &&
                           groups.All(group => group.ConditionText.Length > 0 &&
                                               group.Summary.Contains($"组 {group.Index}", StringComparison.Ordinal) &&
                                               group.ExclusiveGroupIndexes.All(other =>
                                                   other >= 1 && other <= groups.Count && other != group.Index));
                }) &&
                           report.FunctionRoles.SelectMany(role => role.BranchPaths)
                               .Where(path => path.UpstreamConditionGroups.Count > 0)
                               .All(path => path.UpstreamConditionGroups.Sum(group => group.PathCount) ==
                                            path.UpstreamPathCount),
            "条件组（DNF）与枚举路径不一致：组内路径数之和不等于路径数、组集合重复，" +
            "或组并集不等于公共/特有守卫的并集。");
        var allBranchPathsForDecisions = report.FunctionRoles.SelectMany(role => role.BranchPaths).ToArray();
        TestSupport.Assert(report.BranchConditionDecisionCount ==
                           allBranchPathsForDecisions.Sum(path => path.ConditionDecisions.Count) &&
                           report.BranchConditionDecisionCompleteCount ==
                           allBranchPathsForDecisions.Count(path => path.ConditionDecisionSpaceComplete) &&
                           report.BranchConditionDecisionMissingCount ==
                           allBranchPathsForDecisions.Sum(path => path.ConditionDecisionMissingCount) &&
                           report.BranchUpstreamConditionDecisionCount ==
                           allBranchPathsForDecisions.Sum(path => path.UpstreamConditionDecisions.Count),
            "决策点统计与明细不一致。");
        TestSupport.Assert(allBranchPathsForDecisions.All(path =>
            {
                var decisions = path.ConditionDecisions;
                if (path.PathCount <= 1)
                    return decisions.Count == 0 && path.ConditionDecisionCombinationSpace == 0 &&
                           !path.ConditionDecisionSpaceComplete && path.ConditionDecisionMissingCount == 0 &&
                           path.ConditionDecisionSummary.Length == 0;
                if (decisions.Count == 0)
                    return path.ConditionDecisionCombinationSpace == 0 &&
                           !path.ConditionDecisionSpaceComplete && path.ConditionDecisionMissingCount == 0;
                if (path.ConditionDecisionCombinationSpace != 1 << decisions.Count) return false;
                if (decisions.Select(decision => decision.Address).Distinct().Count() != decisions.Count)
                    return false;
                if (decisions.Any(decision => decision.TakenPaths <= 0 || decision.NotTakenPaths <= 0 ||
                                              decision.Instruction.Length == 0))
                    return false;
                var directions = path.ConditionGroups.SelectMany(group => group.Conditions)
                    .GroupBy(condition => condition.Address)
                    .ToDictionary(group => group.Key, group => group.Select(condition => condition.Taken)
                        .Distinct().Count());
                if (!decisions.All(decision => directions.TryGetValue(decision.Address, out var count) && count == 2))
                    return false;
                return path.ConditionDecisionSpaceComplete
                    ? path.ConditionGroups.Count == path.ConditionDecisionCombinationSpace &&
                      path.ConditionDecisionMissingCount == 0
                    : path.ConditionDecisionMissingCount == 0 ||
                      path.ConditionGroups.Count + path.ConditionDecisionMissingCount ==
                      path.ConditionDecisionCombinationSpace;
            }) &&
                           allBranchPathsForDecisions
                               .Where(path => path.UpstreamConditionDecisions.Count > 0)
                               .All(path => path.UpstreamConditionDecisionCombinationSpace ==
                                            1 << path.UpstreamConditionDecisions.Count &&
                                            path.UpstreamConditionDecisionSummary.Length > 0),
            "决策点与组合覆盖不一致：组合空间不是 2^k、决策点不是二向分支点，" +
            "或完整标记与未枚举组合数矛盾。");
        TestSupport.Assert(report.FunctionRoles.SelectMany(role => role.BranchPaths)
                .All(path => path.CommonConditions.All(common => path.Truncated ||
                           path.Conditions.Any(condition => condition.Address == common.Address &&
                                                            condition.Taken == common.Taken)) &&
                           path.CommonConditions.All(common => !path.OptionalConditions.Any(optional =>
                               optional.Address == common.Address && optional.Taken == common.Taken))),
            "公共守卫不是最短路径的子集（未截断时），或与路径特有守卫重叠。");
        TestSupport.Assert(report.FunctionRoles.SelectMany(role => role.BranchPaths)
                .Where(path => path.Conditions.Any(condition => condition.Comparison != null))
                .All(path => path.Conditions.Where(condition => condition.Comparison != null)
                    .All(condition => condition.Operand.Length > 0 && condition.OperandKind.Length > 0 &&
                                      condition.OperandOrigin.Length > 0)),
            "判定条件缺少操作数来源（Operand/OperandKind/OperandOrigin）。");
        Console.WriteLine($"ENGINE_EFFECT_BRANCH_OK {report.EngineVersion} {name} " +
                          $"{report.BranchPathCount}/{report.BranchConditionCount} -> {branchCsvPath}");
        var triggerCsvPath = Path.Combine(outputDirectory, $"引擎触发条件-{name}.csv");
        var triggerLines = new List<string>
        {
            "特效ID,特效名称,函数入口,函数角色,调用点,入口目标,可达,条件数,路径数,上游条件数,上游路径特有守卫数," +
            "值模式,叠加模式,装备号,个人号,个人号与特效号一致,入边跳转,跳转类型,参数摘要,触发条件摘要,多路径合并"
        };
        foreach (var trigger in report.TriggerSummaries)
            triggerLines.Add(string.Join(",",
                Value(trigger.EffectId), Escape(trigger.EffectName),
                Escape($"0x{trigger.FunctionEntry:X8}"), Escape(trigger.FunctionRole),
                Escape($"0x{trigger.CallAddress:X8}"), Escape($"0x{trigger.Target:X8}（{trigger.TargetName}）"),
                Bool(trigger.Reachable), trigger.ConditionCount.ToString(CultureInfo.InvariantCulture),
                trigger.PathCount.ToString(CultureInfo.InvariantCulture),
                trigger.UpstreamConditions.Count.ToString(CultureInfo.InvariantCulture),
                trigger.UpstreamOptionalConditions.Count.ToString(CultureInfo.InvariantCulture),
                Value(trigger.ValueMode), Value(trigger.StackingMode), Value(trigger.ItemEffectId),
                Value(trigger.PersonalEffectId), Bool(trigger.PersonalMatchesEffect),
                Escape(trigger.InboundJumpAddress is { } jumpAddress ? $"0x{jumpAddress:X8}" : "—"),
                Escape(trigger.InboundJumpKind), Escape(trigger.ParameterSummary),
                Escape(trigger.Summary), Escape(trigger.MultiPathSummary)));
        File.WriteAllLines(triggerCsvPath, triggerLines);
        TestSupport.Assert(triggerLines.Count == report.TriggerSummaryCount + 1 &&
                           triggerLines[0] == "特效ID,特效名称,函数入口,函数角色,调用点,入口目标,可达,条件数,路径数," +
                                               "上游条件数,上游路径特有守卫数,值模式,叠加模式,装备号,个人号," +
                                               "个人号与特效号一致,入边跳转,跳转类型,参数摘要,触发条件摘要,多路径合并" &&
                           report.TriggerSummaries.All(trigger => trigger.Summary.Length > 0 &&
                                                                  trigger.ParameterSummary.Length > 0),
            "触发条件 CSV 行数与报告统计不一致，或摘要为空。");
        Console.WriteLine($"ENGINE_EFFECT_TRIGGER_OK {report.EngineVersion} {name} " +
                          $"{report.TriggerSummaryCount}/{report.TriggerConditionCount} -> {triggerCsvPath}");
        Console.WriteLine($"ENGINE_EFFECT_CALLPATH_OK {report.EngineVersion} {name} " +
                          $"{pathLines.Count - 1} -> {pathCsvPath}");
        Console.WriteLine($"ENGINE_EFFECT_REFERENCE_OK {report.EngineVersion} {name} " +
                          $"{referencedEffects.Length}/{references.Count} -> {referenceCsvPath}");
        Console.WriteLine($"ENGINE_EFFECT_EVIDENCE_OK {report.EngineVersion} {name} -> {jsonPath}");
    }

    /// <summary>装备 selector 引用：物品行的特效号字段，运行期由通用分派消费；保留物品明细用于定位。</summary>
    private static Dictionary<int, List<EngineEffectItemEntity>> ReadItemSelectors(
        ProjectWorkspace workspace, EngineEffectScanReport report, bool specialType = false)
    {
        var result = new Dictionary<int, List<EngineEffectItemEntity>>();
        try
        {
            var catalog = new EngineEffectAssignmentService().Read(workspace, report);
            foreach (var item in catalog.Items)
            {
                if (item.SelectorFileOffset < 0 || item.NeedsSpecialType != specialType ||
                    item.Selector is < 0 or >= 255) continue;
                if (!result.TryGetValue(item.Selector, out var list))
                {
                    list = [];
                    result[item.Selector] = list;
                }
                list.Add(item);
            }
        }
        catch (Exception)
        {
            // 布局未识别时不阻断证据生成；该样本退化为“无装备 selector 证据”。
        }
        return result;
    }

    private static object Project(EngineEffectCallSite call) => new
    {
        CallAddress = $"0x{call.CallAddress:X8}",
        call.EntryName,
        EntryTarget = $"0x{call.EntryTarget:X8}",
        FunctionEntry = call.FunctionEntry is { } entry ? $"0x{entry:X8}" : null,
        call.FunctionEntryDistance,
        Pattern = call.Pattern.ToString(),
        Status = call.Status.ToString(),
        call.InternalReason,
        EffectRange = call.EffectRange.ToArray(),
        call.RangeText,
        CallerChainText = call.CallerChainText,
        call.CallerPathCount,
        call.ConsumerRootCount,
        call.ReachesProcessEntry,
        CallerPathsShown = Math.Min(call.CallerPaths.Count, 8),
        CallerPathsOmitted = Math.Max(0, call.CallerPaths.Count - 8),
        ConsumerRoots = call.ConsumerRoots.Select(address => $"0x{address:X8}").ToArray(),
        CallerPaths = call.CallerPaths.Take(8).Select(path => new
        {
            RootEntry = path.RootEntry is { } root ? $"0x{root:X8}" : null,
            path.RootHasNoCallers,
            path.RootIsEntryPoint,
            path.Truncated,
            path.PathLimitReached,
            path.IsCycle,
            path.UnknownCaller,
            path.Summary,
            Nodes = path.Nodes.Select(node => new
            {
                FunctionEntry = node.FunctionEntry == 0 ? null : $"0x{node.FunctionEntry:X8}",
                CallAddress = $"0x{node.CallAddress:X8}",
                ChildTarget = $"0x{node.ChildTarget:X8}",
                node.Depth,
                node.CallerUnknown,
                node.IsCycle
            }).ToArray()
        }).ToArray(),
        FirstCallerEntry = call.FirstCallerEntry is { } first ? $"0x{first:X8}" : null,
        call.CallerCount,
        call.IsTableConsumer,
        call.IsReachable,
        call.Reachability,
        ReachJumpAddress = call.ReachJumpAddress is { } reachJump ? $"0x{reachJump:X8}" : null,
        ReachCaveEntry = call.ReachCaveEntry is { } caveEntry ? $"0x{caveEntry:X8}" : null,
        call.ConsumerSummary,
        call.TriggerSummary,
        WrapperStubEntry = call.WrapperStubEntry is { } stubEntry ? $"0x{stubEntry:X8}" : null,
        WrapperStubCallAddress = call.WrapperStubCallAddress is { } stubCall ? $"0x{stubCall:X8}" : null,
        RedirectSourceAddress = call.RedirectSourceAddress is { } redirect ? $"0x{redirect:X8}" : null,
        call.TableIndex,
        TableAddress = call.TableAddress is { } table ? $"0x{table:X8}" : null,
        call.TableEntryOffset,
        call.TableExpression,
        call.LoopIndex,
        call.LoopExpression,
        call.LoopBase,
        call.LoopLimit,
        call.ParameterMapping,
        Callees = call.Callees.Select(item => new
        {
            Address = $"0x{item.Address:X8}",
            Target = $"0x{item.Target:X8}",
            item.TargetName
        }).ToArray(),
        CallBytes = Hex(call.CallInstruction.RawBytes),
        StubPushes = call.StubPushes.Select(item => new
        {
            Address = $"0x{item.Address:X8}",
            Value = item.Value,
            Source = item.SourceKind.ToString(),
            Bytes = Hex(item.RawBytes)
        }).ToArray(),
        Arguments = new
        {
            Value = ProjectArgument(call.ValuePush),
            Stacking = ProjectArgument(call.StackingPush),
            Item = ProjectArgument(call.ItemPush),
            Personal = ProjectArgument(call.PersonalPush)
        },
        call.DisassemblyText
    };

    private static object ProjectArgument(EngineEffectPushArgument argument) => new
    {
        Address = argument.Address == 0 ? null : $"0x{argument.Address:X8}",
        argument.Value,
        argument.ResolvedValue,
        argument.MinimumValue,
        argument.MaximumValue,
        argument.RangeReason,
        Source = argument.SourceKind.ToString(),
        RawSource = argument.Source,
        Bytes = Hex(argument.RawBytes),
        IsWritable = argument.WritePlan.IsWritable,
        argument.WritePlan.BlockReason,
        Definition = argument.WritePlan.Definition is { } definition ? $"0x{definition.Address:X8}" : null,
        argument.WritePlan.ImmediateFileOffset,
        argument.WritePlan.ImmediateLength,
        argument.WritePlan.OldValue,
        argument.WritePlan.IsSharedDefinition,
        Chain = argument.DefinitionChain.Select(item => new
        {
            Address = $"0x{item.Address:X8}",
            item.Text,
            item.ImmediateOffset,
            item.ImmediateSize,
            Bytes = Hex(item.RawBytes)
        }).ToArray()
    };

    private static string Chain(EngineEffectPushArgument argument)
        => string.Join(" | ", argument.DefinitionChain.Select(item => item.Text));

    private static string PathStatus(EngineEffectCallPath path)
        => path.PathLimitReached ? "路径上限"
            : path.IsCycle ? "调用环"
            : path.UnknownCaller ? "调用者未知"
            : path.Truncated ? "深度上限"
            : path.RootEntry != null ? (path.RootIsEntryPoint ? "到 PE 入口" : "到 E8 根")
            : "未解析";

    private static string Value(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Bool(bool value) => value ? "true" : "false";

    private static string ConditionSources(IReadOnlyList<EngineEffectBranchCondition> conditions)
        => string.Join(" | ", conditions.Select(condition =>
            $"{condition.Comparison ?? condition.Instruction} → " +
            $"{condition.Operand}={condition.OperandKind}；{condition.OperandOrigin}"));

    private static string Escape(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Hex(byte[] bytes)
        => bytes.Length == 0 ? string.Empty : string.Join(" ", bytes.Select(value => value.ToString("X2",
            CultureInfo.InvariantCulture)));
}
