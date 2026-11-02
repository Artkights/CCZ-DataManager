using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>Linear profile of one E8 root, used to explain roots without static inbound edges.</summary>
internal sealed record EngineEffectFunctionProfile(
    uint Entry,
    int SizeBytes,
    int FunctionInstructionCount,
    bool EndsWithReturn,
    int DirectCallCount,
    int ExternalCallCount,
    int RegisterCallCount,
    int EffectCoreCallCount,
    IReadOnlyList<uint> CalleeTargets,
    string EntryBytes,
    int PrecedingPaddingBytes)
{
    public bool CallsCore => EffectCoreCallCount > 0;
}

internal static class EngineEffectFunctionProfiler
{
    private const int MaximumBytes = 0x2000;
    private const int MaximumInstructions = 2000;
    private const int MaximumTargets = 8;
    private const int EntryHeadBytes = 16;
    private const int MaximumPadding = 64;

    public static EngineEffectFunctionProfile Profile(EngineEffectBinaryImage image, uint entry)
        => image.Context.GetOrBuild(EngineEffectAnalysisKind.Profile, entry, MaximumBytes,
            () => ProfileCore(image, entry));

    private static EngineEffectFunctionProfile ProfileCore(EngineEffectBinaryImage image, uint entry)
    {
        var offset = image.VirtualAddressToFileOffset(entry);
        if (offset is not { } fileOffset)
            return Empty(entry);
        var head = image.ReadBytes(fileOffset, EntryHeadBytes) ?? [];
        var padding = 0;
        while (padding < MaximumPadding && fileOffset - padding > 0)
        {
            var previous = image.ReadBytes(fileOffset - padding - 1, 1);
            if (previous == null || previous.Length == 0 || (previous[0] != 0x90 && previous[0] != 0xCC)) break;
            padding++;
        }

        var decoded = EngineEffectDecoder.DecodeForward(image, entry, MaximumBytes);
        var direct = 0;
        var external = 0;
        var register = 0;
        var core = 0;
        var count = 0;
        var targets = new List<uint>(MaximumTargets);
        var endsWithReturn = false;
        var lastEnd = entry;
        foreach (var item in decoded)
        {
            count++;
            lastEnd = item.Address + (uint)item.Length;
            var instruction = item.Instruction;
            if (instruction.Mnemonic == Mnemonic.Ret || instruction.FlowControl == FlowControl.Return)
            {
                endsWithReturn = true;
                break;
            }
            if (instruction.Mnemonic != Mnemonic.Call) continue;
            var kind = instruction.Op0Kind;
            if (kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            {
                direct++;
                var target = unchecked((uint)instruction.NearBranchTarget);
                if (target != 0)
                {
                    if (EngineEffectScanService.KnownEntryTargets.Contains(target)) core++;
                    if (targets.Count < MaximumTargets && !targets.Contains(target)) targets.Add(target);
                }
            }
            else if (kind is OpKind.Register)
            {
                register++;
            }
            else if (kind is OpKind.Memory)
            {
                external++;
            }
            if (count >= MaximumInstructions) break;
        }

        var size = checked((int)(lastEnd - entry));
        return new EngineEffectFunctionProfile(entry, size, count, endsWithReturn, direct, external,
            register, core, targets, EngineEffectDecoder.Hex(head), padding);
    }

    private static EngineEffectFunctionProfile Empty(uint entry)
        => new(entry, 0, 0, false, 0, 0, 0, 0, [], string.Empty, 0);
}
