using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>Function/stub classification and compact disassembly evidence.</summary>
internal static class EngineEffectFunctionAnalyzer
{
    private const int FunctionLookbackBytes = 0x2000;
    private const int EngineRegionBytes = 0x100;
    private const int StubHeadBytes = 64;
    private const int EvidenceInstructions = 12;
    private const int MaximumSwitchEntries = 256;
    private const int PaddingGapBytes = 8;
    private const int MaximumCaveDistance = 0x200;
    private const int MaximumCaveCandidates = 16;

    public static uint? FindFunctionEntry(EngineEffectBinaryImage image, uint callAddress)
        => image.Context.GetOrBuild(EngineEffectAnalysisKind.FunctionEntry, callAddress, FunctionLookbackBytes,
            () => FindFunctionEntryCore(image, callAddress));

    private static uint? FindFunctionEntryCore(EngineEffectBinaryImage image, uint callAddress)
    {
        var section = image.SectionOf(callAddress);
        if (section == null) return null;
        var callOffset = callAddress - section.VirtualAddress;
        var maximum = Math.Min(FunctionLookbackBytes, checked((int)callOffset));
        var bytes = image.Bytes;
        for (var distance = 0; distance <= maximum - 3; distance++)
        {
            var address = callAddress - (uint)distance;
            var fileOffset = section.RawPointer + (address - section.VirtualAddress);
            if (fileOffset < 0 || fileOffset + 3 > bytes.LongLength) continue;
            var index = checked((int)fileOffset);
            var window = bytes.AsSpan(index, Math.Min(7, bytes.Length - index));
            if (EngineEffectInstructionMap.IsPrologue(window, 0)) return address;
        }
        return null;
    }

    /// <summary>
    /// Consecutive NOP/INT3 bytes separating <paramref name="entry"/> from
    /// <paramref name="callAddress"/>. Compilers pad the space between functions,
    /// so such a run means the prologue heuristic may have crossed into another
    /// function when walking backwards.
    /// </summary>
    public static bool HasPaddingGap(EngineEffectBinaryImage image, uint entry, uint callAddress)
    {
        if (callAddress <= entry) return false;
        if (image.VirtualAddressToFileOffset(entry) is not { } from ||
            image.VirtualAddressToFileOffset(callAddress) is not { } to || to <= from) return false;
        var bytes = image.Bytes;
        var run = 0;
        for (var index = checked((int)from); index < to; index++)
        {
            run = bytes[index] is 0x90 or 0xCC ? run + 1 : 0;
            if (run >= PaddingGapBytes) return true;
        }
        return false;
    }

    /// <summary>
    /// Owner of a call site. The prologue heuristic looks back up to 0x2000 bytes
    /// and can cross padding into the previous function; when the control-flow
    /// walk from that candidate cannot reach the call, the site belongs to the
    /// block an inbound jump actually enters (an injected code cave), or to no
    /// identifiable function at all.
    /// </summary>
    public static uint? ResolveFunctionEntry(EngineEffectBinaryImage image, EngineEffectJumpIndex? jumps,
        uint callAddress, IDictionary<uint, HashSet<uint>?>? reachableCache = null)
    {
        var candidate = FindFunctionEntry(image, callAddress);
        if (candidate is not { } entry) return null;
        if (!HasPaddingGap(image, entry, callAddress)) return entry;
        HashSet<uint>? reached;
        if (reachableCache == null || !reachableCache.TryGetValue(entry, out reached))
        {
            reached = ReachableAddresses(image, entry);
            if (reachableCache != null) reachableCache[entry] = reached;
        }
        if (reached == null || reached.Contains(callAddress)) return entry;
        if (jumps == null) return null;
        var candidates = jumps.SourcesNear(callAddress, MaximumCaveDistance)
            .OrderBy(jump => jump.Target == callAddress ? 0 : LooksLikeCaveStart(image, jump.Target) ? 1 : 2)
            .ThenByDescending(jump => jump.Target)
            .Take(MaximumCaveCandidates);
        foreach (var jump in candidates)
        {
            if (jump.Target == callAddress) return callAddress;
            if (IsReachable(image, jump.Target, callAddress) == true) return jump.Target;
        }
        return null;
    }

    public static uint? InternalOwner(uint? functionEntry)
    {
        if (functionEntry is not { } entry) return null;
        foreach (var target in EngineEffectScanService.KnownEntryTargets)
        {
            if (entry >= target && entry < target + EngineRegionBytes) return target;
        }
        return null;
    }

