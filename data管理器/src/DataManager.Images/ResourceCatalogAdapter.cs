using System.Drawing;
using System.Security.Cryptography;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>
/// data 管理器对形象管理器的窄适配层。业务页只依赖本文件中的 DTO，
/// 资源编解码、E5 目录探测和帧解码继续由 ImageManager.Core 负责。
/// </summary>
public sealed partial class ResourceCatalogAdapter : IDisposable
{
    private readonly ProjectProfileService _profileService = new();
    private readonly ImageCatalogService _catalogService = new();
    // Profile detection walks the project root and BuildCatalog opens/probes
    // every archive. Business pages can ask for several previews while a
    // single entity is being bound, so keep a read-only catalog snapshot and
    // invalidate it when the files that make up the snapshot change. The
    // cache deliberately does not hold write authorisation or content hashes;
    // mutation methods always detect a fresh profile before committing.
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CatalogCache> _catalogCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImagePreviewService _previews = new();
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private long _revision;
    private bool _disposed;

    /// <summary>Read cache generation for callers that retain resource picker thumbnails.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Face 0 has eight expressions; all later faces share the resulting +8 physical image offset.</summary>
    public static int HalfBodyImageNumber(int faceId) => faceId < 0 ? -1 : faceId == 0 ? 1 : checked(faceId + 8);

    public ResourceCatalogSnapshot Scan(string rootPath)
    {
        return GetCatalog(rootPath).Snapshot;
    }

