using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImageManager.Core;

public sealed record StrategyAnimationData(
    IReadOnlyList<Bitmap> Frames, IReadOnlyList<Bitmap> PlaneFrames, IReadOnlyList<int> PlaybackSequence,
    byte[] RecordBytes, byte[] PlaneBytes, byte[] TailBytes, int IntervalMs, int Width, int Height,
    int RecordCount, int DeclaredFrameCount, int ActualFrameCount, int Flags, int SoundParameter,
    string Diagnostics) : IDisposable
{
    public byte[] OriginalBytes { get; init; } = [];
    public byte[] EncodedImageBytes { get; init; } = [];
    public string PayloadFormat { get; init; } = "RAW8";
    public int SecondaryPlaneCount { get; init; }
    public bool UsesSecondaryLayer { get; init; }
    public void Dispose()
    {
        foreach (var frame in Frames) frame.Dispose();
        foreach (var frame in PlaneFrames) frame.Dispose();
    }
}

public sealed class StrategyAnimationPackageCodec : IStrategyAnimationPackageCodec
{
    // 6.5 consumer evidence (exact sample SHA is registered beside the disassembly):
    // 40273F reads the header to object+1C; 4025BE and 402838/40285D consume both plane counts.
    // 402884 reads record[0], record[2] and conditional record+4; 40298E reads record[1]*0x300 after both planes.
    // 402C7F -> 417024 -> 40690C -> 415A64 uses record[3]*6+4 as source color weight, not a layer opcode.
    // See 验证/图片交换与单文件验收-20261018/6.5-动画消费者-402537-402e50.txt and the three blend traces.
    // Secondary planes must stay separate. Their exact game-background/32bpp-mask use is not a simple overlay.
    private const int RecordSize = 20;
    private sealed record Layout(int RecordCount, int PrimaryCount, int SecondaryCount, int ActualCount,
        int Width, int Height, int Interval, int Flags, int Sound, int PayloadLength, bool Standard,
        string Format, bool UsesSecondary, bool CountsVerified)
    {
        public int RecordLength => RecordCount * RecordSize;
        public int PlaneSize => checked(Width * Height);
    }

