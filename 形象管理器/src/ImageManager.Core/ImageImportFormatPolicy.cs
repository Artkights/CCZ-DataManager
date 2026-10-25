namespace ImageManager.Core;

public sealed record ImageImportFormatSupport(IReadOnlyList<ImageImportOutputFormat> AllowedFormats, string Description)
{
    public bool IsLocked => AllowedFormats.Count == 1;
}

/// <summary>Describes raster-file output choices; native structures are never replaced with a standard image header.</summary>
public static class ImageImportFormatPolicy
{
    public static ImageImportFormatSupport GetSupport(ImageResourceDescriptor resource, bool nativeIndexed = false)
    {
        if (nativeIndexed)
            return Locked("已确认的 RAW8 像素区保留原始结构、项目调色板与合法尾部，不能改写为普通 BMP/PNG。");
        if (resource.Kind == ResourceKind.DllBitmap)
            return Locked("DLL 位图保留各语言变体的 DIB 色深、调色板与布局。");
        if (resource.Kind == ResourceKind.MapPair)
            return Locked("地图底图固定写入 JPG；地形层使用专用结构。");
        if (resource.Kind is ResourceKind.PmapSpecial or ResourceKind.WeatherSpecial)
            return Locked("该资源保留专用头部、RAW8 像素结构及项目调色板，不能改写为普通 BMP/PNG。");
        if (resource.Kind is not (ResourceKind.E5Indexed or ResourceKind.RComposite or ResourceKind.SComposite or ResourceKind.FaceComposite) ||
            resource.Category == ImageCategory.StrategyAnimation || resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase))
            return Locked("此资源使用专用动画包或容器格式。");
        if (resource.Category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
            return new([ImageImportOutputFormat.Bmp, ImageImportOutputFormat.Preserve],
                "图标使用 BMP24 洋红键色；保留原格式时遵循已有大小图各自的标准图片编码。");
        return new([ImageImportOutputFormat.Bmp, ImageImportOutputFormat.Png, ImageImportOutputFormat.Preserve],
            resource.Kind == ResourceKind.FaceComposite
                ? "Face 可选择 BMP24 或 PNG32；旧 RAW8 头像及 Tou 的 DIB 变体保留原结构。"
                : resource.Kind is ResourceKind.RComposite or ResourceKind.SComposite
                    ? "标准动作条可选择 BMP32 或 PNG32；RAW8 动作条保留原结构。新增使用末尾同动作模板。"
                    : "标准图片可选择 BMP24 或 PNG32；保留原格式沿用目标或末尾新增模板，RAW8 保留原结构。");
    }

    private static ImageImportFormatSupport Locked(string description)
        => new([ImageImportOutputFormat.Preserve], description);
}
