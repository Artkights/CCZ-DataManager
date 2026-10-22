using ImageManager.Core;

namespace ImageManager.App;

internal static class PreviewLoader
{
    public static Bitmap? TryLoad(ProjectProfile project, IImageCatalogService catalog, ImageCategory category, int logicalId, int turn = 1)
    {
        try
        {
            var resource = catalog.BuildCatalog(project).FirstOrDefault(item => item.Category == category);
            if (resource == null) return null;
            var asset = catalog.ReadAssets(project, resource).FirstOrDefault(item => item.LogicalId == logicalId);
            return asset == null ? null : LoadRepresentative(project, catalog, asset, turn);
        }
        catch { return null; }
    }

    public static Bitmap? LoadRepresentative(ProjectProfile project, IImageCatalogService catalog, LogicalImageAsset asset, int turn = 1)
    {
        try
        {
            var selected = asset.Frames.FirstOrDefault(frame => frame.Turn == turn) ?? asset.Frames.FirstOrDefault();
            if (selected == null) return null;
            if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
            {
                var bytes = E5Archive.Open(selected.SourcePath).ReadDecoded(selected.ImageNumber);
                using var strip = ImageRendering.IsStandardImage(bytes)
                    ? ImageRendering.DecodeStandardImage(bytes)
                    : ImageRendering.DecodeIndexed(bytes, selected.Width, selected.Height * (selected.SourceKey.Contains("atk", StringComparison.OrdinalIgnoreCase) ? 12 :
                        selected.SourceKey.Contains("spc", StringComparison.OrdinalIgnoreCase) ? 5 : selected.SourceKey.Contains("mov", StringComparison.OrdinalIgnoreCase) ? 11 : 20),
                        new PaletteService().Load(project));
                return ImageRendering.Crop(strip, new Rectangle(0, selected.FrameIndex * selected.Height, selected.Width, selected.Height));
            }
            var frames = catalog.DecodeFrames(project, asset);
            try { return frames.Count == 0 ? null : new Bitmap(frames[0].Bitmap); }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        catch { return null; }
    }

    public static Bitmap? LoadDescriptor(ProjectProfile project, IImageCatalogService catalog, LogicalImageAsset asset,
        FrameDescriptor descriptor)
    {
        if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
            return LoadRepresentative(project, catalog, asset, descriptor.Turn);
        try
        {
            var frames = catalog.DecodeFrames(project, asset);
            try
            {
                var frame = frames.FirstOrDefault(item => item.Descriptor.SourceKey == descriptor.SourceKey &&
                                                          item.Descriptor.ImageNumber == descriptor.ImageNumber &&
                                                          item.Descriptor.Action == descriptor.Action);
                return frame == null ? null : new Bitmap(frame.Bitmap);
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        catch { return null; }
    }
}
