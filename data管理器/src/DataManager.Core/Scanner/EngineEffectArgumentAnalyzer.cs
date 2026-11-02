﻿using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Recovers the four stack arguments and keeps the defining instruction chain.
/// Only a direct immediate push or a uniquely proven immediate definition is writable.
/// </summary>
internal static class EngineEffectArgumentAnalyzer
{
    public static EngineEffectPushArgument Analyze(EngineEffectBinaryImage image, DecodedInstruction push)
    {
        var instruction = push.Instruction;
        var kind = instruction.OpCount > 0 ? instruction.GetOpKind(0) : OpKind.Register;
        var immediate = EngineEffectDecoder.ImmediateOf(instruction);
        if (immediate is { } value && IsImmediate(kind))
            return Direct(image, push, value);
        if (kind == OpKind.Memory)
            return Memory(push, IsStackMemory(instruction), "参数来自内存，不能直接写回。");
        if (kind == OpKind.Register)
            return RegisterArgument(image, push, instruction.GetOpRegister(0));
        return Blocked(push, EngineEffectArgumentSource.Computed, "无法识别的 push 操作数。");
    }

    private static EngineEffectPushArgument Direct(EngineEffectBinaryImage image, DecodedInstruction push, uint value)
    {
        var offset = push.ImmediateOffset ?? 1;
        var size = push.ImmediateSize ?? (push.OpCode == 0x6A ? 1 : push.OpCode == 0x68 ? 4 : 0);
        var writable = size is 1 or 4 && offset >= 0 && offset + size <= push.RawBytes.Length;
        var before = writable ? push.RawBytes.AsSpan(offset, size).ToArray() : [];
        return new EngineEffectPushArgument
        {
            Address = push.Address,
            FileOffset = push.FileOffset,
            Length = push.Length,
            OpCode = push.OpCode,
            Value = value,
            ResolvedValue = (int)value,
            Source = EngineEffectDecoder.SourceOf(push.Instruction.GetOpKind(0)),
            SourceKind = EngineEffectArgumentSource.Immediate,
            DefinitionChain = [Definition(push, EngineEffectArgumentSource.Immediate, value)],
            WritePlan = writable
                ? new EngineEffectArgumentWritePlan
                {
                    IsWritable = true,
                    Definition = Definition(push, EngineEffectArgumentSource.Immediate, value),
                    ImmediateFileOffset = push.FileOffset + offset,
                    ImmediateLength = size,
                    OldValue = value,
                    BeforeBytes = before
                }
                : new EngineEffectArgumentWritePlan
                {
                    IsWritable = false,
                    BlockReason = "push 立即数位置无法确定。",
                    Definition = Definition(push, EngineEffectArgumentSource.Immediate, value)
                },
            RawBytes = push.RawBytes
        };
    }

