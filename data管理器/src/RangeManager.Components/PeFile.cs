using System.Security.Cryptography;

namespace RangeManager.App;

/// <summary>
/// Minimal PE32 reader/writer. All edits happen on an in-memory copy; the
/// original file is only touched by <see cref="Save"/>, which writes a backup
/// first and replaces the file atomically.
/// </summary>
public sealed class PeFile
{
    public sealed record Section(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize, uint Characteristics)
    {
        public bool Contains(uint va) => va >= VirtualAddress && va < VirtualAddress + Math.Max(VirtualSize, RawSize);
        public bool IsWritable => (Characteristics & 0x80000000) != 0;
    }

    private byte[] _original;
    public string Path { get; }
    public byte[] Bytes { get; private set; }
    public IReadOnlyList<Section> Sections { get; private set; } = [];
    public uint ImageBase { get; private set; }
    public bool IsDirty { get; private set; }

    public PeFile(string path) : this(path, File.ReadAllBytes(path)) { }

    public PeFile(string path, byte[] image)
    {
        Path = path;
        _original = (byte[])image.Clone();
        Bytes = (byte[])_original.Clone();
        ParseHeaders();
    }

    private void ParseHeaders()
    {
        var pe = ReadU32(0x3C);
        if (ReadU16((int)pe) != 0x4550)
            throw new InvalidDataException($"{Path} 不是有效的 PE 文件");
        int sectionCount = ReadU16((int)pe + 6);
        int optionalSize = ReadU16((int)pe + 20);
        ImageBase = ReadU32((int)pe + 24 + 28);
        int header = (int)pe + 24 + optionalSize;
        var sections = new List<Section>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            int off = header + i * 40;
            string name = System.Text.Encoding.ASCII.GetString(Bytes, off, 8).TrimEnd('\0');
            sections.Add(new Section(name, ImageBase + ReadU32(off + 12), ReadU32(off + 8), ReadU32(off + 20), ReadU32(off + 16), ReadU32(off + 36)));
        }
        Sections = sections;
    }

    public bool TryVaToOffset(uint va, out int offset)
    {
        foreach (var section in Sections)
        {
            if (!section.Contains(va)) continue;
            uint delta = va - section.VirtualAddress;
            if (delta >= section.RawSize) break;
            offset = checked((int)(section.RawOffset + delta));
            if (offset >= 0 && offset < Bytes.Length) return true;
        }
        offset = -1;
        return false;
    }

    public int VaToOffset(uint va) => TryVaToOffset(va, out var offset)
        ? offset
        : throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X8} 不在 {System.IO.Path.GetFileName(Path)} 的任何节中");

    public Section SectionOf(uint va) => Sections.FirstOrDefault(s => s.Contains(va))
        ?? throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X8} 没有对应节");

    public byte ReadByte(uint va) => Bytes[VaToOffset(va)];
    public void WriteByte(uint va, byte value)
    {
        Bytes[VaToOffset(va)] = value;
        IsDirty = true;
    }

    public ushort ReadU16(uint va)
    {
        int off = VaToOffset(va);
        return (ushort)(Bytes[off] | Bytes[off + 1] << 8);
    }

    public uint ReadU32(uint va)
    {
        int off = VaToOffset(va);
        return (uint)(Bytes[off] | Bytes[off + 1] << 8 | Bytes[off + 2] << 16 | Bytes[off + 3] << 24);
    }

    public void WriteU16(uint va, ushort value)
    {
        int off = VaToOffset(va);
        Bytes[off] = (byte)value;
        Bytes[off + 1] = (byte)(value >> 8);
        IsDirty = true;
    }

    public void WriteU32(uint va, uint value)
    {
        int off = VaToOffset(va);
        Bytes[off] = (byte)value;
        Bytes[off + 1] = (byte)(value >> 8);
        Bytes[off + 2] = (byte)(value >> 16);
        Bytes[off + 3] = (byte)(value >> 24);
        IsDirty = true;
    }

    public void WriteBytes(uint va, ReadOnlySpan<byte> data)
    {
        int off = VaToOffset(va);
        data.CopyTo(Bytes.AsSpan(off));
        IsDirty = true;
    }

    public bool Match(uint va, params byte[] pattern)
    {
        if (!TryVaToOffset(va, out int off)) return false;
        if (off + pattern.Length > Bytes.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (Bytes[off + i] != pattern[i]) return false;
        return true;
    }

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes));
    public string OriginalSha256 => Convert.ToHexString(SHA256.HashData(_original));
    public string DiskSha256 => File.Exists(Path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path))) : "";

    private ushort ReadU16(int offset) => (ushort)(Bytes[offset] | Bytes[offset + 1] << 8);
    private uint ReadU32(int offset) => (uint)(Bytes[offset] | Bytes[offset + 1] << 8 | Bytes[offset + 2] << 16 | Bytes[offset + 3] << 24);

    /// <summary>Writes the in-memory image back, keeping one timestamped backup.</summary>
    public void Save()
    {
        if (!IsDirty) return;
        string backup = Path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        if (File.Exists(backup)) backup += "-" + Guid.NewGuid().ToString("N")[..6];
        File.Copy(Path, backup, overwrite: false);
        string temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, Bytes);
        File.Move(temp, Path, overwrite: true);
        IsDirty = false;
    }

    public void Reload()
    {
        var disk = File.ReadAllBytes(Path);
        Bytes = (byte[])disk.Clone();
        _original = (byte[])disk.Clone();
        ParseHeaders();
        IsDirty = false;
    }

    public void ReplaceImage(byte[] image)
    {
        Bytes = (byte[])image.Clone();
        ParseHeaders();
        IsDirty = true;
    }
}
