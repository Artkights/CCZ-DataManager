using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Expands loop/table effect lookups into concrete effect ids.  The 6.x engine
/// contains loops such as:
///   mov eax,[ebp-8]
///   cmp al,6
///   movzx ecx,byte ptr [eax+437E19h]
///   add eax,6
///   push ecx; push eax; call 004101D9
/// Enumerating the loop bound and the static table turns one dynamic call into
/// the actual set of effect ids reachable from that consumer.
/// </summary>
internal static class EngineEffectTableAnalyzer
{
    private const int MaximumFunctionBytes = 0x400;
    private const int MaximumTableEntries = 32;

    public static IReadOnlyList<EngineEffectCallSite> Analyze(EngineEffectBinaryImage image,
        IReadOnlyList<EngineEffectCallSite> calls)
    {
        var result = new List<EngineEffectCallSite>();
        foreach (var call in calls)
        {
            if (call.FunctionEntry is not { } functionEntry || call.EntryTarget != EngineEffectScanService.CoreEntry)
                continue;
            var baseOffset = TableBaseOffset(image, call.PersonalPush);
            var tableAddress = TableAddress(image, call.ItemPush);
            if (baseOffset is not { } offset || tableAddress is not { } table) continue;
            var loop = FindLoop(image, functionEntry, call.CallAddress);
            if (loop is not { } bounds) continue;
            var tableOffset = image.VirtualAddressToFileOffset(table);
            if (tableOffset is not { } fileOffset) continue;
            var bytes = image.ReadBytes(fileOffset, bounds.Limit);
            if (bytes == null || bytes.Length < bounds.Limit) continue;

            for (var index = 0; index < bounds.Limit; index++)
            {
                var personal = offset + index;
                if (personal > byte.MaxValue) break;
                var value = bounds.ValueAtTwo ? index == 2 ? 0 : 1 : call.ValueMode ?? 1;
                var stacking = call.StackingMode ?? 1;
                var item = bytes[index];
                var valuePush = Synthetic(value, EngineEffectArgumentSource.Computed,
                    bounds.ValueAtTwo
                        ? $"cmp al,2；索引 {index} → 效果值 {value}"
                        : $"循环共享的效果值 push（索引 {index}）",
                    call.ValuePush, "循环内共享常量，写回会影响所有索引。");
                var stackingPush = Synthetic(stacking, EngineEffectArgumentSource.Immediate,
                    $"循环共享的叠加方式 push（索引 {index}）", call.StackingPush,
                    "循环内共享常量，写回会影响所有索引。");
                var itemDefinition = call.ItemPush.DefinitionChain.FirstOrDefault(definition =>
                    definition.Text.Contains("movzx", StringComparison.OrdinalIgnoreCase));
                var itemPush = new EngineEffectPushArgument
                {
                    Address = call.ItemPush.Address,
                    FileOffset = fileOffset + index,
                    Length = 1,
                    OpCode = 0,
                    Value = item,
                    ResolvedValue = item,
                    Source = "table",
                    SourceKind = EngineEffectArgumentSource.Memory,
                    DefinitionChain =
                    [
                        new EngineEffectArgumentDefinition
                        {
                            Address = itemDefinition?.Address ?? call.ItemPush.Address,
                            Length = itemDefinition?.Length ?? call.ItemPush.Length,
                            FileOffset = fileOffset + index,
                            RawBytes = [item],
                            Text = $"movzx byte ptr [0x{table:X8}+{index}] = 0x{item:X2}",
                            Source = EngineEffectArgumentSource.Memory,
                            Immediate = item
                        }
                    ],
                    WritePlan = new EngineEffectArgumentWritePlan
                    {
                        IsWritable = true,
                        BlockReason = "静态表字节被循环共享；写入会影响所有使用该表的对象。",
                        Definition = new EngineEffectArgumentDefinition
                        {
                            Address = itemDefinition?.Address ?? call.ItemPush.Address,
                            Length = itemDefinition?.Length ?? 1,
                            FileOffset = fileOffset + index,
                            RawBytes = [item],
                            Text = $"movzx byte ptr [0x{table:X8}+{index}]",
                            Source = EngineEffectArgumentSource.Memory,
                            Immediate = item,
                            ImmediateOffset = 0,
                            ImmediateSize = 1
                        },
                        ImmediateFileOffset = fileOffset + index,
                        ImmediateLength = 1,
                        OldValue = item,
                        BeforeBytes = [item],
                        IsSharedDefinition = false
                    },
                    RawBytes = [item]
                };
                var personalPush = new EngineEffectPushArgument
                {
                    Address = call.PersonalPush.Address,
                    FileOffset = call.PersonalPush.FileOffset,
                    Length = call.PersonalPush.Length,
                    OpCode = call.PersonalPush.OpCode,
                    Value = (uint)personal,
                    ResolvedValue = personal,
                    Source = "computed",
                    SourceKind = EngineEffectArgumentSource.Computed,
                    DefinitionChain =
                    [
                        new EngineEffectArgumentDefinition
                        {
                            Address = call.PersonalPush.Address,
                            Length = call.PersonalPush.Length,
                            FileOffset = call.PersonalPush.FileOffset,
                            RawBytes = call.PersonalPush.RawBytes,
                            Text = $"表索引 {index} + 0x{offset:X} = 0x{personal:X2}",
                            Source = EngineEffectArgumentSource.Computed,
                            Immediate = (uint)personal
                        }
                    ],
                    WritePlan = new EngineEffectArgumentWritePlan
                    {
                        IsWritable = false,
                        BlockReason = "个人号由循环索引计算，不能按单个调用点写回。"
                    },
                    RawBytes = call.PersonalPush.RawBytes
                };
                var pushes = new[] { valuePush, stackingPush, itemPush, personalPush };
                var expression = $"表 0x{table:X8}[{index}] = 0x{item:X2}；个人号 0x{offset:X}+{index}=0x{personal:X2}";
                result.Add(new EngineEffectCallSite
                {
                    CallAddress = call.CallAddress,
                    FunctionEntry = call.FunctionEntry,
                    FunctionEntryDistance = call.FunctionEntryDistance,
                    IsReachable = call.IsReachable,
                    Reachability = call.Reachability,
                    ReachJumpAddress = call.ReachJumpAddress,
                    ReachCaveEntry = call.ReachCaveEntry,
                    EntryName = "核心判定（表驱动）",
                    EntryTarget = call.EntryTarget,
                    Status = EngineEffectCallStatus.Static,
                    Pattern = EngineEffectCallPattern.TableDerived,
                    Pushes = pushes,
                    StubPushes = call.StubPushes,
                    ValuePush = valuePush,
                    StackingPush = stackingPush,
                    ItemPush = itemPush,
                    PersonalPush = personalPush,
                    ValueMode = ToByte(value),
                    StackingMode = ToByte(stacking),
                    ItemEffectId = item,
                    PersonalEffectId = personal,
                    CallInstruction = call.CallInstruction,
                    LeadingBytesHex = call.LeadingBytesHex,
                    DisassemblyText = string.Join(Environment.NewLine, new[]
                    {
                        $"表驱动调用：0x{call.CallAddress:X8}（核心判定），表 0x{table:X8}，索引 {index}/{bounds.Limit}",
                        $"循环上限：cmp {bounds.RegisterText},{bounds.Limit}",
                        $"表达式：{expression}",
                        $"值：{value}；叠加：{stacking}；装备：0x{item:X2}；个人：0x{personal:X2}",
                        "写入约束：循环共享常量与静态表字节只读；个人号由索引计算，不能按单点写回。",
                        "调用链：" + call.CallerChainText
                    }),
                    CallerChain = call.CallerChain,
                    CallerChainText = call.CallerChainText,
                    CallerPaths = call.CallerPaths,
                    ConsumerRoots = call.ConsumerRoots,
                    FirstCallerEntry = call.FirstCallerEntry,
                    CallerCount = call.CallerCount,
                    IsTableConsumer = true,
                    ConsumerSummary = call.ConsumerSummary,
                    Callees = call.Callees,
                    RedirectSourceAddress = call.RedirectSourceAddress,
                    TableIndex = index,
                    TableAddress = table,
                    TableEntryOffset = fileOffset + index,
                    TableExpression = expression
                });
            }
        }
        return result.OrderBy(call => call.CallAddress).ThenBy(call => call.TableIndex).ToArray();
    }

