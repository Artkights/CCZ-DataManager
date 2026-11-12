using Iced.Intel;
using TransformManager.App;
using TierManager.App;

namespace EngineGrowth.Core;

public sealed record GrowthCaveReservation(uint Address, int Length, string Owner);

/// <summary>Old TMR1/TIER1 markers own their full recorded cave, not only used code.</summary>
public static class GrowthCaveAllocator
{
    public static IReadOnlyList<GrowthCaveReservation> ReadReservations(PeFile pe, GameBase game, TierProfile tiers)
    {
        var result = new List<GrowthCaveReservation>();
        if (game.IsPatched && game.Cave is { } cave)
            result.Add(new(cave.Address, cave.Length, "质变"));
        // Include every valid tier marker, including legacy full-cave reservations.
        foreach (var section in pe.Sections.Where(s => s.IsExecutable))
        {
            int start = pe.VaToOffset(section.VirtualAddress);
            int length = (int)Math.Min(section.RawSize, (uint)(pe.Bytes.Length - start));
            for (int i = 0; i + 5 <= length; i++)
            {
                if (!pe.Bytes.AsSpan(start + i, 5).SequenceEqual("TIER1"u8)) continue;
                if (new MarkerReader(pe, section.VirtualAddress + (uint)i).TryRead(out var marker))
                    result.Add(new(marker.CaveVa, checked((int)marker.CaveLength), "档位"));
            }
        }
        if (tiers.HasMarker && !result.Any(r => r.Owner == "档位" && r.Address == tiers.CaveVa))
            result.Add(new(tiers.CaveVa, tiers.CaveLength, "档位"));
        return result.Distinct().ToArray();
    }

    public static void ValidateReservations(PeFile pe, IReadOnlyList<GrowthCaveReservation> reserved)
    {
        foreach (var item in reserved)
        {
            if (item.Length <= 0 || !pe.TryVaToOffset(item.Address, out int start) ||
                start < 0 || item.Length > pe.Bytes.Length - start ||
                !pe.SectionOf(item.Address).IsExecutable ||
                (ulong)item.Address + (uint)item.Length > (ulong)pe.SectionOf(item.Address).VirtualAddress + pe.SectionOf(item.Address).RawSize)
                throw new InvalidDataException($"{item.Owner}记录的代码洞无效，停止写入。");
        }
        for (int i = 0; i < reserved.Count; i++)
            for (int j = i + 1; j < reserved.Count; j++)
                if (Overlaps(reserved[i].Address, reserved[i].Length, reserved[j].Address, reserved[j].Length))
                    throw new InvalidDataException("既有质变/档位补丁的代码洞所有权重叠，请恢复安装前备份后重新设置；不会覆盖现有补丁。");
    }

    public static GrowthCaveReservation Allocate(PeFile pe, IReadOnlyList<GrowthCaveReservation> reserved,
        int length, string owner)
        => FindAvailable(pe, reserved, length, owner)
            ?? throw new InvalidDataException($"没有可供{owner}使用的 {length} 字节独立安全代码洞；已有补丁保留整段所有权，不能覆盖。");

    public static GrowthCaveReservation? FindAvailable(PeFile pe, IReadOnlyList<GrowthCaveReservation> reserved,
        int length, string owner)
    {
        ValidateReservations(pe, reserved);
        var absolute = new List<uint>();
        var sections = pe.Sections.Where(s => s.IsExecutable).ToArray();
        // Treat address-shaped data anywhere in the PE as an absolute reference.
        for (int i = 0; i + 4 <= pe.Bytes.Length; i++)
        {
            uint value = BitConverter.ToUInt32(pe.Bytes, i);
            if (sections.Any(s => value >= s.VirtualAddress && value < (ulong)s.VirtualAddress + s.RawSize)) absolute.Add(value);
        }
        var targets = new List<uint>();
        foreach (var section in sections)
        {
            var decoder = Decoder.Create(32, new ByteArrayCodeReader(pe.Bytes,
                (int)section.RawPointer, (int)section.RawSize), section.VirtualAddress, DecoderOptions.None);
            ulong end = (ulong)section.VirtualAddress + section.RawSize;
            while (decoder.IP < end)
            {
                var ins = decoder.Decode();
                if (ins.Length == 0) break;
                if (ins.IsInvalid || ins.FlowControl is not (FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Call)) continue;
                for (int op = 0; op < ins.OpCount; op++)
                    if (ins.GetOpKind(op) is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                        targets.Add((uint)ins.NearBranchTarget);
            }
        }
        absolute.AddRange(targets); absolute.Sort();
        bool Referenced(uint va, int count)
        {
            int index = absolute.BinarySearch(va); if (index < 0) index = ~index;
            return index < absolute.Count && absolute[index] < (ulong)va + (uint)count;
        }
        GrowthCaveReservation? best = null;
        int bestRun = 0;
        foreach (var section in sections)
        {
            int start = (int)section.RawPointer;
            int count = Math.Min((int)section.RawSize, pe.Bytes.Length - start);
            int i = 0;
            while (i < count)
            {
                uint va = section.VirtualAddress + (uint)i;
                var occupied = reserved.FirstOrDefault(r => va >= r.Address && va < (ulong)r.Address + (uint)r.Length);
                if (occupied != null) { i = (int)Math.Min(count, (long)occupied.Address + occupied.Length - section.VirtualAddress); continue; }
                byte fill = pe.Bytes[start + i];
                if (fill is not (0x00 or 0x90 or 0xCC)) { i++; continue; }
                int first = i++;
                while (i < count && pe.Bytes[start + i] == fill &&
                    !reserved.Any(r => section.VirtualAddress + (uint)i >= r.Address && section.VirtualAddress + (uint)i < (ulong)r.Address + (uint)r.Length)) i++;
                int run = i - first;
                uint address = section.VirtualAddress + (uint)first;
                if (run >= length && run > bestRun && !Referenced(address, run))
                { best = new(address, length, owner); bestRun = run; }
            }
        }
        return best;
    }

    private static bool Overlaps(uint a, int alen, uint b, int blen)
        => a < (ulong)b + (uint)blen && b < (ulong)a + (uint)alen;
}
