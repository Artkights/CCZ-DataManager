﻿using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Expands a wrapper call whose effect id is "local base + loop counter".
/// Example (6.5/6.6 0x00448FF8):
///   push 0xA5            ; local [ebp-8] = base
///   xor eax,eax; mov [ebp-0xC],eax
///   inc [ebp-0xC]; cmp [ebp-0xC],5; je exit
///   mov edx,[ebp-0xC]; add edx,[ebp-8]; push edx
///   call 0x00449034
/// </summary>
internal static class EngineEffectLoopAnalyzer
{
    private const int MaximumFunctionBytes = 0x400;
    private const int MaximumLoopCount = 32;

    public static IReadOnlyList<EngineEffectCallSite> Analyze(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph, EngineEffectJumpIndex jumpIndex,
        IReadOnlyList<EngineEffectCallSite> derivedCalls)
    {
        var result = new List<EngineEffectCallSite>();
        result.AddRange(AnalyzeNested(image, callGraph, jumpIndex, derivedCalls));
        foreach (var call in derivedCalls)
        {
            if (call.Pattern != EngineEffectCallPattern.WrapperDerived ||
                call.Status == EngineEffectCallStatus.Static ||
                call.PersonalPush.SourceKind != EngineEffectArgumentSource.Computed) continue;
            if (call.FunctionEntry is not { } functionEntry) continue;
            var offsets = PersonalOffsets(image, call.PersonalPush);
            if (offsets is not { } pair) continue;
            var decoded = Decode(image, functionEntry, call.CallAddress);
            if (decoded.Count == 0) continue;
            var baseValue = BaseLocalValue(image, decoded, pair.BaseOffset);
            if (baseValue is not { } baseOffset) continue;
            if (!CounterStartsAtZero(decoded, pair.CounterOffset)) continue;
            var limit = LoopLimit(decoded, pair.CounterOffset);
            if (limit is not { } count || count <= 0 || count > MaximumLoopCount) continue;
            for (var index = 0; index < count; index++)
            {
                var personal = baseOffset + index;
                if (personal > byte.MaxValue) break;
                var personalPush = new EngineEffectPushArgument
                {
                    Address = call.PersonalPush.Address,
                    FileOffset = call.PersonalPush.FileOffset,
                    Length = call.PersonalPush.Length,
                    OpCode = call.PersonalPush.OpCode,
                    Value = (uint)personal,
                    ResolvedValue = personal,
                    Source = "loop",
                    SourceKind = EngineEffectArgumentSource.Computed,
                    DefinitionChain =
                    [
                        new EngineEffectArgumentDefinition
                        {
                            Address = call.PersonalPush.Address,
                            Length = call.PersonalPush.Length,
                            FileOffset = call.PersonalPush.FileOffset,
                            RawBytes = call.PersonalPush.RawBytes,
                            Text = $"循环 {index}/{count}：0x{baseOffset:X2} + {index} = 0x{personal:X2}",
                            Source = EngineEffectArgumentSource.Computed,
                            Immediate = (uint)personal
                        }
                    ],
                    WritePlan = new EngineEffectArgumentWritePlan
                    {
                        IsWritable = false,
                        BlockReason = "循环展开的个人号由局部基址与计数器计算，不能按单点写回。"
                    },
                    RawBytes = call.PersonalPush.RawBytes
                };
                var expression = $"局部基址 [ebp-0x{pair.BaseOffset:X}] + 计数 [ebp-0x{pair.CounterOffset:X}] " +
                                 $"= 0x{baseOffset:X2} + {index} = 0x{personal:X2}；循环上限 {count}";
                var pushes = new[] { call.ValuePush, call.StackingPush, call.ItemPush, personalPush };
                result.Add(new EngineEffectCallSite
                {
                    CallAddress = call.CallAddress,
                    FunctionEntry = call.FunctionEntry,
                    FunctionEntryDistance = call.FunctionEntryDistance,
                    IsReachable = call.IsReachable,
                    Reachability = call.Reachability,
                    ReachJumpAddress = call.ReachJumpAddress,
                    ReachCaveEntry = call.ReachCaveEntry,
                    EntryName = "循环展开",
                    EntryTarget = call.EntryTarget,
                    Status = EngineEffectCallStatus.Static,
                    Pattern = EngineEffectCallPattern.LoopDerived,
                    Pushes = pushes,
                    StubPushes = call.StubPushes,
                    ValuePush = call.ValuePush,
                    StackingPush = call.StackingPush,
                    ItemPush = call.ItemPush,
                    PersonalPush = personalPush,
                    ValueMode = call.ValueMode,
                    StackingMode = call.StackingMode,
                    ItemEffectId = call.ItemEffectId,
                    PersonalEffectId = personal,
                    CallInstruction = call.CallInstruction,
                    LeadingBytesHex = call.LeadingBytesHex,
                    DisassemblyText = string.Join(Environment.NewLine, new[]
                    {
                        $"循环展开：调用者 0x{call.CallAddress:X8}（{call.EntryName}）",
                        $"局部基址：第 {pair.BaseOffset / 4} 个 push = 0x{baseOffset:X2}；" +
                        $"计数器 [ebp-0x{pair.CounterOffset:X}] = 0..{count - 1}",
                        $"索引 {index}：个人号 0x{personal:X2}",
                        $"值 {call.ValueMode}；叠加 {call.StackingMode}；装备 0x{call.ItemEffectId:X2}（桩内常量）",
                        "写入约束：计算的个人号只读；装备号按真实来源字段管理，共享字段整组生效。",
                        "调用链：" + call.CallerChainText
                    }),
                    CallerChain = call.CallerChain,
                    CallerChainText = call.CallerChainText,
                    CallerPaths = call.CallerPaths,
                    ConsumerRoots = call.ConsumerRoots,
                    FirstCallerEntry = call.FirstCallerEntry,
                    CallerCount = call.CallerCount,
                    IsTableConsumer = false,
                    ConsumerSummary = call.ConsumerSummary,
                    Callees = call.Callees,
                    RedirectSourceAddress = call.RedirectSourceAddress,
                    WrapperStubEntry = call.WrapperStubEntry,
                    WrapperStubCallAddress = call.WrapperStubCallAddress,
                    LoopIndex = index,
                    LoopExpression = expression,
                    LoopBase = baseOffset,
                    LoopLimit = count
                });
            }
        }
        return result.OrderBy(call => call.CallAddress).ThenBy(call => call.LoopIndex).ToArray();
    }

