using System.Drawing;

namespace ImageManager.Core;

public enum ProjectVersion
{
    Unknown,
    Ccz65,
    Ccz66
}

public enum ImageCategory
{
    CharacterR,
    CharacterS,
    Portrait,
    ItemIcon,
    StrategyIcon,
    Scene,
    Range,
    Interface,
    Dynamic,
    HalfBody,
    StrategyAnimation,
    Discovered,
    Experimental
}

public enum ResourceKind
{
    E5Indexed,
    PmapSpecial,
    WeatherSpecial,
    DtAnimation,
    DllBitmap,
    RComposite,
    SComposite,
    FaceComposite,
    MapPair,
    McallAnimation,
    MarkRaw,
    Unknown
}

/// <summary>Logical namespace used by the S 形象 gallery.</summary>
public enum SImageMode
{
    Person,
    Job
}

[Flags]
public enum ImagePageCapabilities
{
    None = 0,
    Add = 1 << 0,
    Delete = 1 << 1,
    Replace = 1 << 2,
    Export = 1 << 3,
    PixelEdit = 1 << 4,
    Play = 1 << 5,
    AllFrames = 1 << 6,
    ReferenceAtlas = 1 << 7,
    BatchRename = 1 << 8,
    Documentation = 1 << 9,
    IdleFilter = 1 << 10,
    BackgroundToggle = 1 << 11,
    SPreviewSelector = 1 << 12,
    Standard = Add | Delete | Replace | Export | PixelEdit | BatchRename | Documentation,
    CharacterRs = Standard | Play | AllFrames | ReferenceAtlas | IdleFilter | BackgroundToggle,
    CharacterS = CharacterRs | SPreviewSelector,
    ItemIcon = Standard | BackgroundToggle
}

public enum MutationKind
{
    Append,
    Delete,
    Replace,
    PixelEdit,
    ImportReference,
    Rename,
    AssignmentSave,
    Undo,
    Redo,
    WholeFileReplace
}

public enum HistoryStatus
{
    Active,
    Undone,
    Conflict,
    Failed
}

public sealed record ProjectProfile(
    string RootPath,
    string DisplayName,
    ProjectVersion Version,
    bool IsReadOnly,
    IReadOnlyDictionary<string, string> ResourcePaths,
    IReadOnlyList<string> Diagnostics)
{
    public string VersionText => Version switch
    {
        ProjectVersion.Ccz65 => "6.5/6.5pl",
        ProjectVersion.Ccz66 => "6.6",
        _ => "未识别"
    };

    public string? TryGetResource(string key)
        => ResourcePaths.TryGetValue(key, out var path) ? path : null;
}

public sealed record ImageResourceDescriptor(
    string Key,
    string DisplayName,
    ImageCategory Category,
    ResourceKind Kind,
    IReadOnlyList<string> Paths,
    int EntryCount,
    bool CanPreview,
    bool CanMutate,
    bool IsExperimental,
    string Status,
    string Usage);

public sealed record FrameDescriptor(
    string SourceKey,
    string SourcePath,
    int ImageNumber,
    int FrameIndex,
    int Turn,
    string Action,
    string Direction,
    int Width,
    int Height,
    int SourceOffset = 0);

public sealed record LogicalImageAsset(
    string ResourceKey,
    int LogicalId,
    string DisplayName,
    ImageCategory Category,
    ResourceKind Kind,
    IReadOnlyList<FrameDescriptor> Frames,
    IReadOnlyList<string> ReferencedBy,
    bool IsFixedSlot,
    bool IsExperimental,
    string Status)
{
    public string StablePhysicalId => PhysicalIdentity ?? $"{ResourceKey}:{LogicalId}";
    public string? PhysicalIdentity { get; init; }
    public IReadOnlyList<EditableSourceSection> EditableSections { get; init; } = Array.Empty<EditableSourceSection>();
    public SLogicalAssetMapping? SMapping { get; init; }
    public int? PhysicalImageNumber { get; init; }
    public int? JobId { get; init; }
    /// <summary>Zero-based faction index: 0 allied, 1 friendly, 2 enemy.</summary>
    public int? FactionIndex { get; init; }
    /// <summary>
    /// S gallery modes distinguish job and general physical Unit ranges.
    /// Compact character assignment IDs are resolved separately.
    /// </summary>
    public SImageMode SMode { get; init; } = SImageMode.Person;
}