    private static EngineEffectPushArgument RegisterArgument(EngineEffectBinaryImage image, DecodedInstruction push, Register register)
    {
        var full = FullRegister(register);
        if (full == Register.None)
            return Blocked(push, EngineEffectArgumentSource.Register, "push 寄存器无法映射到 32 位寄存器。");
        var context = EngineEffectDecoder.DecodeBasicBlockContext(image, push.Address);
        if (context is not { } blockContext || blockContext.Block.Count == 0)
            return Blocked(push, EngineEffectArgumentSource.Register, "无法反汇编 push 前的同一基本块。");
        var window = blockContext.Block;

        var pushIndex = window.Count;
        for (var index = pushIndex - 1; index >= 0; index--)
        {
            var decoded = window[index];
            var writeKind = ClassifyWrite(decoded.Instruction, full, out var partial, out var writeImmediate);
            if (writeKind == WriteKind.None) continue;
            if (HasInterveningRead(window, index, pushIndex, full))
                return Blocked(push, EngineEffectArgumentSource.Register,
                    "寄存器在定义与 push 之间被其他指令读取，重写定义会影响其它用途。", [decoded]);

            switch (writeKind)
            {
                case WriteKind.FullImmediate when writeImmediate is { } fullValue:
                    return Computed(push, [decoded], EngineEffectArgumentSource.Register,
                        (int)fullValue, decoded, "寄存器立即数定义。");
                case WriteKind.PartialImmediate when writeImmediate is { } partialValue:
                {
                    if (IsHighByteRegister(decoded.Instruction.GetOpRegister(0)))
                        return Blocked(push, EngineEffectArgumentSource.Computed,
                            "高位字节寄存器写入不能零扩展为 32 位立即数。");
                    var zeroingIndex = FindZeroing(window, index, full);
                    if (zeroingIndex < 0)
                        return Blocked(push, EngineEffectArgumentSource.Computed,
                            "8 位写入前未证明寄存器高位已清零。");
                    if (HasInterveningRead(window, zeroingIndex, index, full))
                        return Blocked(push, EngineEffectArgumentSource.Computed,
                            "清零与 8 位写入之间寄存器被读取。");
                    return Computed(push, [window[zeroingIndex], decoded],
                        EngineEffectArgumentSource.Computed, (int)partialValue, decoded,
                        "清零后 8 位立即数定义。");
                }
                case WriteKind.Zeroing:
                    return Blocked(push, EngineEffectArgumentSource.Computed,
                        "仅能解析为 0；等长补丁无法把清零改成非零 mov。",
                        [decoded]);
                case WriteKind.Memory:
                case WriteKind.StackMemory:
                {
                    var source = writeKind == WriteKind.StackMemory
                        ? EngineEffectArgumentSource.StackParameter
                        : EngineEffectArgumentSource.Memory;
                    var chain = new List<DecodedInstruction> { decoded };
                    return Memory(push, source == EngineEffectArgumentSource.StackParameter,
                        source == EngineEffectArgumentSource.StackParameter
                            ? "参数来自上层栈参数，不能直接写回。"
                            : "参数来自内存，不能直接写回。", chain);
                }
                case WriteKind.Lea:
                    return Blocked(push, EngineEffectArgumentSource.Computed,
                        "参数是 lea 计算出的地址，不能截断为固定编号。", [decoded]);
                case WriteKind.Arithmetic:
                {
                    var chain = ArithmeticChain(window, index, full);
                    var blocked = Blocked(push, EngineEffectArgumentSource.Computed,
                        "参数是算术计算链，未证明唯一常量来源。", chain);
                    return TryGuardedRange(window, index, chain, full, push.Address, blockContext.Prefix) is { } range
                        ? WithRange(blocked, range)
                        : blocked;
                }
                case WriteKind.Copy:
                    return Blocked(push, EngineEffectArgumentSource.Register,
                        "参数是寄存器复制，来源未证明为唯一立即数。", [decoded]);
                case WriteKind.Exchange:
                case WriteKind.Pop:
                case WriteKind.Other:
                    return Blocked(push, EngineEffectArgumentSource.Computed,
                        "参数来源不是可安全改写的立即数。", [decoded]);
            }
        }
        return Blocked(push, EngineEffectArgumentSource.Register, "push 前未找到寄存器定义。");
    }