    private static IEnumerable<EngineEffectCallSite> AnalyzeNested(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph, EngineEffectJumpIndex jumpIndex,
        IReadOnlyList<EngineEffectCallSite> derivedCalls)
    {
        foreach (var call in derivedCalls)
        {
            if (call.Pattern != EngineEffectCallPattern.WrapperDerived ||
                call.Status == EngineEffectCallStatus.Static ||
                call.PersonalPush.SourceKind != EngineEffectArgumentSource.Computed) continue;
            if (call.FunctionEntry is not { } wrapperEntry) continue;
            var parameter = NestedParameter(image, call.PersonalPush);
            if (parameter is not { } nested) continue;
            foreach (var caller in callGraph.CallersOf(wrapperEntry))
            {
                var block = EngineEffectDecoder.DecodeBasicBlock(image, caller.Address, 0x800);
                if (block == null) continue;
                var pushes = block.Where(item => item.IsPush).ToArray();
                if (pushes.Length == 0) continue;
                var argument = EngineEffectArgumentAnalyzer.Analyze(image, pushes[^1]);
                var values = CallerValues(image, caller, argument);
                foreach (var value in values)
                {
                    var personal = value + nested.Base;
                    if (personal > byte.MaxValue) continue;
                    var personalPush = new EngineEffectPushArgument
                    {
                        Address = argument.Address,
                        FileOffset = argument.FileOffset,
                        Length = argument.Length,
                        OpCode = argument.OpCode,
                        Value = (uint)personal,
                        ResolvedValue = personal,
                        Source = "nested",
                        SourceKind = EngineEffectArgumentSource.Computed,
                        DefinitionChain =
                        [
                            new EngineEffectArgumentDefinition
                            {
                                Address = argument.Address,
                                Length = argument.Length,
                                FileOffset = argument.FileOffset,
                                RawBytes = argument.RawBytes,
                                Text = $"二级包装参数 {value} + 0x{nested.Base:X} = 0x{personal:X2}",
                                Source = EngineEffectArgumentSource.Computed,
                                Immediate = (uint)personal
                            }
                        ],
                        WritePlan = new EngineEffectArgumentWritePlan
                        {
                            IsWritable = false,
                            BlockReason = "二级包装的个人号由调用者参数/循环计算，不能按单点写回。"
                        },
                        RawBytes = argument.RawBytes
                    };
                    var nestedValue = ReadOnlyArgument(call.ValuePush,
                        "二级包装内的效果值常量被所有调用者共享，只读。");
                    var nestedStacking = ReadOnlyArgument(call.StackingPush,
                        "二级包装内的叠加常量被所有调用者共享，只读。");
                    var nestedItem = call.ItemPush; // Preserve the proven physical definition; assignment groups expose its shared scope.
                    var nestedPushes = new[] { nestedValue, nestedStacking, nestedItem, personalPush };
                    var expression = $"调用者 0x{caller.Address:X8} 参数 {value} + 0x{nested.Base:X} " +
                                     $"= 0x{personal:X2}；包装 0x{wrapperEntry:X8} → 0x{call.CallAddress:X8}";
                    var nestedCallerCount = caller.CallerEntry is { } nestedOwner
                        ? callGraph.CallersOf(nestedOwner).Count : 0;
                    var nestedAfterReturn = EngineEffectFunctionAnalyzer.IsAfterReturn(image,
                        caller.CallerEntry, caller.Address);
                    var (nestedReachable, nestedJump, nestedCave) =
                        EngineEffectFunctionAnalyzer.ResolveReachability(image, caller.CallerEntry,
                            caller.Address, jumpIndex,
                            prologueEntry: EngineEffectFunctionAnalyzer.FindFunctionEntry(image, caller.Address));
                    var nestedPaths = caller.CallerEntry is { } nestedPathEntry
                        ? callGraph.PathsOf(nestedPathEntry) : [];
                    var nestedRoots = EngineEffectCallGraph.RootsOf(nestedPaths);
                    yield return new EngineEffectCallSite
                    {
                        CallAddress = caller.Address,
                        FunctionEntry = caller.CallerEntry,
                        FunctionEntryDistance = caller.CallerEntry is { } callerEntry
                            ? checked((int)(caller.Address - callerEntry)) : 0,
                        IsReachable = nestedReachable,
                        Reachability = EngineEffectFunctionAnalyzer.DescribeReachability(nestedReachable,
                            nestedAfterReturn, nestedCallerCount, caller.CallerEntry, nestedJump, nestedCave),
                        ReachJumpAddress = nestedJump,
                        ReachCaveEntry = nestedCave,
                        EntryName = "二级包装",
                        EntryTarget = wrapperEntry,
                        Status = EngineEffectCallStatus.Static,
                        Pattern = EngineEffectCallPattern.NestedDerived,
                        Pushes = nestedPushes,
                        StubPushes = call.StubPushes,
                        ValuePush = nestedValue,
                        StackingPush = nestedStacking,
                        ItemPush = nestedItem,
                        PersonalPush = personalPush,
                        ValueMode = call.ValueMode,
                        StackingMode = call.StackingMode,
                        ItemEffectId = call.ItemEffectId,
                        PersonalEffectId = personal,
                        CallInstruction = CallerInstruction(image, caller.Address, wrapperEntry),
                        LeadingBytesHex = call.LeadingBytesHex,
                        DisassemblyText = string.Join(Environment.NewLine, new[]
                        {
                            $"二级包装展开：调用者 0x{caller.Address:X8} 调用 0x{wrapperEntry:X8}",
                            $"包装内调用点 0x{call.CallAddress:X8}",
                            $"表达式：参数 {value} + 0x{nested.Base:X} = 个人号 0x{personal:X2}",
                            $"值 {call.ValueMode}；叠加 {call.StackingMode}；装备 0x{call.ItemEffectId:X2}（包装内常量）",
                            "写入约束：二级包装的计算/模式参数只读；装备号按共享来源字段整组修改。",
                            "调用链：" + call.CallerChainText
                        }),
                        CallerChain = call.CallerChain,
                        CallerChainText = call.CallerChainText,
                        CallerPaths = nestedPaths,
                        ConsumerRoots = nestedRoots,
                        FirstCallerEntry = caller.CallerEntry,
                        CallerCount = caller.CallerEntry is { } owner ? callGraph.CallersOf(owner).Count : 0,
                        ConsumerSummary = call.ConsumerSummary,
                        Callees = call.Callees,
                        WrapperStubEntry = wrapperEntry,
                        WrapperStubCallAddress = call.CallAddress,
                        LoopIndex = values.Count > 1 ? values.ToList().IndexOf(value) : null,
                        LoopExpression = expression,
                        LoopBase = nested.Base,
                        LoopLimit = values.Count
                    };
                }
            }
        }
    }