    private static Layout Inspect(byte[] bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length < RecordSize * 2) throw new InvalidDataException("策略动画不足两个 20 字节记录。");
        var records = bytes[0] + 1; var primary = bytes[1]; var secondary = bytes[2];
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        var delay = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        if (records <= 1 || primary == 0 || width is <= 0 or > 640 || height is <= 0 or > 640)
            throw new InvalidDataException($"策略动画头部不合法：记录 {records}，主平面 {primary}，尺寸 {width}×{height}。");
        var recordLength = checked(records * RecordSize);
        if (recordLength >= bytes.Length) throw new InvalidDataException("策略动画记录区超出载荷。");
        var payload = bytes.AsSpan(recordLength).ToArray();
        var standard = ImageRendering.IsStandardImage(payload);
        var count = primary + secondary; var verified = true; int length; string format;
        if (standard)
        {
            length = BmpPayloadService.GetStandardImageLength(payload);
            // Validated BMP metadata already includes its pixel boundary. Probing
            // a package need not allocate the complete (often very tall) strip.
            var bmp = BmpPayloadService.ReadLayout(payload);
            Size stripSize;
            if (bmp != null) stripSize = new Size(bmp.Width, bmp.Height);
            else
            {
                using var strip = ImageRendering.DecodeStandardImage(payload.AsSpan(0, length).ToArray());
                stripSize = strip.Size;
            }
            if (stripSize.Width != width || stripSize.Height % height != 0)
                throw new InvalidDataException($"嵌入图片 {stripSize.Width}×{stripSize.Height} 与帧尺寸 {width}×{height} 不一致。");
            count = stripSize.Height / height;
            if (count < primary) throw new InvalidDataException("嵌入图片主平面不足。");
            verified = count == primary + secondary;
            format = payload[0] == 'B' ? "BMP" : payload[0] == 0x89 ? "PNG" : "JPEG";
        }
        else
        {
            var available = payload.Length / checked(width * height);
            if (available < primary) throw new InvalidDataException("RAW8 载荷没有足够的声明主平面。");
            // Some old flags occupy byte 2. Never interpret incomplete data as extra frames.
            if (count > available) { count = primary; verified = false; }
            length = checked(count * width * height);
            format = "RAW8";
        }
        var usesSecondary = false;
        for (var record = 1; record < records; record++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = record * RecordSize;
            var reference = bytes[offset];
            if (reference >= primary || reference >= count)
                throw new InvalidDataException($"播放记录 {record} 引用主平面 {reference}，声明只有 {primary} 个。");
            var enabled = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4)) != 0;
            usesSecondary |= enabled;
            if (enabled && verified && secondary > 0 && bytes[offset + 2] >= secondary)
                throw new InvalidDataException($"播放记录 {record} 引用辅助平面越界。");
        }
        return new(records, primary, secondary, count, width, height, delay is >= 30 and <= 2000 ? delay : 100,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)), length, standard, format, usesSecondary, verified);
    }

    private static string Describe(Layout layout, int fileLength)
        => $"{layout.Format}；记录 {layout.RecordCount}；播放步 {layout.RecordCount - 1}；主/辅助平面 {layout.PrimaryCount}/{layout.SecondaryCount}；" +
           $"实际平面 {layout.ActualCount}；{layout.Width}×{layout.Height}；间隔 {layout.Interval}ms；" +
           $"保留尾部 {fileLength - layout.RecordLength - layout.PayloadLength} 字节" +
           (!layout.CountsVerified ? "；辅助计数未验证，保留未知字节" : "") +
           (layout.UsesSecondary ? "；位置/透明度预览（游戏场景及辅助遮罩保留）" : "");

    public static string Probe(byte[] bytes) => Describe(Inspect(bytes), bytes.Length);
    public static string ProbeFile(string path) => Probe(File.ReadAllBytes(path));

    /// <summary>Decodes one playback step, preserving the same common canvas as full playback.</summary>
    public Bitmap DecodePreviewFrame(ProjectProfile project, byte[] bytes, int frameIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var layout = Inspect(bytes, cancellationToken);
        if (frameIndex < 0 || frameIndex >= layout.RecordCount - 1)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        var records = bytes.AsSpan(0, layout.RecordLength).ToArray();
        var canvas = PlaybackCanvas(records, layout, cancellationToken);
        var record = frameIndex + 1;
        var reference = records[record * RecordSize];
        var selector = records[record * RecordSize + 1];
        var tailOffset = layout.RecordLength + layout.PayloadLength;
        var tailLength = bytes.Length - tailOffset;
        cancellationToken.ThrowIfCancellationRequested();
        Bitmap source;
        if (layout.Standard)
        {
            using var strip = ImageRendering.DecodeStandardImage(bytes.AsSpan(layout.RecordLength, layout.PayloadLength).ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            using var cropped = ImageRendering.Crop(strip,
                new Rectangle(0, reference * layout.Height, layout.Width, layout.Height));
            source = ImageRendering.ApplyKeyTransparency(cropped, false);
        }
        else
        {
            var palette = ResolveRawPalette(project, new PaletteService().Load(project),
                bytes.AsSpan(tailOffset), selector);
            cancellationToken.ThrowIfCancellationRequested();
            source = DecodeRawPlane(bytes.AsSpan(layout.RecordLength + reference * layout.PlaneSize,
                layout.PlaneSize).ToArray(), layout, palette);
        }
        using (source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = ComposePlaybackRecord(project, source, records, record, tailLength, canvas);
            if (!cancellationToken.IsCancellationRequested) return frame;
            frame.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public StrategyAnimationData Decode(ProjectProfile project, byte[] bytes)
    {
        var layout = Inspect(bytes);
        var records = bytes.AsSpan(0, layout.RecordLength).ToArray();
        var payload = bytes.AsSpan(layout.RecordLength, layout.PayloadLength).ToArray();
        var tail = bytes.AsSpan(layout.RecordLength + layout.PayloadLength).ToArray();
        IReadOnlyList<Color> palette = layout.Standard ? Array.Empty<Color>() : new PaletteService().Load(project);
        var planes = new List<Bitmap>(layout.ActualCount);
        var playback = new List<Bitmap>(layout.RecordCount - 1);
        try
        {
            if (layout.Standard)
            {
                using var strip = ImageRendering.DecodeStandardImage(payload);
                for (var i = 0; i < layout.ActualCount; i++)
                {
                    using var frame = ImageRendering.Crop(strip, new Rectangle(0, i * layout.Height, layout.Width, layout.Height));
                    planes.Add(ImageRendering.ApplyKeyTransparency(frame, false));
                }
            }
            else
            {
                for (var i = 0; i < layout.ActualCount; i++)
                {
                    var selectors = Enumerable.Range(1, layout.RecordCount - 1).Where(record => records[record * RecordSize] == i)
                        .Select(record => records[record * RecordSize + 1]).Distinct().ToArray();
                    var planePalette = ResolveRawPalette(project, palette, tail,
                        selectors.Length == 1 ? selectors[0] : 255);
                    planes.Add(DecodeRawPlane(payload.AsSpan(i * layout.PlaneSize, layout.PlaneSize).ToArray(), layout, planePalette));
                }
            }
            var sequence = new List<int>();
            var canvas = PlaybackCanvas(records, layout);
            for (var record = 1; record < layout.RecordCount; record++)
            {
                var reference = records[record * RecordSize]; sequence.Add(reference);
                var selector = records[record * RecordSize + 1];
                using var source = !layout.Standard && UsesExtraPalette(project, selector, tail.Length)
                    ? DecodeRawPlane(payload.AsSpan(reference * layout.PlaneSize, layout.PlaneSize).ToArray(), layout,
                        ResolveRawPalette(project, palette, tail, selector))
                    : new Bitmap(planes[reference]);
                playback.Add(ComposePlaybackRecord(project, source, records, record, tail.Length, canvas));
            }
            return new StrategyAnimationData(playback, planes, sequence, records,
                layout.Standard ? [] : payload, tail, layout.Interval, layout.Width, layout.Height,
                layout.RecordCount, layout.PrimaryCount, layout.ActualCount, layout.Flags, layout.Sound,
                Describe(layout, bytes.Length) + (!layout.Standard && project.Version == ProjectVersion.Ccz66
                    ? "；6.6 RAW8 使用项目调色板，遗留尾部色表仅保留" : ""))
            {
                OriginalBytes = (byte[])bytes.Clone(), EncodedImageBytes = layout.Standard ? payload : [],
                PayloadFormat = layout.Format, SecondaryPlaneCount = layout.SecondaryCount,
                UsesSecondaryLayer = layout.UsesSecondary
            };
        }
        catch
        {
            foreach (var frame in planes) frame.Dispose();
            foreach (var frame in playback) frame.Dispose();
            throw;
        }
    }

    private static Rectangle PlaybackCanvas(byte[] records, Layout layout, CancellationToken cancellationToken = default)
    {
        var bounds = new Rectangle(0, 0, layout.Width, layout.Height);
        for (var record = 1; record < layout.RecordCount; record++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(record * RecordSize + 8, 4));
            var y = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(record * RecordSize + 12, 4));
            if (x is < -4096 or > 4096 || y is < -4096 or > 4096)
                throw new InvalidDataException("动画位置参数超出预览边界，保留原记录，禁止生成无限画布。");
            bounds = Rectangle.Union(bounds, new Rectangle(x, y, layout.Width, layout.Height));
        }
        if ((long)bounds.Width * bounds.Height > 16_777_216) throw new InvalidDataException("动画预览画布超出大小限制。");
        return bounds;
    }

    private static Bitmap ComposePlaybackRecord(ProjectProfile project, Bitmap source, byte[] records, int record,
        int tailLength, Rectangle canvas)
    {
        var at = record * RecordSize;
        var selector = records[at + 1];
        var needsBlend = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(at + 4, 4)) != 0 || records[at + 2] != 0 ||
                         project.Version != ProjectVersion.Ccz66 && selector < 40 &&
                         UsesExtraPalette(project, selector, tailLength);
        var weight = needsBlend && records[at + 3] <= 16 ? 4 + records[at + 3] * 6 : 100;
        var x = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(at + 8, 4));
        var y = BinaryPrimitives.ReadInt32LittleEndian(records.AsSpan(at + 12, 4));
        return ComposePlaybackFrame(source, weight, canvas, x, y);
    }

    private static Bitmap ComposePlaybackFrame(Bitmap source, int percent, Rectangle canvas, int x, int y)
    {
        using var adjusted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var pixels = BmpPayloadService.ReadPixels(source);
        for (var at = 3; at < pixels.Length; at += 4) pixels[at] = (byte)(pixels[at] * percent / 100);
        var data = adjusted.LockBits(new Rectangle(Point.Empty, adjusted.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        { for (var row = 0; row < adjusted.Height; row++) Marshal.Copy(pixels, row * adjusted.Width * 4, IntPtr.Add(data.Scan0, row * data.Stride), adjusted.Width * 4); }
        finally { adjusted.UnlockBits(data); }
        var output = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(adjusted, x - canvas.X, y - canvas.Y);
        return output;
    }

    // The verified 6.6 loader (402879) takes predecoded RGB planes and never
    // loads record[1]'s tail palette. Its RAW converter (40E81C) uses the project
    // palette. Mcall00 still carries a 768-byte BRG table with 0..15 channels;
    // treating that legacy table as RGB8 makes the otherwise intact image black.
    private static bool UsesExtraPalette(ProjectProfile project, int selector, int tailLength)
        => project.Version != ProjectVersion.Ccz66 && selector != 255 &&
           (long)(selector + 1) * 768 <= tailLength;

    private static IReadOnlyList<Color> ResolveRawPalette(ProjectProfile project,
        IReadOnlyList<Color> projectPalette, ReadOnlySpan<byte> tail, int selector)
        => UsesExtraPalette(project, selector, tail.Length)
            ? ReadExtraPalette(tail.Slice(selector * 768, 768)) : projectPalette;

    private static IReadOnlyList<Color> ReadExtraPalette(ReadOnlySpan<byte> bytes)
    {
        var palette = new Color[256];
        for (var i = 0; i < palette.Length; i++)
            palette[i] = Color.FromArgb(255, bytes[i * 3 + 1], bytes[i * 3 + 2], bytes[i * 3]);
        return palette;
    }

    private static Bitmap DecodeRawPlane(byte[] pixels, Layout layout, IReadOnlyList<Color> palette)
    {
        var transparentPalette = palette.Select((color, index) => index == 0 ? Color.Magenta : color).ToArray();
        using var bitmap = ImageRendering.DecodeIndexed(pixels, layout.Width, layout.Height, transparentPalette);
        return ImageRendering.ApplyKeyTransparency(bitmap, false);
    }

    public byte[] RebuildFramePlanes(ProjectProfile project, StrategyAnimationData package,
        IReadOnlyDictionary<int, Bitmap> replacementPlanes)
    {
        if (package.OriginalBytes.Length == 0) throw new InvalidDataException("动画包缺少原始字节快照，拒绝重建。");
        var layout = Inspect(package.OriginalBytes);
        if (replacementPlanes.Keys.Any(index => index < 0 || index >= layout.ActualCount))
            throw new ArgumentOutOfRangeException(nameof(replacementPlanes));
        if (replacementPlanes.Count == 0) return (byte[])package.OriginalBytes.Clone();
        foreach (var (index, bitmap) in replacementPlanes)
            if (bitmap.Width != layout.Width || bitmap.Height != layout.Height)
                throw new InvalidDataException($"动画平面 {index} 尺寸必须为 {layout.Width}×{layout.Height}。");
        byte[] payload;
        if (layout.Standard)
        {
            using var strip = ImageRendering.DecodeStandardImage(package.EncodedImageBytes);
            using (var graphics = Graphics.FromImage(strip))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                foreach (var (index, bitmap) in replacementPlanes)
                    graphics.DrawImageUnscaled(bitmap, 0, index * layout.Height);
            }
            payload = BmpPayloadService.Rebuild(package.EncodedImageBytes, strip);
        }
        else
        {
            var palette = new PaletteService().RequireWritePalette(project);
            payload = (byte[])package.PlaneBytes.Clone();
            foreach (var (index, bitmap) in replacementPlanes)
            {
                var selectors = Enumerable.Range(1, layout.RecordCount - 1).Where(record => package.RecordBytes[record * RecordSize] == index)
                    .Select(record => package.RecordBytes[record * RecordSize + 1]).Distinct().ToArray();
                var usable = selectors.Where(selector => UsesExtraPalette(project, selector, package.TailBytes.Length)).ToArray();
                if (usable.Length > 1 || usable.Length == 1 && selectors.Length > 1)
                    throw new InvalidDataException("该动画平面使用多套调色板，无法唯一确定写入颜色；原始数据保持不变。");
                var targetPalette = ResolveRawPalette(project, palette, package.TailBytes,
                    usable.Length == 1 ? usable[0] : 255);
                var original = package.PlaneBytes.AsSpan(index * layout.PlaneSize, layout.PlaneSize).ToArray();
                BmpPayloadService.RebuildRaw8(original, layout.Width, layout.Height, bitmap, targetPalette)
                    .CopyTo(payload, index * layout.PlaneSize);
            }
        }
        var result = new byte[checked(package.RecordBytes.Length + payload.Length + package.TailBytes.Length)];
        package.RecordBytes.CopyTo(result, 0); payload.CopyTo(result, package.RecordBytes.Length);
        package.TailBytes.CopyTo(result, package.RecordBytes.Length + payload.Length);
        var check = Inspect(result);
        if (check.ActualCount != layout.ActualCount || check.Width != layout.Width || check.Height != layout.Height ||
            !result.AsSpan(0, package.RecordBytes.Length).SequenceEqual(package.RecordBytes))
            throw new InvalidDataException("动画写后结构复读不一致。");
        using var reread = Decode(project, result);
        return result;
    }
}

public static class StrategyAnimationDecoder
{
    private static readonly StrategyAnimationPackageCodec Codec = new();
    public static StrategyAnimationData Decode(ProjectProfile project, byte[] bytes) => Codec.Decode(project, bytes);
}
