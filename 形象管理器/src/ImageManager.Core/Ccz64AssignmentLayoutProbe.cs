using System.Buffers.Binary;

namespace ImageManager.Core;

/// <summary>Proves the current 6.4 executable's assignment layout independently of its icons or filename.</summary>
public static class Ccz64AssignmentLayoutProbe
{
    private sealed record Section(uint Rva, uint Raw, uint Size, uint Flags);
    private static readonly byte?[] RConsumer = Pattern("55 8B EC 51 E8 ?? ?? ?? ?? 0F B7 04 45 ?? ?? ?? ?? 59 5D C3");
    private static readonly byte?[] PersonId = Pattern("55 8B EC 8B 45 08 2B 05 ?? ?? ?? ?? 99 33 C9 B1 48 F7 F9 5D C3");
    private static readonly byte?[] SPrefix = Pattern("55 8B EC 83 EC 08 81 7D 08 00 04 00 00 72 04 33 C9 EB 4D FF 75 08 E8 ?? ?? ?? ?? 89 4D F8 E8 ?? ?? ?? ?? 99 33 C9 B1 03 F7 F9 89 55 FC 8B 4D F8 E8 ?? ?? ?? ?? 89 45 F8");
    private static readonly byte?[] SConsumer = Pattern("8B 55 08 D1 E2 0F B7 82 ?? ?? ?? ?? 85 C0 74 18 83 F8 21 73 0C 48 6B C0 03 83 C0 50 03 45 FC EB 0C 05 8F 00 00 00");
    private static readonly byte?[] SSuffix = Pattern("EB 05 B8 FF FF 00 00 8B E5 5D C2 04 00");

    public static CharacterAssignmentLayoutProfile Inspect(byte[] executable)
    {
        var unavailable = CharacterAssignmentLayoutProfile.For(ProjectVersion.Ccz64);
        if (!TrySections(executable, out var imageBase, out var sections))
            return unavailable with { Evidence = "6.4 EXE 缺少完整且唯一的 PE32 节映射。" };
        var rHits = FindCode(executable, sections, RConsumer);
        var sHits = FindCode(executable, sections, SConsumer);
        if (rHits.Count != 1 || sHits.Count != 1)
            return unavailable with { Evidence = $"6.4 R/S 消费者不唯一（R={rHits.Count}，S={sHits.Count}），禁止读取继承偏移。" };
        var r = rHits[0];
        var s = sHits[0];
        var rVa = U32(executable, r + 13);
        var sVa = U32(executable, s + 8);
        var rSection = sections.Single(section => r >= section.Raw && r < section.Raw + (long)section.Size);
        var target = (long)imageBase + rSection.Rva + r - rSection.Raw + 9 +
            BinaryPrimitives.ReadInt32LittleEndian(executable.AsSpan(r + 5, 4));
        if (target < 0 || target > uint.MaxValue ||
            !TryMap(sections, imageBase, (uint)target, PersonId.Length, code: true, writable: false, out var idOffset) ||
            !Matches(executable, idOffset, PersonId) ||
            !TryMap(sections, imageBase, U32(executable, idOffset + 8), 4, code: false, writable: true, out _) ||
            !Matches(executable, s - SPrefix.Length, SPrefix) || !Matches(executable, s + SConsumer.Length, SSuffix))
            return unavailable with { Evidence = "6.4 人物 ID 指针差/72 或 S 的 ID<1024、0/33 分支完整函数未通过验签。" };
        if (!TryMap(sections, imageBase, rVa, 2048, code: false, writable: true, out var rOffset) ||
            !TryMap(sections, imageBase, sVa, 2048, code: false, writable: true, out var sOffset) ||
            rOffset < sOffset + 2048 && sOffset < rOffset + 2048)
            return unavailable with { Evidence = "6.4 R/S 的 1024×UInt16 区域不在唯一可写 PE 节内，或两表重叠。" };
        return new(ProjectVersion.Ccz64, rOffset, sOffset, 1024, 2, true,
            $"当前 6.4 EXE 完整消费者 R+0x{r:X}/S+0x{s:X}、人物指针差/72、ID<1024 与 0/33 分支已验签；" +
            $"VA 0x{rVa:X}/0x{sVa:X} 经 PE 唯一可写映射为 +0x{rOffset:X}/+0x{sOffset:X}，1024×UInt16。");
    }

    private static bool TrySections(byte[] bytes, out uint imageBase, out List<Section> sections)
    {
        imageBase = 0;
        sections = [];
        if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60, 4));
        if (pe < 0 || pe > bytes.Length - 24 || U32(bytes, pe) != 0x4550) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20, 2));
        var optional = pe + 24;
        if (optionalSize < 32 || optional > bytes.Length - optionalSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional, 2)) != 0x10B ||
            optional + (long)optionalSize + count * 40L > bytes.Length || count == 0 || count > 96) return false;
        imageBase = U32(bytes, optional + 28);
        for (var index = 0; index < count; index++)
        {
            var offset = optional + optionalSize + index * 40;
            var raw = U32(bytes, offset + 20);
            var rawSize = U32(bytes, offset + 16);
            var virtualSize = U32(bytes, offset + 8);
            if ((ulong)raw + rawSize > (ulong)bytes.Length) return false;
            sections.Add(new(U32(bytes, offset + 12), raw,
                virtualSize == 0 ? rawSize : Math.Min(rawSize, virtualSize), U32(bytes, offset + 36)));
        }
        var mappedSections = sections;
        return !mappedSections.Any(a => mappedSections.Any(b => !ReferenceEquals(a, b) && a.Size > 0 && b.Size > 0 &&
            (a.Raw < b.Raw + (long)b.Size && b.Raw < a.Raw + (long)a.Size ||
             a.Rva < b.Rva + (long)b.Size && b.Rva < a.Rva + (long)a.Size)));
    }

    private static bool TryMap(IEnumerable<Section> sections, uint imageBase, uint address, int length,
        bool code, bool writable, out int offset)
    {
        offset = 0;
        if (address < imageBase) return false;
        var relative = (long)address - imageBase;
        var matches = sections.Where(section => relative >= section.Rva &&
            relative + length <= section.Rva + (long)section.Size &&
            (!code || (section.Flags & 0x20000000) != 0) && (!writable || (section.Flags & 0x80000000) != 0)).ToArray();
        if (matches.Length != 1) return false;
        offset = checked((int)(matches[0].Raw + relative - matches[0].Rva));
        return true;
    }

    private static List<int> FindCode(byte[] bytes, IEnumerable<Section> sections, byte?[] pattern)
    {
        var hits = new List<int>();
        foreach (var section in sections.Where(section => (section.Flags & 0x20000000) != 0))
            for (var offset = (int)section.Raw; offset <= section.Raw + (long)section.Size - pattern.Length; offset++)
                if (Matches(bytes, offset, pattern)) hits.Add(offset);
        return hits;
    }

    private static bool Matches(byte[] bytes, int offset, byte?[] pattern)
    {
        if (offset < 0 || offset > bytes.Length - pattern.Length) return false;
        for (var index = 0; index < pattern.Length; index++)
            if (pattern[index] is { } value && bytes[offset + index] != value) return false;
        return true;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static byte?[] Pattern(string value)
        => value.Split(' ').Select(part => part == "??" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();
}
