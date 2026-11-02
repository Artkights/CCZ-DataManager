﻿using System.Globalization;
using DataManager.Core.Models;
using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>
/// Resolves pass-through wrappers at their caller sites.  A wrapper such as
/// 0x004E0000 forwards [ebp+..] slots into the shared effect entry; when the
/// caller pushes constants, those constants decide the real effect id even
/// though the wrapper body itself contains no static id.
/// </summary>
internal static class EngineEffectWrapperAnalyzer
{
    private const int MaximumCallerBlockBytes = 0x2000;
    private const int MaximumDerivedCalls = 512;
    private static readonly string[] SlotNames =
    [
        "效果值模式", "叠加方式", "装备特效号", "个人/兵种特效号"
    ];

    public static IReadOnlyList<EngineEffectCallSite> Analyze(EngineEffectBinaryImage image,
        EngineEffectCallGraph callGraph, EngineEffectJumpIndex jumpIndex, IReadOnlyList<EngineEffectCallSite> calls)
    {
        var candidates = calls
            .Where(call => call.FunctionEntry is { } entry &&
                           !EngineEffectScanService.EntryNames.ContainsKey(entry) &&
                           call.Pushes.Any(push => push.SourceKind == EngineEffectArgumentSource.StackParameter))
            .GroupBy(call => call.FunctionEntry!.Value)
            .ToArray();

        var work = new List<DerivedWork>();
        foreach (var group in candidates)
        {
            var stubEntry = group.Key;
            foreach (var stubCall in group.OrderBy(call => call.CallAddress))
            {
                var slots = new[]
                {
                    ResolveSlot(image, stubEntry, stubCall.ValuePush, 0),
                    ResolveSlot(image, stubEntry, stubCall.StackingPush, 1),
                    ResolveSlot(image, stubEntry, stubCall.ItemPush, 2),
                    ResolveSlot(image, stubEntry, stubCall.PersonalPush, 3)
                };
                if (slots.All(slot => slot.ArgumentIndex == null)) continue;
                var required = slots.Where(slot => slot.ArgumentIndex != null)
                    .Max(slot => slot.ArgumentIndex!.Value);
                foreach (var caller in callGraph.CallersOf(stubEntry))
                {
                    if (work.Count >= MaximumDerivedCalls) break;
                    var item = BuildWork(image, stubCall, slots, required, caller);
                    if (item != null) work.Add(item);
                }
            }
        }

        ApplySharedDefinitions(work);
        return work.Select(item => CreateCall(image, callGraph, jumpIndex, item))
            .OrderBy(call => call.CallAddress)
            .ThenBy(call => call.WrapperStubCallAddress)
            .ToArray();
    }

    private static DerivedWork? BuildWork(EngineEffectBinaryImage image, EngineEffectCallSite stubCall,
        SlotSource[] slots, int requiredArguments, EngineEffectDirectCall caller)
    {
        var block = EngineEffectDecoder.DecodeBasicBlock(image, caller.Address, MaximumCallerBlockBytes);
        if (block == null) return null;
        var pushes = block.Where(item => item.IsPush).ToArray();
        if (pushes.Length < requiredArguments) return null;

        var arguments = new EngineEffectPushArgument[4];
        var mapping = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var slot = slots[index];
            if (slot.ArgumentIndex is { } argumentIndex)
            {
                var callerPush = pushes[^argumentIndex];
                arguments[index] = EngineEffectArgumentAnalyzer.Analyze(image, callerPush);
                mapping.Add($"{SlotNames[index]}=[ebp+0x{slot.StackOffset:X}] 调用参数 {argumentIndex}" +
                            $"（0x{callerPush.Address:X8}：{callerPush.Text}）");
            }
            else
            {
                arguments[index] = slot.Argument;
                mapping.Add($"{SlotNames[index]}=" + (slot.Constant is { } value
                    ? $"桩内常量 0x{value:X}"
                    : "桩内未解析"));
            }
        }