    public Task<ResourceCatalogSnapshot> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
        => Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return Scan(rootPath); }, cancellationToken);

    internal ProjectProfile GetProjectProfile(string rootPath) => GetCatalog(rootPath).Profile;
    internal IReadOnlyList<ImageResourceDescriptor> GetDescriptors(string rootPath)
        => GetCatalog(rootPath).Descriptors.Values.ToArray();
    internal IImageCatalogService CreateImageCatalogService() => new CachedImageCatalogService(this);

    public Bitmap? DecodeFirstFrame(string rootPath, string resourceKey, int logicalId, Size size)
        => DecodeFirstFrameAsync(rootPath, resourceKey, logicalId, size).GetAwaiter().GetResult();

    /// <summary>Returns a caller-owned thumbnail without probing the whole catalog on the UI thread.</summary>
    public async Task<Bitmap?> DecodeFirstFrameAsync(string rootPath, string resourceKey, int logicalId, Size size,
        CancellationToken cancellationToken = default)
    {
        var request = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = GetCatalog(rootPath);
            var descriptor = FindDescriptor(catalog, resourceKey);
            if (descriptor == null || !descriptor.CanPreview) return (Catalog: catalog, Asset: (LogicalImageAsset?)null);
            return (Catalog: catalog, Asset: GetAssets(catalog, descriptor).FirstOrDefault(item => item.LogicalId == logicalId));
        }, cancellationToken).ConfigureAwait(false);
        if (request.Asset == null || request.Asset.Frames.Count == 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var image = await _previews.GetThumbnailAsync(request.Catalog.Profile, CreateImageCatalogService(),
            request.Asset, request.Asset.Frames[0], size, true, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) { image?.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return image;
    }

    /// <summary>Returns independent decoded frames owned by the caller.</summary>
    public IReadOnlyList<Bitmap> DecodeFrames(string rootPath, string resourceKey, int logicalId)
    {
        var catalog = GetCatalog(rootPath);
        var profile = catalog.Profile;
        var descriptor = FindDescriptor(catalog, resourceKey);
        if (descriptor == null || !descriptor.CanPreview) return [];
        var asset = GetAssets(catalog, descriptor).FirstOrDefault(item => item.LogicalId == logicalId);
        if (asset == null) return [];
        var frames = DecodeAssetFrames(profile, asset);
        // DecodeAssetFrames creates caller-owned frames. Transfer their bitmaps
        // directly: cloning every frame here doubled the live pixel surfaces
        // and immediately disposed the originals without changing ownership.
        try { return frames.Select(frame => frame.Bitmap).ToArray(); }
        catch { foreach (var frame in frames) frame.Dispose(); throw; }
    }

    public Task<IReadOnlyList<Bitmap>> DecodeFramesAsync(string rootPath, string resourceKey, int logicalId,
        CancellationToken cancellationToken = default)
        => RunDecodeAsync(() => DecodeFrames(rootPath, resourceKey, logicalId),
            frames => { foreach (var frame in frames) frame.Dispose(); }, cancellationToken);

    public ResourceRangePreviewInfo ResolveRangePreview(string rootPath, bool attack, int fieldValue)
    {
        var key = attack ? "Hitarea.e5" : "Effarea.e5";
        var descriptor = FindDescriptor(GetCatalog(rootPath), key);
        var count = descriptor?.EntryCount ?? 0;
        var imageNumber = fieldValue < 0 || fieldValue == int.MaxValue ? -1 : fieldValue + 1;
        var valid = descriptor is { CanPreview: true } && imageNumber >= 1 && imageNumber <= count;
        var status = valid ? $"图号 {imageNumber}" : descriptor == null ? "未找到范围图片" :
            !descriptor.CanPreview ? descriptor.Status : $"无图（范围编号 {fieldValue}，共 {count} 张）";
        return new ResourceRangePreviewInfo(key, fieldValue, imageNumber, count, valid, status);
    }

    public async Task<Bitmap?> DecodeRangeFirstFrameAsync(string rootPath, bool attack, int fieldValue, Size size,
        CancellationToken cancellationToken = default)
    {
        var info = await Task.Run(() => ResolveRangePreview(rootPath, attack, fieldValue), cancellationToken).ConfigureAwait(false);
        if (!info.CanPreview) return null;
        // Generic E5 logical IDs are physical image numbers (one-based).
        // Range fields are zero-based and therefore need exactly one +1 here.
        return await DecodeFirstFrameAsync(rootPath, info.ResourceKey, info.PhysicalImageNumber, size,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Person assignments preview the first promotion's movement frame zero.</summary>
    public ResourcePreviewAnimation DecodeCharacterS(string rootPath, int sId, int jobId, int turn = 1)
    {
        var mapping = new SPhysicalMappingService().Resolve(sId, jobId, 1);
        var commonCount = BindingDescriptor(GetCatalog(rootPath), ResourceBindingKind.CharacterS)?.EntryCount ?? 0;
        var required = sId is >= 1 and <= 32 ? mapping.ImageNumber + 2 : mapping.ImageNumber;
        var incomplete = required > commonCount ? "；对应资源不完整" : string.Empty;
        var preview = DecodePhysicalSFirstFrame(rootPath, mapping.ImageNumber);
        return preview with { Mapping = $"人物 S{sId} / 兵种 {jobId} → {preview.Mapping}（{mapping.Reason}）{incomplete}" };
    }

    public Task<ResourcePreviewAnimation> DecodeCharacterSAsync(string rootPath, int sId, int jobId, int turn = 1,
        CancellationToken cancellationToken = default)
        => RunDecodeAsync(() => DecodeCharacterS(rootPath, sId, jobId, turn), animation => animation.Dispose(), cancellationToken);

    /// <summary>The job ID identifies the exact job; each faction owns a separate physical entry.</summary>
    public ResourcePreviewAnimation DecodeJobSFirstFrame(string rootPath, int jobId)
        => DecodeJobSFirstFrame(rootPath, jobId, 0);

    public ResourcePreviewAnimation DecodeJobSFirstFrame(string rootPath, int jobId, int faction)
    {
        if (jobId is < 0 or > 79) throw new ArgumentOutOfRangeException(nameof(jobId));
        if (faction is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(faction));
        var preview = DecodePhysicalSFirstFrame(rootPath, jobId * 3 + faction + 1);
        var name = new[] { "我军", "友军", "敌军" }[faction];
        return preview with { Mapping = $"兵种 {jobId} / {name} → {preview.Mapping}" };
    }

    public ResourcePreviewAnimation DecodePhysicalSFirstFrame(string rootPath, int imageNumber)
        => DecodePhysicalSFirstFrameAsync(rootPath, imageNumber).GetAwaiter().GetResult();

    private async Task<Bitmap?> DecodePhysicalSMovementFrameAsync(string rootPath, int imageNumber, Size bounds,
        CancellationToken cancellationToken)
    {
        if (imageNumber < 1) throw new ArgumentOutOfRangeException(nameof(imageNumber));
        var request = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = GetCatalog(rootPath).Profile;
            var path = profile.TryGetResource("Unit_mov.e5") ?? throw new FileNotFoundException("缺少 Unit_mov.e5。");
            EnsureUnambiguousPath(profile, path);
            var descriptor = new FrameDescriptor("Unit_mov.e5", path, imageNumber, 0, 1, "mov", "front", 48, 48);
            var asset = new LogicalImageAsset("SComposite", imageNumber, $"S_{imageNumber:000}", ImageCategory.CharacterS,
                ResourceKind.SComposite, [descriptor], [], false, false, string.Empty)
                { PhysicalIdentity = $"SComposite:Unit:{imageNumber}" };
            return (Profile: profile, Asset: asset, Descriptor: descriptor);
        }, cancellationToken).ConfigureAwait(false);
        var image = await _previews.GetThumbnailAsync(request.Profile, CreateImageCatalogService(), request.Asset,
            request.Descriptor, bounds, true, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) { image?.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return image;
    }

    public Task<ResourcePreviewAnimation> DecodeJobSFirstFrameAsync(string rootPath, int jobId,
        CancellationToken cancellationToken = default)
        => RunDecodeAsync(() => DecodeJobSFirstFrame(rootPath, jobId), animation => animation.Dispose(), cancellationToken);

    public Task<ResourcePreviewAnimation> DecodeJobSFirstFrameAsync(string rootPath, int jobId, int faction,
        CancellationToken cancellationToken = default)
        => RunDecodeAsync(() => DecodeJobSFirstFrame(rootPath, jobId, faction), animation => animation.Dispose(), cancellationToken);

    public async Task<ResourcePreviewAnimation> DecodePhysicalSFirstFrameAsync(string rootPath, int imageNumber,
        CancellationToken cancellationToken = default)
    {
        var frame = await DecodePhysicalSMovementFrameAsync(rootPath, imageNumber, new Size(48, 48), cancellationToken).ConfigureAwait(false);
        return new ResourcePreviewAnimation(frame == null ? [] : [frame], 160, $"Unit_mov #{imageNumber} 移动第0帧");
    }

    public ResourcePreviewAnimation DecodeStrategyAnimation(string rootPath, bool large, int fieldValue)
    {
        var profile = GetCatalog(rootPath).Profile;
        if (fieldValue == 255 || large && fieldValue < 100)
            return new ResourcePreviewAnimation([], 160, $"字段 {fieldValue}：无{(large ? "大" : "小")}动画");
        byte[] bytes;
        string description;
        if (!large)
        {
            // Meff is a strategy animation archive and its canonical location
            // is E5/Meff.e5.  Some 18-state projects also carry a byte-identical
            // root-level copy; that copy must not make the animation unreadable.
            // Resolve the canonical path here instead of routing through the
            // generic root-first resource lookup.
            var path = ResolveMeffPath(profile) ?? throw new FileNotFoundException("缺少 E5\\Meff.e5。");
            var archive = E5Archive.OpenMeff(path);
            if (fieldValue < 0 || fieldValue + 1 > archive.Entries.Count) throw new InvalidDataException($"字段 {fieldValue} 超出 Meff 范围。");
            bytes = archive.ReadDecoded(fieldValue + 1);
            description = $"字段 {fieldValue} → Meff 图号 #{fieldValue + 1}";
        }
        else
        {
            var number = fieldValue - 100;
            var path = profile.ResourcePaths.Values.FirstOrDefault(candidate =>
                Path.GetFileNameWithoutExtension(candidate).StartsWith("Mcall", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(new string(Path.GetFileNameWithoutExtension(candidate).Where(char.IsDigit).ToArray()), out var actual) && actual == number)
                ?? throw new FileNotFoundException($"缺少 Mcall{number:00}.e5。");
            EnsureUnambiguousPath(profile, path);
            bytes = File.ReadAllBytes(path);
            description = $"字段 {fieldValue} → {Path.GetFileName(path)}";
        }
        using var animation = StrategyAnimationDecoder.Decode(profile, bytes);
        return new ResourcePreviewAnimation(animation.Frames.Select(frame => new Bitmap(frame)).ToArray(), animation.IntervalMs, description);
    }

    public Task<ResourcePreviewAnimation> DecodeStrategyAnimationAsync(string rootPath, bool large, int fieldValue,
        CancellationToken cancellationToken = default)
        => RunDecodeAsync(() => DecodeStrategyAnimation(rootPath, large, fieldValue), animation => animation.Dispose(), cancellationToken);

    public IReadOnlyList<LogicalImageAsset> ReadAssets(string rootPath, string resourceKey)
    {
        var catalog = GetCatalog(rootPath);
        var descriptor = FindDescriptor(catalog, resourceKey)
            ?? throw new KeyNotFoundException("未找到资源：" + resourceKey);
        return GetAssets(catalog, descriptor);
    }

    public MutationResult Append(string rootPath, string resourceKey, IReadOnlyList<string> sources)
    {
        IntegratedSceneMutationPolicy.RequireImageResource(resourceKey);
        var profile = _profileService.Detect(rootPath);
        var descriptor = FindDescriptor(profile, resourceKey);
        EnsureMutable(descriptor);
        var plan = IntegratedSceneMutationPolicy.Append(profile, resourceKey,
            () => CreateMutationService(profile).PlanAppend(profile, descriptor, sources));
        try
        {
            IntegratedSceneMutationPolicy.ValidateCommit(profile, plan);
            return ProjectImageHistory.Commit(profile, plan);
        }
        finally { Invalidate(profile.RootPath); }
    }

    public MutationResult Replace(string rootPath, string resourceKey, IReadOnlyList<int> logicalIds, IReadOnlyList<string> sources)
    {
        IntegratedSceneMutationPolicy.RequireImageResource(resourceKey);
        var profile = _profileService.Detect(rootPath);
        var descriptor = FindDescriptor(profile, resourceKey);
        EnsureMutable(descriptor);
        var assets = _catalogService.ReadAssets(profile, descriptor)
            .Where(asset => logicalIds.Contains(asset.LogicalId))
            .OrderBy(asset => Array.IndexOf(logicalIds.ToArray(), asset.LogicalId))
            .ToArray();
        if (assets.Length != sources.Count) throw new InvalidOperationException("资源选择数量与素材数量不一致。");
        var service = CreateMutationService(profile);
        try { return ProjectImageHistory.Commit(profile, service.PlanReplace(profile, assets, sources)); }
        finally { Invalidate(profile.RootPath); }
    }

    public MutationResult Delete(string rootPath, string resourceKey, IReadOnlyList<int> logicalIds)
    {
        IntegratedSceneMutationPolicy.RequireImageResource(resourceKey);
        var profile = _profileService.Detect(rootPath);
        var descriptor = FindDescriptor(profile, resourceKey);
        EnsureMutable(descriptor);
        var assets = _catalogService.ReadAssets(profile, descriptor)
            .Where(asset => logicalIds.Contains(asset.LogicalId)).ToArray();
        var service = CreateMutationService(profile);
        try
        {
            var plan = IntegratedSceneMutationPolicy.Delete(profile, assets, () => service.PlanDelete(profile, assets));
            IntegratedSceneMutationPolicy.ValidateCommit(profile, plan);
            return ProjectImageHistory.Commit(profile, plan);
        }
        finally { Invalidate(profile.RootPath); }
    }

    private ImageResourceDescriptor FindDescriptor(ProjectProfile profile, string key)
        => BuildDescriptors(profile)
            .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException("未找到资源：" + key);

    private ImageResourceDescriptor? FindDescriptor(CatalogCache catalog, string key)
        => catalog.Descriptors.TryGetValue(key, out var descriptor) ? descriptor : null;

    private CatalogCache GetCatalog(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        lock (_cacheGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_catalogCache.TryGetValue(root, out var cached) && IsFresh(cached)) return cached;

            if (cached != null)
            {
                Interlocked.Increment(ref _revision);
                _previews.InvalidatePaths(cached.Files.Select(file => file.Path));
            }

            // Keep this work under the gate so rapidly alternating previews do
            // not all perform the same archive probes in parallel.
            var profile = _profileService.Detect(root);
            var descriptors = BuildDescriptors(profile);
            var resources = descriptors.Select(resource => new ResourceCatalogItem(
                    resource.Key,
                    ResourceDisplayNames.Name(resource),
                    ResourceDisplayNames.Category(resource.Category),
                    ResourceDisplayNames.Kind(resource.Kind),
                    resource.EntryCount,
                    resource.CanPreview,
                    resource.CanMutate,
                    resource.IsExperimental,
                    resource.Status,
                    resource.Usage,
                    resource.Paths.ToArray()))
                .ToArray();
            var snapshot = new ResourceCatalogSnapshot(profile.RootPath, profile.VersionText, profile.IsReadOnly,
                profile.Diagnostics.ToArray(), resources);
            var entry = new CatalogCache(profile, snapshot,
                descriptors.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase),
                CaptureDirectoryStamp(root), CaptureDirectoryStamp(Path.Combine(root, "E5")),
                CaptureDirectoryStamp(profile.TryGetResource("MapDirectory") ?? Path.Combine(root, "Map")),
                CaptureFileStamps(profile, descriptors));
            _catalogCache[root] = entry;
            return entry;
        }
    }

    public void Invalidate(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        lock (_cacheGate)
        {
            Interlocked.Increment(ref _revision);
            if (_catalogCache.Remove(root, out var removed))
                _previews.InvalidatePaths(removed.Files.Select(file => file.Path));
        }
    }

    private static bool IsFresh(CatalogCache entry)
    {
        if (!entry.RootDirectory.Equals(CaptureDirectoryStamp(entry.Profile.RootPath)) ||
            !entry.E5Directory.Equals(CaptureDirectoryStamp(Path.Combine(entry.Profile.RootPath, "E5"))) ||
            !entry.MapDirectory.Equals(CaptureDirectoryStamp(entry.Profile.TryGetResource("MapDirectory") ??
                Path.Combine(entry.Profile.RootPath, "Map"))))
            return false;
        foreach (var original in entry.Files)
        {
            try
            {
                var info = new FileInfo(original.Path);
                if (!info.Exists || info.Length != original.Length || info.LastWriteTimeUtc != original.LastWriteUtc)
                    return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    private static DirectoryStamp CaptureDirectoryStamp(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return new DirectoryStamp(info.Exists, info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
        }
        catch (IOException) { return new DirectoryStamp(false, DateTime.MinValue); }
        catch (UnauthorizedAccessException) { return new DirectoryStamp(false, DateTime.MinValue); }
    }

    private static IReadOnlyList<FileStamp> CaptureFileStamps(ProjectProfile profile, IReadOnlyList<ImageResourceDescriptor> descriptors)
        => profile.ResourcePaths.Values.Concat(descriptors.SelectMany(descriptor => descriptor.Paths))
            .Concat(new[] { Path.Combine(profile.RootPath, "System.ini"), Path.Combine(profile.RootPath, "E5", "System.ini") })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return new FileStamp(Path.GetFullPath(path), info.Length, info.LastWriteTimeUtc);
                }
                catch (IOException) { return new FileStamp(Path.GetFullPath(path), -1, DateTime.MinValue); }
                catch (UnauthorizedAccessException) { return new FileStamp(Path.GetFullPath(path), -1, DateTime.MinValue); }
            })
            .ToArray();

    private ImageResourceDescriptor[] BuildDescriptors(ProjectProfile profile)
    {
        var descriptors = _catalogService.BuildCatalog(profile).ToList();
        if (descriptors.FirstOrDefault(descriptor => descriptor.Key == "Face") is { } face &&
            face.Paths.Any(path => Path.GetFileName(path).Equals("Tou.dll", StringComparison.OrdinalIgnoreCase)))
            descriptors.Add(face with { Key = "Tou", DisplayName = "大头像", Usage = "大头像与小头像成对维护" });
        return descriptors.Select(descriptor =>
        {
            if (descriptor.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
                return NormalizeMeffDescriptor(profile, descriptor);

            var duplicateNames = descriptor.Paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => name != null && ProjectProfileService.IsUsableResourcePath(Path.Combine(profile.RootPath, name)) &&
                               ProjectProfileService.IsUsableResourcePath(Path.Combine(profile.RootPath, "E5", name))).ToArray();
            return descriptor with
            {
                DisplayName = ResourceDisplayNames.Name(descriptor),
                CanPreview = descriptor.CanPreview && duplicateNames.Length == 0,
                CanMutate = descriptor.CanMutate && duplicateNames.Length == 0,
                Status = duplicateNames.Length == 0 ? descriptor.Status :
                    "根目录与 E5 目录存在同名资源，需要确认：" + string.Join("、", duplicateNames)
            };
        }).ToArray();
    }

    private static ImageResourceDescriptor NormalizeMeffDescriptor(ProjectProfile profile,
        ImageResourceDescriptor descriptor)
    {
        var rootPath = Path.Combine(profile.RootPath, "Meff.e5");
        var e5Path = Path.Combine(profile.RootPath, "E5", "Meff.e5");
        var rootExists = ProjectProfileService.IsUsableResourcePath(rootPath);
        var e5Exists = ProjectProfileService.IsUsableResourcePath(e5Path);
        var canonical = e5Exists ? Path.GetFullPath(e5Path) :
            rootExists ? Path.GetFullPath(rootPath) : descriptor.Paths.FirstOrDefault();
        if (canonical == null) return descriptor;

        var entryCount = descriptor.EntryCount;
        var probeStatus = descriptor.Status;
        try
        {
            var archive = E5Archive.OpenMeff(canonical);
            entryCount = archive.Entries.Count;
            probeStatus = entryCount == 64
                ? "Meff 8bpp 动画；逐条 E5 索引探针通过（64 项）"
                : $"Meff 8bpp 动画；逐条 E5 索引探针通过（{entryCount} 项）";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            probeStatus = "Meff.e5 索引探针失败：" + ex.Message;
            entryCount = 0;
        }

        var duplicate = rootExists && e5Exists;
        var sameContent = duplicate && FilesHaveSameContent(rootPath, e5Path);
        var ambiguity = duplicate && !sameContent;
        var status = duplicate
            ? sameContent
                ? probeStatus + "；根目录副本内容相同，按 E5\\Meff.e5 规范路径读取；写入需显式选择目标"
                : probeStatus + "；根目录同名内容不同，按 E5\\Meff.e5 规范路径只读预览"
            : probeStatus;

        return descriptor with
        {
            Paths = [canonical],
            EntryCount = entryCount,
            CanPreview = descriptor.CanPreview && entryCount > 0,
            // A duplicate root copy can be loaded by a different engine build;
            // keep the deterministic preview but require an explicit target for
            // mutation.  A lone canonical E5 archive remains writable when the
            // project itself is writable.
            CanMutate = descriptor.CanMutate && !duplicate && !ambiguity,
            Status = status
        };
    }

    private static string? ResolveMeffPath(ProjectProfile profile)
    {
        var e5Path = Path.Combine(profile.RootPath, "E5", "Meff.e5");
        if (ProjectProfileService.IsUsableResourcePath(e5Path)) return Path.GetFullPath(e5Path);
        var rootPath = Path.Combine(profile.RootPath, "Meff.e5");
        if (ProjectProfileService.IsUsableResourcePath(rootPath)) return Path.GetFullPath(rootPath);
        return profile.TryGetResource("Meff.e5");
    }

    private static bool FilesHaveSameContent(string first, string second)
    {
        try
        {
            var left = new FileInfo(first);
            var right = new FileInfo(second);
            return left.Length == right.Length &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first))) ==
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private IReadOnlyList<LogicalImageAsset> GetAssets(CatalogCache catalog, ImageResourceDescriptor descriptor)
    {
        lock (catalog.Assets)
        {
            if (catalog.Assets.TryGetValue(descriptor.Key, out var cached)) return cached;
            IReadOnlyList<LogicalImageAsset> assets = descriptor.CanPreview
                ? _catalogService.ReadAssets(catalog.Profile, descriptor)
                : Array.Empty<LogicalImageAsset>();
            if (descriptor.Key == "Tou")
                assets = assets.Where(asset => asset.Frames.Any(frame => frame.SourceKey == "Tou.dll"))
                    .Select(asset => asset with
                    {
                        DisplayName = $"大头像 {asset.LogicalId:000}",
                        Frames = asset.Frames.OrderBy(frame => frame.SourceKey == "Tou.dll" ? 0 : 1).ToArray()
                    }).ToArray();
            catalog.Assets[descriptor.Key] = assets;
            return assets;
        }
    }

    private IReadOnlyList<DecodedFrame> DecodeAssetFrames(ProjectProfile profile, LogicalImageAsset asset)
        => asset.ResourceKey == "Tou" ? new FaceResourceCodec().DecodeFrames(profile, asset) : _catalogService.DecodeFrames(profile, asset);

    private async Task<T> RunDecodeAsync<T>(Func<T> decode, Action<T> release, CancellationToken cancellationToken)
    {
        await _decodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = decode();
                if (cancellationToken.IsCancellationRequested)
                {
                    release(result);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _decodeSlots.Release(); }
    }

    private static void EnsureMutable(ImageResourceDescriptor descriptor)
    {
        if (!descriptor.CanMutate) throw new InvalidOperationException("资源当前不可修改：" + descriptor.Status);
    }

    private static void EnsureUnambiguousPath(ProjectProfile profile, string path)
    {
        var name = Path.GetFileName(path);
        if (ProjectProfileService.IsUsableResourcePath(Path.Combine(profile.RootPath, name)) && ProjectProfileService.IsUsableResourcePath(Path.Combine(profile.RootPath, "E5", name)))
            throw new InvalidOperationException("根目录与 E5 目录存在同名资源，需要确认：" + name);
    }

    public void Dispose()
    {
        lock (_cacheGate)
        {
            if (_disposed) return;
            _disposed = true;
            _catalogCache.Clear();
            _previews.Dispose();
        }
        // Pending callers may still be unwinding a decode slot. Leaving the
        // managed semaphore alive avoids Release racing its disposal.
    }

    private sealed class CachedImageCatalogService(ResourceCatalogAdapter owner) : IImageCatalogService
    {
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => owner.GetDescriptors(project.RootPath);
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        {
            var catalog = owner.GetCatalog(project.RootPath);
            var current = owner.FindDescriptor(catalog, resource.Key);
            return current == null ? [] : owner.GetAssets(catalog, current);
        }
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
            => owner.DecodeAssetFrames(project, asset);
        public DecodedFrame? DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
            CancellationToken cancellationToken = default)
            => asset.ResourceKey == "Tou"
                ? PreviewTou(project, asset, descriptor, cancellationToken)
                : owner._catalogService.DecodePreviewFrame(project, asset, descriptor, cancellationToken);

        private static DecodedFrame? PreviewTou(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
            CancellationToken cancellationToken)
            => ((IResourceCodec)new FaceResourceCodec()).DecodePreviewFrame(project, asset, descriptor, cancellationToken);
    }

    private sealed record CatalogCache(
        ProjectProfile Profile,
        ResourceCatalogSnapshot Snapshot,
        IReadOnlyDictionary<string, ImageResourceDescriptor> Descriptors,
        DirectoryStamp RootDirectory,
        DirectoryStamp E5Directory,
        DirectoryStamp MapDirectory,
        IReadOnlyList<FileStamp> Files)
    {
        public Dictionary<string, IReadOnlyList<LogicalImageAsset>> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct DirectoryStamp(bool Exists, DateTime LastWriteUtc);
    private readonly record struct FileStamp(string Path, long Length, DateTime LastWriteUtc);

    private static ImageMutationService CreateMutationService(ProjectProfile profile)
    {
        return new ImageMutationService(ProjectImageHistory.Create(profile));
    }
}

public sealed record ResourceCatalogSnapshot(
    string RootPath,
    string Version,
    bool IsReadOnly,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ResourceCatalogItem> Resources);

public sealed record ResourceCatalogItem(
    string Key,
    string DisplayName,
    string Category,
    string Kind,
    int EntryCount,
    bool CanPreview,
    bool CanMutate,
    bool IsExperimental,
    string Status,
    string Usage,
    IReadOnlyList<string> Paths);

public sealed record ResourcePreviewAnimation(IReadOnlyList<Bitmap> Frames, int IntervalMs, string Mapping) : IDisposable
{
    public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }
}

public sealed record ResourceRangePreviewInfo(string ResourceKey, int FieldValue, int PhysicalImageNumber,
    int EntryCount, bool CanPreview, string Status);
