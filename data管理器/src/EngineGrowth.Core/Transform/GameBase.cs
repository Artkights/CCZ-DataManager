using System.Text;
using Iced.Intel;

namespace TransformManager.App;

public enum EngineVersion { V64, V65, V66, Unknown }

public sealed record CaveInfo(uint Address, int Length, byte[] OriginalBytes);

/// <summary>Detects a supported 6.4/6.5/6.6 base and locates the treasure transform function and its tables.</summary>
public sealed class GameBase
{
    static GameBase() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string RootPath { get; }
    public string ExePath => Path.Combine(RootPath, "Ekd5.exe");
    public string DataPath => Path.Combine(RootPath, "Data.e5");
    public string StarPath => Path.Combine(RootPath, "Star.e5");
    public PeFile Exe { get; private set; } = null!;

    public EngineVersion Version { get; private set; } = EngineVersion.Unknown;
    public string VersionText => Version switch
    {
        EngineVersion.V64 => "6.4",
        EngineVersion.V65 => "6.5",
        EngineVersion.V66 => "6.6",
        _ => "未知"
    };

    public uint TransformEntryVa { get; private set; }
    public uint KeyReadVa { get; private set; }
    public uint CoverageTableVa { get; private set; }
    public int CoverageFirst { get; } = 0x1A;
    public int CoverageLast { get; private set; }
    public uint? ExtraTableVa { get; private set; }
    /// <summary>装备特效名称来源（EffectNamePoolService 的 Item 通道）。</summary>
    public string NameSource { get; private set; } = "";
    public long NameSourceOffset { get; private set; } = -1;
    public int NamePoolHits { get; private set; }
    public IReadOnlyList<int> NativeStages => _stages;
    public byte[] EntryOriginal { get; private set; } = Array.Empty<byte>();
    public CaveInfo? Cave { get; private set; }
    public string ReadOnlyReason { get; private set; } = "";
    public List<string> Diagnostics { get; } = new();

    public bool CanEdit => ReadOnlyReason.Length == 0;
    public bool IsPatched => _patchedCave != 0;

    private readonly List<int> _stages = new();

    private static readonly int[] RepresentativeEffects =
    [
        0x1F, 0x2F, 0x32, 0x33, 0x36, 0x42, 0x46, 0x55, 0x5C,
        0x64, 0x72, 0x76, 0x7C, 0x48, 0x6F, 0x31, 0x66, 0x6C
    ];

    public GameBase(string rootPath) : this(rootPath, File.ReadAllBytes(Path.Combine(rootPath, "Ekd5.exe"))) { }

    public GameBase(string rootPath, byte[] executableBytes)
    {
        RootPath = Path.GetFullPath(rootPath);
        Exe = new PeFile(ExePath, executableBytes);
        Detect();
    }

    internal void UseCave(CaveInfo cave)
    {
        Cave = cave;
        if (ReadOnlyReason.StartsWith("已安装规则的代码洞位于被数据引用的区域", StringComparison.Ordinal)
            && !HasAbsoluteReference(cave.Address, cave.Length)) ReadOnlyReason = "";
    }