        var callOffset = image.VirtualAddressToFileOffset(caller.Address) ?? -1;
        var raw = callOffset >= 0 ? image.ReadBytes(callOffset, 5) ?? [] : [];
        return new DerivedWork
        {
            StubCall = stubCall,
            StubEntry = stubCall.FunctionEntry!.Value,
            Caller = caller,
            Arguments = arguments,
            CallInstruction = new EngineEffectCallInstruction
            {
                Address = caller.Address,
                FileOffset = callOffset,
                Target = stubCall.FunctionEntry.Value,
                RawBytes = raw
            },
            MappingText = string.Join("；", mapping),
            Evidence = BuildEvidence(stubCall, caller, arguments, mapping)
        };
    }

    private static string BuildEvidence(EngineEffectCallSite stubCall, EngineEffectDirectCall caller,
        IReadOnlyList<EngineEffectPushArgument> arguments, IReadOnlyList<string> mapping)
    {
        var stubEntry = stubCall.FunctionEntry!.Value;
        var callerEntry = caller.CallerEntry is { } entry ? $"0x{entry:X8}" : "—";
        var lines = new List<string>
        {
            $"派生调用：桩内 0x{stubCall.CallAddress:X8}（{stubCall.EntryName}） ← 桩函数 0x{stubEntry:X8}",
            $"调用者：0x{caller.Address:X8}（函数 {callerEntry}）",
            "参数映射：" + string.Join("；", mapping),
            "桩内 push：" + Describe(stubCall.Pushes)
        };
        lines.Add("实际参数 push：" + Describe(arguments));
        lines.Add("写入约束：调用者常量按来源写回；桩内装备常量按共享绑定组修改，影响所有关联调用者。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(IReadOnlyList<EngineEffectPushArgument> pushes)
        => pushes.Count == 0
            ? "—"
            : string.Join(" | ", pushes.Select(push =>
                $"0x{push.Address:X8} {EngineEffectDecoder.Hex(push.RawBytes)} {DescribeValue(push)}"));

    private static string DescribeValue(EngineEffectPushArgument push)
    {
        var value = EffectiveValue(push);
        return value is { } item ? $"0x{item:X}" : "动态";
    }

    private static SlotSource ResolveSlot(EngineEffectBinaryImage image, uint stubEntry,
        EngineEffectPushArgument push, int index)
    {
        if (push.Address == 0) return new SlotSource(index, null, null, null, push);
        var value = EffectiveValue(push);
        if (value != null)
        {
            if (index == 2 && push.WritePlan.IsWritable)
            {
                push.WritePlan.IsSharedDefinition = true;
                return new SlotSource(index, value, null, null, push);
            }
            return new SlotSource(index, value, null, null,
                ReadOnly(push, $"常量固化在共享桩 0x{stubEntry:X8} 内，修改会影响所有调用者。"));
        }
        if (push.SourceKind == EngineEffectArgumentSource.StackParameter)
        {
            var offset = StackOffset(image, push);
            if (offset is { } displacement)
                return new SlotSource(index, null, (displacement - 8) / 4 + 1, displacement, push);
            return new SlotSource(index, null, null, null,
                ReadOnly(push, "无法解析桩内栈参数偏移。"));
        }
        return new SlotSource(index, null, null, null,
            ReadOnly(push, "桩内参数来自寄存器/内存/计算链，无法跨过程解析。"));
    }

    private static int? StackOffset(EngineEffectBinaryImage image, EngineEffectPushArgument push)
    {
        if (MemoryStackOffset(image, push.Address, push.Length, 0) is { } direct) return direct;
        foreach (var definition in push.DefinitionChain)
        {
            if (definition.Address == push.Address || definition.Length <= 0) continue;
            if (MemoryStackOffset(image, definition.Address, definition.Length, 1) is { } loaded) return loaded;
            if (MemoryStackOffset(image, definition.Address, definition.Length, 2) is { } second) return second;
        }
        return null;
    }

    private static int? MemoryStackOffset(EngineEffectBinaryImage image, uint address, int length, int operand)
    {
        if (length <= 0) return null;
        var decoded = EngineEffectDecoder.DecodeForward(image, address, length);
        if (decoded.FirstOrDefault()?.Instruction is not { } instruction) return null;
        if (instruction.OpCount <= operand || instruction.GetOpKind(operand) != OpKind.Memory) return null;
        if (instruction.MemoryBase is not (Register.EBP or Register.RBP)) return null;
        var displacement = instruction.MemoryDisplacement64;
        if (displacement < 8 || (displacement - 8) % 4 != 0) return null;
        return checked((int)displacement);
    }

    private static void ApplySharedDefinitions(IReadOnlyList<DerivedWork> work)
        => EngineEffectArgumentAnalyzer.MarkSharedArguments(work.SelectMany(item =>
            item.Arguments.Select((argument, index) => (argument, index))));

    private static EngineEffectCallSite CreateCall(EngineEffectBinaryImage image, EngineEffectCallGraph callGraph,
        EngineEffectJumpIndex jumpIndex, DerivedWork item)
    {
        var entry = item.StubEntry;
        var callerEntry = item.Caller.CallerEntry;
        var chain = callGraph.ChainOf(entry, 2);
        var paths = callerEntry is { } pathEntry ? callGraph.PathsOf(pathEntry) : [];
        var roots = EngineEffectCallGraph.RootsOf(paths);
        var chainText = $"0x{item.Caller.Address:X8} 调用桩 0x{entry:X8}" +
                        (chain.Count == 0 ? string.Empty : "；" + EngineEffectCallGraph.Describe(chain));
        var personal = EffectiveValue(item.Arguments[3]);
        var itemId = EffectiveValue(item.Arguments[2]);
        var status = personal is <= byte.MaxValue ? EngineEffectCallStatus.Static
            : itemId is <= byte.MaxValue ? EngineEffectCallStatus.Dynamic
            : EngineEffectCallStatus.Unresolved;
        var entryCallerCount = callerEntry is { } entryFunction ? callGraph.CallersOf(entryFunction).Count : 0;
        var afterReturn = EngineEffectFunctionAnalyzer.IsAfterReturn(image, callerEntry, item.Caller.Address);
        var (reachable, jumpSource, caveEntry) =
            EngineEffectFunctionAnalyzer.ResolveReachability(image, callerEntry, item.Caller.Address, jumpIndex,
                prologueEntry: EngineEffectFunctionAnalyzer.FindFunctionEntry(image, item.Caller.Address));
        var reachability = EngineEffectFunctionAnalyzer.DescribeReachability(reachable, afterReturn,
            entryCallerCount, callerEntry, jumpSource, caveEntry);
        if (caveEntry != null) chainText = reachability + "；" + chainText;
        return new EngineEffectCallSite
        {
            CallAddress = item.Caller.Address,
            FunctionEntry = callerEntry,
            IsReachable = reachable,
            Reachability = reachability,
            ReachJumpAddress = jumpSource,
            ReachCaveEntry = caveEntry,
            FunctionEntryDistance = callerEntry is { } function ? checked((int)(item.Caller.Address - function)) : 0,
            EntryName = $"{item.StubCall.EntryName}（派生）",
            EntryTarget = entry,
            Status = status,
            Pattern = EngineEffectCallPattern.WrapperDerived,
            Pushes = item.Arguments,
            StubPushes = item.StubCall.Pushes,
            ValuePush = item.Arguments[0],
            StackingPush = item.Arguments[1],
            ItemPush = item.Arguments[2],
            PersonalPush = item.Arguments[3],
            ValueMode = ToByte(EffectiveValue(item.Arguments[0])),
            StackingMode = ToByte(EffectiveValue(item.Arguments[1])),
            ItemEffectId = EngineEffectByteContract.Decode(itemId),
            PersonalEffectId = EngineEffectByteContract.Decode(personal),
            CallInstruction = item.CallInstruction,
            LeadingBytesHex = HexBefore(image, item.Caller.Address, 16),
            DisassemblyText = item.Evidence,
            CallerChain = chain,
            CallerChainText = chainText,
            CallerPaths = paths,
            ConsumerRoots = roots,
            FirstCallerEntry = callerEntry,
            CallerCount = callerEntry is { } callerFunction ? callGraph.CallersOf(callerFunction).Count : 0,
            WrapperStubEntry = entry,
            WrapperStubCallAddress = item.StubCall.CallAddress,
            ParameterMapping = item.MappingText,
            Callees = EngineEffectFunctionAnalyzer.Callees(image, entry)
        };
    }

    private static uint? EffectiveValue(EngineEffectPushArgument argument)
        => argument.Value ?? (argument.ResolvedValue is { } resolved and >= 0 ? (uint)resolved : null);

    private static int? ToByte(uint? value) => value is <= byte.MaxValue ? (int)value!.Value : null;

    private static EngineEffectPushArgument ReadOnly(EngineEffectPushArgument argument, string reason)
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

    private static string HexBefore(EngineEffectBinaryImage image, uint address, int length)
    {
        var offset = image.VirtualAddressToFileOffset(address);
        if (offset is not { } fileOffset) return string.Empty;
        var available = (int)Math.Min(length, fileOffset);
        var bytes = image.ReadBytes(fileOffset - available, available);
        return bytes == null ? string.Empty : EngineEffectDecoder.Hex(bytes);
    }

    private sealed record SlotSource(int Index, uint? Constant, int? ArgumentIndex, int? StackOffset,
        EngineEffectPushArgument Argument);

    private sealed class DerivedWork
    {
        public required EngineEffectCallSite StubCall { get; init; }
        public required uint StubEntry { get; init; }
        public required EngineEffectDirectCall Caller { get; init; }
        public required EngineEffectPushArgument[] Arguments { get; init; }
        public required EngineEffectCallInstruction CallInstruction { get; init; }
        public required string MappingText { get; init; }
        public required string Evidence { get; init; }
    }
}