    public static string InternalReason(uint owner, uint? functionEntry)
        => functionEntry is { } entry
            ? $"位于{EngineEffectScanService.EntryNames[owner]}函数体（入口 0x{owner:X8}，函数起点 +0x{entry - owner:X}）"
            : $"位于{EngineEffectScanService.EntryNames[owner]}函数体";

    public static IReadOnlyList<EngineEffectPushArgument> StubPushes(EngineEffectBinaryImage image, uint? functionEntry)
    {
        if (functionEntry is not { } entry) return [];
        var decoded = EngineEffectDecoder.DecodeForward(image, entry, StubHeadBytes, stopAtCall: true);
        return decoded.Where(item => item.IsPush).Select(MakeStubPush).ToArray();
    }

    /// <summary>
    /// Recovers the four argument pushes of a call that a simple backtrack cannot
    /// cover, e.g. when a wrapper forwards stack parameters after its prologue.
    /// </summary>
    public static IReadOnlyList<EngineEffectPushArgument> FunctionPushArguments(EngineEffectBinaryImage image,
        uint? functionEntry, uint callAddress, int maximumBytes = 0x800)
    {
        if (functionEntry is not { } entry || entry >= callAddress) return [];
        var length = Math.Min(maximumBytes, checked((int)(callAddress - entry) + 1));
        var decoded = EngineEffectDecoder.DecodeForward(image, entry, length);
        if (decoded.Count == 0) return [];
        var pushes = new List<DecodedInstruction>();
        foreach (var item in decoded)
        {
            if (item.Address >= callAddress) break;
            if (item.Instruction.Mnemonic == Mnemonic.Ret) return [];
            if (item.IsPush) pushes.Add(item);
        }
        var last = pushes.TakeLast(4).ToArray();
        if (last.Length < 4 || !last.All(EngineEffectDecoder.IsArgumentPush)) return [];
        return last.Select(item => EngineEffectArgumentAnalyzer.Analyze(image, item)).ToArray();
    }

    /// <summary>Calls made inside a stub function, stopping at its first return.</summary>
    public static IReadOnlyList<EngineEffectFunctionCall> Callees(EngineEffectBinaryImage image, uint? functionEntry,
        int maximumBytes = 0x400)
    {
        if (functionEntry is not { } entry) return [];
        var decoded = EngineEffectDecoder.DecodeForward(image, entry, maximumBytes);
        var result = new List<EngineEffectFunctionCall>();
        foreach (var item in decoded)
        {
            var instruction = item.Instruction;
            if (instruction.Mnemonic == Mnemonic.Ret) break;
            if (instruction.FlowControl != FlowControl.Call) continue;
            if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
                continue;
            var target = unchecked((uint)instruction.NearBranchTarget);
            if (target == 0) continue;
            result.Add(new EngineEffectFunctionCall
            {
                Address = item.Address,
                Target = target,
                TargetName = EngineEffectScanService.EntryNames.TryGetValue(target, out var name) ? name : string.Empty
            });
            if (result.Count >= 16) break;
        }
        return result;
    }

    /// <summary>
    /// Walks the function body from its entry along fall-through and direct branch
    /// targets and reports whether the given call address can be reached.  Returns
    /// null when no prologue was found and reachability is therefore unknown.
    /// </summary>
    public static bool? IsReachable(EngineEffectBinaryImage image, uint? functionEntry, uint callAddress,
        int instructionBudget = 60000)
    {
        if (functionEntry is not { } entry) return null;
        if (entry == callAddress) return true;
        if (entry > callAddress) return false;
        var reached = ReachableAddresses(image, entry, instructionBudget);
        return reached?.Contains(callAddress) ?? false;
    }

    /// <summary>
    /// Instruction addresses reachable from a function entry along fall-through,
    /// conditional branches and direct jumps, stopping at returns, indirect
    /// branches and direct jumps out of the walked block. Bytes behind a ret are
    /// therefore only reached when a branch or jump targets them, which is what
    /// separates real call sites from stray E8 bytes inside code sections.
    /// </summary>
    public static HashSet<uint>? ReachableAddresses(EngineEffectBinaryImage image, uint entry,
        int instructionBudget = 60000)
        => image.Context.GetOrBuild(EngineEffectAnalysisKind.Reachable, entry, instructionBudget,
            () => ReachableAddressesCore(image, entry, instructionBudget));