    private void Detect()
    {
        Diagnostics.Clear();
        var code = Exe.Sections.FirstOrDefault(section => section.IsExecutable)
                   ?? throw new InvalidDataException("Ekd5.exe 没有可执行节");

        bool packed = Exe.Sections.Any(section => section.Name.Contains("VProtect", StringComparison.OrdinalIgnoreCase))
                      || code.VirtualSize > code.RawSize * 4;
        if (packed)
        {
            ReadOnlyReason = "当前基底是加壳版本，文件内容无法安全读取。";
            Diagnostics.Add($"保护壳：{code.Name} VSIZE=0x{code.VirtualSize:X} / RawSize=0x{code.RawSize:X}");
            return;
        }

        if (!TryLocateTransform(out uint entry, out uint keyRead))
        {
            ReadOnlyReason = "没有找到质变函数，可能是未知变体。";
            return;
        }
        TransformEntryVa = entry;
        KeyReadVa = keyRead;

        var window = Exe.ReadBytes(entry, 0x160);
        if (window.Length == 0)
        {
            ReadOnlyReason = "质变函数不在可读文件数据内。";
            return;
        }

        bool has66 = ContainsUInt32(window, 0x004CB5CA);
        bool has45623F = ContainsUInt32(window, 0x0046236F);
        if (has66)
        {
            Version = EngineVersion.V66;
            CoverageTableVa = 0x004CB5CA;
            CoverageLast = 0x87;
            ExtraTableVa = 0x004CB6BA;
        }
        else if (has45623F)
        {
            CoverageTableVa = 0x0046236F;
            CoverageLast = 0x7E;
            bool has65Gates = ContainsSequence(window, 0x80, 0xFA, 0x07) &&
                              ContainsSequence(window, 0x80, 0xFA, 0x0A);
            bool has64Gates = ContainsSequence(window, 0x80, 0xFA, 0x12) &&
                              ContainsSequence(window, 0x80, 0xFA, 0x0C);
            Version = has65Gates ? EngineVersion.V65
                : has64Gates ? EngineVersion.V64
                : EngineVersion.Unknown;
        }

        if (Version == EngineVersion.Unknown)
        {
            ReadOnlyReason = "这是未确认的变体基底（质变表位置不同），只能查看不能修改。";
        }

        EntryOriginal = Exe.ReadBytes(entry, 5);
        if (_patchedCave != 0)
        {
            var memory = Exe.ReadBytes(_patchedCave, 8192);
            var metadata = RuleCompiler.ReadMetadata(memory);
            if (metadata == null || Exe.ReadBytes(_patchedCave, metadata.CaveLength).Length != metadata.CaveLength)
            {
                ReadOnlyReason = "质变函数入口已被其他补丁占用，无法安全修改。";
            }
            else
            {
                EntryOriginal = metadata.Prologue.Take(5).ToArray();
                Cave = new CaveInfo(_patchedCave, metadata.CaveLength,
                    Enumerable.Repeat((byte)metadata.FillByte, metadata.CaveLength).ToArray());
                // 旧版曾把 6.6 规则装到被绝对指针引用的 0x457D7B。已安装的
                // 旧洞如果仍被数据引用，禁止继续写入，要求先从备份恢复。
                if (HasAbsoluteReference(_patchedCave, metadata.CaveLength))
                    ReadOnlyReason = "已安装规则的代码洞位于被数据引用的区域；请从 Ekd5.exe.bak-transform-* 恢复原文件后再重新写入。";
            }
        }
        else if (EntryOriginal.Length != 5 || EntryOriginal[0] != 0x55 || EntryOriginal[1] != 0x8B ||
                 EntryOriginal[2] != 0xEC || EntryOriginal[3] != 0x0F || EntryOriginal[4] != 0xB6)
        {
            ReadOnlyReason = "质变函数入口已被其他补丁占用，无法安全安装规则。";
        }

        DetectNamePool();
        DetectStages(window);
        FilterStages();
        if (Version != EngineVersion.Unknown && _patchedCave == 0)
            Cave = FindCave(code);

        Diagnostics.Add($"基底：{RootPath}");
        Diagnostics.Add($"版本：{VersionText}");
        Diagnostics.Add($"质变函数入口：VA 0x{TransformEntryVa:X8}（文件 0x{Exe.VaToOffset(TransformEntryVa):X}）");
        if (CoverageTableVa != 0)
            Diagnostics.Add($"覆盖表：VA 0x{CoverageTableVa:X8}（文件 0x{Exe.VaToOffset(CoverageTableVa):X}，0x{CoverageFirst:X2}..0x{CoverageLast:X2}）");
        if (ExtraTableVa is { } extra)
            Diagnostics.Add($"6.6 后缀类型表：VA 0x{extra:X8}（同时用于 ×1.5 门控，只读）");
        Diagnostics.Add($"装备特效名称：{NameSource}，代表特效号命中 {NamePoolHits}/18");
        Diagnostics.Add($"原生变化段：{(NativeStages.Count == 0 ? "未识别" : string.Join(" / ", NativeStages.Select(v => v + " 级")))}");
        Diagnostics.Add(Cave is { } cave
            ? $"代码洞：VA 0x{cave.Address:X8}，长度 {cave.Length} 字节"
            : "代码洞：未找到（自定义规则不可用）");
    }

    private uint _patchedCave;

    private bool TryLocateTransform(out uint entry, out uint keyRead)
    {
        entry = 0;
        keyRead = 0;
        _patchedCave = 0;
        var code = Exe.Sections.First(section => section.IsExecutable);
        var bytes = Exe.Bytes;
        int start = checked((int)code.RawPointer);
        int end = start + checked((int)code.RawSize) - 4;
        ReadOnlySpan<byte> needle = [0x0F, 0xB6, 0x40, 0xFE];
        for (int offset = start; offset <= end; offset++)
        {
            if (bytes[offset] != needle[0] || !bytes.AsSpan(offset, 4).SequenceEqual(needle)) continue;
            uint candidateKey = code.VirtualAddress + (uint)(offset - start);
            if (candidateKey < 11) continue;
            uint candidateEntry = candidateKey - 9;
            if (!Exe.Match(candidateEntry + 6, 0x0F, 0xB6, 0x08)) continue;
            if (!Exe.Match(candidateEntry + 9, 0x0F, 0xB6, 0x40, 0xFE)) continue;

            if (Exe.Match(candidateEntry, 0x55, 0x8B, 0xEC, 0x0F, 0xB6, 0xD2))
            {
                entry = candidateEntry;
                keyRead = candidateKey;
                return true;
            }
            if (Exe.Match(candidateEntry, 0xE9))
            {
                var relBytes = Exe.ReadBytes(candidateEntry + 1, 4);
                if (relBytes.Length != 4) continue;
                uint target = candidateEntry + 5 + unchecked((uint)BitConverter.ToInt32(relBytes, 0));
                if (!Exe.TryVaToOffset(target, out _)) continue;
                var metadata = RuleCompiler.ReadMetadata(Exe.ReadBytes(target, 8192));
                if (metadata == null ||
                    !metadata.Prologue.SequenceEqual(new byte[] { 0x55, 0x8B, 0xEC, 0x0F, 0xB6, 0xD2 }))
                    continue;
                _patchedCave = target;
                entry = candidateEntry;
                keyRead = candidateKey;
                return true;
            }
        }
        return false;
    }