public sealed record EditableSourceSection(
    string Name,
    string SourceKey,
    string SourcePath,
    int ImageNumber,
    int Width,
    int Height,
    int FrameWidth,
    int FrameHeight,
    int FrameCount);

public sealed record DecodedFrame(FrameDescriptor Descriptor, Bitmap Bitmap, byte[]? PaletteIndexes = null, byte[]? PaletteBgra = null) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

public sealed record MutationFile(string TargetPath, byte[] CandidateBytes, string Description,
    bool DeleteTarget = false, bool AllowCreate = false)
{
    public MutationSourceSnapshot ExpectedSource { get; init; } = MutationSourceSnapshot.Capture(TargetPath);
}

public sealed record MutationPlan(
    string OperationId,
    MutationKind Kind,
    string Summary,
    IReadOnlyList<MutationFile> Files,
    IReadOnlyList<string> Warnings,
    bool RequiresRiskConfirmation,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, MutationSourceSnapshot>? SourceSnapshots = null);

public sealed record MutationResult(
    string OperationId,
    bool Success,
    string Summary,
    string? HistoryId,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> Diagnostics);

public sealed class ReferenceAtlasSection
{
    public string Name { get; set; } = string.Empty;
    public int Turn { get; set; }
    public string Action { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed record ReferenceAtlasPlacement(DecodedFrame Frame, Rectangle Bounds);

public sealed record ReferenceAtlasLayoutPlan(
    int CanvasSize,
    string LayoutId,
    int Gutter,
    int TurnCount,
    IReadOnlyList<ReferenceAtlasPlacement> Placements,
    IReadOnlyList<ReferenceAtlasSection> Sections);

public sealed record ReferenceAtlasExportResult(
    string PngPath,
    string TransparentPngPath,
    string PromptPath);

public enum AtlasResamplingKernel
{
    Area,
    NearestNeighbor,
    Lanczos3
}

public enum FrameAlignmentMode
{
    None,
    AlignToReferenceOccupancy
}

public enum AtlasBackgroundKind
{
    Transparent,
    KeyColor,
    OpaqueColor
}

public enum AtlasIssueSeverity
{
    Information,
    Warning,
    Error
}

public sealed record AtlasMaskStroke(bool Keep, Point Center, int Radius);

public sealed record AtlasFrameAdjustment(int OffsetX, int OffsetY, int ScalePercent)
{
    public static AtlasFrameAdjustment Default { get; } = new(0, 0, 100);
}

public sealed class AiAtlasNormalizationOptions
{
    public AtlasResamplingKernel Kernel { get; set; } = AtlasResamplingKernel.NearestNeighbor;
    public FrameAlignmentMode GeometryMode { get; set; } = FrameAlignmentMode.None;
    public int BackgroundTolerance { get; set; } = 12;
    public int AlphaThreshold { get; set; } = 128;
    public Point? BackgroundSample { get; set; }
    public bool OrderedDither { get; set; }
    public bool AutomaticQualityGate { get; set; }
    public List<AtlasMaskStroke> MaskStrokes { get; } = [];
    public Dictionary<string, AtlasFrameAdjustment> Adjustments { get; } = new(StringComparer.Ordinal);

    public AiAtlasNormalizationOptions Clone()
    {
        var clone = new AiAtlasNormalizationOptions
        {
            Kernel = Kernel,
            GeometryMode = GeometryMode,
            BackgroundTolerance = BackgroundTolerance,
            AlphaThreshold = AlphaThreshold,
            BackgroundSample = BackgroundSample,
            OrderedDither = OrderedDither,
            AutomaticQualityGate = AutomaticQualityGate
        };
        clone.MaskStrokes.AddRange(MaskStrokes);
        foreach (var pair in Adjustments) clone.Adjustments[pair.Key] = pair.Value;
        return clone;
    }
}

public sealed record AiAtlasFrameDiagnostic(
    string FrameKey,
    FrameDescriptor Descriptor,
    Rectangle SourceBounds,
    Rectangle TargetBounds,
    double Confidence,
    AtlasIssueSeverity Severity,
    string Message)
{
    public Rectangle ReferenceForegroundBounds { get; init; }
    public Rectangle AiForegroundBounds { get; init; }
    public Rectangle AlignedForegroundBounds { get; init; }
    public double Scale { get; init; } = 1;
    public double BaselineOffset { get; init; }
    public double ForegroundAreaRatio { get; init; }
    public double SilhouetteIoU { get; init; }
    public double ColorError { get; init; }
    public double AiColorError { get; init; }
    public int KeyPixelCount { get; init; }
    public int NonKeyPixelCount { get; init; }
    public bool IsClipped { get; init; }
    public bool RequiresManualReview { get; init; }
}

public sealed record FrameGeometryProfile(
    Rectangle ForegroundBounds,
    PointF Center,
    float Baseline,
    double Occupancy,
    bool IncludesEffect,
    Rectangle SafeBounds);

public sealed record FrameColorQuantizationReport(
    int KeyPixelCount,
    int NonKeyPixelCount,
    int PaletteEntriesUsed,
    double AverageColorError,
    double MaximumColorError);

public sealed class AiAtlasFrameComparison : IDisposable
{
    public required FrameDescriptor Descriptor { get; init; }
    public required Bitmap ReferenceFrame { get; init; }
    public required Bitmap RawAiFrame { get; init; }
    public required Bitmap AlignedAiFrame { get; init; }
    public required Bitmap FinalCczFrame { get; init; }

    public void Dispose()
    {
        ReferenceFrame.Dispose();
        RawAiFrame.Dispose();
        AlignedAiFrame.Dispose();
        FinalCczFrame.Dispose();
    }
}

public sealed record AiAtlasPalettePreview(
    FrameColorQuantizationReport Report,
    IReadOnlyList<Color> SourceColors,
    IReadOnlyList<Color> TargetPalette);

public sealed class AiAtlasFinalFrame : IDisposable
{
    public required DecodedFrame Frame { get; init; }
    public required FrameColorQuantizationReport Quantization { get; init; }
    public void Dispose() => Frame.Dispose();
}

public sealed class AiAtlasNormalizationResult : IDisposable
{
    public required string SourcePath { get; init; }
    public required Size SourceSize { get; init; }
    public required int TargetCanvasSize { get; init; }
    public required AtlasBackgroundKind BackgroundKind { get; init; }
    public required Color BackgroundColor { get; init; }
    public required IReadOnlyList<DecodedFrame> Frames { get; init; }
    public required IReadOnlyList<AiAtlasFrameDiagnostic> FrameDiagnostics { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
    public required Bitmap SourcePreview { get; init; }
    public required Bitmap MaskPreview { get; init; }
    public required Bitmap NormalizedAtlas { get; init; }
    public Bitmap? OriginalAtlasPreview { get; init; }
    public Bitmap? RawAiAtlasPreview { get; init; }
    public Bitmap? AlignedAiAtlasPreview { get; init; }
    public Bitmap? FinalCczAtlasPreview { get; init; }
    public IReadOnlyDictionary<string, FrameGeometryProfile> ReferenceGeometry { get; init; } =
        new Dictionary<string, FrameGeometryProfile>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, FrameColorQuantizationReport> QuantizationReports { get; init; } =
        new Dictionary<string, FrameColorQuantizationReport>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AiAtlasFrameComparison> FrameComparisons { get; init; } =
        new Dictionary<string, AiAtlasFrameComparison>(StringComparer.Ordinal);
    public bool HasBlockingIssues => FrameDiagnostics.Any(item => item.Severity == AtlasIssueSeverity.Error);
    public bool HasWarnings => FrameDiagnostics.Any(item => item.Severity == AtlasIssueSeverity.Warning) ||
                               BackgroundKind == AtlasBackgroundKind.OpaqueColor;
    public double ScaleFactor => TargetCanvasSize == 0 ? 0 : SourceSize.Width / (double)TargetCanvasSize;

    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
        SourcePreview.Dispose();
        MaskPreview.Dispose();
        NormalizedAtlas.Dispose();
        OriginalAtlasPreview?.Dispose();
        RawAiAtlasPreview?.Dispose();
        AlignedAiAtlasPreview?.Dispose();
        FinalCczAtlasPreview?.Dispose();
        foreach (var comparison in FrameComparisons.Values) comparison.Dispose();
    }
}

public sealed class HistoryTarget
{
    public FileAttributes? BeforeAttributes { get; set; }
    public FileAttributes? AfterAttributes { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public string AfterSnapshotPath { get; set; } = string.Empty;
    public string BeforeSha256 { get; set; } = string.Empty;
    public string AfterSha256 { get; set; } = string.Empty;
    public long BeforeLength { get; set; }
    public long AfterLength { get; set; }
    public bool BeforeExists { get; set; } = true;
    public bool AfterExists { get; set; } = true;
}

public sealed class HistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public MutationKind Kind { get; set; }
    public string Summary { get; set; } = string.Empty;
    public HistoryStatus Status { get; set; } = HistoryStatus.Active;
    public bool KeepForever { get; set; }
    public string? ParentId { get; set; }
    public List<HistoryTarget> Targets { get; set; } = [];
    public List<RenameHistoryTarget> RenameTargets { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public sealed record RenameRule(
    string Search,
    string Replacement,
    string Prefix,
    string Suffix,
    string? Template,
    bool UseRegex,
    bool IncludeDirectories);

public sealed record RenameItem(string SourcePath, string TargetPath, bool IsDirectory)
{
    public string SourceFingerprint { get; init; } = RenameTransaction.Fingerprint(SourcePath, IsDirectory);
    public string? ResolvedTargetPath { get; init; }
    public string PreviewTargetPath => ResolvedTargetPath ?? TargetPath;
}

public sealed class RenameHistoryTarget
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string BeforeFingerprint { get; set; } = string.Empty;
    public string AfterFingerprint { get; set; } = string.Empty;
}

public sealed record RenamePlan(
    string RootPath,
    IReadOnlyList<RenameItem> Items,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool CanApply => Items.Count > 0 && Errors.Count == 0;
}

public sealed record CharacterAssignment(int RowIndex, string Name, int JobId, int RId, int SId)
{
    public CharacterAssignment(int rowIndex, string name, int rId, int sId)
        : this(rowIndex, name, -1, rId, sId) { }
}

public sealed record CharacterAssignmentLayoutProfile(
    ProjectVersion Version,
    int ROffset,
    int SOffset,
    int RowCount,
    int FieldWidth,
    bool CanWrite,
    string Evidence)
{
    public int RequiredExecutableLength => Math.Max(ROffset, SOffset) + checked(RowCount * FieldWidth);

    public static CharacterAssignmentLayoutProfile For(ProjectVersion version) => version switch
    {
        ProjectVersion.Ccz65 => new(version, 0xE1000, 0xD2800, 1024, 2, true,
            "6.5/6.5pl 本地样本及旧形象指定器配置"),
        ProjectVersion.Ccz66 => new(version, 0xE1000, 0xD2800, 1024, 2, true,
            "六个本地 6.6 修正版项目逐表扫描"),
        _ => new(version, 0xE1000, 0xD2800, 1024, 2, false, "版本未识别")
    };
}

public sealed record SPhysicalMapping(int ImageNumber, bool SupportsRequestedTurn, int EffectiveTurn, string Reason);

public sealed record STurnPhysicalBinding(int Turn, int ImageNumber);

public sealed record SLogicalAssetMapping(
    int SId,
    bool IsThreeTurn,
    IReadOnlyList<STurnPhysicalBinding> Turns,
    int CommonPhysicalCount)
{
    public STurnPhysicalBinding ResolveTurn(int requestedTurn)
        => Turns.FirstOrDefault(item => item.Turn == requestedTurn) ?? Turns[0];
}

public enum SPreviewDirection
{
    Front,
    Side,
    Back
}

public sealed record SPreviewContext(int Turn, SPreviewDirection Direction)
{
    public static SPreviewContext Default { get; } = new(1, SPreviewDirection.Front);
}

public sealed record StrategyRow(int RowIndex, string Name, int IconId, int MeffId, int McallId);
