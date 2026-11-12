using System.Security.Cryptography;
using System.Text;

namespace RangeManager.App;

public enum EngineVersion
{
    V64,
    V65,
    V66
}

/// <summary>
/// One detected game base. The tool writes only Ekd5.exe: the job penetration
/// table at 0x407881 and the range shape library at 0x4D1400. Data.e5 is not
/// opened, validated or modified.
/// </summary>
public sealed class GameBase : IDisposable
{
    static GameBase()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public const uint PenetrationTableVa = 0x00407881;
    public const uint RangeShapeTableVa = 0x004D1400;
    public const int RangeShapeCount = 36;
    public const int JobCount = 80;
    public const int JobNameOffset = 0xD18D0;
    public const int JobNameStride = 9;

    public string RootPath { get; }
    public string ExePath => Path.Combine(RootPath, "Ekd5.exe");
    public PeFile Exe { get; }
    public EngineVersion Version { get; private set; }
    public string VersionText => Version switch
    {
        EngineVersion.V64 => "6.4",
        EngineVersion.V65 => "6.5",
        EngineVersion.V66 => "6.6",
        _ => "?"
    };

    public uint PenetrationLoadVa { get; private set; }
    public uint RangeDispatchVa { get; private set; }
    public int MaxPhysicalShapeId => Version switch
    {
        EngineVersion.V64 => 9,
        EngineVersion.V65 => 0xB,
        EngineVersion.V66 => 0xA,
        _ => 0xB
    };

    public string[] JobNames { get; private set; } = Array.Empty<string>();
    public List<string> Diagnostics { get; } = new();

    public GameBase(string rootPath) : this(rootPath, null) { }

    public GameBase(string rootPath, byte[]? executableImage)
    {
        RootPath = System.IO.Path.GetFullPath(rootPath);
        if (executableImage == null && !File.Exists(ExePath)) throw new FileNotFoundException("找不到 Ekd5.exe", ExePath);
        Exe = executableImage == null ? new PeFile(ExePath) : new PeFile(ExePath, executableImage);
        Detect();
    }

    private void Detect()
    {
        Diagnostics.Clear();
        // The penetration loader is the strongest anchor: it exists in every
        // 6.4/6.5/6.6 build we support and names the table address directly.
        if (Exe.Match(0x0040786D, 0x8A, 0x80, 0x81, 0x78, 0x40, 0x00))
        {
            Version = EngineVersion.V66;
            PenetrationLoadVa = 0x0040786D;
            RangeDispatchVa = 0x0043659C;
        }
        else if (Exe.Match(0x00407877, 0x8A, 0x80, 0x81, 0x78, 0x40, 0x00))
        {
            PenetrationLoadVa = 0x00407877;
            Version = Exe.Match(0x00435D02, 0x09) ? EngineVersion.V64 : EngineVersion.V65;
            RangeDispatchVa = 0x004365A1;
        }
        else
        {
            throw new InvalidDataException("未找到兵种穿透表读取指令（0x407881），这不是受支持的 6.4/6.5/6.6 基底");
        }

        if (!Exe.Match(RangeDispatchVa, 0x8B, 0x14, 0x85, 0x00, 0x14, 0x4D, 0x00))
            throw new InvalidDataException($"未找到范围形状分派指令（0x{RangeDispatchVa:X8}），无法确认 0x4D1400 表");

        if (!Exe.TryVaToOffset(PenetrationTableVa, out _) || !Exe.TryVaToOffset(RangeShapeTableVa, out _))
            throw new InvalidDataException("穿透表/形状表不在 Ekd5.exe 的节映射中");

        var shapeSection = Exe.SectionOf(RangeShapeTableVa);
        if (!shapeSection.IsWritable)
            Diagnostics.Add($"警告：形状表所在节 {shapeSection.Name} 不可写，写入可能被系统拒绝");

        LoadJobNames();
        Diagnostics.Add($"基底：{RootPath}");
        Diagnostics.Add($"版本：{VersionText}（穿透读取 0x{PenetrationLoadVa:X8}，范围分派 0x{RangeDispatchVa:X8}）");
        Diagnostics.Add($"Ekd5.exe SHA-256：{Exe.OriginalSha256}");
        Diagnostics.Add($"兵种穿透表：VA 0x{PenetrationTableVa:X8} → 文件 0x{Exe.VaToOffset(PenetrationTableVa):X}（{JobCount} 行）");
        Diagnostics.Add($"范围形状表：VA 0x{RangeShapeTableVa:X8} → 文件 0x{Exe.VaToOffset(RangeShapeTableVa):X}（{RangeShapeCount} 项）");
        Diagnostics.Add($"兵种名称：文件 0x{JobNameOffset:X}，80×9 字节 GBK");
    }

    private void LoadJobNames()
    {
        var names = new string[JobCount];
        int off = JobNameOffset;
        if (off + JobCount * JobNameStride > Exe.Bytes.Length)
        {
            for (int i = 0; i < names.Length; i++) names[i] = $"兵种 {i:D2}";
        }
        else
        {
            for (int i = 0; i < names.Length; i++)
            {
                var slice = Exe.Bytes.AsSpan(off + i * JobNameStride, JobNameStride);
                int len = slice.IndexOf((byte)0);
                if (len < 0) len = slice.Length;
                string text;
                try { text = Encoding.GetEncoding(936).GetString(slice[..len]).Trim(); }
                catch { text = ""; }
                names[i] = string.IsNullOrWhiteSpace(text) ? $"兵种 {i:D2}" : text;
            }
        }
        JobNames = names;
    }

    public string ReadJobName(int id) => id >= 0 && id < JobNames.Length ? JobNames[id] : $"兵种 {id:D2}";

    public void SaveAll() => Exe.Save();

    public void ReloadAll() => Exe.Reload();

    public bool IsDirty => Exe.IsDirty;

    public void Dispose()
    {
        // In-memory only; nothing unmanaged to release.
    }
}