    private static EngineEffectPushArgument ReadOnlyArgument(EngineEffectPushArgument argument, string reason)
        => new()
        {
            Address = argument.Address,
            FileOffset = argument.FileOffset,
            Length = argument.Length,
            OpCode = argument.OpCode,
            Value = argument.Value,
            ResolvedValue = argument.ResolvedValue,
            Source = argument.Source,
            SourceKind = argument.SourceKind,
            DefinitionChain = argument.DefinitionChain,
            WritePlan = new EngineEffectArgumentWritePlan
            {
                IsWritable = false,
                BlockReason = reason,
                Definition = argument.WritePlan.Definition,
                ImmediateFileOffset = argument.WritePlan.ImmediateFileOffset,
                ImmediateLength = argument.WritePlan.ImmediateLength,
                OldValue = argument.WritePlan.OldValue,
                BeforeBytes = argument.WritePlan.BeforeBytes,
                IsSharedDefinition = argument.WritePlan.IsSharedDefinition
            },
            RawBytes = argument.RawBytes
        };

    private static EngineEffectCallInstruction CallerInstruction(EngineEffectBinaryImage image, uint address,
        uint target)
    {
        var offset = image.VirtualAddressToFileOffset(address) ?? -1;
        var raw = offset >= 0 ? image.ReadBytes(offset, 5) ?? [] : [];
        return new EngineEffectCallInstruction
        {
            Address = address,
            FileOffset = offset,
            Target = target,
            RawBytes = raw
        };
    }

