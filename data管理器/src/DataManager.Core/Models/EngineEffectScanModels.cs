﻿namespace DataManager.Core.Models;

public sealed record EngineEffectScanReport
{
    public string ExePath { get; init; } = string.Empty;
    public string ImsgPath { get; init; } = string.Empty;
    public string EngineVersion { get; init; } = string.Empty;
    public string ExeSha256 { get; init; } = string.Empty;
    public string ImsgSha256 { get; init; } = string.Empty;
    public uint EntryPoint { get; init; }
    public int NameCount { get; init; }
    public int CallSiteCount { get; init; }
    public int EffectiveCallSiteCount { get; init; }
    public int InternalCallSiteCount { get; init; }
    public int ResolvedCallSiteCount { get; init; }
    public int DynamicCallSiteCount { get; init; }
    public int UnresolvedCallSiteCount { get; init; }
    public int DerivedCallCount { get; init; }
    public int ResolvedDerivedCallCount { get; init; }
    public int TableCallCount { get; init; }
    public int LoopCallCount { get; init; }
    public int UnreachableCallCount { get; init; }
    public int CaveEntryCallCount { get; init; }
    public int DeadEffectStubCount { get; init; }
    /// <summary>Instructions recovered by the boundary map (call targets, prologues, jump tables).</summary>
    public int InstructionCount { get; init; }
    /// <summary>Indexed jump-table entries used only as decode anchors, never as jump edges.</summary>
    public int IndexedJumpAnchorCount { get; init; }
    /// <summary>Raw 0xE8 bytes that aligned decoding of the caller function rejects.</summary>
    public int UnalignedCallCount { get; init; }
    public IReadOnlyList<uint> UnalignedCallAddresses { get; init; } = [];
    public IReadOnlyList<int> DerivedOnlyEffectIds { get; init; } = [];
    public IReadOnlyList<int> UnreachableOnlyEffectIds { get; init; } = [];
    public IReadOnlyList<int> UnreferencedEffectIds { get; init; } = [];
    public IReadOnlyList<EngineEffectWrapperInfo> Wrappers { get; init; } = [];
    public IReadOnlyList<EngineEffectEntryStatistics> EntryStatistics { get; init; } = [];
    public IReadOnlyList<EngineEffectRootInfo> RootInfos { get; init; } = [];
    public IReadOnlyList<EngineEffectFunctionRole> FunctionRoles { get; init; } = [];
    public int IndirectCallCount { get; init; }
    public int IndirectImportCount { get; init; }
    public int IndirectRegisterCount { get; init; }
    public int IndirectMemberCount { get; init; }
    public int IndirectTableCount { get; init; }
    public int IndirectCodeTargetCount { get; init; }
    public int IndirectEffectEntryCount { get; init; }
    /// <summary>Four-byte absolute pointers anywhere in the image that point at an effect entry.</summary>
    public int EntryPointerCount { get; init; }
    /// <summary>Direct E9/EB jumps whose target is an effect entry.</summary>
    public int EntryJumpCount { get; init; }
    public IReadOnlyList<EngineEffectIndirectCall> IndirectCalls { get; init; } = [];
    /// <summary>Effect-entry calls whose guard conditions were recovered from the function CFG.</summary>
    public int BranchPathCount { get; init; }
    public int BranchConditionCount { get; init; }
    public int BranchPathUnreachableCount { get; init; }
    /// <summary>Unreachable paths that were tied to an inbound E9/EB/Jcc jump entering the call site.</summary>
    public int BranchPathJumpEnteredCount { get; init; }
    /// <summary>Jump-entered paths whose guards were also recovered in the jumping function.</summary>
    public int BranchPathUpstreamResolvedCount { get; init; }
    public int BranchPathUpstreamConditionCount { get; init; }
    /// <summary>Call sites reached by more than one static entry→call path.</summary>
    public int BranchMultiPathCount { get; init; }
    /// <summary>Guards that hold on every enumerated path (intersection).</summary>
    public int BranchCommonConditionCount { get; init; }
    /// <summary>Guards that only hold on some enumerated paths (union minus intersection).</summary>
    public int BranchOptionalConditionCount { get; init; }
    /// <summary>Call sites whose path enumeration hit the path/depth/step cap.</summary>
    public int BranchMultiPathTruncatedCount { get; init; }
    /// <summary>Branch instructions that are taken on some paths and not taken on others.</summary>
    public int BranchConflictingConditionCount { get; init; }
    /// <summary>Distinct guard sets (DNF groups) recovered across all reachable call sites.</summary>
    public int BranchConditionGroupCount { get; init; }
    /// <summary>Enumerated paths that share a guard set with an earlier path and were collapsed.</summary>
    public int BranchConditionGroupDuplicatePathCount { get; init; }
    /// <summary>Call sites whose distinct guard sets hit the group cap.</summary>
    public int BranchConditionGroupLimitCount { get; init; }
    public int BranchUpstreamConditionGroupCount { get; init; }
    public int BranchUpstreamCommonConditionCount { get; init; }
    public int BranchUpstreamOptionalConditionCount { get; init; }
    /// <summary>Two-direction branch points recovered across all call sites.</summary>
    public int BranchConditionDecisionCount { get; init; }
    /// <summary>Sites whose decision-point combination space is fully enumerated.</summary>
    public int BranchConditionDecisionCompleteCount { get; init; }
    /// <summary>Decision-point combinations that path enumeration never reached.</summary>
    public int BranchConditionDecisionMissingCount { get; init; }
    /// <summary>Branch addresses that only some enumerated paths test (optional guard).</summary>
    public int BranchConditionDecisionNonFixedCount { get; init; }
    /// <summary>Branch addresses tested more than once inside a single path (loop back edge).</summary>
    public int BranchConditionDecisionRepeatedCount { get; init; }
    public int BranchUpstreamConditionDecisionCount { get; init; }
    public int BranchUpstreamConditionDecisionNonFixedCount { get; init; }
    public int BranchUpstreamConditionDecisionRepeatedCount { get; init; }
    /// <summary>Guard conditions merged with the pushed effect/value/stacking/item arguments.</summary>
    public int TriggerSummaryCount { get; init; }
    public int TriggerConditionCount { get; init; }
    public int TriggerSequentialCount { get; init; }
    public int TriggerUnreachableCount { get; init; }
    public int TriggerJumpEnteredCount { get; init; }
    public IReadOnlyList<EngineEffectTriggerSummary> TriggerSummaries { get; init; } = [];
    /// <summary>Static per-effect assignment table read by the generic dispatch (null when unverified).</summary>
    public EngineEffectAssignmentTableInfo? AssignmentTable { get; init; }
    /// <summary>Native 16-byte effect-name pool inside Ekd5.exe (read-only shared names).</summary>
    public EngineEffectNamePoolInfo? EmbeddedNamePool { get; init; }
    public IReadOnlyList<EngineEffectScanRow> Effects { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> UnassignedCalls { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> InternalCalls { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> DerivedCalls { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> TableCalls { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> LoopCalls { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed class EngineEffectEntryStatistics
{
    public uint Target { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Static { get; init; }
    public int Dynamic { get; init; }
    public int Unresolved { get; init; }
    public int Internal { get; init; }
}

/// <summary>Structural role of one function that appears in an effect call chain.</summary>
public sealed class EngineEffectFunctionRole
{
    public uint Entry { get; init; }
    public string Role { get; init; } = string.Empty;
    public IReadOnlyList<int> EffectIds { get; init; } = [];
    public string Evidence { get; init; } = string.Empty;
    /// <summary>Guard conditions recovered for each direct effect-entry call of this function.</summary>
    public IReadOnlyList<EngineEffectBranchPath> BranchPaths { get; init; } = [];
}

/// <summary>One branch decision on the shortest static path from a function entry to a call site.</summary>
public sealed class EngineEffectBranchCondition
{
    public uint Address { get; init; }
    public string Instruction { get; init; } = string.Empty;
    /// <summary>Nearest <c>cmp</c>/<c>test</c> that produced the flags for this branch.</summary>
    public string? Comparison { get; init; }
    /// <summary>Register or memory operand the comparison actually tests, e.g. <c>al</c> or <c>[ebp+8]</c>.</summary>
    public string Operand { get; init; } = string.Empty;
    /// <summary>常量 / 栈参数 / 栈局部 / 全局变量 / 对象字段 / 对象数组 / 调用返回值 / 寄存器计算 / 未定.</summary>
    public string OperandKind { get; init; } = string.Empty;
    /// <summary>Provenance chain of the operand, e.g. <c>movzx eax,[ecx+4] ← 对象字段 [ecx+4] ← mov ecx,[ebp+8] ← 栈参数 1 [ebp+8]</c>.</summary>
    public string OperandOrigin { get; init; } = string.Empty;
    public bool Taken { get; init; }
    public uint Target { get; init; }
}

/// <summary>Static decision path reaching one effect-entry call inside a function.</summary>
public sealed class EngineEffectBranchPath
{
    public uint CallAddress { get; init; }
    public uint Target { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public int BlockCount { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<EngineEffectBranchCondition> Conditions { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
    /// <summary>
    /// Number of distinct entry→call paths that were enumerated for this call site.
    /// <see cref="Conditions"/> still carries the shortest path; the common/optional
    /// lists below describe the whole set, so a guard outside the shortest path is no
    /// longer silently dropped.
    /// </summary>
    public int PathCount { get; init; }
    /// <summary>True when the enumeration hit the path, depth or step cap.</summary>
    public bool PathsTruncated { get; init; }
    /// <summary>Guards that every enumerated path passes through.</summary>
    public IReadOnlyList<EngineEffectBranchCondition> CommonConditions { get; init; } = [];
    /// <summary>Guards that only some enumerated paths pass through.</summary>
    public IReadOnlyList<EngineEffectBranchCondition> OptionalConditions { get; init; } = [];
    /// <summary>Branch addresses that are taken on one path and not taken on another.</summary>
    public int ConflictingConditionCount { get; init; }
    public string MultiPathSummary { get; init; } = string.Empty;
    /// <summary>
    /// Disjoint guard sets of the enumerated paths: every group is one conjunction
    /// (AND) of branch decisions that reaches the call, and the groups together are
    /// the disjunction (OR) of all alternatives. Duplicate guard sets are collapsed;
    /// <see cref="EngineEffectBranchConditionGroup.PathCount"/> keeps how many paths
    /// the group stands for.
    /// </summary>
    public IReadOnlyList<EngineEffectBranchConditionGroup> ConditionGroups { get; init; } = [];
    /// <summary>True when the number of distinct guard sets hit the group cap.</summary>
    public bool ConditionGroupsTruncated { get; init; }
    /// <summary>Enumerated paths collapsed into an already known guard set.</summary>
    public int ConditionGroupDuplicatePathCount { get; init; }
    /// <summary>
    /// Branch addresses that appear in every enumerated path and were seen in both
    /// directions. Their directions are the free variables of the DNF; the combination
    /// space below is 2^k over exactly these points.
    /// </summary>
    public IReadOnlyList<EngineEffectConditionDecision> ConditionDecisions { get; init; } = [];
    /// <summary>Theoretical number of direction combinations (2^k) of <see cref="ConditionDecisions"/>.</summary>
    public int ConditionDecisionCombinationSpace { get; init; }
    /// <summary>
    /// True when every combination of the decision points was enumerated, and no path
    /// tests an address that is missing from another path (so the 2^k model holds).
    /// </summary>
    public bool ConditionDecisionSpaceComplete { get; init; }
    /// <summary>Combinations of the decision points that were never enumerated.</summary>
    public int ConditionDecisionMissingCount { get; init; }
    public string ConditionDecisionSummary { get; init; } = string.Empty;
    /// <summary>Branch addresses that only some enumerated paths test, with presence statistics.</summary>
    public IReadOnlyList<EngineEffectConditionDeviation> ConditionDecisionNonFixedPoints { get; init; } = [];
    /// <summary>Branch addresses tested more than once inside a single path (loop back edge).</summary>
    public IReadOnlyList<EngineEffectConditionDeviation> ConditionDecisionRepeatedPoints { get; init; } = [];
    /// <summary>Direct jump whose target is the call site when the call is outside this function's blocks.</summary>
    public uint? InboundJumpAddress { get; init; }
    public string InboundJumpKind { get; init; } = string.Empty;
    /// <summary>Function that owns the inbound jump, recovered from the call graph.</summary>
    public uint? InboundJumpFunction { get; init; }
    /// <summary>Whether the jumping block itself is reachable from its function entry.</summary>
    public bool? InboundJumpReachable { get; init; }
    /// <summary>Guards from the jumping function's entry to the inbound jump (taken side included).</summary>
    public IReadOnlyList<EngineEffectBranchCondition> UpstreamConditions { get; init; } = [];
    public bool UpstreamTruncated { get; init; }
    public string UpstreamSummary { get; init; } = string.Empty;
    /// <summary>Distinct paths from the jumping function's entry to the inbound jump.</summary>
    public int UpstreamPathCount { get; init; }
    public bool UpstreamPathsTruncated { get; init; }
    public IReadOnlyList<EngineEffectBranchCondition> UpstreamCommonConditions { get; init; } = [];
    public IReadOnlyList<EngineEffectBranchCondition> UpstreamOptionalConditions { get; init; } = [];
    public string UpstreamMultiPathSummary { get; init; } = string.Empty;
    /// <summary>Guard sets of the enumerated jump paths in the function that owns the inbound jump.</summary>
    public IReadOnlyList<EngineEffectBranchConditionGroup> UpstreamConditionGroups { get; init; } = [];
    public bool UpstreamConditionGroupsTruncated { get; init; }
    /// <summary>Two-direction branch points of the upstream jump paths.</summary>
    public IReadOnlyList<EngineEffectConditionDecision> UpstreamConditionDecisions { get; init; } = [];
    public int UpstreamConditionDecisionCombinationSpace { get; init; }
    public bool UpstreamConditionDecisionSpaceComplete { get; init; }
    public int UpstreamConditionDecisionMissingCount { get; init; }
    public string UpstreamConditionDecisionSummary { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectConditionDeviation> UpstreamConditionDecisionNonFixedPoints { get; init; } = [];
    public IReadOnlyList<EngineEffectConditionDeviation> UpstreamConditionDecisionRepeatedPoints { get; init; } = [];
}

/// <summary>
/// One alternative guard conjunction (DNF term). Conditions are kept in execution
/// order; <see cref="PathCount"/> says how many enumerated entry→call paths share the
/// exact same set, and <see cref="ExclusiveGroupIndexes"/> lists the other groups
/// that disagree on the direction of a shared branch — those groups cannot hold at
/// the same time.
/// </summary>
public sealed class EngineEffectBranchConditionGroup
{
    public int Index { get; init; }
    public IReadOnlyList<EngineEffectBranchCondition> Conditions { get; init; } = [];
    public int PathCount { get; init; }
    public IReadOnlyList<int> ExclusiveGroupIndexes { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
    /// <summary>Formatted branch text, e.g. <c>cmp eax,64h → jbe（不跳转）</c>.</summary>
    public string ConditionText { get; init; } = string.Empty;
}

/// <summary>
/// One branch address that behaves as a boolean free variable: it appears in every
/// enumerated path, and the jump is taken on some paths and not taken on others.
/// Together these points explain why the DNF has many terms: the terms are their
/// direction combinations, so 2^k bounds how many can exist.
/// </summary>
public sealed class EngineEffectConditionDecision
{
    public uint Address { get; init; }
    /// <summary>Branch instruction text, e.g. <c>test eax,eax → je 0040CC3Fh</c>.</summary>
    public string Instruction { get; init; } = string.Empty;
    public string? Comparison { get; init; }
    /// <summary>Enumerated paths that take the jump at this address.</summary>
    public int TakenPaths { get; init; }
    /// <summary>Enumerated paths that fall through at this address.</summary>
    public int NotTakenPaths { get; init; }
}

/// <summary>
/// One branch address that makes the 2^k model of the decision points inapplicable.
/// An optional guard appears in only some of the enumerated paths; a loop back edge
/// tests the same address more than once inside one path, where the path can both
/// take and fall through the branch. Both cases are excluded from the combination
/// space and listed here with their presence statistics instead of being dropped.
/// </summary>
public sealed class EngineEffectConditionDeviation
{
    public uint Address { get; init; }
    /// <summary>Branch instruction text, e.g. <c>test eax,eax → je 0040CC3Fh</c>.</summary>
    public string Instruction { get; init; } = string.Empty;
    public string? Comparison { get; init; }
    /// <summary>Enumerated paths that test this address at least once.</summary>
    public int PresentPaths { get; init; }
    /// <summary>Total enumerated paths of the site.</summary>
    public int PathCount { get; init; }
    /// <summary>Occurrences that take the jump (a loop path can exceed the path count).</summary>
    public int TakenPaths { get; init; }
    /// <summary>Occurrences that fall through (a loop path can exceed the path count).</summary>
    public int NotTakenPaths { get; init; }
    /// <summary>True when a single path tests the address more than once.</summary>
    public bool Repeated { get; init; }
}

/// <summary>
/// Trigger summary of one effect-entry call: the recovered guard path merged with
/// the four pushed stack arguments, so "effect 12" reads as "reach this call while
/// these cmp/test decisions hold and push value/stacking/item/personal = ...".
/// </summary>
public sealed class EngineEffectTriggerSummary
{
    public EngineEffectCallIdentity Identity { get; init; }
    public int? EffectId { get; init; }
    public string EffectName { get; init; } = string.Empty;
    public uint FunctionEntry { get; init; }
    public string FunctionRole { get; init; } = string.Empty;
    public uint CallAddress { get; init; }
    public uint Target { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public bool Truncated { get; init; }
    public int ConditionCount { get; init; }
    public int? ValueMode { get; init; }
    public int? StackingMode { get; init; }
    public int? ItemEffectId { get; init; }
    public int? PersonalEffectId { get; init; }
    public bool PersonalMatchesEffect { get; init; }
    public string ParameterSummary { get; init; } = string.Empty;
    public string ConditionSummary { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public uint? InboundJumpAddress { get; init; }
    public string InboundJumpKind { get; init; } = string.Empty;
    public bool? InboundJumpReachable { get; init; }
    public IReadOnlyList<EngineEffectBranchCondition> Conditions { get; init; } = [];
    /// <summary>Guards that only some of the enumerated entry→call paths pass through.</summary>
    public IReadOnlyList<EngineEffectBranchCondition> OptionalConditions { get; init; } = [];
    public int PathCount { get; init; }
    public string MultiPathSummary { get; init; } = string.Empty;
    /// <summary>Guards recovered in the upstream function that jumps into this call site.</summary>
    public IReadOnlyList<EngineEffectBranchCondition> UpstreamConditions { get; init; } = [];
    /// <summary>Upstream guards that only some of the enumerated jump paths pass through.</summary>
    public IReadOnlyList<EngineEffectBranchCondition> UpstreamOptionalConditions { get; init; } = [];
}

/// <summary>One <c>call r/m32</c> site and the static target it can be reduced to.</summary>
public sealed class EngineEffectIndirectCall
{
    public uint Address { get; init; }
    public uint? CallerEntry { get; init; }
    public string Instruction { get; init; } = string.Empty;
    /// <summary>特效入口 / 代码目标 / 导入表 / 寄存器 / 成员 / 表。</summary>
    public string Kind { get; init; } = string.Empty;
    public uint? ResolvedTarget { get; init; }
    public bool TargetsEffectEntry { get; init; }
    public string Evidence { get; init; } = string.Empty;
    /// <summary>Formatted memory operand such as <c>[eax+10h]</c> for member (virtual slot) calls.</summary>
    public string MemberExpression { get; init; } = string.Empty;
    /// <summary>Static offset-table base when the call has an index but no base register.</summary>
    public uint? TableAddress { get; init; }
    /// <summary>Executable dwords read from the table the indexed call dispatches through.</summary>
    public IReadOnlyList<uint> CandidateTargets { get; init; } = [];
}

public sealed record EngineEffectScanRow
{
    public int EffectId { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte? SuffixByte { get; init; }
    public string SuffixLabel { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int DescriptionByteCount { get; init; }
    public bool HasNameSlot { get; init; }
    public bool HasDescriptionSlot { get; init; }
    public long NameSlotOffset { get; init; }
    public long DescriptionSlotOffset { get; init; }
    public int NameCapacity { get; init; }
    public IReadOnlyList<EngineEffectCallSite> CallSiteDetails { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> DerivedCallDetails { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> TableCallDetails { get; init; } = [];
    public IReadOnlyList<EngineEffectCallSite> LoopCallDetails { get; init; } = [];
    public IReadOnlyList<int> StackingModes { get; init; } = [];
    public IReadOnlyList<int> ValueModes { get; init; } = [];
    public IReadOnlyList<int> ItemEffectIds { get; init; } = [];
    public IReadOnlyList<string> Entries { get; init; } = [];
    public IReadOnlyList<uint> CallSites => CallSiteDetails.Select(call => call.CallAddress).ToArray();
    public bool HasCalls => CallSiteDetails.Count > 0 || DerivedCallDetails.Count > 0 ||
                            TableCallDetails.Count > 0 || LoopCallDetails.Count > 0;
    public bool HasReachableCall { get; init; }
    public bool OnlyUnreachableCalls { get; init; }
    public int TotalCallCount => CallSiteDetails.Count + DerivedCallDetails.Count +
                                 TableCallDetails.Count + LoopCallDetails.Count;
    /// <summary>Assignment table row for this effect id (empty when the engine has no verified table).</summary>
    public IReadOnlyList<int> AssignmentPersonIds { get; init; } = [];
    public IReadOnlyList<int> AssignmentPersonValues { get; init; } = [];
    public IReadOnlyList<int> AssignmentJobIds { get; init; } = [];
    public IReadOnlyList<int> AssignmentJobValues { get; init; } = [];
    public long AssignmentRowOffset { get; init; } = -1;
    public bool HasAssignmentRow => AssignmentRowOffset >= 0;
    public bool HasPersonAssignment => AssignmentPersonIds.Count > 0;
    public bool HasJobAssignment => AssignmentJobIds.Count > 0;
    public bool AssignmentCovered => HasPersonAssignment || HasJobAssignment;
}

/// <summary>
/// Geometry of the per-effect assignment table the generic dispatch consumes:
/// row index = effect id, 3 persons + 1 job (8-byte rows) or 4 persons + 2 jobs (16-byte rows).
/// </summary>
public sealed record EngineEffectAssignmentTableInfo
{
    public bool Verified { get; init; }
    public uint PointerAddress { get; init; }
    public uint TableAddress { get; init; }
    public long TableFileOffset { get; init; } = -1;
    public int RowCount { get; init; }
    public int RowSize { get; init; }
    public int PersonSlotCount { get; init; }
    public int JobSlotCount { get; init; }
    public bool SharedValue { get; init; }
    public int NonEmptyRowCount { get; init; }
    public int PersonRowCount { get; init; }
    public int JobRowCount { get; init; }
    public string Evidence { get; init; } = string.Empty;
}

/// <summary>Native effect-name pool: 16-byte GBK slots, shared and intentionally read-only.</summary>
public sealed record EngineEffectNamePoolInfo
{
    public bool Verified { get; init; }
    public uint TableAddress { get; init; }
    public long TableFileOffset { get; init; } = -1;
    public int RowCount { get; init; }
    public int SlotSize { get; init; } = 16;
    public bool IsWritable { get; init; }
    public string Evidence { get; init; } = string.Empty;
}

public readonly record struct EngineEffectCallIdentity(uint CallAddress, uint? WrapperStubEntry,
    uint? WrapperStubCallAddress, uint? TableAddress, int? TableIndex, int? LoopIndex,
    uint? RedirectSourceAddress);

public sealed class EngineEffectCallSite
{
    public EngineEffectCallIdentity Identity => new(CallAddress, WrapperStubEntry, WrapperStubCallAddress,
        TableAddress, TableIndex, LoopIndex, RedirectSourceAddress);
    public uint CallAddress { get; init; }
    public uint? FunctionEntry { get; init; }
    public string EntryName { get; init; } = string.Empty;
    public uint EntryTarget { get; init; }
    public int? StackingMode { get; init; }
    public int? ValueMode { get; init; }
    public int? ItemEffectId { get; init; }
    public int? PersonalEffectId { get; init; }
    /// <summary>
    /// Personal effect ids this call covers when the id is a guarded stack
    /// parameter plus a constant (for example [ebp+8]+0x90 with 0≤[ebp+8]≤4).
    /// </summary>
    public IReadOnlyList<int> EffectRange { get; set; } = [];
    public string RangeText { get; set; } = string.Empty;
    public EngineEffectCallStatus Status { get; init; }
    public EngineEffectCallPattern Pattern { get; init; }
    public int FunctionEntryDistance { get; init; }
    public string? InternalReason { get; init; }
    public EngineEffectPushArgument ValuePush { get; init; } = new();
    public EngineEffectPushArgument StackingPush { get; init; } = new();
    public EngineEffectPushArgument ItemPush { get; init; } = new();
    public EngineEffectPushArgument PersonalPush { get; init; } = new();
    public EngineEffectCallInstruction CallInstruction { get; init; } = new();
    public IReadOnlyList<EngineEffectPushArgument> Pushes { get; init; } = [];
    public IReadOnlyList<EngineEffectPushArgument> StubPushes { get; init; } = [];
    public string LeadingBytesHex { get; init; } = string.Empty;
    public string DisassemblyText { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectCallChainNode> CallerChain { get; init; } = [];
    public string CallerChainText { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectCallPath> CallerPaths { get; init; } = [];
    public IReadOnlyList<uint> ConsumerRoots { get; init; } = [];
    public int CallerPathCount => CallerPaths.Count;
    public int ConsumerRootCount => ConsumerRoots.Count;
    public bool ReachesE8Root => ConsumerRoots.Count > 0;
    public bool ReachesProcessEntry => CallerPaths.Any(path => path.RootIsEntryPoint);
    public uint? FirstCallerEntry { get; init; }
    public int CallerCount { get; init; }
    public bool IsTableConsumer { get; init; }
    public string ConsumerSummary { get; set; } = string.Empty;
    /// <summary>Guard conditions merged with the pushed arguments; filled after function roles are built.</summary>
    public string TriggerSummary { get; set; } = string.Empty;
    public uint? WrapperStubEntry { get; init; }
    public uint? WrapperStubCallAddress { get; init; }
    public string ParameterMapping { get; init; } = string.Empty;
    public IReadOnlyList<EngineEffectFunctionCall> Callees { get; init; } = [];
    public uint? RedirectSourceAddress { get; init; }
    public int? TableIndex { get; init; }
    public uint? TableAddress { get; init; }
    public long? TableEntryOffset { get; init; }
    public string TableExpression { get; init; } = string.Empty;
    public int? LoopIndex { get; init; }
    public string LoopExpression { get; init; } = string.Empty;
    public int? LoopBase { get; init; }
    public int? LoopLimit { get; init; }
    public bool? IsReachable { get; init; }
    public string Reachability { get; init; } = string.Empty;
    public uint? ReachJumpAddress { get; init; }
    public uint? ReachCaveEntry { get; init; }
}

/// <summary>One full E8 caller path from a call site's function to a root.</summary>
public sealed class EngineEffectCallPath
{
    public IReadOnlyList<EngineEffectCallPathNode> Nodes { get; init; } = [];
    public uint? RootEntry { get; init; }
    public bool RootHasNoCallers { get; init; }
    public bool RootIsEntryPoint { get; init; }
    public bool Truncated { get; init; }
    public bool PathLimitReached { get; init; }
    public bool IsCycle { get; init; }
    public bool UnknownCaller { get; init; }
    public string Summary { get; init; } = string.Empty;
}

/// <summary>One caller edge: the function containing the call and the function it calls.</summary>
public sealed class EngineEffectCallPathNode
{
    public uint FunctionEntry { get; init; }
    public uint CallAddress { get; init; }
    public uint ChildTarget { get; init; }
    public int Depth { get; init; }
    public bool CallerUnknown { get; init; }
    public bool IsCycle { get; init; }
}

public sealed class EngineEffectWrapperInfo
{
    public uint Entry { get; init; }
    public string Name { get; init; } = string.Empty;
    public int DerivedCallCount { get; init; }
    public int StaticDerivedCallCount { get; init; }
    public IReadOnlyList<uint> CoreCallAddresses { get; init; } = [];
}

public sealed class EngineEffectFunctionCall
{
    public uint Address { get; init; }
    public uint Target { get; init; }
    public string TargetName { get; init; } = string.Empty;
}

public sealed class EngineEffectCallChainNode
{
    public uint Target { get; init; }
    public uint CallAddress { get; init; }
    public uint? CallerEntry { get; init; }
    public int Depth { get; init; }
}

/// <summary>Why a function has no incoming E8 call: process entry, export or data pointer.</summary>
public sealed class EngineEffectRootInfo
{
    public uint Address { get; init; }
    public bool IsEntryPoint { get; init; }
    public bool IsExported { get; init; }
    public int DataReferenceCount { get; init; }
    public int CodeReferenceCount { get; init; }
    public int ReferenceCount => DataReferenceCount + CodeReferenceCount;
    public int InboundJumpCount { get; init; }
    public IReadOnlyList<uint> InboundJumpSources { get; init; } = [];
    public int InteriorEntryCount { get; init; }
    public IReadOnlyList<uint> InteriorEntryTargets { get; init; } = [];
    public IReadOnlyList<uint> InteriorEntryCalls { get; init; } = [];
    public IReadOnlyList<int> EffectIds { get; init; } = [];
    /// <summary>Effect stub with no static inbound reference whose same effect also has a reachable stub.</summary>
    public bool IsDeadEffectStub { get; init; }
    /// <summary>Reachable stub entries of the same effects, used to prove the dead one is a duplicate.</summary>
    public IReadOnlyList<uint> ReachableAlternatives { get; init; } = [];
    /// <summary>Bytes decoded from the entry to its first return (capped at 0x2000).</summary>
    public int FunctionSize { get; init; }
    public int FunctionInstructionCount { get; init; }
    public bool EndsWithReturn { get; init; }
    public int DirectCallCount { get; init; }
    public int ExternalCallCount { get; init; }
    public int RegisterCallCount { get; init; }
    /// <summary>Calls to core entry, dual-channel wrapper or value path inside this function.</summary>
    public int EffectCoreCallCount { get; init; }
    public bool CallsEffectCore => EffectCoreCallCount > 0;
    public IReadOnlyList<uint> CalleeTargets { get; init; } = [];
    public string EntryBytes { get; init; } = string.Empty;
    public int PrecedingPaddingBytes { get; init; }
    /// <summary>No static inbound edge, no effect binding and no effect-core call: ordinary engine code.</summary>
    public bool IsNoStaticNonEffectRoot { get; init; }
    /// <summary>Shortest E8 distance to core/wrapper/value; 1 = direct, 2+ = through intermediates, -1 = none.</summary>
    public int EffectReachDepth { get; init; } = -1;
    public uint EffectReachViaEntry { get; init; }
    public uint EffectReachViaTarget { get; init; }
    public int EffectReachExpandedFunctions { get; init; }
    /// <summary>Shortest E8 path from this root to the first effect entry it reaches.</summary>
    public IReadOnlyList<uint> EffectReachPath { get; init; } = [];
    public bool ReachesEffectEntry => EffectReachDepth >= 0;
    public bool ReachesEffectIndirectly => EffectReachDepth >= 2;
    public IReadOnlyList<EngineEffectRootReference> DataReferences { get; init; } = [];
    public string Kind { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
}

public sealed class EngineEffectRootReference
{
    public string Section { get; init; } = string.Empty;
    public bool IsExecutable { get; init; }
    public long FileOffset { get; init; }
    public uint? Address { get; init; }
}

public sealed class EngineEffectPushArgument
{
    public uint Address { get; init; }
    public long FileOffset { get; init; }
    public int Length { get; init; }
    public byte OpCode { get; init; }
    public uint? Value { get; init; }
    public int? ResolvedValue { get; init; }
    /// <summary>Proven lower bound when the push is a guarded stack parameter plus a constant.</summary>
    public int? MinimumValue { get; init; }
    /// <summary>Proven upper bound when the push is a guarded stack parameter plus a constant.</summary>
    public int? MaximumValue { get; init; }
    /// <summary>Instructions that prove the bound, e.g. cmp/ja/add.</summary>
    public string RangeReason { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public EngineEffectArgumentSource SourceKind { get; init; }
    public IReadOnlyList<EngineEffectArgumentDefinition> DefinitionChain { get; init; } = [];
    public EngineEffectArgumentWritePlan WritePlan { get; init; } = new();
    public byte[] RawBytes { get; init; } = [];
}

public sealed class EngineEffectArgumentDefinition
{
    public uint Address { get; init; }
    public int Length { get; init; }
    public long FileOffset { get; init; }
    public byte[] RawBytes { get; init; } = [];
    public string Text { get; init; } = string.Empty;
    public EngineEffectArgumentSource Source { get; init; }
    public uint? Immediate { get; init; }
    public int? ImmediateOffset { get; init; }
    public int? ImmediateSize { get; init; }
}

public sealed class EngineEffectArgumentWritePlan
{
    public bool IsWritable { get; set; }
    public string? BlockReason { get; set; }
    public EngineEffectArgumentDefinition? Definition { get; init; }
    public long ImmediateFileOffset { get; init; }
    public int ImmediateLength { get; init; }
    public uint? OldValue { get; init; }
    public byte[] BeforeBytes { get; init; } = [];
    public bool IsSharedDefinition { get; set; }
}

public sealed class EngineEffectCallInstruction
{
    public uint Address { get; init; }
    public long FileOffset { get; init; }
    public uint Target { get; init; }
    public byte[] RawBytes { get; init; } = [];
}

public enum EngineEffectCallStatus
{
    Static,
    Dynamic,
    Unresolved
}

public enum EngineEffectCallPattern
{
    DirectStub,
    ComputedStub,
    WrapperPassThrough,
    WrapperDerived,
    TableDerived,
    LoopDerived,
    NestedDerived,
    EngineInternal,
    Unknown
}

public enum EngineEffectArgumentSource
{
    Immediate,
    Register,
    Memory,
    StackParameter,
    Computed
}
