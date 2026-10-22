using System.Buffers.Binary;
using System.Drawing;

namespace ImageManager.Core;

public sealed class PmapResourceCodec : IResourceCodec
{
    public static bool IsVerifiedPayload(byte[] bytes)
    {
        if (bytes.Length < 17) return false;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2));
        return width > 0 && height > 0 && 16L + (long)width * height == bytes.Length;
    }

    public static Size PayloadSize(byte[] bytes)
        => IsVerifiedPayload(bytes) ? new(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2))) : throw new InvalidDataException("Pmap的16字节头与像素平面长度不一致。");

    public static byte[] RebuildPayload(ProjectProfile project, byte[] original, Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        var size = PayloadSize(original);
        var pixels = ImageImportTargetEncoding.Raw8(original.AsSpan(16).ToArray(), size.Width, size.Height, bitmap,
            new PaletteService().RequireWritePalette(project), cancellationToken);
        var result = original.AsSpan(0, 16).ToArray().Concat(pixels).ToArray();
        if (!IsVerifiedPayload(result) || !result.AsSpan(0, 16).SequenceEqual(original.AsSpan(0, 16)))
            throw new InvalidDataException("Pmap重编码后头部或像素尺寸变化。");
        return result;
    }
    public ResourceKind Kind => ResourceKind.PmapSpecial;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => E5Archive.Open(resource.Paths.Single()).Entries.Select(entry => Asset(resource, entry.ImageNumber,
            "16 字节头 + 8bpp 平面；文件导入保持头部与画布，按项目调色板重编码")).ToArray();

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
    {
        var descriptor = asset.Frames.Single();
        var bytes = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
        if (bytes.Length < 17) throw new InvalidDataException("Pmap 条目不足 16 字节头。");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2));
        if (width <= 0 || height <= 0 || checked(16 + width * height) != bytes.Length)
            throw new InvalidDataException($"Pmap #{descriptor.ImageNumber} 头部尺寸 {width}x{height} 与 {bytes.Length} 字节不一致。");
        var pixels = bytes.AsSpan(16).ToArray();
        var bitmap = ImageRendering.DecodeIndexed(pixels, width, height, new PaletteService().Load(project));
        return [new DecodedFrame(descriptor with { Width = width, Height = height }, bitmap, pixels)];
    }

    private static LogicalImageAsset Asset(ImageResourceDescriptor resource, int imageNumber, string status)
    {
        var path = resource.Paths.Single();
        var frame = new FrameDescriptor(resource.Key, path, imageNumber, 0, 1, "pmap", "none", 0, 0);
        return new LogicalImageAsset(resource.Key, imageNumber - 1, $"Pmap {imageNumber - 1:000}",
            resource.Category, resource.Kind, [frame], Array.Empty<string>(), false, false, status);
    }
}

public sealed class WeatherResourceCodec : IResourceCodec
{
    public const int FrameWidth = 216;
    public const int FrameHeight = 50;
    public const int FrameCount = 4;
    public ResourceKind Kind => ResourceKind.WeatherSpecial;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => E5Archive.Open(resource.Paths.Single()).Entries.Select(entry =>
        {
            var frame = new FrameDescriptor(resource.Key, resource.Paths.Single(), entry.ImageNumber, 0, 1,
                "weather", "none", FrameWidth, FrameHeight);
            return new LogicalImageAsset(resource.Key, entry.ImageNumber - 1, $"天气 {entry.ImageNumber - 1:000}",
                resource.Category, resource.Kind, [frame], Array.Empty<string>(), false, false, "216×50，纵向四帧循环");
        }).ToArray();

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
    {
        var descriptor = asset.Frames.Single();
        var bytes = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
        if (bytes.Length != FrameWidth * FrameHeight * FrameCount)
            throw new InvalidDataException($"Weather #{descriptor.ImageNumber} 应为 43200 字节，实际 {bytes.Length}。");
        var palette = new PaletteService().Load(project);
        return Enumerable.Range(0, FrameCount).Select(index =>
        {
            var pixels = bytes.AsSpan(index * FrameWidth * FrameHeight, FrameWidth * FrameHeight).ToArray();
            return new DecodedFrame(descriptor with { FrameIndex = index, SourceOffset = index * FrameWidth * FrameHeight },
                ImageRendering.DecodeIndexed(pixels, FrameWidth, FrameHeight, palette), pixels,
                ImageRendering.BuildPaletteBgra(palette));
        }).ToArray();
    }