    private static int? TableBaseOffset(EngineEffectBinaryImage image, EngineEffectPushArgument personal)
    {
        foreach (var definition in personal.DefinitionChain)
        {
            if (!definition.Text.StartsWith("add ", StringComparison.OrdinalIgnoreCase)) continue;
            var instruction = InstructionAt(image, definition.Address, definition.Length);
            if (instruction is not { } decoded || decoded.OpCount < 2) continue;
            if (ImmediateOperand(decoded, 1) is { } value && value <= byte.MaxValue) return checked((int)value);
        }
        return null;
    }

    private static uint? TableAddress(EngineEffectBinaryImage image, EngineEffectPushArgument item)
    {
        foreach (var definition in item.DefinitionChain)
        {
            if (!definition.Text.Contains("movzx", StringComparison.OrdinalIgnoreCase)) continue;
            var instruction = InstructionAt(image, definition.Address, definition.Length);
            if (instruction is not { } decoded) continue;
            var isMemory = false;
            for (var operand = 0; operand < decoded.OpCount; operand++)
                if (decoded.GetOpKind(operand) == OpKind.Memory) { isMemory = true; break; }
            if (!isMemory) continue;
            var address = decoded.MemoryDisplacement64;
            if (address is <= uint.MaxValue and >= 0x400000) return (uint)address;
        }
        return null;
    }