    private static HashSet<uint>? ReachableAddressesCore(EngineEffectBinaryImage image, uint entry,
        int instructionBudget)
    {
        if (image.SectionOf(entry) == null) return null;
        var reached = new HashSet<uint>();
        var pending = new Stack<uint>();
        pending.Push(entry);
        var budget = instructionBudget;
        while (pending.Count > 0 && budget > 0)
        {
            var address = pending.Pop();
            while (budget > 0)
            {
                if (image.SectionOf(address) == null || !reached.Add(address)) break;
                var item = EngineEffectDecoder.DecodeOne(image, address);
                if (item == null) break;
                budget--;
                var next = item.Address + (uint)item.Length;
                var instruction = item.Instruction;
                var stop = false;
                switch (instruction.FlowControl)
                {
                    case FlowControl.ConditionalBranch:
                        EnqueueTarget(pending, image, instruction);
                        address = next;
                        break;
                    case FlowControl.UnconditionalBranch:
                        EnqueueTarget(pending, image, instruction);
                        stop = true;
                        break;
                    case FlowControl.IndirectBranch:
                        EnqueueSwitchTargets(pending, image, instruction);
                        stop = true;
                        break;
                    case FlowControl.Return:
                    case FlowControl.Interrupt:
                    case FlowControl.Exception:
                        stop = true;
                        break;
                    default:
                        address = next;
                        break;
                }
                if (stop) break;
            }
        }
        return reached;
    }

    private static void EnqueueTarget(Stack<uint> pending, EngineEffectBinaryImage image, Instruction instruction)
    {
        if (BranchTarget(instruction) is not { } target || target == 0) return;
        if (image.SectionOf(target) == null) return;
        pending.Push(target);
    }

