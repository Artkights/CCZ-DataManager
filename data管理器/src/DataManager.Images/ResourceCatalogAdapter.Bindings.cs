using System.Drawing;
using ImageManager.Core;

namespace DataManager.Images;

/// <summary>Data fields use these logical IDs; generic E5 image numbers remain unchanged.</summary>
public enum ResourceBindingKind { ItemIcon, StrategyIcon, CharacterR, CharacterS, Portrait }

public sealed record ResourceIdOption(int Id, string Name, string ResourceKey, bool CanPreview, string Status);

public sealed record ResourceBindingPreviewInfo(ResourceBindingKind Kind, string ResourceKey, int Id,
    int LogicalCount, IReadOnlyList<int> PhysicalImageNumbers, bool CanPreview, string Status);

public sealed partial class ResourceCatalogAdapter
{
    public Task<IReadOnlyList<ResourceIdOption>> GetIdOptionsAsync(string rootPath, ResourceBindingKind kind,
        int jobId = 0, int turn = 1, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = GetIdOptions(rootPath, kind, jobId, turn);
            cancellationToken.ThrowIfCancellationRequested();
            return options;
        }, cancellationToken);

    public IReadOnlyList<ResourceIdOption> GetIdOptions(string rootPath, ResourceBindingKind kind,
        int jobId = 0, int turn = 1)
    {
        var catalog = GetCatalog(rootPath);
        var descriptor = BindingDescriptor(catalog, kind);
        if (descriptor == null || !descriptor.CanPreview) return [];
        if (kind == ResourceBindingKind.CharacterS)
        {
            // Assignment IDs are compact person IDs, independent of gallery physical IDs.
            var fallback = PersonSAsset(catalog, descriptor, 0, jobId);
            return new[] { new ResourceIdOption(0, "默认兵种形象", descriptor.Key, fallback != null,
                    fallback?.Status ?? "当前兵种没有对应的默认 S 形象") }
                .Concat(new SLogicalAssetMappingService().BuildPersonAssignments(descriptor.EntryCount)
                    .Select(mapping => new ResourceIdOption(mapping.SId, $"人物 S{mapping.SId:000}", descriptor.Key,
                        true, $"第一转移动第0帧 → Unit #{mapping.Turns[0].ImageNumber}"))).ToArray();
        }
        var assets = GetAssets(catalog, descriptor);
        var options = assets.OrderBy(asset => asset.LogicalId)
            .Select(asset => new ResourceIdOption(asset.LogicalId, asset.DisplayName, descriptor.Key,
                asset.Frames.Count > 0, asset.Status)).ToList();
        return options;
    }

    public ResourceBindingPreviewInfo ResolveBindingPreview(string rootPath, ResourceBindingKind kind,
        int id, int jobId = 0, int turn = 1)
    {
        var request = ResolveBindingAsset(rootPath, kind, id, jobId, turn);
        var count = request.Descriptor is { CanPreview: true } descriptor
            ? kind == ResourceBindingKind.CharacterS
                ? new SLogicalAssetMappingService().BuildPersonAssignments(descriptor.EntryCount).Count + 1
                : GetAssets(request.Catalog, descriptor).Count : 0;
        var numbers = request.Asset?.Frames.Select(frame => frame.ImageNumber).Distinct().ToArray() ?? [];
        var valid = request.Descriptor is { CanPreview: true } && request.Asset is { Frames.Count: > 0 };
        var status = valid ? kind == ResourceBindingKind.CharacterS ? request.Asset!.Status :
            $"编号 {id} → 图号 {string.Join("、", numbers)}" :
            request.Descriptor == null ? "未找到对应图片资源" :
            !request.Descriptor.CanPreview ? request.Descriptor.Status : $"未找到编号 {id} 的图片";
        return new ResourceBindingPreviewInfo(kind, request.Descriptor?.Key ?? string.Empty, id,
            count, numbers, valid, status);
    }

    /// <summary>
    /// Returns an owned, proportional thumbnail. Item/strategy IDs start at 0
    /// in both versions: Item.e5 N maps to physical 2N+1/2N+2; DLL N maps to
    /// the Nth small/large bitmap pair. Mtem uses the same paired layout.
    /// R and S keep their established IDs.
    /// </summary>
    public async Task<Bitmap?> DecodeBindingThumbnailAsync(string rootPath, ResourceBindingKind kind,
        int id, Size bounds, int jobId = 0, int turn = 1, CancellationToken cancellationToken = default)
    {
        var request = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ResolveBindingAsset(rootPath, kind, id, jobId, turn);
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Asset is not { Frames.Count: > 0 } asset) return null;
        if (kind == ResourceBindingKind.CharacterS)
            return await DecodePhysicalSMovementFrameAsync(rootPath, asset.Frames[0].ImageNumber, bounds,
                cancellationToken).ConfigureAwait(false);
        var frame = kind switch
        {
            ResourceBindingKind.ItemIcon or ResourceBindingKind.StrategyIcon =>
                asset.Frames.FirstOrDefault(candidate => candidate.Action == "large") ?? asset.Frames[^1],
            _ => asset.Frames[0]
        };
        var image = await _previews.GetThumbnailAsync(request.Catalog.Profile, CreateImageCatalogService(),
            asset, frame, bounds, true, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) { image?.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return image;
    }

    private (CatalogCache Catalog, ImageResourceDescriptor? Descriptor, LogicalImageAsset? Asset) ResolveBindingAsset(
        string rootPath, ResourceBindingKind kind, int id, int jobId, int turn)
    {
        var catalog = GetCatalog(rootPath);
        var descriptor = BindingDescriptor(catalog, kind);
        if (descriptor == null || !descriptor.CanPreview || id < 0) return (catalog, descriptor, null);
        var asset = kind == ResourceBindingKind.CharacterS
            ? PersonSAsset(catalog, descriptor, id, jobId)
            : GetAssets(catalog, descriptor).FirstOrDefault(candidate => candidate.LogicalId == id);
        return (catalog, descriptor, asset);
    }

    private static ImageResourceDescriptor? BindingDescriptor(CatalogCache catalog, ResourceBindingKind kind)
    {
        var keys = kind switch
        {
            ResourceBindingKind.ItemIcon => new[] { "Item.e5", "Itemicon.dll" },
            ResourceBindingKind.StrategyIcon => ["Mtem.e5", "Mgcicon.dll"],
            ResourceBindingKind.CharacterR => ["Pmapobj.e5"],
            ResourceBindingKind.CharacterS => ["SComposite"],
            ResourceBindingKind.Portrait => ["Face"],
            _ => []
        };
        return keys.Select(key => catalog.Descriptors.GetValueOrDefault(key)).FirstOrDefault(descriptor => descriptor != null);
    }

    private static LogicalImageAsset? PersonSAsset(CatalogCache catalog, ImageResourceDescriptor descriptor,
        int sId, int jobId)
    {
        if (sId < 0 || sId == 0 && jobId is < 0 or > 79) return null;
        SPhysicalMapping mapping;
        try { mapping = new SPhysicalMappingService().Resolve(sId, jobId, 1); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException) { return null; }
        if (mapping.ImageNumber < 1 || mapping.ImageNumber > descriptor.EntryCount) return null;
        var path = catalog.Profile.TryGetResource("Unit_mov.e5");
        if (path == null) return null;
        var name = sId == 0 ? "默认兵种形象" : $"人物 S{sId:000}";
        var incomplete = sId is >= 1 and <= 32 && mapping.ImageNumber + 2 > descriptor.EntryCount
            ? "；三转资源不完整" : string.Empty;
        return new LogicalImageAsset(descriptor.Key, sId, name, ImageCategory.CharacterS,
            ResourceKind.SComposite,
            [new FrameDescriptor("Unit_mov.e5", path, mapping.ImageNumber, 0, 1, "mov", "front", 48, 48)],
            [], false, false, $"{name} → Unit #{mapping.ImageNumber} 移动第0帧{incomplete}")
        { PhysicalIdentity = $"{descriptor.Key}:person-preview:{sId}:Unit:{mapping.ImageNumber}" };
    }
}
