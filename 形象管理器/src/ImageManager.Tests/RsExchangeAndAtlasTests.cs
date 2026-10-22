using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class RsExchangeAndAtlasTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.RsExchange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = CreateFixture(root);
            CheckNativeExchange(fixture, root);
            CheckCurrentSetAtlases(fixture, root);
            CheckPixelGeometryAndRepair(root);
            CheckStoredFormatsAndPaletteIndexes(root);
            CheckKeyedNativeRoundTrip(root);
            CheckWeatherFrameMutation(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckNativeExchange((ProjectProfile Project, LogicalImageAsset R, LogicalImageAsset S) fixture, string root)
    {
        var service = new RsStripExchangeService();
        var destination = Path.Combine(root, "动作条");
        var r = service.Export(fixture.Project, fixture.R, destination);
        Require(r.Files.Count == 2, "R 导出必须是正背两条。");
        CheckBmp(Path.Combine(destination, "R000", "front.bmp"), new Size(48, 1280));
        CheckBmp(Path.Combine(destination, "R000", "back.bmp"), new Size(48, 1280));
        var s = service.Export(fixture.Project, fixture.S, destination, preferredTurn: 2);
        Require(s.Files.Count == 3, "S 当前套导出必须是三条。");
        CheckBmp(Path.Combine(destination, "兵种", "S_002", "mov.bmp"), new Size(48, 528));
        CheckBmp(Path.Combine(destination, "兵种", "S_002", "atk.bmp"), new Size(64, 768));
        CheckBmp(Path.Combine(destination, "兵种", "S_002", "spc.bmp"), new Size(48, 240));
        var plan = service.PlanReplace(fixture.Project, fixture.S, destination, preferredTurn: 2);
        Require(plan.Files.Count == 3 && plan.Kind == MutationKind.Replace, "三种动作必须在一个事务中替换。");
        foreach (var file in plan.Files)
        {
            var original = E5Archive.Open(file.TargetPath);
            var candidate = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            for (var image = 1; image <= 3; image++)
                Require(original.ReadDecoded(image).SequenceEqual(candidate.ReadDecoded(image)),
                    "无修改动作条往返改变了像素索引或两字节尾部。");
        }
        var all = service.Export(fixture.Project, fixture.S, Path.Combine(root, "全转动作条"), allTurns: true);
        Require(all.Files.Count == 3 && all.Files.All(path => Path.GetFileName(Path.GetDirectoryName(path)!) == "S_002"),
            "独立物理S即使请求旧全转参数也必须只导出当前物理图的三条动作。");
        var thirdDescriptors = fixture.S.Frames.Select(frame => frame with { ImageNumber = 3 }).ToArray();
        var second = fixture.S with
        {
            LogicalId = 3, DisplayName = "S_003", Frames = thirdDescriptors,
            SMapping = new SLogicalAssetMapping(3, false, [new(1, 3)], 3)
        };
        var batchDirectory = Path.Combine(root, "批量动作条");
        service.Export(fixture.Project, fixture.S, batchDirectory);
        service.Export(fixture.Project, second, batchDirectory);
        foreach (var item in new[] { (Name: "S_002", Gray: 90), (Name: "S_003", Gray: 100) })
        {
            var editPath = Path.Combine(batchDirectory, "兵种", item.Name, "mov.bmp");
            using var edited = new Bitmap(48, 528, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(edited)) graphics.Clear(Color.FromArgb(item.Gray, item.Gray, item.Gray));
            edited.Save(editPath, ImageFormat.Bmp);
        }
        var batch = service.PlanReplaceMany(fixture.Project, [fixture.S, second], batchDirectory);
        var movement = batch.Files.Single(file => Path.GetFileName(file.TargetPath) == "Unit_mov.e5");
        var combined = E5Archive.Parse(movement.TargetPath, movement.CandidateBytes);
        Require(combined.ReadDecoded(1)[0] == 81 && combined.ReadDecoded(2)[0] == 90 && combined.ReadDecoded(3)[0] == 100,
            "批量动作条计划互相覆盖了同一档案中的不同资源。");
        var legacyDirectory = Path.Combine(root, "显式旧编号");
        var legacySlot = Path.Combine(legacyDirectory, "兵种S001");
        Directory.CreateDirectory(legacySlot);
        foreach (var file in s.Files) File.Copy(file, Path.Combine(legacySlot, Path.GetFileName(file)));
        var legacy = service.PlanReplace(fixture.Project, fixture.S, legacyDirectory, naming: RsImportNaming.Legacy);
        foreach (var file in legacy.Files)
        {
            var original = E5Archive.Open(file.TargetPath);
            var candidate = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            for (var image = 1; image <= 3; image++)
                Require(original.ReadDecoded(image).SequenceEqual(candidate.ReadDecoded(image)),
                    "显式旧兵种S001目录没有正确映射物理S_002。");
        }
        Throws<InvalidDataException>(() => service.PlanReplace(fixture.Project, fixture.S, legacyDirectory));
        var aggregate = fixture.S with { Frames = fixture.S.Frames.Concat(thirdDescriptors).ToArray() };
        Throws<InvalidDataException>(() => service.Export(fixture.Project, aggregate, Path.Combine(root, "拒绝跨物理导出"), allTurns: true));
        var aggregateFrames = new ImageCatalogService().DecodeFrames(fixture.Project, aggregate);
        try { Throws<InvalidDataException>(() => ReferenceAtlasContext.Create(aggregate, aggregateFrames)); }
        finally { foreach (var frame in aggregateFrames) frame.Dispose(); }
        using (var bad = new Bitmap(48, 527)) bad.Save(Path.Combine(destination, "兵种", "S_002", "mov.bmp"), ImageFormat.Bmp);
        Throws<InvalidDataException>(() => service.PlanReplace(fixture.Project, fixture.S, destination, 2));
        Require(E5Archive.Open(fixture.S.Frames[0].SourcePath).ReadDecoded(2)[0] == 82,
            "尺寸错误的替换计划修改了原档案。");
    }

    private static void CheckCurrentSetAtlases((ProjectProfile Project, LogicalImageAsset R, LogicalImageAsset S) fixture, string root)
    {
        var frames = new ImageCatalogService().DecodeFrames(fixture.Project, fixture.S);
        try
        {
            var context = ReferenceAtlasContext.Create(fixture.S, frames, 2);
            Require(context.Frames.Count == 28 && context.TargetTurn == 1 && context.PhysicalImageNumber == 2,
                "参考图必须只绑定当前套真实物理槽。");
            var service = new ReferenceAtlasService();
            var destination = Path.Combine(root, "参考图");
            var exported = service.Export(destination, context, Color.Magenta);
            Require(exported.TransparentPngPath.Length == 0 && Directory.GetFiles(Path.GetDirectoryName(exported.PngPath)!).Length == 2,
                "默认参考图必须只导出 PNG 和提示词。");
            using (var bitmap = new Bitmap(exported.PngPath)) Require(bitmap.Size == new Size(348, 348), "单套 S 参考图尺寸错误。");
            using (var normalized = service.Normalize(exported.PngPath, context, new AiAtlasNormalizationOptions()))
            {
                Require(normalized.Frames.Count == 28 && normalized.Frames.All(frame => frame.Descriptor.Turn == 1 && frame.Descriptor.ImageNumber == 2),
                    "单套参考图回导丢失真实转数或物理图号。");
                foreach (var frame in normalized.Frames)
                {
                    var original = frames.Single(item => AiAtlasNormalizer.FrameKey(item.Descriptor) == AiAtlasNormalizer.FrameKey(frame.Descriptor));
                    Require(ReferenceAtlasService.PixelHash(original.Bitmap) == ReferenceAtlasService.PixelHash(frame.Bitmap), "原尺寸参考图回导改变了像素。");
                }
                var plan = new RsImportService().PlanImport(fixture.Project, fixture.S, normalized.Frames);
                foreach (var file in plan.Files)
                {
                    var old = E5Archive.Open(file.TargetPath);
                    var candidate = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
                    Require(old.ReadStored(1).SequenceEqual(candidate.ReadStored(1)) && old.ReadStored(3).SequenceEqual(candidate.ReadStored(3)),
                        "当前套参考图误改其他转数。");
                }
            }
            foreach (var scale in new[] { 2, 4 })
            {
                var enlarged = service.Export(Path.Combine(root, "参考图" + scale), context, Color.Magenta,
                    new ReferenceAtlasExportOptions { Scale = scale, IncludeTransparent = true });
                using var bitmap = new Bitmap(enlarged.PngPath);
                Require(bitmap.Size == new Size(348 * scale, 348 * scale), "整数倍率参考图尺寸错误。");
                using var normalized = service.Normalize(enlarged.PngPath, context, new AiAtlasNormalizationOptions());
                Require(normalized.FrameDiagnostics.All(item => item.Confidence == 1), "整数倍率完整格位不应重新猜测语义行。");
                foreach (var frame in normalized.Frames)
                {
                    var original = context.Frames.Single(item => AiAtlasNormalizer.FrameKey(item.Descriptor) == AiAtlasNormalizer.FrameKey(frame.Descriptor));
                    Require(ReferenceAtlasService.PixelHash(original.Bitmap) == ReferenceAtlasService.PixelHash(frame.Bitmap), "整数最近邻往返改变了像素。");
                }
                var prompt = File.ReadAllText(enlarged.PromptPath);
                Require(prompt.Contains($"{348 * scale}×{348 * scale}", StringComparison.Ordinal) && !prompt.Contains("转数 |", StringComparison.Ordinal) &&
                    prompt.Contains("禁止抗锯齿", StringComparison.Ordinal), "当前套提示词缺少动态尺寸或格式约束。");
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => service.Normalize(exported.PngPath, context, new AiAtlasNormalizationOptions(), cancellation.Token));
            Require(frames[0].Bitmap.GetPixel(0, 0).A == 255, "非拥有 Context 不应释放原帧。");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckPixelGeometryAndRepair(string root)
    {
        var frames = new List<DecodedFrame>();
        foreach (var action in new[] { (Name: "mov", Count: 11, Size: 48), (Name: "spc", Count: 5, Size: 48), (Name: "atk", Count: 12, Size: 64) })
        for (var frame = 0; frame < action.Count; frame++)
        {
            var descriptor = new FrameDescriptor("Unit_" + action.Name + ".e5", "memory", 242, frame, 1, action.Name, "front", action.Size, action.Size);
            var bitmap = new Bitmap(action.Size, action.Size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.FillRectangle(Brushes.Red, 20, 20, 8, 8);
            }
            frames.Add(new DecodedFrame(descriptor, bitmap));
        }
        var asset = new LogicalImageAsset("SComposite", 242, "S_242", ImageCategory.CharacterS, ResourceKind.SComposite,
            frames.Select(frame => frame.Descriptor).ToArray(), [], false, false, string.Empty);
        try
        {
            var context = ReferenceAtlasContext.Create(asset, frames, 2);
            var service = new ReferenceAtlasService();
            var files = service.Export(Path.Combine(root, "像素占位"), context, Color.Magenta);
            var layout = new ReferenceAtlasLayoutPlanner().Plan(asset, frames);
            using (var bitmap = new Bitmap(files.PngPath))
            {
                var bounds = layout.Placements[0].Bounds;
                using (var graphics = Graphics.FromImage(bitmap)) graphics.FillRectangle(Brushes.Lime, bounds.X + 8, bounds.Y + 8, 32, 32);
                bitmap.Save(files.PngPath + ".edited.png", ImageFormat.Png);
            }
            using var edited = service.Normalize(files.PngPath + ".edited.png", context, new AiAtlasNormalizationOptions());
            Require(edited.Frames[0].Bitmap.GetPixel(9, 9).ToArgb() == Color.Lime.ToArgb(),
                "默认导入不应把 AI 新轮廓缩回原人物前景框。");
            Require(edited.FrameDiagnostics.All(item => item.Scale == 1), "默认流水线不应进行占位二次缩放。");

            // A non-integral AI output uses the repair path, but retains hard
            // nearest-neighbor samples rather than creating blended colors.
            var repairedPath = files.PngPath + ".noninteger.png";
            using (var original = new Bitmap(files.PngPath))
            using (var enlarged = new Bitmap(1024, 1024))
            {
                using (var graphics = Graphics.FromImage(enlarged))
                {
                    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.DrawImage(original, new Rectangle(0, 0, 1024, 1024), new Rectangle(Point.Empty, original.Size), GraphicsUnit.Pixel);
                }
                enlarged.Save(repairedPath, ImageFormat.Png);
            }
            using var repaired = service.Normalize(repairedPath, context, new AiAtlasNormalizationOptions());
            Require(!repaired.HasBlockingIssues && repaired.Diagnostics.Any(text => text.Contains("自动语义行配准", StringComparison.Ordinal)),
                "非整数 AI 图应自动配准而非直接拒绝。");
            foreach (var frame in repaired.Frames)
            for (var y = 0; y < frame.Bitmap.Height; y++)
            for (var x = 0; x < frame.Bitmap.Width; x++)
            {
                var color = frame.Bitmap.GetPixel(x, y);
                Require(color.A == 0 || color.ToArgb() == Color.Red.ToArgb(), "默认最近邻修复不应产生面积卷积中间色。");
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckStoredFormatsAndPaletteIndexes(string root)
    {
        foreach (var format in new[] { "BMP8", "BMP24", "PNG" })
        {
            var path = Path.Combine(root, format + ".e5");
            var original = CreateStoredStrip(format);
            File.WriteAllBytes(path, BuildE5([original, original]));
            // Standard images have their own color evidence and remain
            // writable even when the project has no external RAW palette.
            var project = new ProjectProfile(root, format, ProjectVersion.Ccz65, false,
                new Dictionary<string, string> { ["Pmapobj.e5"] = path }, []);
            var descriptors = new List<FrameDescriptor>();
            for (var side = 0; side < 2; side++)
            for (var frame = 0; frame < 20; frame++)
                descriptors.Add(new FrameDescriptor("Pmapobj.e5", path, side + 1, frame, 1, "r-action", side == 0 ? "front" : "back", 48, 64));
            var asset = new LogicalImageAsset("Pmapobj.e5", 0, "R000", ImageCategory.CharacterR, ResourceKind.RComposite,
                descriptors, [], false, false, string.Empty);
            var frames = new ImageCatalogService().DecodeFrames(project, asset);
            try
            {
                var service = new RsImportService();
                var roundTrip = service.PlanImport(project, asset, frames);
                var unchanged = E5Archive.Parse(path, roundTrip.Files.Single().CandidateBytes);
                Require(unchanged.ReadDecoded(1).SequenceEqual(original), format + " 无修改写回改变了原格式、调色板、重复索引或尾部。");
                var selected = frames.Single(frame => frame.Descriptor.Direction == "front" && frame.Descriptor.FrameIndex == 2);
                using var changedBitmap = new Bitmap(selected.Bitmap);
                changedBitmap.SetPixel(7, 8, Color.Blue);
                var changed = frames.Select(frame => ReferenceEquals(frame, selected)
                    ? new DecodedFrame(frame.Descriptor, changedBitmap, Enumerable.Repeat((byte)99, 48 * 64).ToArray(),
                        Enumerable.Repeat((byte)0, 1024).ToArray()) : frame).ToArray();
                var edit = service.PlanImport(project, asset, changed);
                var candidate = E5Archive.Parse(path, edit.Files.Single().CandidateBytes);
                var bytes = candidate.ReadDecoded(1);
                Require(ImageRendering.IsStandardImage(bytes), format + " 被错误写成裸索引数据。");
                using var decoded = BmpPayloadService.Decode(bytes);
                Require(decoded.GetPixel(7, 2 * 64 + 8).ToArgb() == Color.Blue.ToArgb(), format + " 写回未采用目标颜色语义。");
                Require(candidate.ReadStored(2).SequenceEqual(E5Archive.Open(path).ReadStored(2)), format + " 编辑前面误改后面资源。");
                if (format == "BMP8")
                {
                    Require(ImageRendering.TryExtract8BppBmpIndexes(bytes, 48, 1280, out var indexes), "BMP8 索引丢失。");
                    Require(indexes[0] == 1 && indexes[1] == 2 && indexes[(2 * 64 + 8) * 48 + 7] == 4,
                        "BMP8 未保留原有重复颜色索引，或盲目采用外来 99 索引。");
                    Require(bytes.AsSpan(54, 1024).SequenceEqual(original.AsSpan(54, 1024)), "BMP8 原调色板被外来调色板覆盖。");
                }
                if (format == "BMP24") Require(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) == 24, "BMP24 位深被改变。");
                if (format == "PNG") Require(bytes.AsSpan(0, 8).SequenceEqual(original.AsSpan(0, 8)), "PNG 编码被改变。");
                Require(bytes.AsSpan(bytes.Length - 3).SequenceEqual(original.AsSpan(original.Length - 3)), format + " 尾部三字节丢失。");

                var single = new FrameDescriptor("image", path, 1, 0, 1, "image", "none", 48, 1280);
                var generic = new LogicalImageAsset("image", 0, format, ImageCategory.Discovered, ResourceKind.E5Indexed,
                    [single], [], false, false, string.Empty);
                var mutations = new ImageMutationService(new HistoryService(Path.Combine(root, "历史")));
                var sourcePath = Path.Combine(root, format + "-编辑.png");
                using (var fullStrip = BmpPayloadService.Decode(original))
                {
                    fullStrip.SetPixel(7, 8, Color.Blue);
                    fullStrip.Save(sourcePath, ImageFormat.Png);
                    var direct = mutations.PlanReplaceSingleFrame(project, generic, single, fullStrip);
                    var directBytes = E5Archive.Parse(path, direct.Files.Single().CandidateBytes).ReadDecoded(1);
                    Require(directBytes.AsSpan(directBytes.Length - 3).SequenceEqual(original.AsSpan(original.Length - 3)), format + " 通用像素编辑丢失尾部。");
                    var copy = mutations.PlanReplaceSingleFrame(project, generic, single, fullStrip, appendCopy: true);
                    var copyArchive = E5Archive.Parse(path, copy.Files.Single().CandidateBytes);
                    Require(copyArchive.Entries.Count == 3 && ImageRendering.IsStandardImage(copyArchive.ReadDecoded(3)), format + " 通用复制追加丢失编码。");
                }
                Throws<InvalidDataException>(() => mutations.PlanReplace(project, [generic], [sourcePath]));

                var appended = service.PlanAppendCopy(project, asset, frames);
                var appendedArchive = E5Archive.Parse(path, appended.Files.Single().CandidateBytes);
                Require(appendedArchive.ReadDecoded(3).SequenceEqual(original) && appendedArchive.ReadDecoded(4).SequenceEqual(original),
                    format + " 复制追加未保留编码或原索引。");
                var materials = Path.Combine(root, format + "-追加素材");
                new RsStripExchangeService().Export(project, asset, materials);
                var appendFromStrips = service.PlanAppend(project,
                    new ImageResourceDescriptor("Pmapobj.e5", "R形象", ImageCategory.CharacterR, ResourceKind.RComposite,
                        [path], 2, true, true, false, string.Empty, string.Empty), materials);
                var appendedStrips = E5Archive.Parse(path, appendFromStrips.Files.Single().CandidateBytes);
                foreach (var number in new[] { 3, 4 })
                {
                    var payload = appendedStrips.ReadDecoded(number);
                    Require(payload.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                        BmpPayloadService.GetStandardImageLength(payload) == payload.Length,
                        format + " 外部 R 素材新增未规范编码为无未知尾部的 PNG32。");
                }
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
    }

    private static void CheckWeatherFrameMutation(string root)
    {
        var path = Path.Combine(root, "Weather.e5");
        var original = Enumerable.Range(0, WeatherResourceCodec.FrameCount)
            .SelectMany(index => Enumerable.Repeat((byte)(index + 1), WeatherResourceCodec.FrameWidth * WeatherResourceCodec.FrameHeight)).ToArray();
        File.WriteAllBytes(path, BuildE5([original]));
        var palettePath = Path.Combine(root, "Spalet.e5");
        var project = new ProjectProfile(root, "weather", ProjectVersion.Ccz65, false,
            new Dictionary<string, string> { ["Weather.e5"] = path, ["Spalet.e5"] = palettePath }, []);
        var resource = new ImageResourceDescriptor("Weather.e5", "天气", ImageCategory.Dynamic, ResourceKind.WeatherSpecial,
            [path], 1, true, true, false, string.Empty, string.Empty);
        var codec = new WeatherResourceCodec();
        var asset = codec.ReadAssets(project, resource).Single();
        var frames = codec.DecodeFrames(project, asset);
        try
        {
            var mutations = new ImageMutationService(new HistoryService(Path.Combine(root, "天气历史")));
            using var edited = new Bitmap(frames[2].Bitmap);
            var palette = new PaletteService().RequireWritePalette(project);
            edited.SetPixel(5, 6, palette[10]);
            var plan = mutations.PlanReplaceSingleFrame(project, asset, frames[2].Descriptor, edited);
            var replacement = E5Archive.Parse(path, plan.Files.Single().CandidateBytes).ReadDecoded(1);
            var expectedOffset = 2 * 216 * 50 + 6 * 216 + 5;
            Require(replacement[expectedOffset] == 10 && Enumerable.Range(0, replacement.Length)
                .All(index => index == expectedOffset || replacement[index] == original[index]),
                "天气单帧编辑误改了其他帧或其他像素。");
            Throws<InvalidOperationException>(() => mutations.PlanReplaceSingleFrame(project, asset, frames[2].Descriptor, edited, true));
            using var strip = ImageRendering.DecodeIndexed(original, 216, 200, palette);
            strip.SetPixel(9, 155, palette[11]);
            var source = Path.Combine(root, "天气动作条.png"); strip.Save(source, ImageFormat.Png);
            var replacePlan = mutations.PlanReplace(project, [asset], [source]);
            var allFrames = E5Archive.Parse(path, replacePlan.Files.Single().CandidateBytes).ReadDecoded(1);
            Require(allFrames.Length == 43200 && allFrames[155 * 216 + 9] == 11, "天气完整条带替换未保留四帧几何。");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static byte[] CreateStoredStrip(string format)
    {
        using var image = new Bitmap(48, 1280, format == "BMP8" ? PixelFormat.Format8bppIndexed : PixelFormat.Format24bppRgb);
        if (format == "BMP8")
        {
            var palette = image.Palette;
            for (var index = 0; index < 256; index++) palette.Entries[index] = Color.FromArgb(255, index, index, index);
            palette.Entries[0] = Color.Magenta; palette.Entries[1] = Color.Red;
            palette.Entries[2] = Color.Red; palette.Entries[4] = Color.Blue;
            image.Palette = palette;
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                var row = Enumerable.Range(0, data.Stride).Select(index => (byte)(index % 2 == 0 ? 1 : 2)).ToArray();
                for (var y = 0; y < image.Height; y++) System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            finally { image.UnlockBits(data); }
        }
        else using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
        using var stream = new MemoryStream(); image.Save(stream, format == "PNG" ? ImageFormat.Png : ImageFormat.Bmp);
        return stream.ToArray().Concat(new byte[] { 0x81, 0x92, 0xA3 }).ToArray();
    }

    private static void CheckKeyedNativeRoundTrip(string root)
    {
        var directory = Path.Combine(root, "F7键色动作条往返");
        Directory.CreateDirectory(directory);
        var fixture = CreateFixture(directory);
        var key = Color.FromArgb(247, 0, 255);
        var palettePath = fixture.Project.TryGetResource("Spalet.e5")!;
        var paletteArchive = E5Archive.Open(palettePath);
        var palette = paletteArchive.ReadDecoded(1);
        palette[1] = 247;
        File.WriteAllBytes(palettePath, paletteArchive.Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [1] = palette }));
        E5Archive.Invalidate(palettePath);
        foreach (var group in fixture.R.Frames.Concat(fixture.S.Frames).GroupBy(frame => frame.SourcePath))
        {
            var archive = E5Archive.Open(group.Key);
            var replacements = new Dictionary<int, byte[]>();
            foreach (var physical in group.GroupBy(frame => frame.ImageNumber))
            {
                var first = physical.First();
                using var strip = new Bitmap(first.Width, first.Height * physical.Count(), PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(strip))
                {
                    graphics.Clear(key);
                    foreach (var descriptor in physical)
                        graphics.FillRectangle(Brushes.Red, 4, descriptor.FrameIndex * descriptor.Height + 4,
                            first.Width - 8, first.Height - 8);
                }
                using var stream = new MemoryStream(); strip.Save(stream, ImageFormat.Bmp);
                // Unknown target suffixes are rejected by file import; this
                // fixture isolates valid native key-color conversion.
                replacements[physical.Key] = stream.ToArray();
            }
            File.WriteAllBytes(group.Key, archive.Rebuild(replacementDecoded: replacements));
        }
        var service = new RsStripExchangeService();
        var materials = Path.Combine(directory, "导出");
        var exported = service.Export(fixture.Project, fixture.R, materials).Files
            .Concat(service.Export(fixture.Project, fixture.S, materials, allTurns: true).Files).ToArray();
        foreach (var file in exported)
        {
            using var image = BmpPayloadService.Decode(File.ReadAllBytes(file));
            Require(image.GetPixel(0, 0).ToArgb() == key.ToArgb(), $"原生动作条没有沿用项目 F700FF 键色：{image.GetPixel(0, 0)}；解析键色 {ReferenceAtlasService.ResolveKeyColor(new PaletteService().Load(fixture.Project))}。");
        }
        foreach (var asset in new[] { fixture.R, fixture.S })
        {
            var plan = service.PlanReplace(fixture.Project, asset, materials, allTurns: true);
            foreach (var file in plan.Files)
            {
                var original = E5Archive.Open(file.TargetPath);
                var candidate = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
                var selected = asset.Frames.Where(frame => frame.SourcePath == file.TargetPath).Select(frame => frame.ImageNumber).ToHashSet();
                foreach (var entry in original.Entries)
                {
                    var payload = candidate.ReadDecoded(entry.ImageNumber);
                    if (!selected.Contains(entry.ImageNumber))
                    {
                        Require(payload.SequenceEqual(original.ReadDecoded(entry.ImageNumber)), "外部动作条替换误改了未选中物理图号。");
                        continue;
                    }
                    using var decoded = BmpPayloadService.Decode(payload);
                    Require(decoded.GetPixel(5, 5).ToArgb() == Color.Red.ToArgb(), "外部动作条替换改变了非键色像素。");
                    if (asset.Kind == ResourceKind.RComposite)
                        Require(payload[0] == 0x89 && decoded.GetPixel(0, 0).A == 0, "R 外部动作条未转为透明 PNG32。");
                    else Require(payload[0] == 'B' && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(28, 2)) == 32,
                        "S BMP 外部动作条替换未保持 BMP 存储并重编码为32位。");
                    Require(BmpPayloadService.GetStandardImageLength(payload) == payload.Length,
                        "重编码后保留了不属于图片的旧未知尾部。");
                }
            }
        }
        // Materials from older exports may use FF00FF; R file replacement
        // decodes the key and emits transparent PNG32.
        var frontPath = Path.Combine(materials, "R000", "front.bmp");
        using (var original = BmpPayloadService.Decode(File.ReadAllBytes(frontPath)))
        using (var alternate = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(alternate))
            using (var transparent = ImageRendering.ApplyKeyTransparency(original, false))
            { graphics.Clear(Color.Magenta); graphics.DrawImageUnscaled(transparent, 0, 0); }
            alternate.Save(frontPath, ImageFormat.Bmp);
        }
        var aliasPlan = service.PlanReplace(fixture.Project, fixture.R, materials);
        var aliasFile = aliasPlan.Files.Single();
        using var aliased = BmpPayloadService.Decode(E5Archive.Parse(aliasFile.TargetPath, aliasFile.CandidateBytes).ReadDecoded(1));
        Require(aliased.GetPixel(0, 0).A == 0 && aliased.GetPixel(5, 5).ToArgb() == Color.Red.ToArgb(),
            "FF00FF 外部 R 素材没有生成正确透明 PNG32。");
    }

    private static (ProjectProfile Project, LogicalImageAsset R, LogicalImageAsset S) CreateFixture(string root)
    {
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var palette = new byte[768];
        for (var index = 0; index < 256; index++)
            palette[index * 3] = palette[index * 3 + 1] = palette[index * 3 + 2] = (byte)index;
        palette[0] = 255; palette[1] = 255; palette[2] = 0;
        var palettePath = Path.Combine(root, "Spalet.e5");
        File.WriteAllBytes(palettePath, BuildE5([palette])); resources["Spalet.e5"] = palettePath;
        var rPath = Path.Combine(root, "Pmapobj.e5");
        File.WriteAllBytes(rPath, BuildE5([RawStrip(48, 64, 20, 30), RawStrip(48, 64, 20, 60)]));
        resources["Pmapobj.e5"] = rPath;
        var rFrames = new List<FrameDescriptor>();
        for (var side = 0; side < 2; side++)
        for (var frame = 0; frame < 20; frame++)
            rFrames.Add(new FrameDescriptor("Pmapobj.e5", rPath, side + 1, frame, 1, "r-action", side == 0 ? "front" : "back", 48, 64));
        var sFrames = new List<FrameDescriptor>();
        foreach (var spec in new[] { (Name: "mov", Size: 48, Count: 11), (Name: "atk", Size: 64, Count: 12), (Name: "spc", Size: 48, Count: 5) })
        {
            var fileName = "Unit_" + spec.Name + ".e5";
            var path = Path.Combine(root, fileName); resources[fileName] = path;
            File.WriteAllBytes(path, BuildE5(Enumerable.Range(1, 3).Select(turn => RawStrip(spec.Size, spec.Size, spec.Count, 80 + turn)).ToArray()));
            for (var frame = 0; frame < spec.Count; frame++)
                sFrames.Add(new FrameDescriptor(fileName, path, 2, frame, 1, spec.Name, "front", spec.Size, spec.Size));
        }
        var project = new ProjectProfile(root, "R/S test", ProjectVersion.Ccz65, false, resources, []);
        var r = new LogicalImageAsset("Pmapobj.e5", 0, "R000", ImageCategory.CharacterR, ResourceKind.RComposite, rFrames, [], false, false, string.Empty);
        var s = new LogicalImageAsset("SComposite", 2, "S_002", ImageCategory.CharacterS, ResourceKind.SComposite, sFrames, [], true, false, string.Empty)
        { SMapping = new SLogicalAssetMapping(2, false, [new(1, 2)], 3), SMode = SImageMode.Job, IsFixedSlot = true };
        return (project, r, s);
    }

    private static byte[] RawStrip(int width, int height, int count, int value)
    {
        var bytes = Enumerable.Repeat((byte)value, width * height * count + 2).ToArray();
        bytes[^2] = 0x12; bytes[^1] = 0x34; return bytes;
    }

    private static byte[] BuildE5(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var slot = E5Archive.IndexOffset + index * E5Archive.EntrySize;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(slot, 4), (uint)payloads[index].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(slot + 4, 4), (uint)payloads[index].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(slot + 8, 4), (uint)offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }

    private static void CheckBmp(string path, Size expected)
    {
        using var bitmap = new Bitmap(path);
        Require(bitmap.Size == expected && bitmap.PixelFormat == PixelFormat.Format24bppRgb,
            "动作条导出必须为正确尺寸的彩色 24bpp BMP。");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("期望异常：" + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