    /// <summary>
    /// Switch dispatch compiles to <c>jmp [index*4+table]</c> with the case labels
    /// stored in a table right after the dispatch. The jump target is data, so the
    /// walk cannot follow it statically; reading the table restores the edges and
    /// keeps case blocks reachable without accepting bytes behind a ret.
    /// </summary>
    private static void EnqueueSwitchTargets(Stack<uint> pending, EngineEffectBinaryImage image,
        Instruction instruction)
    {
        if (instruction.Op0Kind != OpKind.Memory) return;
        if (instruction.MemoryBase != Register.None) return;
        if (instruction.MemoryIndex == Register.None) return;
        var table = unchecked((uint)instruction.MemoryDisplacement64);
        if (table == 0) return;
        var section = image.SectionOf(table);
        if (section == null || !section.IsExecutable) return;
        var offset = image.VirtualAddressToFileOffset(table);
        if (offset is not { } fileOffset) return;
        for (var index = 0; index < MaximumSwitchEntries; index++)
        {
            var entryOffset = fileOffset + index * 4;
            if (entryOffset < 0 || entryOffset + 4 > image.Bytes.LongLength) break;
            var target = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                image.Bytes.AsSpan(checked((int)entryOffset), 4));
            var targetSection = image.SectionOf(target);
            if (target == 0 || targetSection == null || !targetSection.IsExecutable) break;
            pending.Push(target);
        }
    }


    /// <summary>
    /// Reachability including inbound jumps.  A code cave appended after a
    /// function's final return has no intra-function path, but is live when an
    /// injected jump enters it; the jump index supplies that edge.
    /// </summary>
    public static (bool? Reachable, uint? JumpSource, uint? CaveEntry) ResolveReachability(
        EngineEffectBinaryImage image, uint? functionEntry, uint callAddress, EngineEffectJumpIndex? jumps,
        int maximumCaveDistance = 0x200, uint? prologueEntry = null)
    {
        var reachable = IsReachable(image, functionEntry, callAddress);
        if (reachable == true && functionEntry is { } attributed && prologueEntry is { } prologue &&
            attributed != prologue)
        {
            var inbound = jumps?.SourcesOf(attributed).OrderBy(jump => jump.Address).FirstOrDefault();
            if (inbound != null) return (true, inbound.Address, attributed);
        }
        if (reachable != false || functionEntry == null || jumps == null) return (reachable, null, null);
        var candidates = jumps.SourcesNear(callAddress, maximumCaveDistance)
            .OrderBy(jump => LooksLikeCaveStart(image, jump.Target) ? 0 : 1)
            .ThenByDescending(jump => jump.Target)
            .ToArray();
        foreach (var jump in candidates)
        {
            if (jump.Target == callAddress ||
                IsReachable(image, jump.Target, callAddress) == true)
                return (true, jump.Address, jump.Target);
        }
        return (false, null, null);
    }

    /// <summary>Linear decode from the entry; reports whether a return precedes the call.</summary>
    public static bool IsAfterReturn(EngineEffectBinaryImage image, uint? functionEntry, uint callAddress,
        int maximumBytes = 0x2000)
    {
        if (functionEntry is not { } entry || entry >= callAddress) return false;
        var length = Math.Min(maximumBytes, checked((int)(callAddress - entry) + 1));
        foreach (var item in EngineEffectDecoder.DecodeForward(image, entry, length))
        {
            if (item.Address >= callAddress) break;
            if (item.Instruction.Mnemonic == Mnemonic.Ret) return true;
        }
        return false;
    }

    public static string DescribeReachability(bool? reachable, bool afterReturn, int callerCount,
        uint? functionEntry, uint? jumpSource = null, uint? caveEntry = null)
    {
        if (reachable == null) return "可达性未知（未识别到函数序言）";
        if (reachable == false)
        {
            return afterReturn
                ? "不可达：位于函数返回之后的代码碎片（无分支目标进入，运行时不会执行）"
                : "不可达：函数入口无法沿顺序/分支到达该调用点";
        }
        if (caveEntry is { } cave)
        {
            var where = afterReturn ? "位于函数返回之后的代码碎片" : "位于函数体内";
            return $"可达：{where}，由 0x{jumpSource:X8} 跳转进入代码洞 0x{cave:X8} 后执行";
        }
        if (afterReturn)
            return "可达：位于函数返回之后，但存在函数内分支/跳转目标进入（代码碎片）";
        if (callerCount == 0)
            return functionEntry is { } entry
                ? $"可达：函数 0x{entry:X8} 内可执行，但函数入口无直接 E8 调用者（可能由跳转表/数据字段分派）"
                : "可达：函数内可执行，但未发现函数入口调用者";
        return $"可达：函数入口有 {callerCount} 处 E8 调用";
    }

    /// <summary>
    /// Injected blocks normally start after NOP/INT3 padding or a return/jump.
    /// Used only to prefer the most plausible cave entry when several byte
    /// patterns decode as jumps.
    /// </summary>
    private static bool LooksLikeCaveStart(EngineEffectBinaryImage image, uint target)
    {
        var offset = image.VirtualAddressToFileOffset(target);
        if (offset is not { } fileOffset || fileOffset <= 0) return false;
        var lookback = checked((int)Math.Min(6, fileOffset));
        var bytes = image.ReadBytes(fileOffset - lookback, lookback);
        if (bytes == null || bytes.Length == 0) return false;
        var last = bytes[^1];
        if (last is 0x90 or 0xCC or 0xC3 or 0xCB or 0x00) return true;
        if (bytes.Length >= 3 && bytes[^3] == 0xC2) return true;
        if (bytes.Length >= 5 && bytes[^5] == 0xE9) return true;
        if (bytes.Length >= 2 && bytes[^2] == 0xEB) return true;
        return false;
    }

    private static uint? BranchTarget(Instruction instruction)
    {
        if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            return unchecked((uint)instruction.NearBranchTarget);
        return null;
    }

    public static EngineEffectCallPattern Pattern(bool internalCall, IReadOnlyList<EngineEffectPushArgument> pushes)
    {
        if (internalCall) return EngineEffectCallPattern.EngineInternal;
        if (pushes.Any(push => push.SourceKind == EngineEffectArgumentSource.StackParameter))
            return EngineEffectCallPattern.WrapperPassThrough;
        if (pushes.Count >= 4 && pushes.Take(4).All(push => push.SourceKind == EngineEffectArgumentSource.Immediate))
            return EngineEffectCallPattern.DirectStub;
        if (pushes.Any(push => push.SourceKind != EngineEffectArgumentSource.Immediate ||
                               push.DefinitionChain.Count > 1))
            return EngineEffectCallPattern.ComputedStub;
        return EngineEffectCallPattern.Unknown;
    }

    public static string BuildEvidence(EngineEffectBinaryImage image, uint callAddress, uint target,
        uint? functionEntry, IReadOnlyList<EngineEffectPushArgument> pushes, IReadOnlyList<EngineEffectPushArgument> stubPushes)
    {
        var window = EngineEffectDecoder.DecodeBacktrack(image, callAddress, 96) ?? [];
        var lines = window.TakeLast(EvidenceInstructions)
            .Select(item => $"{item.Address:X8}  {EngineEffectDecoder.Hex(item.RawBytes),-26} {item.Text}")
            .ToList();
        var callOffset = image.VirtualAddressToFileOffset(callAddress);
        var callRaw = callOffset is { } offset ? image.ReadBytes(offset, 5) : null;
        lines.Add($"{callAddress:X8}  {(callRaw == null ? string.Empty : EngineEffectDecoder.Hex(callRaw)),-26} call 0x{target:X8}");
        if (functionEntry is { } entry)
            lines.Insert(0, $"函数入口 0x{entry:X8}（调用点 +0x{callAddress - entry:X}）；桩内 push：" +
                            (stubPushes.Count == 0 ? "—" : string.Join(" ", stubPushes.Select(FormatStubPush))));
        lines.Add("参数定义链：");
        var names = new[] { "效果值模式", "叠加方式", "装备特效号", "个人/兵种特效号" };
        for (var index = 0; index < 4; index++)
        {
            var push = index < pushes.Count ? pushes[index] : null;
            lines.Add($"  {names[index]}：{DescribeArgument(push)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string DescribeArgument(EngineEffectPushArgument? push)
    {
        if (push == null) return "缺失";
        var value = push.Value ?? (push.ResolvedValue is { } resolved ? (uint)resolved : null);
        var head = value is { } item ? $"0x{item:X}" : "动态";
        var chain = push.DefinitionChain.Count == 0
            ? push.RawBytes.Length == 0 ? "—" : $"{push.Address:X8} {EngineEffectDecoder.Hex(push.RawBytes)}"
            : string.Join("；", push.DefinitionChain.Select(item => $"{item.Text} @0x{item.Address:X8}"));
        var plan = push.WritePlan.IsWritable
            ? $"可写（定义 0x{push.WritePlan.Definition?.Address:X8}）"
            : $"只读（{push.WritePlan.BlockReason ?? "来源未证明"}）";
        return $"{head}；{chain}；{plan}";
    }

    private static string FormatStubPush(EngineEffectPushArgument push)
        => push.Value is { } value ? $"0x{value:X}"
            : push.SourceKind == EngineEffectArgumentSource.StackParameter ? "[栈]"
            : push.SourceKind == EngineEffectArgumentSource.Memory ? "[内存]"
            : "[寄存器]";

    private static EngineEffectPushArgument MakeStubPush(DecodedInstruction decoded)
    {
        var instruction = decoded.Instruction;
        var kind = instruction.OpCount > 0 ? instruction.GetOpKind(0) : OpKind.Register;
        var immediate = EngineEffectDecoder.ImmediateOf(instruction);
        var source = immediate != null
            ? EngineEffectArgumentSource.Immediate
            : kind == OpKind.Memory
                ? IsStack(instruction) ? EngineEffectArgumentSource.StackParameter : EngineEffectArgumentSource.Memory
                : EngineEffectArgumentSource.Register;
        return new EngineEffectPushArgument
        {
            Address = decoded.Address,
            FileOffset = decoded.FileOffset,
            Length = decoded.Length,
            OpCode = decoded.RawBytes.Length > 0 ? decoded.RawBytes[0] : (byte)0,
            Value = immediate,
            ResolvedValue = immediate is { } value ? (int)value : null,
            Source = source switch
            {
                EngineEffectArgumentSource.Immediate => EngineEffectDecoder.SourceOf(kind),
                EngineEffectArgumentSource.StackParameter => "stack",
                EngineEffectArgumentSource.Memory => "mem",
                _ => "reg"
            },
            SourceKind = source,
            DefinitionChain =
            [
                new EngineEffectArgumentDefinition
                {
                    Address = decoded.Address,
                    Length = decoded.Length,
                    FileOffset = decoded.FileOffset,
                    RawBytes = decoded.RawBytes,
                    Text = decoded.Text,
                    Source = source,
                    Immediate = immediate,
                    ImmediateOffset = decoded.ImmediateOffset,
                    ImmediateSize = decoded.ImmediateSize
                }
            ],
            WritePlan = new EngineEffectArgumentWritePlan
            {
                IsWritable = false,
                BlockReason = "桩头证据，不作为写回目标。"
            },
            RawBytes = decoded.RawBytes
        };
    }

    private static bool IsStack(Instruction instruction)
        => instruction.MemoryBase is Register.EBP or Register.RBP or Register.ESP or Register.RSP;
}