    public DecodedFrame DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.FrameIndex is < 0 or >= FrameCount) throw new ArgumentOutOfRangeException(nameof(descriptor));
        var source = asset.Frames.Single();
        var bytes = E5Archive.Open(source.SourcePath).ReadDecoded(source.ImageNumber);
        if (bytes.Length != FrameWidth * FrameHeight * FrameCount)
            throw new InvalidDataException($"Weather #{source.ImageNumber} 应为 43200 字节，实际 {bytes.Length}。");
        cancellationToken.ThrowIfCancellationRequested();
        var offset = descriptor.FrameIndex * FrameWidth * FrameHeight;
        var pixels = bytes.AsSpan(offset, FrameWidth * FrameHeight).ToArray();
        var palette = new PaletteService().Load(project);
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = ImageRendering.DecodeIndexed(pixels, FrameWidth, FrameHeight, palette);
        if (cancellationToken.IsCancellationRequested)
        {
            bitmap.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new DecodedFrame(source with { FrameIndex = descriptor.FrameIndex, SourceOffset = offset },
            bitmap, pixels, ImageRendering.BuildPaletteBgra(palette));
    }

    public static byte[] RebuildPayload(ProjectProfile project, byte[] original, Bitmap verticalStrip, CancellationToken cancellationToken = default)
    {
        if (original.Length != FrameWidth * FrameHeight * FrameCount) throw new InvalidDataException("天气载荷必须为 43200 字节。");
        var result = ImageImportTargetEncoding.Raw8(original, FrameWidth, FrameHeight * FrameCount, verticalStrip,
            new PaletteService().RequireWritePalette(project), cancellationToken);
        if (result.Length != original.Length) throw new InvalidDataException("天气写后大小不一致。");
        return result;
    }

    public static byte[] ReplaceFrame(ProjectProfile project, byte[] original, int frameIndex, Bitmap edited)
    {
        if (frameIndex is < 0 or >= FrameCount || original.Length != FrameWidth * FrameHeight * FrameCount)
            throw new InvalidDataException("天气帧号或载荷大小不正确。");
        var palette = new PaletteService().RequireWritePalette(project);
        var result = (byte[])original.Clone();
        BmpPayloadService.RebuildRaw8(original.AsSpan(frameIndex * FrameWidth * FrameHeight, FrameWidth * FrameHeight).ToArray(),
            FrameWidth, FrameHeight, edited, palette).CopyTo(result, frameIndex * FrameWidth * FrameHeight);
        return result;
    }
}

public sealed class DtAnimationResourceCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.DtAnimation;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;

    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => E5Archive.Open(resource.Paths.Single()).Entries.Select(entry =>
        {
            var frame = new FrameDescriptor(resource.Key, resource.Paths.Single(), entry.ImageNumber, 0, 1,
                "dt", "none", 0, 0);
            return new LogicalImageAsset(resource.Key, entry.ImageNumber - 1, $"DT {entry.ImageNumber - 1:000}",
                resource.Category, resource.Kind, [frame], Array.Empty<string>(), false, false,
                "20 字节记录表后的嵌入 BMP；已验证条目可预览，结构写入关闭");
        }).ToArray();

    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
    {
        var descriptor = asset.Frames.Single();
        var bytes = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
        using var animation = StrategyAnimationDecoder.Decode(project, bytes);
        return animation.Frames.Select((bitmap, index) => new DecodedFrame(descriptor with
            { FrameIndex = index, Width = bitmap.Width, Height = bitmap.Height }, new Bitmap(bitmap))).ToArray();
    }

    public DecodedFrame DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = asset.Frames.Single();
        var bytes = E5Archive.Open(source.SourcePath).ReadDecoded(source.ImageNumber);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new StrategyAnimationPackageCodec().DecodePreviewFrame(project, bytes,
            descriptor.FrameIndex, cancellationToken);
        return new DecodedFrame(source with
            { FrameIndex = descriptor.FrameIndex, Width = bitmap.Width, Height = bitmap.Height }, new Bitmap(bitmap));
    }
}

public sealed class MarkDiagnosticCodec : IResourceCodec
{
    public ResourceKind Kind => ResourceKind.MarkRaw;
    public bool CanHandle(ImageResourceDescriptor resource) => resource.Kind == Kind;
    public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        => Array.Empty<LogicalImageAsset>();
    public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        => Array.Empty<DecodedFrame>();
}