    private static IReadOnlyList<int> CallerValues(EngineEffectBinaryImage image, EngineEffectDirectCall caller,
        EngineEffectPushArgument argument)
    {
        var value = argument.Value ?? (argument.ResolvedValue is { } resolved and >= 0 ? (uint)resolved : null);
        if (value is { } immediate && immediate <= byte.MaxValue) return [checked((int)immediate)];
        if (argument.SourceKind != EngineEffectArgumentSource.Register) return [];
        var entry = caller.CallerEntry ?? EngineEffectFunctionAnalyzer.FindFunctionEntry(image, caller.Address);
        if (entry is not { } functionEntry) return [];
        var decoded = Decode(image, functionEntry, caller.Address);
        var counterOffset = CounterOffset(image, argument) ?? LastCounterOffset(decoded);
        if (counterOffset is null) return [];
        var limit = LoopLimitAny(decoded);
        if (limit is not { } count || count <= 0 || count > MaximumLoopCount) return [];
        return Enumerable.Range(0, count).ToArray();
    }

    private static int? LastCounterOffset(IReadOnlyList<DecodedInstruction> decoded)
    {
        int? offset = null;
        foreach (var item in decoded)
        {
            var instruction = item.Instruction;
            if (instruction.Mnemonic != Mnemonic.Mov || instruction.OpCount < 2) continue;
            if (instruction.GetOpKind(0) != OpKind.Register) continue;
            if (instruction.GetOpKind(1) != OpKind.Memory) continue;
            if (instruction.MemoryBase is not (Register.EBP or Register.RBP)) continue;
            var value = Math.Abs(unchecked((int)instruction.MemoryDisplacement64));
            if (value >= 4) offset = value;
        }
        return offset;
    }

