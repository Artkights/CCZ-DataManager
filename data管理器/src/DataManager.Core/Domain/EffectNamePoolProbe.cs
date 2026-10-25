using System.Buffers.Binary;
using DataManager.Core.Binary;

namespace DataManager.Core.Domain;

/// <summary>
/// Locates the native personal/job effect names using two independent executable
/// consumers. This read-only probe never treats an overlay or a guessed offset as a pool.
/// </summary>
public sealed class EffectNamePoolProbe
{
    private sealed record Section(uint Rva, uint RawStart, uint MappedRawSize, bool Executable);
    private static readonly byte?[] ConsumerA = Pattern("55 8B EC 83 EC 40 8B 45 08 C1 E0 04 05 ?? ?? ?? ?? 52 50 8D 45 C0 50 E8 ?? ?? ?? ??");
    private static readonly byte?[] ConsumerB = Pattern("0F B6 40 07 6B C0 10 05 ?? ?? ?? ?? 50 8D 55 D0 52 E8 ?? ?? ?? ??");

    public EffectNamePoolProbeResult Inspect(string filePath)
    {
        if (!File.Exists(filePath)) return Failed("未找到项目 EXE，特技名称使用数字 ID。", string.Empty);
        var count = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath).FilePrivatePart == 4 ? 180 : 255;
        return Inspect(File.ReadAllBytes(filePath), count);
    }

    public EffectNamePoolProbeResult Inspect(byte[] bytes, int rowCount = 255)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var hash = HashService.Sha256(bytes);
        if (!TrySections(bytes, out var imageBase, out var sections))
            return Failed("项目 EXE 的 PE 映射无效，特技名称使用数字 ID。", hash);
        var first = Find(bytes, sections, ConsumerA, 13);
        var second = Find(bytes, sections, ConsumerB, 8);
        if (first.Count != 1 || second.Count != 1 || first[0].Address != second[0].Address)
            return Failed($"未找到唯一一致的两个特技名称消费者（A={first.Count}，B={second.Count}）；特技名称使用数字 ID。", hash);
        var va = (ulong)first[0].Address;
        if (rowCount is not (180 or 255)) throw new ArgumentOutOfRangeException(nameof(rowCount));
        var poolLength = rowCount * 16;
        if (va < imageBase || va - imageBase > uint.MaxValue)
            return Failed("特技名称地址不在项目 PE 映射内，使用数字 ID。", hash);
        var rva = va - imageBase;
        var mapped = sections.Where(section => rva >= section.Rva &&
            rva - section.Rva + (ulong)poolLength <= section.MappedRawSize).ToArray();
        if (mapped.Length != 1)
            return Failed("特技名称池未完整落在唯一 PE 节的文件映射内，使用数字 ID。", hash);
        var offset = checked((long)mapped[0].RawStart + (long)(rva - mapped[0].Rva));
        if (offset < 0 || offset + poolLength > bytes.LongLength)
            return Failed("特技名称池超出原始文件，使用数字 ID。", hash);
        var names = new Dictionary<int, string>();
        for (var id = 0; id < rowCount; id++)
        {
            var raw = bytes.AsSpan(checked((int)offset + id * 16), 16);
            var end = raw.IndexOf((byte)0);
            if (end < 0) return Failed($"特技名称槽 {id} 缺少 NUL 结束符，使用数字 ID。", hash);
            string name;
            try { name = EncodingService.GameEncoding.GetString(raw[..end]); }
            catch (System.Text.DecoderFallbackException)
            { return Failed($"特技名称槽 {id} 含无效 GBK，使用数字 ID。", hash); }
            if (name.Any(char.IsControl)) return Failed($"特技名称槽 {id} 含控制字符，使用数字 ID。", hash);
            names[id] = name;
        }
        return new(true, names,
            $"特技名称：两个独立消费者 +0x{first[0].Offset:X}/+0x{second[0].Offset:X} → VA 0x{va:X} → 文件 +0x{offset:X}；{rowCount}×16 字节，严格 GBK；EXE SHA-256 {hash}。",
            hash, offset, va);
    }

    private static EffectNamePoolProbeResult Failed(string message, string hash)
        => new(false, new Dictionary<int, string>(), message, hash);

    private static List<(int Offset, uint Address)> Find(byte[] bytes, IEnumerable<Section> sections, byte?[] pattern, int addressOffset)
    {
        var result = new List<(int, uint)>();
        foreach (var section in sections.Where(section => section.Executable))
        {
            var start = checked((int)section.RawStart);
            var end = checked(start + (int)section.MappedRawSize);
            for (var offset = start; offset <= end - pattern.Length; offset++)
            {
                var match = true;
                for (var index = 0; index < pattern.Length; index++)
                    if (pattern[index] is { } expected && bytes[offset + index] != expected) { match = false; break; }
                if (match) result.Add((offset, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + addressOffset, 4))));
            }
        }
        return result;
    }

    private static bool TrySections(byte[] bytes, out ulong imageBase, out List<Section> sections)
    {
        imageBase = 0; sections = [];
        if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') return false;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60, 4));
        if (pe < 0 || pe > bytes.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe, 4)) != 0x4550) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6, 2));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20, 2));
        var optional = (long)pe + 24;
        var sectionStart = optional + optionalSize;
        if (optionalSize < 32 || sectionStart + count * 40L > bytes.LongLength) return false;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)optional, 2));
        if (magic == 0x10B) imageBase = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)optional + 28, 4));
        else if (magic == 0x20B) imageBase = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)optional + 24, 8));
        else return false;
        for (var index = 0; index < count; index++)
        {
            var raw = bytes.AsSpan(checked((int)sectionStart + index * 40), 40);
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
            var rva = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]);
            var rawStart = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[36..]);
            if ((ulong)rawStart + rawSize > (ulong)bytes.LongLength) return false;
            var mappedSize = virtualSize == 0 ? rawSize : Math.Min(rawSize, virtualSize);
            sections.Add(new(rva, rawStart, mappedSize, (flags & 0x20000000) != 0));
        }
        return sections.Count > 0;
    }

    private static byte?[] Pattern(string value)
        => value.Split(' ').Select(part => part == "??" ? (byte?)null : Convert.ToByte(part, 16)).ToArray();
}
