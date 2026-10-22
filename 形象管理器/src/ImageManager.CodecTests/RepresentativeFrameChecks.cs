using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using ImageManager.Core;

internal static class RepresentativeFrameChecks
{
    public static int Run(string testRoot)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var project = new ProjectProfile(testRoot, "代表帧", ProjectVersion.Ccz65, false,
            new Dictionary<string, string>(), []);
        var codec = new StrategyAnimationPackageCodec();
        var palette = new byte[768];
        palette[3] = 13; palette[4] = 91; palette[5] = 27;
        palette[6] = 19; palette[7] = 170; palette[8] = 40;
        var raw = Header().Concat(new byte[] { 1, 0, 2, 1, 1, 2 }).Concat(palette)
            .Concat(new byte[] { 0xfa, 0xfb }).ToArray();
        using (var full = codec.Decode(project, raw))
        {
            for (var index = 0; index < full.Frames.Count; index++)
            {
                using var preview = codec.DecodePreviewFrame(project, raw, index);
                Check(Same(full.Frames[index], preview), $"RAW代表帧{index}与全量播放不一致");
            }
            using var first = codec.DecodePreviewFrame(project, raw);
            Check(first.Size == new Size(6, 4), "代表帧遗漏后续记录对公共画布的影响");
            var pixel = first.GetPixel(0, 3);
            Check(pixel.A == 132 && Math.Abs(pixel.R - 170) <= 1 && Math.Abs(pixel.G - 40) <= 1,
                "代表帧没有按首记录平面1、附加调色板、位移及52%透明度合成");
        }
        foreach (var format in new[] { ImageFormat.Bmp, ImageFormat.Png })
        {
            using var strip = new Bitmap(2, 3, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(strip))
            {
                graphics.Clear(Color.Red);
                graphics.FillRectangle(Brushes.Lime, 0, 1, 2, 1);
                graphics.FillRectangle(Brushes.Blue, 0, 2, 2, 1);
            }
            strip.SetPixel(1, 1, Color.Magenta);
            using var stream = new MemoryStream();
            strip.Save(stream, format);
            var bytes = Header().Concat(stream.ToArray()).Concat(new byte[] { 0xfa, 0xfb }).ToArray();
            using var full = codec.Decode(project, bytes);
            for (var index = 0; index < full.Frames.Count; index++)
            {
                using var preview = codec.DecodePreviewFrame(project, bytes, index);
                Check(Same(full.Frames[index], preview), $"{format}代表帧{index}与全量播放不一致");
            }
            using var first = codec.DecodePreviewFrame(project, bytes);
            Check(first.GetPixel(1, 3).A == 0, "代表帧未保留嵌入图片的色键透明度");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { using var unexpected = codec.DecodePreviewFrame(project, [], cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { checks++; }
        try { using var unexpected = codec.DecodePreviewFrame(project, raw, 3); throw new InvalidOperationException("代表帧接受了越界播放步"); }
        catch (ArgumentOutOfRangeException) { checks++; }

        var source = Path.Combine(testRoot, "代表帧-测试源.bin");
        File.WriteAllBytes(source, [1]);
        var descriptor = new FrameDescriptor("test", source, 0, 0, 1, "test", "none", 1, 1);
        var asset = new LogicalImageAsset("test", 0, "test", ImageCategory.Dynamic, ResourceKind.WeatherSpecial,
            [descriptor], [], false, false, "test");
        var previewCatalog = new PreviewOnlyCatalog();
        using (var previews = new ImagePreviewService())
        {
            using var first = previews.GetThumbnailAsync(project, previewCatalog, asset, descriptor, new Size(1, 1), true)
                .GetAwaiter().GetResult();
            Check(first != null && previewCatalog.PreviewCalls == 1, "缩略图没有调用单帧接口");
            using var cached = previews.GetThumbnailAsync(project, previewCatalog, asset, descriptor, new Size(1, 1), true)
                .GetAwaiter().GetResult();
            Check(cached != null && previewCatalog.PreviewCalls == 1, "同一版本缩略图未复用缓存");
            var tsbPath = Path.Combine(testRoot, "tsb");
            var tsb = File.ReadAllBytes(tsbPath);
            var modified = File.GetLastWriteTimeUtc(tsbPath);
            tsb[6] = (byte)(tsb[6] + 1);
            File.WriteAllBytes(tsbPath, tsb);
            File.SetLastWriteTimeUtc(tsbPath, modified);
            using var changed = previews.GetThumbnailAsync(project, previewCatalog, asset, descriptor, new Size(1, 1), true)
                .GetAwaiter().GetResult();
            Check(changed != null && previewCatalog.PreviewCalls == 2 && changed.GetPixel(0, 0) != first!.GetPixel(0, 0),
                "仅调色板内容改变时缩略图缓存没有失效");
            tsb[6]--;
            File.WriteAllBytes(tsbPath, tsb);
            File.SetLastWriteTimeUtc(tsbPath, modified);
        }
        var fallbackCatalog = new FramesOnlyCatalog();
        using (var fallback = ((IImageCatalogService)fallbackCatalog).DecodePreviewFrame(project, asset, descriptor))
            Check(fallback?.Bitmap.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb() && fallbackCatalog.Calls == 1,
                "默认单帧接口破坏现有catalog测试替身");
        Console.WriteLine($"代表帧专项通过：RAW/BMP/PNG、首记录/坐标/透明度/公共画布、取消、接口委托与调色板缓存；断言={checks}。");
        return checks;
    }

    private static byte[] Header()
    {
        var bytes = new byte[80];
        bytes[0] = 3; bytes[1] = 2; bytes[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1);
        bytes[20] = 1; bytes[21] = 0; bytes[23] = 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), -1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 2);
        bytes[40] = 0; bytes[41] = 255;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), -1);
        bytes[60] = 1; bytes[61] = 255;
        return bytes;
    }

    private static bool Same(Bitmap left, Bitmap right)
        => left.Size == right.Size && SHA256.HashData(BmpPayloadService.ReadPixels(left))
            .AsSpan().SequenceEqual(SHA256.HashData(BmpPayloadService.ReadPixels(right)));

    private sealed class PreviewOnlyCatalog : IImageCatalogService
    {
        public int PreviewCalls { get; private set; }
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => [];
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
            => throw new InvalidOperationException("缩略图触发了全动画解码");
        public DecodedFrame DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            var bitmap = new Bitmap(1, 1);
            bitmap.SetPixel(0, 0, new PaletteService().Load(project)[1]);
            return new DecodedFrame(descriptor, bitmap);
        }
    }

    private sealed class FramesOnlyCatalog : IImageCatalogService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => [];
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        {
            Calls++;
            var bitmap = new Bitmap(1, 1);
            bitmap.SetPixel(0, 0, Color.Red);
            return [new DecodedFrame(asset.Frames[0], bitmap)];
        }
    }
}