    private static int? CounterOffset(EngineEffectBinaryImage image, EngineEffectPushArgument argument)
    {
        foreach (var definition in argument.DefinitionChain)
        {
            if (!definition.Text.StartsWith("mov ", StringComparison.OrdinalIgnoreCase)) continue;
            var offset = MemoryOffset(image, definition);
            if (offset is { } value && value >= 4) return value;
        }
        return null;
    }

    private static int? LoopLimitAny(IReadOnlyList<DecodedInstruction> decoded)
    {
        int? limit = null;
        for (var index = 0; index + 1 < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.Mnemonic != Mnemonic.Cmp || instruction.OpCount < 2) continue;
            if (instruction.GetOpKind(0) != OpKind.Register) continue;
            if (instruction.GetOpRegister(0) is not (Register.AL or Register.EAX)) continue;
            var value = instruction.GetOpKind(1) switch
            {
                OpKind.Immediate8 or OpKind.Immediate8_2nd => (uint?)instruction.GetImmediate(1),
                OpKind.Immediate32 or OpKind.Immediate8to32 => (uint?)instruction.GetImmediate(1),
                _ => null
            };
            if (value is not { } count || count is < 2 or > MaximumLoopCount) continue;
            if (decoded[index + 1].Instruction.FlowControl != FlowControl.ConditionalBranch) continue;
            limit = checked((int)count);
        }
        return limit;
    }

    private static (int ArgumentOffset, int Base)? NestedParameter(EngineEffectBinaryImage image,
        EngineEffectPushArgument personal)
    {
        int? argumentOffset = null;
        int? baseOffset = null;
        foreach (var definition in personal.DefinitionChain)
        {
            if (definition.Text.StartsWith("mov ", StringComparison.OrdinalIgnoreCase))
            {
                var offset = MemoryOffset(image, definition);
                if (offset is { } value && value >= 8 && (value - 8) % 4 == 0) argumentOffset = value;
            }
            else if (definition.Text.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
            {
                var instruction = InstructionAt(image, definition.Address, definition.Length);
                if (instruction is { } decoded && ImmediateOperand(decoded, 1) is { } value)
                    baseOffset = checked((int)value);
            }
        }
        return argumentOffset is { } argument && baseOffset is { } baseValue
            ? (argument, baseValue)
            : null;
    }

    private static Instruction? InstructionAt(EngineEffectBinaryImage image, uint address, int length)
    {
        var decoded = EngineEffectDecoder.DecodeForward(image, address, Math.Max(1, length));
        return decoded.FirstOrDefault()?.Instruction;
    }

    private static uint? ImmediateOperand(Instruction instruction, int operand)
    {
        if (instruction.OpCount <= operand) return null;
        return instruction.GetOpKind(operand) switch
        {
            OpKind.Immediate8 or OpKind.Immediate8_2nd => unchecked((uint)instruction.GetImmediate(operand)),
            OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate8to16 or OpKind.Immediate8to32 =>
                unchecked((uint)instruction.GetImmediate(operand)),
            OpKind.Immediate64 or OpKind.Immediate8to64 or OpKind.Immediate32to64 =>
                unchecked((uint)instruction.GetImmediate(operand)),
            _ => null
        };
    }

    private static IReadOnlyList<DecodedInstruction> Decode(EngineEffectBinaryImage image, uint entry, uint callAddress)
    {
        if (callAddress <= entry) return [];
        var length = Math.Min(MaximumFunctionBytes, checked((int)(callAddress - entry) + 1));
        return EngineEffectDecoder.DecodeForward(image, entry, length);
    }

    private static (int CounterOffset, int BaseOffset)? PersonalOffsets(EngineEffectBinaryImage image,
        EngineEffectPushArgument personal)
    {
        int? counter = null;
        int? baseOffset = null;
        foreach (var definition in personal.DefinitionChain)
        {
            var offset = MemoryOffset(image, definition);
            if (offset is not { } value || value < 4) continue;
            if (definition.Text.StartsWith("mov ", StringComparison.OrdinalIgnoreCase)) counter = value;
            else if (definition.Text.StartsWith("add ", StringComparison.OrdinalIgnoreCase)) baseOffset = value;
        }
        return counter is { } counterOffset && baseOffset is { } baseValue
            ? (counterOffset, baseValue)
            : null;
    }

    private static int? MemoryOffset(EngineEffectBinaryImage image, EngineEffectArgumentDefinition definition)
    {
        var decoded = EngineEffectDecoder.DecodeForward(image, definition.Address,
            Math.Max(1, definition.Length));
        if (decoded.FirstOrDefault()?.Instruction is not { } instruction) return null;
        for (var operand = 0; operand < instruction.OpCount; operand++)
            if (instruction.GetOpKind(operand) == OpKind.Memory &&
                instruction.MemoryBase is Register.EBP or Register.RBP)
                return Math.Abs(unchecked((int)instruction.MemoryDisplacement64));
        return null;
    }

    private static int? BaseLocalValue(EngineEffectBinaryImage image,
        IReadOnlyList<DecodedInstruction> decoded, int baseOffset)
    {
        var index = 0;
        if (index < decoded.Count && decoded[index].Instruction.Mnemonic == Mnemonic.Push) index++;
        if (index < decoded.Count && decoded[index].Instruction.Mnemonic == Mnemonic.Mov &&
            decoded[index].Instruction.Op0Register == Register.EBP) index++;
        if (index < decoded.Count && decoded[index].Instruction.Mnemonic == Mnemonic.Sub &&
            decoded[index].Instruction.Op0Register == Register.ESP) return null;
        var pushIndex = 0;
        while (index < decoded.Count && decoded[index].Instruction.Mnemonic == Mnemonic.Push)
        {
            var immediate = EngineEffectDecoder.ImmediateOf(decoded[index].Instruction);
            if (immediate is not { } value) return null;
            pushIndex++;
            if (pushIndex * 4 == baseOffset) return checked((int)value);
            index++;
        }
        return null;
    }

    private static bool CounterStartsAtZero(IReadOnlyList<DecodedInstruction> decoded, int counterOffset)
    {
        for (var index = 0; index < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.Mnemonic != Mnemonic.Mov || instruction.OpCount < 2) continue;
            if (instruction.GetOpKind(0) != OpKind.Memory ||
                instruction.MemoryBase is not (Register.EBP or Register.RBP) ||
                Math.Abs(unchecked((int)instruction.MemoryDisplacement64)) != counterOffset) continue;
            if (instruction.GetOpKind(1) == OpKind.Register &&
                instruction.GetOpRegister(1) == Register.EAX)
            {
                if (index > 0 && decoded[index - 1].Instruction.Mnemonic is Mnemonic.Xor or Mnemonic.Sub &&
                    decoded[index - 1].Instruction.Op0Register == Register.EAX &&
                    decoded[index - 1].Instruction.Op1Register == Register.EAX) return true;
            }
            if (instruction.GetOpKind(1) is OpKind.Immediate8 or OpKind.Immediate32 &&
                instruction.GetImmediate(1) == 0) return true;
        }
        return false;
    }

    private static int? LoopLimit(IReadOnlyList<DecodedInstruction> decoded, int counterOffset)
    {
        int? limit = null;
        for (var index = 0; index + 1 < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.Mnemonic != Mnemonic.Cmp || instruction.OpCount < 2) continue;
            if (instruction.GetOpKind(0) != OpKind.Memory ||
                instruction.MemoryBase is not (Register.EBP or Register.RBP) ||
                Math.Abs(unchecked((int)instruction.MemoryDisplacement64)) != counterOffset) continue;
            var immediate = instruction.GetOpKind(1) switch
            {
                OpKind.Immediate8 or OpKind.Immediate8_2nd => (uint?)instruction.GetImmediate(1),
                OpKind.Immediate32 or OpKind.Immediate8to32 => (uint?)instruction.GetImmediate(1),
                _ => null
            };
            if (immediate is not { } value || value is 0 or > MaximumLoopCount) continue;
            if (decoded[index + 1].Instruction.FlowControl != FlowControl.ConditionalBranch) continue;
            limit = checked((int)value);
        }
        return limit;
    }
}
