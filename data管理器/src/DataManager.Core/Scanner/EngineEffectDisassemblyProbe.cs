using Iced.Intel;

namespace DataManager.Core.Scanner;

/// <summary>One arithmetic immediate found inside an effect handler's first block.</summary>
public sealed record EngineEffectImmediateCandidate(uint Address, string Mnemonic, int Value);

/// <summary>Read-only disassembly probe used by diagnostics and constant evidence.</summary>
public static class EngineEffectDisassemblyProbe
{
    private static readonly HashSet<Mnemonic> CandidateMnemonics =
    [
        Mnemonic.Cmp, Mnemonic.Add, Mnemonic.Sub, Mnemonic.Imul, Mnemonic.And,
        Mnemonic.Or, Mnemonic.Shl, Mnemonic.Shr, Mnemonic.Test, Mnemonic.Mov
    ];

    private static readonly HashSet<uint> EffectArgumentHelpers =
    [
        0x004101D9, 0x0042518F, 0x004EEA00, 0x00412A87, 0x00436334, 0x004240D6,
        0x00409821, 0x0043786B, 0x0040941F, 0x00450986
    ];

    private static readonly HashSet<uint> ValueModifierHelpers =
    [
        0x00425AD9, 0x0047FA89, 0x0047FA9E, 0x0040BAD4, 0x00425AF0
    ];

    public static IReadOnlyList<EngineEffectImmediateCandidate> CollectImmediateCandidates(
        string exePath, uint entry, int? effectId = null, int maxBytes = 480)
    {
        var image = EngineEffectBinaryImage.Read(File.ReadAllBytes(exePath));
        var result = new List<EngineEffectImmediateCandidate>();
        var pendingPushes = new List<EngineEffectImmediateCandidate>();
        foreach (var item in EngineEffectDecoder.DecodeForward(image, entry, Math.Max(1, maxBytes)))
        {
            var instruction = item.Instruction;
            if (instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch)
            {
                pendingPushes.Clear();
                break;
            }
            if (instruction.Mnemonic == Mnemonic.Push)
            {
                if (instruction.OpCount == 1 && IsImmediateKind(instruction.GetOpKind(0)))
                {
                    var pushed = instruction.GetImmediate(0);
                    if (pushed is >= 2 and <= 254 && effectId != null && checked((int)pushed) != effectId)
                        pendingPushes.Add(new EngineEffectImmediateCandidate(
                            checked((uint)instruction.IP), "push", checked((int)pushed)));
                }
                continue;
            }
            if (instruction.Mnemonic == Mnemonic.Nop) continue;
            if (instruction.Mnemonic == Mnemonic.Call)
            {
                if (instruction.OpCount == 1 && IsImmediateKind(instruction.GetOpKind(0)))
                {
                    var target = checked((uint)instruction.GetImmediate(0));
                    if (EffectArgumentHelpers.Contains(target))
                        pendingPushes.Clear();
                    else if (ValueModifierHelpers.Contains(target))
                        FlushPushes();
                    else
                        pendingPushes.Clear();
                }
                else
                {
                    pendingPushes.Clear();
                }
                continue;
            }
            pendingPushes.Clear();
            if (!CandidateMnemonics.Contains(instruction.Mnemonic)) continue;
            if (instruction.Op0Kind == OpKind.Register &&
                instruction.GetOpRegister(0) == Register.ESP &&
                instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub)
                continue;
            for (var index = 0; index < instruction.OpCount; index++)
            {
                var kind = instruction.GetOpKind(index);
                if (!IsImmediateKind(kind)) continue;
                var value = instruction.GetImmediate(index);
                if (value is < 2 or > 254) continue;
                result.Add(new EngineEffectImmediateCandidate(checked((uint)instruction.IP),
                    instruction.Mnemonic.ToString().ToLowerInvariant(), checked((int)value)));
                break;
            }
        }
        return result;

        void FlushPushes()
        {
            result.AddRange(pendingPushes);
            pendingPushes.Clear();
        }
    }

    private static bool IsImmediateKind(OpKind kind) =>
        kind is OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or
            OpKind.Immediate16 or OpKind.Immediate32;

    public static IReadOnlyList<string> Dump(string exePath, uint address, int byteCount)
    {
        var image = EngineEffectBinaryImage.Read(File.ReadAllBytes(exePath));
        return EngineEffectDecoder.DecodeForward(image, address, Math.Max(1, byteCount))
            .Select(item => $"0x{item.Address:X8}  {item.Text}").ToArray();
    }

    public static IReadOnlyList<string> DumpFunction(string exePath, uint entry)
    {
        var image = EngineEffectBinaryImage.Read(File.ReadAllBytes(exePath));
        var lines = new List<string>();
        foreach (var item in EngineEffectDecoder.DecodeForward(image, entry, 2048))
        {
            lines.Add($"0x{item.Address:X8}  {item.Text}");
            if (item.Instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch) break;
        }
        return lines;
    }

    public static IReadOnlyList<string> DumpBytes(string exePath, uint address, int byteCount)
    {
        var image = EngineEffectBinaryImage.Read(File.ReadAllBytes(exePath));
        if (image.VirtualAddressToFileOffset(address) is not { } offset) return [];
        var bytes = image.ReadBytes(offset, Math.Max(1, byteCount));
        if (bytes == null) return [];
        var lines = new List<string>();
        for (var index = 0; index < bytes.Length; index += 16)
            lines.Add($"+0x{offset + index:X6} (0x{address + (uint)index:X8})  " +
                string.Join(' ', bytes.Skip(index).Take(16).Select(value => value.ToString("X2"))));
        return lines;
    }

    public static IReadOnlyList<uint> FindPattern(string exePath, byte[] pattern, int maxHits = 64)
    {
        if (pattern.Length == 0) return [];
        var image = EngineEffectBinaryImage.Read(File.ReadAllBytes(exePath));
        var hits = new List<uint>();
        foreach (var section in image.ExecutableSections)
        {
            var length = (int)Math.Min(section.RawSize, int.MaxValue);
            var blob = image.ReadBytes(section.RawPointer, length);
            if (blob == null) continue;
            for (var index = 0; index <= blob.Length - pattern.Length; index++)
            {
                if (!blob.AsSpan(index).StartsWith(pattern)) continue;
                hits.Add(section.VirtualAddress + (uint)index);
                if (hits.Count >= maxHits) return hits;
            }
        }
        return hits;
    }
}