    private readonly Dictionary<int, string> _effectNames = new();

    /// <summary>
    /// 与 data管理器 EffectNamePoolService 的 Item 通道一致：
    /// 6.4/6.5 读取 Ekd5.exe 的两段 GBK 变长名称池（0x8A9E8/0x9F800）；
    /// 6.6 读取 +0x9D9A0 起 230 个 16 字节固定槽，槽 0 对应特效号 0x1A。
    /// 兵种特效名称池（0x9E800 / 0xD0D60）不用于宝物特效。
    /// </summary>
    private void DetectNamePool()
    {
        _effectNames.Clear();
        if (Version is EngineVersion.V64 or EngineVersion.V65)
        {
            int low = ReadPackedNames(0x8A9E8, 885, 62, 0x1A);
            int high = ReadPackedNames(0x9F800, 368, 40, 0x58);
            NameSource = $"装备特效名称池（变长 0x8A9E8/0x9F800，{low}+{high} 槽）";
            NameSourceOffset = 0x8A9E8;
        }
        else if (Version == EngineVersion.V66)
        {
            int count = ReadFixedNames(0x9D9A0, 230, 0x1A, 16);
            NameSource = $"装备特效名称池（固定 16 字节 0x9D9A0，{count}/230 槽）";
            NameSourceOffset = 0x9D9A0;
        }
        else
        {
            NameSource = "未识别";
            NameSourceOffset = -1;
        }
        NamePoolHits = RepresentativeEffects.Count(effect => EffectName(effect).Length > 0);
    }

    private int ReadPackedNames(long fileOffset, int byteLength, int maximum, int firstEffect)
    {
        var bytes = Exe.Bytes;
        long end = fileOffset + byteLength;
        if (fileOffset < 0 || end > bytes.Length) return 0;
        int position = checked((int)fileOffset);
        int limit = checked((int)end);
        int count = 0;
        while (position < limit && count < maximum)
        {
            while (position < limit && bytes[position] == 0) position++;
            if (position >= limit) break;
            int start = position;
            while (position < limit && bytes[position] != 0) position++;
            if (position >= limit) break;
            string name = DecodeGbk(bytes, start, position - start);
            if (name.Length > 0) _effectNames[firstEffect + count] = name;
            count++;
            position++;
        }
        return count;
    }

    private int ReadFixedNames(long fileOffset, int slotCount, int firstEffect, int slotSize)
    {
        var bytes = Exe.Bytes;
        long end = fileOffset + (long)slotCount * slotSize;
        if (fileOffset < 0 || end > bytes.Length) return 0;
        int count = 0;
        for (int slot = 0; slot < slotCount; slot++)
        {
            int start = checked((int)(fileOffset + slot * slotSize));
            int terminator = Array.IndexOf(bytes, (byte)0, start, slotSize);
            int length = terminator < 0 ? slotSize - 1 : terminator - start;
            length = Math.Min(length, slotSize - 1);
            if (length <= 0) continue;
            string name = DecodeGbk(bytes, start, length);
            if (name.Length == 0) continue;
            _effectNames[firstEffect + slot] = name;
            count++;
        }
        return count;
    }

    private static string DecodeGbk(byte[] bytes, int start, int length)
    {
        try
        {
            string text = Encoding.GetEncoding(936).GetString(bytes, start, length).Trim();
            return text.Length > 0 && text.All(ch => ch >= 0x20) ? text : "";
        }
        catch
        {
            return "";
        }
    }

    public string EffectName(int effect)
        => _effectNames.TryGetValue(effect, out string? name) ? name : "";

