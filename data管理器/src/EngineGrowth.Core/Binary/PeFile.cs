using System.Security.Cryptography;

namespace TransformManager.App;

public sealed class PeSection
{
    public required string Name { get; init; }
    public required uint VirtualAddress { get; init; }
    public required uint VirtualSize { get; init; }
    public required uint RawSize { get; init; }
    public required uint RawPointer { get; init; }
    public required uint Characteristics { get; init; }
    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
    public bool IsWritable => (Characteristics & 0x80000000) != 0;
}

/// <summary>Minimal PE reader/writer for Ekd5.exe. All edits stay in memory until saved by a transaction.</summary>
public sealed class PeFile
{
    public string Path { get; }
    public byte[] Bytes { get; }
    public string OriginalSha256 { get; }
    public IReadOnlyList<PeSection> Sections => _sections;

    private readonly List<PeSection> _sections = new();

    public PeFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Bytes = File.ReadAllBytes(Path);
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(Bytes));
        Parse();
    }

    /// <summary>Wraps an in-memory image destined for the same path (used to stage edits).</summary>
    public PeFile(string path, byte[] image)
    {
        Path = System.IO.Path.GetFullPath(path);
        Bytes = image;
        OriginalSha256 = Convert.ToHexString(SHA256.HashData(image));
        Parse();
    }

    private void Parse()
    {
        if (Bytes.Length < 0x40 || Bytes[0] != (byte)'M' || Bytes[1] != (byte)'Z')
            throw new InvalidDataException("不是有效的 PE 文件");
        int pe = BitConverter.ToInt32(Bytes, 0x3C);
        if (pe <= 0 || pe + 0x18 > Bytes.Length || BitConverter.ToUInt32(Bytes, pe) != 0x00004550)
            throw new InvalidDataException("PE 头无效");
        int coff = pe + 4;
        int sectionCount = BitConverter.ToUInt16(Bytes, coff + 2);
        int optionalSize = BitConverter.ToUInt16(Bytes, coff + 16);
        int optional = coff + 20;
        uint imageBase = BitConverter.ToUInt32(Bytes, optional + 28);
        int sectionTable = optional + optionalSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int offset = sectionTable + i * 40;
            if (offset + 40 > Bytes.Length) break;
            string name = System.Text.Encoding.ASCII.GetString(Bytes, offset, 8).TrimEnd('\0');
            _sections.Add(new PeSection
            {
                Name = name,
                VirtualSize = BitConverter.ToUInt32(Bytes, offset + 8),
                VirtualAddress = imageBase + BitConverter.ToUInt32(Bytes, offset + 12),
                RawSize = BitConverter.ToUInt32(Bytes, offset + 16),
                RawPointer = BitConverter.ToUInt32(Bytes, offset + 20),
                Characteristics = BitConverter.ToUInt32(Bytes, offset + 36)
            });
        }
        if (_sections.Count == 0) throw new InvalidDataException("PE 节表为空");
    }

    public PeSection SectionOf(uint va)
    {
        foreach (var section in _sections)
            if (va >= section.VirtualAddress && va < section.VirtualAddress + Math.Max(section.VirtualSize, section.RawSize))
                return section;
        throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X8} 不在任何节内");
    }

    public bool TryVaToOffset(uint va, out int offset)
    {
        try
        {
            var section = SectionOf(va);
            uint delta = va - section.VirtualAddress;
            if (delta >= section.RawSize)
            {
                offset = -1;
                return false;
            }
            offset = checked((int)(section.RawPointer + delta));
            return offset >= 0 && offset < Bytes.Length;
        }
        catch (ArgumentOutOfRangeException)
        {
            offset = -1;
            return false;
        }
    }

    public int VaToOffset(uint va) => TryVaToOffset(va, out int offset)
        ? offset : throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X8} 没有文件数据");

    public byte ReadByte(uint va) => Bytes[VaToOffset(va)];
    public ushort ReadUInt16(uint va) => BitConverter.ToUInt16(Bytes, VaToOffset(va));
    public uint ReadUInt32(uint va) => BitConverter.ToUInt32(Bytes, VaToOffset(va));

    public byte[] ReadBytes(uint va, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (!TryVaToOffset(va + (uint)i, out int offset)) return Array.Empty<byte>();
            result[i] = Bytes[offset];
        }
        return result;
    }

    public bool Match(uint va, params byte[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (!TryVaToOffset(va + (uint)i, out int offset) || Bytes[offset] != pattern[i]) return false;
        }
        return true;
    }

    public void Patch(uint va, IReadOnlyList<byte> patch)
    {
        for (int i = 0; i < patch.Count; i++)
        {
            int offset = VaToOffset(va + (uint)i);
            Bytes[offset] = patch[i];
        }
    }

    public IEnumerable<uint> ExecutableVAs()
    {
        foreach (var section in _sections)
        {
            if (!section.IsExecutable) continue;
            for (uint i = 0; i < section.RawSize; i++) yield return section.VirtualAddress + i;
        }
    }
}