    private static LoopBounds? FindLoop(EngineEffectBinaryImage image, uint functionEntry, uint callAddress)
    {
        if (callAddress <= functionEntry) return null;
        var length = Math.Min(MaximumFunctionBytes, checked((int)(callAddress - functionEntry) + 1));
        var decoded = EngineEffectDecoder.DecodeForward(image, functionEntry, length);
        LoopBounds? bounds = null;
        var loopIndex = -1;
        for (var index = 0; index < decoded.Count; index++)
        {
            var item = decoded[index];
            var instruction = item.Instruction;
            if (instruction.Mnemonic != Mnemonic.Cmp || instruction.OpCount < 2) continue;
            if (ImmediateOperand(instruction, 1) is not { } limit || limit is 0 or > MaximumTableEntries) continue;
            if (instruction.Op0Kind is not (OpKind.Register)) continue;
            var register = instruction.GetOpRegister(0);
            if (register is not (Register.AL or Register.EAX)) continue;
            if (index + 1 >= decoded.Count) continue;
            if (decoded[index + 1].Instruction.Mnemonic != Mnemonic.Jae) continue;
            bounds = new LoopBounds(checked((int)limit), false,
                register == Register.AL ? "al" : "eax");
            loopIndex = index;
        }
        if (bounds == null) return null;
        return bounds with { ValueAtTwo = loopIndex >= 0 && HasValueAtTwo(decoded, loopIndex + 1) };
    }

    private static bool HasValueAtTwo(IReadOnlyList<DecodedInstruction> decoded, int startIndex)
    {
        for (var index = startIndex; index < decoded.Count; index++)
        {
            var instruction = decoded[index].Instruction;
            if (instruction.Mnemonic != Mnemonic.Cmp || instruction.OpCount < 2) continue;
            if (instruction.GetOpRegister(0) is not (Register.AL or Register.EAX)) continue;
            if (ImmediateOperand(instruction, 1) == 2) return true;
        }
        return false;
    }

    private static Instruction? InstructionAt(EngineEffectBinaryImage image, uint address, int length)
    {
        if (length <= 0) length = 16;
        var decoded = EngineEffectDecoder.DecodeForward(image, address, length);
        return decoded.FirstOrDefault()?.Instruction;
    }

    private static uint? ImmediateOperand(Instruction instruction, int operand)
    {
        if (instruction.OpCount <= operand) return null;
        return instruction.GetOpKind(operand) switch
        {
            OpKind.Immediate8 or OpKind.Immediate8_2nd =>
                unchecked((uint)instruction.GetImmediate(operand)),
            OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate8to16 or OpKind.Immediate8to32 =>
                unchecked((uint)instruction.GetImmediate(operand)),
            OpKind.Immediate64 or OpKind.Immediate8to64 or OpKind.Immediate32to64 =>
                unchecked((uint)instruction.GetImmediate(operand)),
            _ => null
        };
    }

    private static EngineEffectPushArgument Synthetic(int value, EngineEffectArgumentSource source,
        string text, EngineEffectPushArgument origin, string reason)
        => new()
        {
            Address = origin.Address,
            FileOffset = origin.FileOffset,
            Length = origin.Length,
            OpCode = origin.OpCode,
            Value = (uint)value,
            ResolvedValue = value,
            Source = source == EngineEffectArgumentSource.Computed ? "computed" : "imm",
            SourceKind = source,
            DefinitionChain =
            [
                new EngineEffectArgumentDefinition
                {
                    Address = origin.Address,
                    Length = origin.Length,
                    FileOffset = origin.FileOffset,
                    RawBytes = origin.RawBytes,
                    Text = text,
                    Source = source,
                    Immediate = (uint)value
                }
            ],
            WritePlan = new EngineEffectArgumentWritePlan
            {
                IsWritable = false,
                BlockReason = reason
            },
            RawBytes = origin.RawBytes
        };

    private static int? ToByte(int value) => value is >= 0 and <= byte.MaxValue ? value : null;

    private sealed record LoopBounds(int Limit, bool ValueAtTwo, string RegisterText);
}
