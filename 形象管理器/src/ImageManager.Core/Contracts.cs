namespace ImageManager.Core;

public interface IProjectProfileService
{
    ProjectProfile Detect(string rootPath);
}

public interface IImageCatalogService
{
    IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project);
    IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource);
    IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset);
    DecodedFrame? DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => PreviewFrameDecoding.CopyMatching(() => DecodeFrames(project, asset), descriptor, cancellationToken);
}

public interface IResourceCodec
{
    ResourceKind Kind { get; }
    bool CanHandle(ImageResourceDescriptor resource);
    IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource);
    IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset);
    DecodedFrame? DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => PreviewFrameDecoding.CopyMatching(() => DecodeFrames(project, asset), descriptor, cancellationToken);
}

public interface IImageMutationService
{
    MutationPlan PlanAppend(ProjectProfile project, ImageResourceDescriptor resource, IReadOnlyList<string> sources);
    MutationPlan PlanReplace(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets, IReadOnlyList<string> sources);
    MutationPlan PlanReplaceSingleFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor, Bitmap edited,
        bool appendCopy = false);
    MutationPlan PlanDelete(ProjectProfile project, IReadOnlyList<LogicalImageAsset> assets);
    MutationPlan PlanWholeFileReplace(ProjectProfile project, string targetPath, string sourcePath);
    MutationPlan PlanItemIconEdit(ProjectProfile project, LogicalImageAsset asset, Bitmap large, bool appendCopy);
    MutationResult Commit(ProjectProfile project, MutationPlan plan);
}

public interface IReferenceAtlasService
{
    ReferenceAtlasExportResult Export(string destinationDirectory, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> frames, Color keyColor);
    AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options);
    ReferenceAtlasExportResult Export(string destinationDirectory, ReferenceAtlasContext context,
        Color keyColor, ReferenceAtlasExportOptions? options = null);
    AiAtlasNormalizationResult Normalize(string pngPath, ReferenceAtlasContext context,
        AiAtlasNormalizationOptions options, CancellationToken cancellationToken = default);
}

public interface IReferenceAtlasLayoutPlanner
{
    ReferenceAtlasLayoutPlan Plan(LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames);
}

public interface IAiAtlasNormalizer
{
    AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options);
    AiAtlasNormalizationResult Normalize(string pngPath, LogicalImageAsset asset,
        IReadOnlyList<DecodedFrame> referenceFrames, AiAtlasNormalizationOptions options,
        CancellationToken cancellationToken);
}

public interface IHistoryService
{
    string BackupRoot(ProjectProfile project);
    IReadOnlyList<HistoryRecord> List(ProjectProfile project);
    HistoryRecord Record(ProjectProfile project, MutationPlan plan, IReadOnlyList<HistoryTarget> targets);
    MutationResult Undo(ProjectProfile project, string historyId);
    MutationResult Redo(ProjectProfile project, string historyId);
    void SetKeepForever(ProjectProfile project, string historyId, bool keepForever);
}

public interface IBatchRenameService
{
    RenamePlan Preview(string rootPath, RenameRule rule);
    MutationResult Apply(ProjectProfile project, RenamePlan plan);
}

public interface ICharacterAssignmentService
{
    CharacterAssignmentLayoutProfile GetLayout(ProjectProfile project);
    IReadOnlyList<CharacterAssignment> Read(ProjectProfile project);
    MutationPlan PlanSave(ProjectProfile project, IReadOnlyList<CharacterAssignment> assignments);
}

public interface IStrategyRowService
{
    IReadOnlyList<StrategyRow> Read(ProjectProfile project);
}

public interface IThumbnailCache : IDisposable
{
    Bitmap? TryGetClone(string key);
    void Store(string key, Bitmap bitmap, string sourcePath);
    void InvalidatePath(string sourcePath);
    void Clear();
}

public interface IImagePreviewService : IDisposable
{
    Task<Bitmap?> GetThumbnailAsync(ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground,
        CancellationToken cancellationToken = default);
    void InvalidatePaths(IEnumerable<string> paths);
}

public interface IStrategyAnimationPackageCodec
{
    StrategyAnimationData Decode(ProjectProfile project, byte[] bytes);
    byte[] RebuildFramePlanes(ProjectProfile project, StrategyAnimationData package,
        IReadOnlyDictionary<int, Bitmap> replacementPlanes);
}
