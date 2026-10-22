using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class RasterFileImportTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.RasterFileImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StandardImages(root);
            OrdinaryTargetSpecifications(root);
            Portraits(root);
            PortraitExpressions(root);
            E5Icons(root);
            DllImports(root);
            Dll32RowOrderAndAlpha(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void StandardImages(string root)
    {
        var path = Path.Combine(root, "RasterLogo.e5");
        var initial = Png(5, 4, Color.Red);
        File.WriteAllBytes(path, Archive(initial, Png(3, 7, Color.Blue)));
        var resource = Resource(path, ImageCategory.Interface, ResourceKind.E5Indexed);
        var target = Asset(resource, [Frame(path, 1, "image", 5, 4)]);
        var source = Save(root, "new-shape.bmp", Bmp(19, 11, Color.Magenta));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var output = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes);
            Require(output.ReadDecoded(1)[0] == 0x89 && prepared.Rows.Single().OutputFormat == "PNG",
                "普通替换未将 BMP 来源转换为目标 PNG 编码。");
            Require(prepared.Rows.Single().After!.Size == new Size(5, 4), "普通替换未保持 MOD 目标实际尺寸。");
            Require(prepared.Rows.Single().After!.GetPixel(2, 2).R >= 247, "普通整图错误应用了图标洋红键。");
            Require(prepared.Rows.Single().After!.GetPixel(2, 3).R >= 247, "普通整图没有缩放填满目标画布。");
            Require(output.ReadDecoded(2).SequenceEqual(Png(3, 7, Color.Blue)), "普通图适配误改了相邻条目。");
            Require(E5Archive.OpenFresh(path).ReadDecoded(1).SequenceEqual(initial), "准备普通替换写入了资源。");
        }
        var tailed = initial.Concat(new byte[] { 0x12, 0x34 }).ToArray();
        File.WriteAllBytes(path, Archive(tailed));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", source)))
            Require(!prepared.CanCommit && prepared.Rows.Any(row => row.Error?.Contains("尾部") == true), "未知标准图尾部被静默丢弃。");
        File.WriteAllBytes(path, Archive(initial));
        var jpeg = Save(root, "source-without-correct-extension.bin", Jpg(13, 17, Color.Orange));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Append, new ImageImportAssignment(null, "image", jpeg)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(2);
            Require(payload[0] == 0x89 && prepared.Rows.Single().OutputSize == new Size(5, 4),
                "普通新增未将任意尺寸 JPG 来源转换为末尾模板 PNG/5×4 规格。");
        }
        using var alphaBitmap = new Bitmap(5, 4, PixelFormat.Format32bppArgb);
        alphaBitmap.SetPixel(2, 2, Color.FromArgb(128, 240, 40, 80));
        var alphaSource = Save(root, "ordinary-alpha.png", ImageBytes(alphaBitmap, ImageFormat.Png));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", alphaSource)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var expected = Color.FromArgb(128, 240, 40, 80);
            Require(prepared.Rows.Single().After!.GetPixel(2, 2).ToArgb() == expected.ToArgb(),
                "普通PNG候选预览发生未缩放的半透明RGB舍入。");
            var project = new ProjectProfile(root, "alpha gallery fixture", ProjectVersion.Ccz65, false,
                new Dictionary<string, string>(), []);
            var mutations = new ImageMutationService(new HistoryService(Path.Combine(root, "alpha-history")));
            var committed = mutations.Commit(project, prepared.Plan!);
            Require(committed.Success, string.Join("；", committed.Diagnostics));
            var codec = new GenericE5ResourceCodec();
            var galleryAsset = codec.ReadAssets(project, resource).Single(asset => asset.Frames.Single().ImageNumber == 1);
            using var galleryFrame = codec.DecodeFrames(project, galleryAsset).Single();
            Require(galleryFrame.Bitmap.GetPixel(2, 2).ToArgb() == expected.ToArgb() &&
                    galleryFrame.Bitmap.GetPixel(2, 2).ToArgb() == prepared.Rows.Single().After!.GetPixel(2, 2).ToArgb(),
                "普通PNG落盘后的公开图库解码与素材候选预览半透明颜色不一致。");
        }
        foreach (var format in new[] { PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed,
                     PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb })
        {
            var wanted = Color.FromArgb(40, 170, 220);
            using var bmpSource = BitmapSource(7, 5, format, wanted);
            var bmpBytes = ImageBytes(bmpSource, ImageFormat.Bmp);
            var bits = Image.GetPixelFormatSize(format);
            Require(BinaryPrimitives.ReadUInt16LittleEndian(bmpBytes.AsSpan(28)) == bits,
                $"{bits}位BMP测试素材未以要求的实际色深编码。");
            var sourcePath = Save(root, $"ordinary-{bits}bpp.bmp", bmpBytes);
            using var prepared = Prepare(root, resource, ImageImportOperation.Replace,
                new ImageImportAssignment(target, "image", sourcePath));
            Require(prepared.CanCommit, $"{bits}位BMP来源未通过实际准备：{Errors(prepared)}");
            var candidate = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes);
            Require(candidate.ReadDecoded(1)[0] == 0x89, $"{bits}位BMP普通导入未转换为目标PNG编码。");
            Require(prepared.Rows.Single().After!.Size == new Size(5, 4) &&
                    prepared.Rows.Single().After!.GetPixel(2, 2).ToArgb() == wanted.ToArgb(),
                $"{bits}位BMP候选预览没有适配目标尺寸或改变了主体颜色。");
        }
    }

    private static void Portraits(string root)
    {
        var path = Path.Combine(root, "Face.e5");
        File.WriteAllBytes(path, Archive(Png(80, 80, Color.Blue)));
        var resource = Resource(path, ImageCategory.Portrait, ResourceKind.FaceComposite);
        var target = Asset(resource, [Frame(path, 1, "face-small", 80, 80, "Face.e5")]);
        using var wide = new Bitmap(240, 120, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(wide))
        {
            graphics.Clear(Color.Blue);
            graphics.FillRectangle(Brushes.Magenta, 60, 0, 120, 120);
        }
        var source = Save(root, "arbitrary-portrait.png", ImageBytes(wide, ImageFormat.Png));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var row = prepared.Rows.Single();
            Require(row.OutputSize == new Size(120, 120) && row.OutputFormat == "PNG32", "标准 Face 未规范为120 PNG32。");
            Require(row.After!.GetPixel(60, 60).ToArgb() == Color.Magenta.ToArgb() && row.After.GetPixel(10, 60).R > 240,
                "Face 没有居中裁正方形或误用了图标透明键。");
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(1);
            Require(payload[0] == 0x89, "标准 Face 仍沿用旧的非PNG编码。");
        }
        var raw = Enumerable.Repeat((byte)1, 64 * 80).Concat(new byte[] { 0xa3, 0x5c }).ToArray();
        File.WriteAllBytes(path, Archive(raw));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(1);
            Require(payload.Length == raw.Length && payload[^2] == 0xa3 && payload[^1] == 0x5c, "旧头像RAW两字节尾部改变。");
            Require(prepared.Rows.Single().OutputSize == new Size(64, 80), "旧头像被强制120而破坏已确认布局。");
        }
        var incompletePng = Png(120, 120, Color.Green);
        var incompleteSource = Save(root, "portrait-missing-iend.png", incompletePng[..^12]);
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", incompleteSource)))
            Require(!prepared.CanCommit && prepared.Rows.Any(row => row.Error?.Contains("IEND", StringComparison.Ordinal) == true),
                "Face转码入口接受了缺少IEND但GDI可以显示的截断PNG。");
    }

    private static void OrdinaryTargetSpecifications(string root)
    {
        var path = Path.Combine(root, "OrdinarySpecs.e5");
        var resource = Resource(path, ImageCategory.Scene, ResourceKind.E5Indexed);
        var source = Save(root, "spec-source.png", Png(19, 11, Color.FromArgb(40, 170, 220)));
        foreach (var format in new[] { PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed,
                     PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb })
        {
            using var template = BitmapSource(7, 5, format, Color.FromArgb(40, 170, 220));
            var original = ImageBytes(template, ImageFormat.Bmp);
            File.WriteAllBytes(path, Archive(original));
            var target = Asset(resource, [Frame(path, 1, "image", 7, 5)]);
            var bits = Image.GetPixelFormatSize(format);
            foreach (var operation in new[] { ImageImportOperation.Replace, ImageImportOperation.Append })
            {
                using var prepared = Prepare(root, resource, operation,
                    new ImageImportAssignment(operation == ImageImportOperation.Replace ? target : null, "image", source));
                Require(prepared.CanCommit, $"{bits}位BMP目标适配失败：{Errors(prepared)}");
                var number = operation == ImageImportOperation.Replace ? 1 : 2;
                var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(number);
                Require(payload[0] == 'B' && payload[1] == 'M' && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(28)) == bits,
                    $"{bits}位BMP目标未保持目标存储色深。");
                Require(prepared.Rows.Single().OutputSize == new Size(7, 5), "普通BMP目标适配改变了目标尺寸。");
                if (bits <= 8)
                {
                    var paletteLength = (1 << bits) * 4;
                    Require(payload.AsSpan(54, paletteLength).SequenceEqual(original.AsSpan(54, paletteLength)),
                        "普通BMP目标适配改变了目标调色板。");
                }
            }
        }
        File.WriteAllBytes(path, Archive(Jpg(8, 9, Color.Blue)));
        var jpegTarget = Asset(resource, [Frame(path, 1, "image", 8, 9)]);
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(jpegTarget, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(1);
            Require(payload[0] == 0xff && payload[1] == 0xd8 && prepared.Rows.Single().OutputSize == new Size(8, 9),
                "PNG来源未转换为目标JPG规格。");
        }
        var rawPath = Path.Combine(root, "Logo.e5");
        var raw = Enumerable.Repeat((byte)1, 48 * 48).Concat(new byte[] { 0xa5, 0x3e }).ToArray();
        File.WriteAllBytes(rawPath, Archive(raw));
        var rawResource = Resource(rawPath, ImageCategory.Scene, ResourceKind.E5Indexed);
        var rawTarget = Asset(rawResource, [Frame(rawPath, 1, "image", 48, 48)]);
        foreach (var operation in new[] { ImageImportOperation.Replace, ImageImportOperation.Append })
        {
            using var prepared = Prepare(root, rawResource, operation,
                new ImageImportAssignment(operation == ImageImportOperation.Replace ? rawTarget : null, "image", source));
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(rawPath, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(operation == ImageImportOperation.Replace ? 1 : 2);
            Require(payload.Length == raw.Length && payload[^2] == 0xa5 && payload[^1] == 0x3e &&
                    prepared.Rows.Single().OutputSize == new Size(48, 48), "普通RAW静态图未保留确认几何或合法尾部。");
        }
        File.WriteAllBytes(rawPath, Archive(new byte[48 * 48 + 3]));
        using (var prepared = Prepare(root, rawResource, ImageImportOperation.Replace, new ImageImportAssignment(rawTarget, "image", source)))
            Require(!prepared.CanCommit, "普通RAW静态图接受了不合法的三字节尾部。");
        var rangePath = Path.Combine(root, "Effarea.e5");
        var rangeRaw = Enumerable.Repeat((byte)1, 64 * 64).Concat(new byte[] { 0x7a, 0x2c }).ToArray();
        File.WriteAllBytes(rangePath, Archive(rangeRaw));
        var rangeResource = Resource(rangePath, ImageCategory.Range, ResourceKind.E5Indexed);
        var rangeTarget = Asset(rangeResource, [Frame(rangePath, 1, "image", 0, 0)]);
        foreach (var operation in new[] { ImageImportOperation.Replace, ImageImportOperation.Append })
        {
            using var prepared = Prepare(root, rangeResource, operation,
                new ImageImportAssignment(operation == ImageImportOperation.Replace ? rangeTarget : null, "image", source));
            Require(prepared.CanCommit, Errors(prepared));
            var payload = E5Archive.Parse(rangePath, prepared.Plan!.Files.Single().CandidateBytes).ReadDecoded(operation == ImageImportOperation.Replace ? 1 : 2);
            Require(payload.Length == rangeRaw.Length && payload[^2] == 0x7a && payload[^1] == 0x2c &&
                    prepared.Rows.Single().OutputSize == new Size(64, 64), "已知范围64×64 RAW没有按MOD规格转换或保尾。");
        }
        File.WriteAllBytes(path, Archive(new byte[200]));
        var unknownTarget = Asset(resource, [Frame(path, 1, "image", 0, 0)]);
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(unknownTarget, "image", source)))
            Require(!prepared.CanCommit && prepared.Rows.Any(row => row.Error?.Contains("几何") == true), "未识别RAW结构被任意图片覆盖。");
    }

    private static void E5Icons(string root)
    {
        var path = Path.Combine(root, "Item.e5");
        var tail = Png(9, 13, Color.Orange);
        File.WriteAllBytes(path, Archive(Bmp(16, 16, Color.Red), Bmp(32, 32, Color.Blue), tail));
        var resource = Resource(path, ImageCategory.ItemIcon, ResourceKind.E5Indexed);
        var target = Asset(resource, [Frame(path, 1, "small", 16, 16), Frame(path, 2, "large", 32, 32)]);
        using var padded = new Bitmap(70, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(padded)) { graphics.Clear(Color.Transparent); graphics.FillRectangle(Brushes.Red, 21, 13, 10, 10); }
        var large = Save(root, "large-padded.png", ImageBytes(padded, ImageFormat.Png));
        using var smallCanvas = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        smallCanvas.SetPixel(3, 4, Color.Lime);
        var small = Save(root, "explicit-small.png", ImageBytes(smallCanvas, ImageFormat.Png));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "large", large), new ImageImportAssignment(target, "small", small)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var output = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes);
            var largeBytes = output.ReadDecoded(2); var smallBytes = output.ReadDecoded(1);
            Require(BinaryPrimitives.ReadInt16LittleEndian(largeBytes.AsSpan(28)) == 24, "E5 图标没有编码24bpp BMP。");
            using var largeBitmap = BmpPayloadService.Decode(largeBytes);
            using var smallBitmap = BmpPayloadService.Decode(smallBytes);
            Require(largeBitmap.Size == new Size(32, 32) && largeBitmap.GetPixel(0, 0).R == 255,
                "非标准尺寸大图没有裁透明有效区并适配32。");
            Require(smallBitmap.GetPixel(3, 4).G == 255 && smallBitmap.GetPixel(0, 0).R == 247,
                "显式标准小图留白或游戏透明键未保留。");
            Require(output.ReadDecoded(3).SequenceEqual(tail), "图标替换误改未配对尾图。");
        }
        using (var prepared = Prepare(root, resource, ImageImportOperation.Append, new ImageImportAssignment(null, "large", large)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var output = E5Archive.Parse(path, prepared.Plan!.Files.Single().CandidateBytes);
            Require(output.Entries.Count == 5 && output.ReadDecoded(5).SequenceEqual(tail), "新增图标没有在未配对尾图前插入完整大小图。");
            using var smallBitmap = BmpPayloadService.Decode(output.ReadDecoded(3));
            Require(smallBitmap.Size == new Size(16, 16) && smallBitmap.GetPixel(4, 4).R == 255, "小图没有从归一大图派生。");
        }
    }

    private static void DllImports(string root)
    {
        var path = Path.Combine(root, "Tou.dll");
        var template24 = Dib24(3, 5, Color.Blue, [0x71, 0x72, 0x73]);
        var template8 = Dib8(4, 6, [0x92, 0x93]);
        var unrelated = Dib24(7, 2, Color.Orange, [0x21]);
        FixtureDll(path, [(301, 2052, template24), (301, 1041, template8), (999, 2052, unrelated)]);
        var facePath = Path.Combine(root, "Face.e5");
        File.WriteAllBytes(facePath, Archive(Png(120, 120, Color.Red)));
        var resource = Resource(facePath, ImageCategory.Portrait, ResourceKind.FaceComposite) with { Paths = [facePath, path] };
        var target = Asset(resource, [Frame(facePath, 1, "face-small", 120, 120, "Face.e5"), Frame(path, 301, "tou-large", 3, 5, "Tou.dll")]);
        var source = Save(root, "tou-source.png", Png(23, 11, Color.Magenta));
        using (var prepared = Prepare(root, resource, ImageImportOperation.Replace, new ImageImportAssignment(target, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            Require(prepared.Rows.Count(row => row.Role == "Tou") == 2, "Tou没有联动全部语言变体。");
            var candidate = Save(root, "TouCandidate.dll", prepared.Plan!.Files.Single(file => file.TargetPath == path).CandidateBytes);
            var records = DllBitmapArchive.Read(candidate).ToDictionary(record => (record.Id, record.Language));
            Require(records[(301, 2052)].Width == 3 && records[(301, 2052)].Height == 5 && records[(301, 1041)].Width == 4,
                "Tou被统一强制120，未按目标变体尺寸处理。");
            var out24 = records[(301, 2052)].Dib; var out8 = records[(301, 1041)].Dib;
            Require(out24.AsSpan(0, 40).SequenceEqual(template24.AsSpan(0, 40)) && out24[^3..].SequenceEqual(template24[^3..]),
                "Tou24信息头或特殊尾部被重建。");
            Require(out24[40] >= 250 && out24[41] == 0 && out24[42] >= 250 && out24[49] == template24[49],
                $"头像误把洋红转透明或改变DIB行填充：BGR={out24[40]},{out24[41]},{out24[42]}, padding={out24[49]}/{template24[49]}。");
            Require(out8.AsSpan(0, 1064).SequenceEqual(template8.AsSpan(0, 1064)) && out8[^2..].SequenceEqual(template8[^2..]),
                "Tou8调色板或尾部改变。");
            Require(records[(999, 2052)].Dib.SequenceEqual(unrelated), "DLL导入改变了未选资源。");
        }
        var iconPath = Path.Combine(root, "ImportIcons.dll");
        FixtureDll(iconPath, [(100, 2052, Dib24(16, 16, Color.Blue, [0xb1])), (101, 2052, Dib24(32, 32, Color.Blue, [0xb2]))]);
        var iconResource = Resource(iconPath, ImageCategory.ItemIcon, ResourceKind.DllBitmap);
        var iconTarget = Asset(iconResource, [Frame(iconPath, 100, "small", 16, 16), Frame(iconPath, 101, "large", 32, 32)]);
        using var hard = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        hard.SetPixel(0, 0, Color.Red); // Only visible point of its 2x2 block must survive hard transparent downsample.
        var hardSource = Save(root, "hard-transparent.png", ImageBytes(hard, ImageFormat.Png));
        using (var prepared = Prepare(root, iconResource, ImageImportOperation.Replace, new ImageImportAssignment(iconTarget, "large", hardSource)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var row = prepared.Rows.Single(item => item.Role == "small");
            Require(row.After!.GetPixel(0, 0).R == 255 && row.After.GetPixel(0, 0).B == 0,
                "DLL小图没有采用透明优先2×2硬采样。");
            Require(row.After.GetPixel(1, 0).B == 255, "标准画布被错误裁边放大。");
        }
        var nativeResource = Resource(path, ImageCategory.Interface, ResourceKind.DllBitmap);
        var nativeTarget = Asset(nativeResource, [Frame(path, 999, "bitmap", 7, 2)]);
        using (var prepared = Prepare(root, nativeResource, ImageImportOperation.Replace, new ImageImportAssignment(nativeTarget, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            Require(prepared.Rows.Single().After!.GetPixel(2, 1).ToArgb() == Color.Magenta.ToArgb(), "普通DLL图片错误应用图标透明键。");
            Require(prepared.Rows.Single().OutputSize == new Size(7, 2), "普通DLL替换没有保留目标专用画布。");
        }
        using (var prepared = Prepare(root, nativeResource, ImageImportOperation.Append, new ImageImportAssignment(null, "image", source)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var candidate = Save(root, "NativeAppend.dll", prepared.Plan!.Files.Single().CandidateBytes);
            var records = DllBitmapArchive.Read(candidate);
            var appended = records.Where(record => record.Id == 1000).ToArray();
            Require(appended.Length == 2 && appended.Single(record => record.Language == 2052).Width == 3 &&
                    appended.Single(record => record.Language == 2052).Height == 5 &&
                    appended.Single(record => record.Language == 1041).Width == 4 &&
                    appended.Single(record => record.Language == 1041).Height == 6,
                "普通DLL新增没有保留模板各语言实际画布/稀疏ID。");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(appended.Single(record => record.Language == 1041).Dib.AsSpan(14)) == 8 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(appended.Single(record => record.Language == 2052).Dib.AsSpan(14)) == 24,
                "普通DLL新增未继承模板编码种类。");
            Require(records.Single(record => record.Id == 999).Dib.SequenceEqual(unrelated), "普通DLL新增误改既有稀疏资源。");
        }
        // The parent large canvas is a per-language template, rather than a hard-coded 32x32.
        var scaledIconPath = Path.Combine(root, "LargeVariantIcons.dll");
        FixtureDll(scaledIconPath, [(100, 2052, Dib24(32, 32, Color.Blue, [])), (101, 2052, Dib24(64, 64, Color.Blue, []))]);
        var scaledResource = Resource(scaledIconPath, ImageCategory.StrategyIcon, ResourceKind.DllBitmap);
        var scaledTarget = Asset(scaledResource, [Frame(scaledIconPath, 100, "small", 32, 32), Frame(scaledIconPath, 101, "large", 64, 64)]);
        using var scaled = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        scaled.SetPixel(4, 6, Color.Red);
        var scaledSource = Save(root, "scaled-icon.png", ImageBytes(scaled, ImageFormat.Png));
        using (var prepared = Prepare(root, scaledResource, ImageImportOperation.Replace, new ImageImportAssignment(scaledTarget, "large", scaledSource)))
        {
            Require(prepared.CanCommit, Errors(prepared));
            Require(prepared.Rows.Single(row => row.Role == "large").OutputSize == new Size(64, 64), "DLL大图未按变体尺寸归一。");
            var smallRow = prepared.Rows.Single(row => row.Role == "small");
            Require(smallRow.OutputSize == new Size(32, 32) && smallRow.After!.GetPixel(2, 3).R == 255 &&
                    smallRow.After.GetPixel(2, 3).B == 0, "DLL64/32变体派生仍错误使用32父画布。");
        }
    }

    private static void PortraitExpressions(string root)
    {
        var folder = Path.Combine(root, "expression-import"); Directory.CreateDirectory(folder);
        var face = Path.Combine(folder, "Face.e5");
        var tou = Path.Combine(folder, "Tou.dll");
        var original = Archive(Enumerable.Range(0, 9).Select(index => Png(120, 120, Color.FromArgb(255, index * 20, 40, 60))).ToArray());
        File.WriteAllBytes(face, original);
        FixtureDll(tou, [(303, 2052, Dib24(3, 5, Color.Red, [])), (303, 1033, Dib24(7, 9, Color.Blue, [])),
            (304, 2052, Dib24(3, 5, Color.Lime, []))]);
        var originalTou = File.ReadAllBytes(tou);
        var source = Save(folder, "任意表情素材.webp", Solid(37, 63, Color.Magenta, ImageFormat.Gif));
        var project = new ProjectProfile(folder, "表情导入", ProjectVersion.Ccz65, false,
            new Dictionary<string, string> { ["Face.e5"] = face, ["Tou.dll"] = tou }, []);
        var resource = Resource(face, ImageCategory.Portrait, ResourceKind.FaceComposite) with { Paths = [face, tou] };
        var codec = new FaceResourceCodec();
        var target = codec.ReadAssets(project, resource).Single(asset => asset.LogicalId == 0);
        using var prepared = new ImageImportPreparationService().PrepareAsync(new(project, resource, ImageImportOperation.Replace,
            [new(target, "expression:2", source)])).GetAwaiter().GetResult();
        Require(prepared.CanCommit, Errors(prepared));
        Require(prepared.Rows.Count == 3 && prepared.Rows.First().TargetLabel.Contains("表情2"), "八表情导入未列出对应Face与Tou语言变体。");
        var candidate = E5Archive.Parse(face, prepared.Plan!.Files.Single(file => file.TargetPath == face).CandidateBytes);
        for (var number = 1; number <= 9; number++)
        {
            if (number == 3)
            {
                using var image = BmpPayloadService.Decode(candidate.ReadDecoded(number));
                Require(image.Size == new Size(120, 120) && image.GetPixel(60, 60).ToArgb() == Color.Magenta.ToArgb(), "表情素材未独立居中裁切或误去洋红。");
            }
            else Require(candidate.ReadStored(number).SequenceEqual(E5Archive.Parse(face, original).ReadStored(number)), "八表情导入写到了未选择的物理图片。");
        }
        var history = new HistoryService(Path.Combine(folder, "history"));
        var mutation = new ImageMutationService(history);
        var result = mutation.Commit(project, prepared.Plan);
        Require(result.Success, string.Join("；", result.Diagnostics));
        var records = DllBitmapArchive.Read(tou, true);
        foreach (var record in records.Where(record => record.Id == 303))
        {
            using var image = DllBitmapArchive.Decode(record);
            Require(image.GetPixel(1, 1).ToArgb() == Color.Magenta.ToArgb(), "表情Tou没有从同一原始素材按语言画布生成。");
        }
        using (var image = DllBitmapArchive.Decode(records.Single(record => record.Id == 304)))
            Require(image.GetPixel(1, 1).ToArgb() == Color.Lime.ToArgb(), "表情导入修改了相邻Tou ID。");
        Require(history.Undo(project, result.HistoryId!).Success && File.ReadAllBytes(face).SequenceEqual(original) && File.ReadAllBytes(tou).SequenceEqual(originalTou),
            "表情导入撤回未恢复Face/Tou。");
        Require(history.Redo(project, result.HistoryId!).Success, "表情导入重做失败。");
        Require(history.Undo(project, result.HistoryId!).Success, "表情导入清理失败。");
        using var unspecified = new ImageImportPreparationService().PrepareAsync(new(project, resource, ImageImportOperation.Replace,
            [new(target, "image", source)])).GetAwaiter().GetResult();
        Require(!unspecified.CanCommit, "未明确表情编号时写入了八表情组。");
    }

    private static PreparedImageImport Prepare(string root, ImageResourceDescriptor resource, ImageImportOperation operation,
        params ImageImportAssignment[] assignments)
    {
        var project = new ProjectProfile(root, "raster fixture", ProjectVersion.Ccz65, false,
            new Dictionary<string, string>(), []);
        return new ImageImportPreparationService().PrepareAsync(new(project, resource, operation, assignments)).GetAwaiter().GetResult();
    }

    private static void Dll32RowOrderAndAlpha(string root)
    {
        var path = Path.Combine(root, "RowOrder32.dll");
        FixtureDll(path, [(201, 2052, Dib32(2, 3, positiveHeight: true)), (202, 2052, Dib32(2, 3, positiveHeight: false))]);
        var resource = Resource(path, ImageCategory.Interface, ResourceKind.DllBitmap);
        var positive = Asset(resource, [Frame(path, 201, "bitmap", 2, 3)]) with { LogicalId = 201 };
        var negative = Asset(resource, [Frame(path, 202, "bitmap", 2, 3)]) with { LogicalId = 202 };
        var top = Color.FromArgb(128, 240, 40, 80); var bottom = Color.FromArgb(255, 20, 70, 210);
        using var sourceBitmap = new Bitmap(2, 3, PixelFormat.Format32bppArgb);
        for (var x = 0; x < 2; x++) { sourceBitmap.SetPixel(x, 0, top); sourceBitmap.SetPixel(x, 1, Color.Lime); sourceBitmap.SetPixel(x, 2, bottom); }
        var source = Save(root, "32bpp-alpha-source.png", ImageBytes(sourceBitmap, ImageFormat.Png));
        using var prepared = Prepare(root, resource, ImageImportOperation.Replace,
            new ImageImportAssignment(positive, "image", source), new ImageImportAssignment(negative, "image", source));
        Require(prepared.CanCommit, Errors(prepared));
        var candidate = Save(root, "RowOrder32Candidate.dll", prepared.Plan!.Files.Single().CandidateBytes);
        var records = DllBitmapArchive.Read(candidate);
        foreach (var record in records.Where(record => record.Id is 201 or 202))
        {
            // Check actual storage bytes independently of either decoder.
            Require(record.Dib[40] == top.B && record.Dib[41] == top.G && record.Dib[42] == top.R && record.Dib[43] == 128,
                $"32bpp导入没有把半透明上行写到实际像素首行：id={record.Id} actual BGRA={record.Dib[40]},{record.Dib[41]},{record.Dib[42]},{record.Dib[43]} expected={top.B},{top.G},{top.R},128。");
            Require(record.Dib[56] == bottom.B && record.Dib[57] == bottom.G && record.Dib[58] == bottom.R && record.Dib[59] == 255,
                "32bpp导入把上下行倒置。");
            Require(BinaryPrimitives.ReadInt32LittleEndian(record.Dib.AsSpan(8)) == (record.Id == 201 ? 3 : -3),
                "32bpp导入改变了目标高度符号。");
        }
        var project = new ProjectProfile(root, "row order fixture", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        var codec = new DllBitmapResourceCodec();
        var candidateResource = resource with { Paths = [candidate] };
        foreach (var asset in codec.ReadAssets(project, candidateResource).Where(asset => asset.LogicalId is 201 or 202))
        {
            using var frame = codec.DecodeFrames(project, asset).Single();
            var row = prepared.Rows.Single(item => item.TargetLabel.Contains($"ID={asset.LogicalId} /", StringComparison.Ordinal));
            var actualTop = frame.Bitmap.GetPixel(0, 0); var actualBottom = frame.Bitmap.GetPixel(0, 2);
            Require(actualTop.ToArgb() == row.After!.GetPixel(0, 0).ToArgb() && actualTop.A == 128 && actualTop.R >= 238,
                "落盘32bpp目录预览与候选预览不一致或丢失alpha。");
            Require(actualBottom.ToArgb() == bottom.ToArgb(), "落盘32bpp目录预览上下倒置。");
        }
    }

    private static ImageResourceDescriptor Resource(string path, ImageCategory category, ResourceKind kind)
        => new(Path.GetFileName(path), Path.GetFileName(path), category, kind, [path], 1, true, true, false, "", "");
    private static LogicalImageAsset Asset(ImageResourceDescriptor resource, IReadOnlyList<FrameDescriptor> frames)
        => new(resource.Key, 1, "测试目标", resource.Category, resource.Kind, frames, [], false, false, "");
    private static FrameDescriptor Frame(string path, int number, string action, int width, int height, string? key = null)
        => new(key ?? Path.GetFileName(path), path, number, 0, 1, action, "none", width, height, 2052);
    private static string Save(string root, string name, byte[] bytes) { var path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
    private static byte[] Png(int width, int height, Color color) => Solid(width, height, color, ImageFormat.Png);
    private static byte[] Bmp(int width, int height, Color color) => Solid(width, height, color, ImageFormat.Bmp);
    private static byte[] Jpg(int width, int height, Color color) => Solid(width, height, color, ImageFormat.Jpeg);
    private static byte[] Solid(int width, int height, Color color, ImageFormat format)
    {
        using var bitmap = new Bitmap(width, height); using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        return ImageBytes(bitmap, format);
    }
    private static Bitmap BitmapSource(int width, int height, PixelFormat format, Color color)
    {
        var bitmap = new Bitmap(width, height, format);
        if ((format & PixelFormat.Indexed) == 0)
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(color); return bitmap;
        }
        var palette = bitmap.Palette;
        palette.Entries[0] = Color.Blue; palette.Entries[1] = color; bitmap.Palette = palette;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, format);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (var x = 0; x < width; x++)
            {
                if (format == PixelFormat.Format8bppIndexed) row[x] = 1;
                else row[x / 2] |= (byte)(x % 2 == 0 ? 0x10 : 0x01);
            }
            for (var y = 0; y < height; y++) Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    private static byte[] ImageBytes(Bitmap bitmap, ImageFormat format) { using var stream = new MemoryStream(); bitmap.Save(stream, format); return stream.ToArray(); }
    private static byte[] Archive(params byte[][] payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Length * E5Archive.EntrySize + 4;
        var result = new byte[offset + payloads.Sum(payload => payload.Length)];
        "Ls11"u8.CopyTo(result); for (var index = 0; index < 256; index++) result[16 + index] = (byte)index;
        for (var index = 0; index < payloads.Length; index++)
        {
            var at = E5Archive.IndexOffset + index * 12;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at + 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at + 8), offset);
            payloads[index].CopyTo(result, offset); offset += payloads[index].Length;
        }
        return result;
    }
    private static byte[] Dib24(int width, int height, Color color, byte[] tail)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var result = new byte[40 + stride * height + tail.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(12), 1); BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(14), 24);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), stride * height);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var at = 40 + y * stride + x * 3; result[at] = color.B; result[at + 1] = color.G; result[at + 2] = color.R; }
        tail.CopyTo(result, result.Length - tail.Length); return result;
    }
    private static byte[] Dib8(int width, int height, byte[] tail)
    {
        var stride = (width + 3) / 4 * 4; var result = new byte[1064 + stride * height + tail.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(12), 1); BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(14), 8);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), stride * height);
        result[40] = 255; result[44 + 2] = 255; result[48] = 255; result[48 + 2] = 255;
        tail.CopyTo(result, result.Length - tail.Length); return result;
    }
    private static byte[] Dib32(int width, int height, bool positiveHeight)
    {
        var result = new byte[40 + width * height * 4 + 2];
        BinaryPrimitives.WriteInt32LittleEndian(result, 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), positiveHeight ? height : -height);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(12), 1); BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), width * height * 4);
        result[^2] = 0x73; result[^1] = 0x29; return result;
    }
    private static void FixtureDll(string path, IReadOnlyList<(int Id, int Language, byte[] Dib)> resources)
    {
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"), path);
        var handle = BeginUpdateResourceW(path, false);
        Require(handle != IntPtr.Zero, "无法建立DLL测试载荷。");
        var completed = false;
        try
        {
            foreach (var resource in resources)
                Require(UpdateResourceW(handle, (IntPtr)2, (IntPtr)resource.Id, (ushort)resource.Language, resource.Dib, resource.Dib.Length), "无法添加测试位图。");
            Require(EndUpdateResourceW(handle, false), "无法保存DLL测试载荷。"); completed = true;
        }
        finally { if (!completed) EndUpdateResourceW(handle, true); }
    }
    private static string Errors(PreparedImageImport prepared) => string.Join("；", prepared.Rows.Select(row => row.Error).Where(error => error != null));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateResourceW(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] data, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndUpdateResourceW(IntPtr handle, bool discard);
}
