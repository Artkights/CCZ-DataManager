using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ImageManager.Core;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using DrawingSize = System.Drawing.Size;

namespace ImageManager.Tests;

internal static class ImageImportSourceDecoderTests
{
    private sealed record Fixture(string Format, byte[] Bytes, DrawingSize Size, Color Left, Color Right);
    private static readonly Color FirstLeft = Color.FromArgb(220, 40, 80);
    private static readonly Color FirstRight = Color.FromArgb(25, 70, 210);

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.SourceDecoder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixtures = Fixtures();
            foreach (var fixture in fixtures)
            {
                CheckDirectDecoder(fixture);
                CheckPngPrepareAndCommit(root, fixture);
                CheckSpecializedRoutes(root, fixture);
            }
            CheckInvalidInputs(root, fixtures);
            CheckTransparentIndexedTargets(root);
            CheckTrueColorPrecision(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IReadOnlyList<Fixture> Fixtures()
    {
        using var gif = MarkedSharp(17, 11, FirstLeft, FirstRight);
        using var gifSecond = MarkedSharp(17, 11, Color.Lime, Color.Yellow);
        gif.Frames.AddFrame(gifSecond.Frames.RootFrame);
        using var gifBytes = new MemoryStream(); gif.Save(gifBytes, new GifEncoder());

        using var tiff = MarkedSharp(19, 13, FirstLeft, FirstRight);
        using var tiffSecond = MarkedSharp(19, 13, Color.Lime, Color.Yellow);
        tiff.Frames.AddFrame(tiffSecond.Frames.RootFrame);
        using var tiffBytes = new MemoryStream(); tiff.Save(tiffBytes, new TiffEncoder());

        var icoFirst = MarkedPng(13, 9, FirstLeft, FirstRight);
        var icoSecond = MarkedPng(29, 19, Color.Lime, Color.Yellow);
        var ico = Ico(icoFirst, new DrawingSize(13, 9), icoSecond, new DrawingSize(29, 19));

        var alphaLeft = Color.FromArgb(128, 220, 40, 80);
        using var webp = MarkedSharp(21, 15, alphaLeft, FirstRight);
        using var webpBytes = new MemoryStream();
        webp.Save(webpBytes, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        return
        [
            new("GIF", gifBytes.ToArray(), new(17, 11), FirstLeft, FirstRight),
            new("TIFF", tiffBytes.ToArray(), new(19, 13), FirstLeft, FirstRight),
            new("ICO", ico, new(13, 9), FirstLeft, FirstRight),
            new("WEBP", webpBytes.ToArray(), new(21, 15), alphaLeft, FirstRight)
        ];
    }

    private static void CheckDirectDecoder(Fixture fixture)
    {
        using var bitmap = ImageImportSourceDecoder.Decode(fixture.Bytes);
        Require(bitmap.Size == fixture.Size, $"{fixture.Format} 未采用第一帧/页/ICO第一目录项的实际尺寸。");
        Require(Near(bitmap.GetPixel(1, 1), fixture.Left, 0) && Near(bitmap.GetPixel(bitmap.Width - 2, 1), fixture.Right, 0),
            $"{fixture.Format} 解码颜色不是独立写入的首帧红/蓝标记，或alpha丢失。");
        var description = ImageImportSourceDecoder.Describe(fixture.Bytes);
        Require(description.Contains(fixture.Format, StringComparison.OrdinalIgnoreCase), $"{fixture.Format} 实际内容识别描述错误：{description}");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { using var unused = ImageImportSourceDecoder.Decode(fixture.Bytes, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, $"{fixture.Format} 解码忽略了已经取消的令牌。");
    }

    private static void CheckPngPrepareAndCommit(string root, Fixture fixture)
    {
        var folder = Path.Combine(root, fixture.Format); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "LogoScene.e5");
        File.WriteAllBytes(path, Archive(Png(12, 8, Color.Black), Png(3, 5, Color.Orange)));
        var original = File.ReadAllBytes(path);
        // No valid extension or numeric naming convention participates in content detection.
        var source = Path.Combine(folder, "随意文件名_没有编号.data"); File.WriteAllBytes(source, fixture.Bytes);
        var resource = Resource(path, ImageCategory.Scene, ResourceKind.E5Indexed);
        var target = Asset(resource, [Frame(path, 1, "image", 12, 8)]);
        var project = Project(folder);
        var history = new HistoryService(Path.Combine(folder, "history"));
        using var prepared = Prepare(project, resource, ImageImportOperation.Replace, new(target, "image", source));
        Require(prepared.CanCommit, Errors(prepared));
        Require(File.ReadAllBytes(path).SequenceEqual(original) && history.List(project).Count == 0,
            $"{fixture.Format} 准备阶段写入了目标或历史。");
        var row = prepared.Rows.Single();
        Require(row.SourceSize == fixture.Size && row.OutputSize == new DrawingSize(12, 8),
            $"{fixture.Format} 没有从任意素材尺寸适配目标PNG画布。");
        Require(row.SourceFormat.Contains(fixture.Format, StringComparison.OrdinalIgnoreCase) && row.OutputFormat == "PNG",
            $"{fixture.Format} 没有按实际内容识别并转换为目标PNG。");
        Require(Near(row.After!.GetPixel(2, 2), fixture.Left, 2) && Near(row.After.GetPixel(9, 2), fixture.Right, 2),
            $"{fixture.Format} 普通PNG候选没有保留首帧标记/alpha。");
        var candidate = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes);
        Require(candidate.ReadDecoded(1).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            $"{fixture.Format} 候选未实际编码成PNG。");
        Require(candidate.ReadDecoded(2).SequenceEqual(E5Archive.Parse(path, original).ReadDecoded(2)), "素材转码误改相邻条目。");
        var mutation = new ImageMutationService(history);
        var result = mutation.Commit(project, prepared.Plan!);
        Require(result.Success, string.Join("；", result.Diagnostics));
        var codec = new GenericE5ResourceCodec();
        var stored = codec.ReadAssets(project, resource).Single(item => item.Frames.Single().ImageNumber == 1);
        using var decoded = codec.DecodeFrames(project, stored).Single();
        Require(EqualPixels(row.After, decoded.Bitmap), $"{fixture.Format} 实际提交后公开图库解码与候选预览不一致。");
        Require(Near(decoded.Bitmap.GetPixel(2, 2), fixture.Left, 2), $"{fixture.Format} 落盘后首帧像素标记或alpha改变。");
        Require(history.Undo(project, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(original),
            $"{fixture.Format} 转码提交未能逐字节撤回。");
        using var append = Prepare(project, resource, ImageImportOperation.Append, new(null, "image", source));
        Require(append.CanCommit && append.Rows.Single().OutputSize == new DrawingSize(3, 5),
            $"{fixture.Format} 新增未按末尾模板实际尺寸准备。");
    }

    private static void CheckSpecializedRoutes(string root, Fixture fixture)
    {
        var folder = Path.Combine(root, fixture.Format);
        var source = Path.Combine(folder, "随意文件名_没有编号.data");
        var project = Project(folder);

        var facePath = Path.Combine(folder, "Face.e5"); File.WriteAllBytes(facePath, Archive(Png(120, 120, Color.Black)));
        var faceResource = Resource(facePath, ImageCategory.Portrait, ResourceKind.FaceComposite);
        var faceTarget = Asset(faceResource, [Frame(facePath, 1, "face-small", 120, 120, "Face.e5")]);
        using (var prepared = Prepare(project, faceResource, ImageImportOperation.Replace, new(faceTarget, "image", source)))
        {
            Require(prepared.CanCommit, $"{fixture.Format} Face入口失败：{Errors(prepared)}");
            var row = prepared.Rows.Single();
            Require(row.OutputSize == new DrawingSize(120, 120) && row.OutputFormat == "PNG32" &&
                    Near(row.After!.GetPixel(20, 60), fixture.Left, 3) && Near(row.After.GetPixel(100, 60), fixture.Right, 3),
                $"{fixture.Format} Face入口没有使用首帧并按120头像规范转换。");
        }

        var itemPath = Path.Combine(folder, "Item.e5"); File.WriteAllBytes(itemPath, Archive(Png(16, 16, Color.Black), Png(32, 32, Color.Black)));
        var itemResource = Resource(itemPath, ImageCategory.ItemIcon, ResourceKind.E5Indexed);
        var itemTarget = Asset(itemResource, [Frame(itemPath, 1, "small", 16, 16), Frame(itemPath, 2, "large", 32, 32)]);
        using (var prepared = Prepare(project, itemResource, ImageImportOperation.Replace, new(itemTarget, "large", source)))
        {
            Require(prepared.CanCommit, $"{fixture.Format} 图标入口失败：{Errors(prepared)}");
            var large = prepared.Rows.Single(row => row.Role == "large");
            Require(large.OutputSize == new DrawingSize(32, 32) && prepared.Rows.Any(row => row.Role == "small") &&
                    NearRgb(large.After!.GetPixel(6, 16), fixture.Left, 2) && NearRgb(large.After.GetPixel(25, 16), fixture.Right, 2),
                $"{fixture.Format} 图标入口没有按大小图规范转换首帧。");
        }

        var dllPath = Path.Combine(folder, "Interface.dll"); FixtureDll(dllPath);
        var dllResource = Resource(dllPath, ImageCategory.Interface, ResourceKind.DllBitmap);
        var dllTarget = Asset(dllResource, [Frame(dllPath, 201, "image", 12, 8)]);
        using (var prepared = Prepare(project, dllResource, ImageImportOperation.Replace, new(dllTarget, "image", source)))
        {
            Require(prepared.CanCommit, $"{fixture.Format} DLL入口失败：{Errors(prepared)}");
            var row = prepared.Rows.Single();
            Require(row.OutputSize == new DrawingSize(12, 8) && row.OutputFormat == "DIB32" &&
                    Near(row.After!.GetPixel(2, 2), fixture.Left, 2) && Near(row.After.GetPixel(9, 2), fixture.Right, 2),
                $"{fixture.Format} DLL入口没有按目标DIB画布转换首帧/alpha。");
        }
    }

    private static void CheckInvalidInputs(string root, IReadOnlyList<Fixture> fixtures)
    {
        var folder = Path.Combine(root, "invalid"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "InvalidSource.e5"); File.WriteAllBytes(path, Archive(Png(12, 8, Color.Red)));
        var original = File.ReadAllBytes(path);
        var project = Project(folder);
        var resource = Resource(path, ImageCategory.Scene, ResourceKind.E5Indexed);
        var target = Asset(resource, [Frame(path, 1, "image", 12, 8)]);
        var history = new HistoryService(Path.Combine(folder, "history"));
        foreach (var fixture in fixtures)
        {
            var pathSource = Path.Combine(folder, "伪装成图片." + fixture.Format.ToLowerInvariant());
            File.WriteAllBytes(pathSource, "not an image, despite its filename"u8.ToArray());
            using (var prepared = Prepare(project, resource, ImageImportOperation.Replace, new(target, "image", pathSource)))
                Require(!prepared.CanCommit && prepared.Plan == null && prepared.Rows.Any(row => row.Error != null),
                    $"{fixture.Format}扩展名伪装绕过了解码验证。");
            File.WriteAllBytes(pathSource, fixture.Bytes[..Math.Min(12, fixture.Bytes.Length)]);
            using (var prepared = Prepare(project, resource, ImageImportOperation.Replace, new(target, "image", pathSource)))
                Require(!prepared.CanCommit && prepared.Plan == null, $"损坏的{fixture.Format}文件产生了可提交候选。");
            Require(File.ReadAllBytes(path).SequenceEqual(original) && history.List(project).Count == 0,
                $"伪装或损坏{fixture.Format}输入写入了目标或历史。");
        }
    }

    private static void CheckTransparentIndexedTargets(string root)
    {
        var folder = Path.Combine(root, "transparent-indexed"); Directory.CreateDirectory(folder);
        var palette = new byte[1024];
        for (var index = 0; index < 256; index++) palette[index * 4 + 1] = 255;
        var colors = PrecisionPalette();
        for (var index = 0; index < colors.Length; index++)
        {
            palette[index * 4] = colors[index].B; palette[index * 4 + 1] = colors[index].G;
            palette[index * 4 + 2] = colors[index].R;
        }
        File.WriteAllBytes(Path.Combine(folder, "tsb"), palette);
        var source = Path.Combine(folder, "完全透明黑色_任意名称.data");
        File.WriteAllBytes(source, PrecisionSource());
        var raw = new byte[] { 1, 5, 4, 5, 0xa5, 0x3e };
        var bmp = IndexedBlackBmp();
        foreach (var targetKind in new[] { "RAW8", "BMP8" })
        {
            var path = Path.Combine(folder, targetKind + ".e5");
            var original = Archive(targetKind == "RAW8" ? raw : bmp); File.WriteAllBytes(path, original);
            var resource = Resource(path, ImageCategory.Scene, ResourceKind.E5Indexed);
            var target = Asset(resource, [Frame(path, 1, "image", 2, 2)]);
            using var prepared = Prepare(Project(folder), resource, ImageImportOperation.Replace, new(target, "image", source));
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(1);
            byte[] indexes;
            if (targetKind == "RAW8")
            {
                indexes = payload[..4];
                Require(payload[^2] == 0xa5 && payload[^1] == 0x3e, "透明RAW素材转换改变了合法两字节尾部。");
            }
            else
            {
                Require(BmpPayloadService.TryGetIndexes(payload, out indexes), "透明BMP素材转换丢失了8位目标布局。");
                Require(payload.AsSpan(54, colors.Length * 4).SequenceEqual(bmp.AsSpan(54, colors.Length * 4)), "透明BMP素材转换改变了目标嵌入调色板。");
            }
            Require(indexes.SequenceEqual(new byte[] { 0, 2, 3, 2 }),
                $"{targetKind}的独立素材量化错误：透明黑应为0，alpha1/128须按原RGB量化，重复色应取首个精确色且不继承原索引。实际：{string.Join(",", indexes)}");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "索引透明候选准备写入了目标文件。");
        }
    }

    private static Color[] PrecisionPalette() =>
    [
        Color.FromArgb(247, 0, 255), Color.Black, Color.FromArgb(220, 40, 80),
        Color.FromArgb(240, 40, 80), Color.FromArgb(239, 39, 79), Color.FromArgb(220, 40, 80)
    ];

    private static byte[] PrecisionSource()
    {
        using var bitmap = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0));
        bitmap.SetPixel(1, 0, Color.FromArgb(1, 220, 40, 80));
        bitmap.SetPixel(0, 1, Color.FromArgb(128, 240, 40, 80));
        bitmap.SetPixel(1, 1, Color.FromArgb(220, 40, 80));
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static void CheckTrueColorPrecision(string root)
    {
        var folder = Path.Combine(root, "true-color-precision"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "alpha1_and_alpha128.data"); File.WriteAllBytes(source, PrecisionSource());
        foreach (var bits in new[] { 24, 32 })
        {
            var path = Path.Combine(folder, "TrueColor" + bits + ".e5");
            using var template = new Bitmap(2, 2, bits == 24 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
            using var stream = new MemoryStream(); template.Save(stream, ImageFormat.Bmp);
            var original = Archive(stream.ToArray()); File.WriteAllBytes(path, original);
            var resource = Resource(path, ImageCategory.Scene, ResourceKind.E5Indexed);
            var target = Asset(resource, [Frame(path, 1, "image", 2, 2)]);
            using var prepared = Prepare(Project(folder), resource, ImageImportOperation.Replace, new(target, "image", source));
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(1);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(10));
            var stride = (2 * bits + 31) / 32 * 4;
            var alphaOne = offset + stride + bits / 8; // Top-right in a bottom-up BMP.
            var alphaHalf = offset; // Bottom-left.
            Require(payload[alphaOne] == 80 && payload[alphaOne + 1] == 40 && payload[alphaOne + 2] == 220 &&
                    payload[alphaHalf] == 80 && payload[alphaHalf + 1] == 40 && payload[alphaHalf + 2] == 240,
                $"BMP{bits}文件导入对非零alpha RGB做了GDI预乘舍入或把alpha1误判透明。");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "真彩精度候选准备写入了目标文件。");
        }
    }

    private static byte[] IndexedBlackBmp()
    {
        var colors = PrecisionPalette(); var offset = 54 + colors.Length * 4;
        var bytes = new byte[offset + 8]; bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(46), colors.Length);
        for (var index = 0; index < colors.Length; index++)
        {
            var at = 54 + index * 4; bytes[at] = colors[index].B;
            bytes[at + 1] = colors[index].G; bytes[at + 2] = colors[index].R;
        }
        bytes[offset] = 4; bytes[offset + 1] = 5; bytes[offset + 4] = 1; bytes[offset + 5] = 5;
        return bytes;
    }

    private static SixLabors.ImageSharp.Image<Rgba32> MarkedSharp(int width, int height, Color left, Color right)
    {
        var image = new SixLabors.ImageSharp.Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var pixel = x < width / 2 ? left : right;
            image[x, y] = new Rgba32(pixel.R, pixel.G, pixel.B, pixel.A);
        }
        return image;
    }

    private static byte[] MarkedPng(int width, int height, Color left, Color right)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, x < width / 2 ? left : right);
        using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png); return output.ToArray();
    }

    private static byte[] Ico(byte[] first, DrawingSize firstSize, byte[] second, DrawingSize secondSize)
    {
        var bytes = new byte[38 + first.Length + second.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        WriteEntry(6, firstSize, first.Length, 38); WriteEntry(22, secondSize, second.Length, 38 + first.Length);
        first.CopyTo(bytes, 38); second.CopyTo(bytes, 38 + first.Length); return bytes;
        void WriteEntry(int at, DrawingSize size, int length, int offset)
        {
            bytes[at] = (byte)size.Width; bytes[at + 1] = (byte)size.Height;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 4), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 6), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 8), length); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 12), offset);
        }
    }

    private static PreparedImageImport Prepare(ProjectProfile project, ImageResourceDescriptor resource,
        ImageImportOperation operation, ImageImportAssignment assignment)
        => new ImageImportPreparationService().PrepareAsync(new(project, resource, operation, [assignment])).GetAwaiter().GetResult();
    private static ProjectProfile Project(string root) => new(root, "decoder fixture", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
    private static ImageResourceDescriptor Resource(string path, ImageCategory category, ResourceKind kind)
        => new(Path.GetFileName(path), Path.GetFileName(path), category, kind, [path], 1, true, true, false, "", "");
    private static LogicalImageAsset Asset(ImageResourceDescriptor resource, IReadOnlyList<FrameDescriptor> frames)
        => new(resource.Key, 1, "source fixture", resource.Category, resource.Kind, frames, [], false, false, "");
    private static FrameDescriptor Frame(string path, int number, string action, int width, int height, string? key = null)
        => new(key ?? Path.GetFileName(path), path, number, 0, 1, action, "none", width, height, 2052);
    private static byte[] Png(int width, int height, Color color) => MarkedPng(width, height, color, color);
    private static byte[] Archive(params byte[][] payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Length * 12 + 4;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        "Ls11"u8.CopyTo(bytes); for (var index = 0; index < 256; index++) bytes[16 + index] = (byte)index;
        for (var index = 0; index < payloads.Length; index++)
        {
            var at = E5Archive.IndexOffset + index * 12;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 8), offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }
    private static bool Near(Color left, Color right, int tolerance) => Math.Abs(left.A - right.A) <= tolerance && NearRgb(left, right, tolerance);
    private static bool NearRgb(Color left, Color right, int tolerance)
        => Math.Abs(left.R - right.R) <= tolerance && Math.Abs(left.G - right.G) <= tolerance && Math.Abs(left.B - right.B) <= tolerance;
    private static bool EqualPixels(Bitmap left, Bitmap right)
    {
        if (left.Size != right.Size) return false;
        for (var y = 0; y < left.Height; y++) for (var x = 0; x < left.Width; x++)
            if (left.GetPixel(x, y).ToArgb() != right.GetPixel(x, y).ToArgb()) return false;
        return true;
    }
    private static string Errors(PreparedImageImport prepared) => string.Join("；", prepared.Rows.Select(row => row.Error).Where(error => error != null));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void FixtureDll(string path)
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), path);
        var dib = new byte[40 + 12 * 8 * 4];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -8); BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), 12 * 8 * 4);
        var handle = BeginUpdateResourceW(path, false); Require(handle != IntPtr.Zero, "无法建立DLL来源格式测试。");
        var completed = false;
        try
        {
            Require(UpdateResourceW(handle, (IntPtr)2, (IntPtr)201, 2052, dib, dib.Length), "无法添加测试位图。");
            Require(EndUpdateResourceW(handle, false), "无法保存测试位图。"); completed = true;
        }
        finally { if (!completed) EndUpdateResourceW(handle, true); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndUpdateResourceW(IntPtr handle, bool discard);
}