    private void DetectStages(byte[] window)
    {
        _stages.Clear();
        for (int i = 0; i + 2 < window.Length; i++)
        {
            if (window[i] != 0x80 || window[i + 1] != 0xFA) continue;
            int level = window[i + 2];
            if (level is < 1 or > 99) continue;
            if (!_stages.Contains(level)) _stages.Add(level);
        }
    }

    private void FilterStages()
    {
        int[] expected = Version switch
        {
            EngineVersion.V65 => [7, 10],
            EngineVersion.V64 => [12, 18],
            EngineVersion.V66 => [16, 18],
            _ => []
        };
        if (expected.Length == 0) return;
        var kept = _stages.Where(expected.Contains).OrderBy(level => level).ToList();
        if (kept.Count == 0) return;
        _stages.Clear();
        _stages.AddRange(kept);
    }

    private CaveInfo? FindCave(PeSection code)
    {
        var targets = CollectBranchTargets(code);
        var bytes = Exe.Bytes;
        int start = checked((int)code.RawPointer);
        int end = start + checked((int)code.RawSize);
        CaveInfo? best = null;
        int index = start;
        while (index < end)
        {
            byte value = bytes[index];
            if (value is not (0x90 or 0xCC or 0x00))
            {
                index++;
                continue;
            }
            int runStart = index;
            while (index < end && bytes[index] == value) index++;
            int length = index - runStart;
            uint va = code.VirtualAddress + (uint)(runStart - start);
            if (length < 600) continue;
            bool referenced = targets.Any(target => target >= va && target < va + (uint)length);
            if (referenced) continue;
            // 代码洞必须是真正无人引用的填充区；除跳转目标外，还要排除被
            // 绝对地址/数据指针引用的区域，避免把数据表当成代码洞覆盖。
            if (HasAbsoluteReference(va, length)) continue;
            if (best == null || length > best.Length)
                best = new CaveInfo(va, length, bytes.AsSpan(runStart, length).ToArray());
        }
        return best;
    }

    private bool HasAbsoluteReference(uint start, int length)
    {
        var bytes = Exe.Bytes;
        uint end = start + (uint)length;
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            uint value = BitConverter.ToUInt32(bytes, i);
            if (value >= start && value < end) return true;
        }
        return false;
    }

    private HashSet<uint> CollectBranchTargets(PeSection code)
    {
        var targets = new HashSet<uint>();
        var reader = new ByteArrayCodeReader(Exe.Bytes, checked((int)code.RawPointer), checked((int)code.RawSize));
        var decoder = Iced.Intel.Decoder.Create(32, reader, code.VirtualAddress, DecoderOptions.None);
        var instruction = new Instruction();
        uint end = code.VirtualAddress + code.RawSize;
        while (decoder.IP < end)
        {
            instruction = decoder.Decode();
            if (instruction.Length == 0) break;
            if (instruction.IsInvalid) continue;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Call)
            {
                for (int i = 0; i < instruction.OpCount; i++)
                {
                    if (instruction.GetOpKind(i) == OpKind.NearBranch16 || instruction.GetOpKind(i) == OpKind.NearBranch32 ||
                        instruction.GetOpKind(i) == OpKind.NearBranch64)
                        targets.Add((uint)instruction.NearBranchTarget);
                }
            }
        }
        return targets;
    }

    private static bool ContainsUInt32(byte[] buffer, uint value)
    {
        var pattern = BitConverter.GetBytes(value);
        return buffer.AsSpan().IndexOf(pattern) >= 0;
    }

    private static bool ContainsSequence(byte[] buffer, params byte[] pattern)
        => buffer.AsSpan().IndexOf(pattern) >= 0;

    public byte ReadCoverage(int effect)
    {
        if (CoverageTableVa == 0 || effect < CoverageFirst || effect > CoverageLast) return 0xFE;
        return Exe.ReadByte(CoverageTableVa + (uint)(effect - CoverageFirst));
    }

    public byte ReadExtra(int effect)
    {
        if (ExtraTableVa is not { } table || effect < CoverageFirst || effect > CoverageLast) return 0;
        return Exe.ReadByte(table + (uint)(effect - CoverageFirst));
    }

    public string ReadItemsLayoutHint()
    {
        // 17-byte name layout keeps +0x0F/+0x10 at zero; 15-byte layout uses them for the icon word.
        var raw = File.ReadAllBytes(DataPath);
        const int blockOffset = 0x818C;
        int zeroTail = 0, total = 0;
        for (int id = 0; id < 104; id++)
        {
            int offset = blockOffset + id * 25;
            if (offset + 25 > raw.Length) break;
            total++;
            if (raw[offset + 0x0F] == 0 && raw[offset + 0x10] == 0) zeroTail++;
        }
        if (total == 0) return "空";
        return zeroTail * 10 >= total * 9 ? "17 字节名称（6.4/6.5 风格）" : "15 字节名称（6.6 风格）";
    }
}
