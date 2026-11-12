namespace TierManager.App;

public enum TierPathKind
{
    Direct,
    Mask,
    Table,
    Correction,
    TableCorrection,
    Unknown
}

public sealed class TierEntry
{
    public int Ordinal { get; set; }
    public byte? Threshold { get; set; }
    public string Label { get; set; } = "";
    public byte OriginalReturn { get; set; }
    public bool IsBaseTier => Threshold == null;
}

public sealed class TierProfile
{
    // 显示函数使用 8 字节栈缓冲区拼接“旧字母↑新字母”，箭头占 2 字节，
    // 因此单个字母最多 2 个 GBK 字节（1 个汉字），否则会写坏栈。
    public const int MaxLabelBytes = 2;

    public string RootPath { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string ExeSha256 { get; init; } = "";
    public TierPathKind Kind { get; set; }
    public string KindText => Kind switch
    {
        TierPathKind.Direct => "直接阈值",
        TierPathKind.Mask => "6.5 阈值+强化成长掩码",
        TierPathKind.Table => "6.6 阈值表",
        TierPathKind.Correction => "6.6 阈值+修正",
        TierPathKind.TableCorrection => "6.6 阈值表+修正",
        _ => "未识别"
    };

    public uint TierFuncVa { get; set; }
    public uint OriginalTierFuncVa { get; set; }
    public uint OriginalTableVa { get; set; }
    public uint DisplayPointerVa { get; set; }
    public uint DisplayTableVa { get; set; }
    public int DisplayIndexOffset { get; set; }
    public uint? MaskTarget { get; set; }
    public bool InlineMask { get; set; }
    public byte[] MaskSequence { get; set; } = [];
    public uint? CorrectionTarget { get; set; }
    public uint? CorrectionHelper { get; set; }
    public bool UnsignedThresholds { get; set; }
    public int TierBase { get; set; } = 1;
    public List<uint> CallSites { get; } = [];
    public List<byte> Thresholds { get; } = [];
    public List<string> Labels { get; } = [];
    public List<TierEntry> Entries { get; } = [];
    public uint CaveVa { get; set; }
    public int CaveLength { get; set; }
    public bool HasMarker { get; set; }
    public uint MarkerVa { get; set; }
    public string ReadOnlyReason { get; set; } = "";
    public bool CanWrite => string.IsNullOrEmpty(ReadOnlyReason) && CaveLength >= 768 && CallSites.Count > 0;
    public bool CanAddDelete => CanWrite;
    public int TierCount => Entries.Count;

    public void RebuildEntries()
    {
        Entries.Clear();
        for (int i = 0; i <= Thresholds.Count; i++)
        {
            Entries.Add(new TierEntry
            {
                Ordinal = TierBase + i,
                Threshold = i == 0 ? null : Thresholds[i - 1],
                Label = i < Labels.Count ? Labels[i] : "?",
                OriginalReturn = (byte)Math.Clamp(TierBase + i, 0, 255)
            });
        }
    }

    public string Summary =>
        $"{KindText} · {TierCount} 档 · 基准 {TierBase} · 阈值 " +
        (Thresholds.Count == 0 ? "—" : string.Join("/", Thresholds)) +
        $" · 字母 {string.Join("/", Labels)}";
}