    public static void ValidateBranchTargets(EngineEffectBinaryImage image, EngineEffectCallSite call)
    {
        var computed = call.Pushes.Where(push => push.WritePlan.IsWritable &&
                                                 push.WritePlan.Definition != null &&
                                                 push.WritePlan.Definition.Address != push.Address).ToArray();
        if (computed.Length == 0) return;
        if (call.FunctionEntry is not { } entry)
        {
            Block(computed, "找不到函数入口，不能证明分支目标唯一。");
            return;
        }
        var requested = checked((int)(call.CallAddress - entry) + 16);
        var length = requested is > 0 and <= 0x400 ? requested : 0x400;
        var decoded = EngineEffectDecoder.DecodeForward(image, entry, length);
        if (decoded.Count == 0 || decoded[^1].Address + (uint)decoded[^1].Length < call.CallAddress)
        {
            Block(computed, "函数反汇编不完整，不能证明分支目标唯一。");
            return;
        }
        var targets = decoded
            .Where(item => item.Instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            .Select(item => unchecked((uint)item.Instruction.NearBranchTarget))
            .ToArray();
        foreach (var argument in computed)
        {
            var definition = argument.WritePlan.Definition!;
            if (targets.Any(target => target > definition.Address && target <= argument.Address))
                Block(argument, "定义与 push 之间存在分支目标。");
        }
    }

    public static void MarkSharedDefinitions(IEnumerable<EngineEffectCallSite> calls)
        => MarkSharedArguments(calls.SelectMany(call =>
            call.Pushes.Select((argument, index) => (argument, index))));

    /// <summary>
    /// Read-only arguments still consume physical definitions. Their preserved source chain
    /// participates in the hazard index without giving them a value or a writable plan.
    /// In particular, the second of two register pushes may already be blocked because the
    /// first push reads that register: filtering it out would make the first push unsafe.
    /// </summary>
    internal static void MarkSharedArguments(IEnumerable<(EngineEffectPushArgument argument, int index)> arguments)
    {
        var uses = arguments.SelectMany(item => DefinitionLocations(item.argument)
                .Select(location => (item.argument, item.index, location)))
            .DistinctBy(item => (item.argument, item.index, item.location)).ToArray();
        foreach (var group in uses.GroupBy(item => item.location))
        {
            if (group.Count() <= 1) continue;
            var itemOnly = group.All(item => item.index == 2);
            foreach (var (argument, _, _) in group)
            {
                if (itemOnly) argument.WritePlan.IsSharedDefinition = true;
                else Block(argument, "定义跨不同参数共享，不能独立修改。", shared: true);
            }
        }
    }

    private static IEnumerable<(long Offset, int Width)> DefinitionLocations(EngineEffectPushArgument argument)
    {
        var plan = argument.WritePlan;
        if (plan.Definition != null && plan.ImmediateFileOffset > 0 && plan.ImmediateLength is 1 or 4)
            yield return (plan.ImmediateFileOffset, plan.ImmediateLength);
        foreach (var source in argument.DefinitionChain)
            if (source.ImmediateOffset is { } offset && source.ImmediateSize is 1 or 4 &&
                offset >= 0 && offset + source.ImmediateSize.Value <= source.RawBytes.Length && source.FileOffset + offset > 0)
                yield return (source.FileOffset + offset, source.ImmediateSize.Value);
    }

    private static void Block(IEnumerable<EngineEffectPushArgument> arguments, string reason)
    {
        foreach (var argument in arguments) Block(argument, reason);
    }

    private static void Block(EngineEffectPushArgument argument, string reason, bool shared = false)
    {
        argument.WritePlan.IsWritable = false;
        argument.WritePlan.BlockReason = reason;
        argument.WritePlan.IsSharedDefinition = shared;
    }

    private static EngineEffectPushArgument Computed(DecodedInstruction push,
        IReadOnlyList<DecodedInstruction> before, EngineEffectArgumentSource source, int value,
        DecodedInstruction definition, string reason)
    {
        var offset = definition.ImmediateOffset;
        var size = definition.ImmediateSize;
        var writable = offset is { } o && size is 1 or 4 && o >= 0 && o + size <= definition.RawBytes.Length;
        var beforeBytes = writable
            ? definition.RawBytes.AsSpan(offset!.Value, size!.Value).ToArray()
            : [];
        var chain = before.Concat([push]).ToArray();
        return new EngineEffectPushArgument
        {
            Address = push.Address,
            FileOffset = push.FileOffset,
            Length = push.Length,
            OpCode = push.OpCode,
            Value = null,
            ResolvedValue = value,
            Source = source switch
            {
                EngineEffectArgumentSource.Immediate => "imm",
                EngineEffectArgumentSource.StackParameter => "stack",
                EngineEffectArgumentSource.Memory => "mem",
                EngineEffectArgumentSource.Computed => "computed",
                _ => "reg"
            },
            SourceKind = source,
            DefinitionChain = chain.Select(item => Definition(item,
                item == definition ? EngineEffectArgumentSource.Immediate : EngineEffectArgumentSource.Computed,
                item == definition ? (uint?)value : null)).ToArray(),
            WritePlan = writable
                ? new EngineEffectArgumentWritePlan
                {
                    IsWritable = true,
                    Definition = Definition(definition, EngineEffectArgumentSource.Immediate, (uint)value),
                    ImmediateFileOffset = definition.FileOffset + offset!.Value,
                    ImmediateLength = size!.Value,
                    OldValue = (uint)value,
                    BeforeBytes = beforeBytes
                }
                : new EngineEffectArgumentWritePlan
                {
                    IsWritable = false,
                    BlockReason = "定义指令立即数位置或长度无法确认。",
                    Definition = Definition(definition, EngineEffectArgumentSource.Immediate, (uint)value)
                },
            RawBytes = push.RawBytes
        };
    }

    private static EngineEffectPushArgument Memory(DecodedInstruction push, bool stack, string reason,
        IReadOnlyList<DecodedInstruction>? before = null)
    {
        var chain = (before ?? []).Concat([push]).ToArray();
        return new EngineEffectPushArgument
        {
            Address = push.Address,
            FileOffset = push.FileOffset,
            Length = push.Length,
            OpCode = push.OpCode,
            Value = null,
            ResolvedValue = null,
            Source = stack ? "stack" : "mem",
            SourceKind = stack ? EngineEffectArgumentSource.StackParameter : EngineEffectArgumentSource.Memory,
            DefinitionChain = chain.Select(item => Definition(item,
                stack ? EngineEffectArgumentSource.StackParameter : EngineEffectArgumentSource.Memory, null)).ToArray(),
            WritePlan = new EngineEffectArgumentWritePlan { IsWritable = false, BlockReason = reason },
            RawBytes = push.RawBytes
        };
    }

    private static EngineEffectPushArgument Blocked(DecodedInstruction push, EngineEffectArgumentSource source,
        string reason, IReadOnlyList<DecodedInstruction>? before = null)
    {
        var chain = (before ?? []).Concat([push]).ToArray();
        return new EngineEffectPushArgument
        {
            Address = push.Address,
            FileOffset = push.FileOffset,
            Length = push.Length,
            OpCode = push.OpCode,
            Value = null,
            ResolvedValue = null,
            Source = source switch
            {
                EngineEffectArgumentSource.StackParameter => "stack",
                EngineEffectArgumentSource.Memory => "mem",
                EngineEffectArgumentSource.Computed => "computed",
                EngineEffectArgumentSource.Register => "reg",
                _ => "imm"
            },
            SourceKind = source,
            DefinitionChain = chain.Select(item => Definition(item, source, null)).ToArray(),
            WritePlan = new EngineEffectArgumentWritePlan { IsWritable = false, BlockReason = reason },
            RawBytes = push.RawBytes
        };
    }

    private static IReadOnlyList<DecodedInstruction> ArithmeticChain(IReadOnlyList<DecodedInstruction> window,
        int index, Register full)
    {
        var chain = new List<DecodedInstruction> { window[index] };
        for (var cursor = index - 1; cursor >= 0 && chain.Count < 4; cursor--)
        {
            var kind = ClassifyWrite(window[cursor].Instruction, full, out _, out _);
            if (kind == WriteKind.None) continue;
            chain.Add(window[cursor]);
            if (kind is WriteKind.FullImmediate or WriteKind.PartialImmediate or WriteKind.Zeroing
                or WriteKind.Memory or WriteKind.StackMemory or WriteKind.Lea)
                break;
        }
        chain.Reverse();
        return chain;
    }

    private readonly record struct GuardedRange(int Minimum, int Maximum, string Reason);

    /// <summary>
    /// Recognizes <c>mov reg,[ebp+X]</c> (or <c>movzx</c>), an upper-bound guard
    /// (<c>cmp reg,N</c> followed by <c>ja/jae/jg/jge</c> jumping past the call),
    /// and <c>add reg,constant</c>. The guard proves the stack parameter is in
    /// <c>[0,N]</c>, so the pushed id covers a contiguous effect range.
    /// </summary>
    private static GuardedRange? TryGuardedRange(IReadOnlyList<DecodedInstruction> window, int addIndex,
        IReadOnlyList<DecodedInstruction> chain, Register full, uint pushAddress,
        IReadOnlyList<DecodedInstruction> prefix)
    {
        if (chain.Count == 0) return null;
        var add = chain[^1].Instruction;
        if (add.Mnemonic != Mnemonic.Add || add.Op0Kind != OpKind.Register || add.GetOpRegister(0) != full)
            return null;
        if (add.Op1Kind is not (OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate32))
            return null;
        var constant = (int)add.GetImmediate(1);
        if (constant is < 0 or > 255) return null;
        var before = prefix.Concat(window.Take(addIndex)).ToArray();
        if (before.Length == 0) return null;
        var loadIndex = -1;
        DecodedInstruction? load = null;
        for (var index = before.Length - 1; index >= 0; index--)
        {
            // cmp/test only set flags; they never define the register.
            if (before[index].Instruction.Mnemonic is Mnemonic.Cmp or Mnemonic.Test) continue;
            var kind = ClassifyWrite(before[index].Instruction, full, out _, out _);
            if (kind == WriteKind.None) continue;
            if (kind is not (WriteKind.Memory or WriteKind.StackMemory))
            {
                return null;
            }
            loadIndex = index;
            load = before[index];
            break;
        }
        if (load is not { } loadDecoded || loadIndex < 0) return null;
        var loadInstruction = loadDecoded.Instruction;
        if (loadInstruction.Op0Kind != OpKind.Register || loadInstruction.GetOpRegister(0) != full) return null;
        if (loadInstruction.Op1Kind != OpKind.Memory ||
            loadInstruction.MemoryBase is not (Register.EBP or Register.RBP)) return null;
        // Only incoming stack parameters ([ebp+8] and above); locals are not
        // supplied by the caller and cannot bound an effect id this way.
        if (loadInstruction.MemoryDisplacement64 < 8) return null;
        for (var index = loadIndex + 1; index < before.Length; index++)
        {
            var compare = before[index].Instruction;
            if (compare.Mnemonic != Mnemonic.Cmp || compare.OpCount < 2) continue;
            uint bound;
            switch (compare.GetOpKind(1))
            {
                case OpKind.Immediate8:
                case OpKind.Immediate8to32:
                case OpKind.Immediate32:
                    bound = (uint)compare.GetImmediate(1);
                    break;
                default:
                    continue;
            }
            var comparesFull = compare.Op0Kind == OpKind.Register && compare.GetOpRegister(0) == full;
            var comparesLowByte = loadInstruction.Mnemonic == Mnemonic.Movzx &&
                                  compare.Op0Kind == OpKind.Register &&
                                  FullRegister(compare.GetOpRegister(0)) == full;
            if (!comparesFull && !comparesLowByte) continue;
            if (index + 1 >= before.Length) continue;
            var branch = before[index + 1].Instruction;
            if (branch.FlowControl != FlowControl.ConditionalBranch) continue;
            if (branch.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
                continue;
            var target = unchecked((uint)branch.NearBranchTarget);
            if (target <= pushAddress) continue;
            int? upper = branch.Mnemonic switch
            {
                Mnemonic.Ja or Mnemonic.Jg => (int)bound,
                Mnemonic.Jae or Mnemonic.Jge => (int)bound - 1,
                _ => null
            };
            if (upper is not { } maximum || maximum < 0) continue;
            var reason = $"{before[index].Text}；{before[index + 1].Text} 跳出后续调用（0x{target:X8}）" +
                         $"⇒ 参数 ∈ [0,{maximum}]；{chain[^1].Text} ⇒ 编号 ∈ [{constant},{constant + maximum}]";
            return new GuardedRange(constant, constant + maximum, reason);
        }
        return null;
    }

    private static EngineEffectPushArgument WithRange(EngineEffectPushArgument argument, GuardedRange range)
        => new()
        {
            Address = argument.Address,
            FileOffset = argument.FileOffset,
            Length = argument.Length,
            OpCode = argument.OpCode,
            Value = argument.Value,
            ResolvedValue = argument.ResolvedValue,
            MinimumValue = range.Minimum,
            MaximumValue = range.Maximum,
            RangeReason = range.Reason,
            Source = argument.Source,
            SourceKind = argument.SourceKind,
            DefinitionChain = argument.DefinitionChain,
            WritePlan = argument.WritePlan,
            RawBytes = argument.RawBytes
        };

    private static int FindZeroing(IReadOnlyList<DecodedInstruction> window, int writeIndex, Register full)
    {
        for (var index = writeIndex - 1; index >= 0; index--)
        {
            var kind = ClassifyWrite(window[index].Instruction, full, out _, out _);
            if (kind == WriteKind.Zeroing) return index;
            if (kind != WriteKind.None) return -1;
            if (ReadsRegister(window[index].Instruction, full)) return -1;
        }
        return -1;
    }

    private static bool HasInterveningRead(IReadOnlyList<DecodedInstruction> window, int definitionIndex,
        int pushIndex, Register full)
    {
        for (var index = definitionIndex + 1; index < pushIndex; index++)
            if (ReadsRegister(window[index].Instruction, full)) return true;
        return false;
    }

    private static WriteKind ClassifyWrite(Instruction instruction, Register full, out bool partial, out uint? immediate)
    {
        partial = false;
        immediate = null;
        // A previous push consumes the register; it does not redefine it. Keep
        // walking to its real definition so HasInterveningRead can block this
        // use and preserve the source shared with an earlier argument.
        if (instruction.Mnemonic == Mnemonic.Push) return WriteKind.None;
        if (instruction.OpCount == 0 || instruction.GetOpKind(0) != OpKind.Register) return WriteKind.None;
        var destination = instruction.GetOpRegister(0);
        if (FullRegister(destination) != full) return WriteKind.None;
        // Iced reports register widths in bytes, so a complete x86 argument is 4.
        partial = RegisterExtensions.GetSize(destination) < 4;
        var mnemonic = instruction.Mnemonic;
        if (mnemonic == Mnemonic.Mov)
        {
            if (instruction.OpCount >= 2 && IsImmediate(instruction.GetOpKind(1)))
            {
                immediate = unchecked((uint)instruction.GetImmediate(1));
                return partial ? WriteKind.PartialImmediate : WriteKind.FullImmediate;
            }
            if (instruction.OpCount >= 2 && instruction.GetOpKind(1) == OpKind.Memory)
                return IsStackMemory(instruction) ? WriteKind.StackMemory : WriteKind.Memory;
            if (instruction.OpCount >= 2 && instruction.GetOpKind(1) == OpKind.Register)
                return WriteKind.Copy;
            return WriteKind.Other;
        }
        if (mnemonic is Mnemonic.Movzx or Mnemonic.Movsx)
        {
            if (instruction.OpCount >= 2 && instruction.GetOpKind(1) == OpKind.Memory)
                return IsStackMemory(instruction) ? WriteKind.StackMemory : WriteKind.Memory;
            return WriteKind.Copy;
        }
        if (mnemonic == Mnemonic.Lea) return WriteKind.Lea;
        if (IsZeroing(instruction, full)) return WriteKind.Zeroing;
        if (mnemonic is Mnemonic.Add or Mnemonic.Sub or Mnemonic.And or Mnemonic.Or or Mnemonic.Shl
            or Mnemonic.Shr or Mnemonic.Imul or Mnemonic.Neg or Mnemonic.Inc or Mnemonic.Dec)
        {
            if (instruction.OpCount >= 2 && IsImmediate(instruction.GetOpKind(1)))
                immediate = unchecked((uint)instruction.GetImmediate(1));
            else if (instruction.OpCount >= 3 && IsImmediate(instruction.GetOpKind(2)))
                immediate = unchecked((uint)instruction.GetImmediate(2));
            return WriteKind.Arithmetic;
        }
        if (mnemonic == Mnemonic.Pop) return WriteKind.Pop;
        if (mnemonic == Mnemonic.Xchg) return WriteKind.Exchange;
        return WriteKind.Other;
    }

    private static bool ReadsRegister(Instruction instruction, Register full)
    {
        if (IsZeroing(instruction, full)) return false;
        for (var index = 0; index < instruction.OpCount; index++)
        {
            var kind = instruction.GetOpKind(index);
            if (kind == OpKind.Register)
            {
                if (FullRegister(instruction.GetOpRegister(index)) != full) continue;
                if (index == 0 && IsPureDestination(instruction)) continue;
                return true;
            }
            if (kind == OpKind.Memory &&
                (FullRegister(instruction.MemoryBase) == full || FullRegister(instruction.MemoryIndex) == full))
                return true;
        }
        return false;
    }

    private static bool IsZeroing(Instruction instruction, Register full)
        => instruction.Mnemonic is Mnemonic.Xor or Mnemonic.Sub && instruction.OpCount >= 2 &&
           instruction.GetOpKind(0) == OpKind.Register && instruction.GetOpKind(1) == OpKind.Register &&
           RegisterExtensions.GetSize(instruction.GetOpRegister(0)) == 4 &&
           RegisterExtensions.GetSize(instruction.GetOpRegister(1)) == 4 &&
           FullRegister(instruction.GetOpRegister(0)) == full &&
           FullRegister(instruction.GetOpRegister(1)) == full;

    private static bool IsPureDestination(Instruction instruction)
        => instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Lea or Mnemonic.Pop;

    private static bool IsStackMemory(Instruction instruction)
        => instruction.MemoryBase is Register.EBP or Register.RBP or Register.ESP or Register.RSP;

    private static bool IsImmediate(OpKind kind)
        => kind is OpKind.Immediate8 or OpKind.Immediate8_2nd or OpKind.Immediate16 or OpKind.Immediate32
            or OpKind.Immediate64 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
            or OpKind.Immediate32to64;

    private static bool IsHighByteRegister(Register register)
        => register is Register.AH or Register.BH or Register.CH or Register.DH;

    private static Register FullRegister(Register register)
        => register == Register.None ? Register.None : RegisterExtensions.GetFullRegister32(register);

    private static EngineEffectArgumentDefinition Definition(DecodedInstruction instruction,
        EngineEffectArgumentSource source, uint? immediate)
        => new()
        {
            Address = instruction.Address,
            Length = instruction.Length,
            FileOffset = instruction.FileOffset,
            RawBytes = instruction.RawBytes,
            Text = instruction.Text,
            Source = source,
            Immediate = immediate,
            ImmediateOffset = instruction.ImmediateOffset,
            ImmediateSize = instruction.ImmediateSize
        };

    private enum WriteKind
    {
        None,
        FullImmediate,
        PartialImmediate,
        Zeroing,
        Memory,
        StackMemory,
        Copy,
        Lea,
        Arithmetic,
        Pop,
        Exchange,
        Other
    }
}
