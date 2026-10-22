using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

/// <summary>
/// Saves real mutation plans, reopens their physical files, and exercises both
/// history directions. Generated fixtures replace optional clipboard inputs;
/// real image/DLL samples are copied before any mutation is planned.
/// </summary>
internal static class ResourceWritebackMatrixTests
{
    private static readonly Dictionary<string, string> SourceHashes = new(StringComparer.OrdinalIgnoreCase);
    private static string _temporaryRoot = string.Empty;
    private static string _sampleRoot = string.Empty;
    private static int _transactions;

    public static void Run(string? repositoryRoot = null)
    {
        _sampleRoot = FindSamples(repositoryRoot ?? Directory.GetCurrentDirectory());
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "ImageManagerWritebackMatrix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
        SourceHashes.Clear(); _transactions = 0;
        try
        {
            GenericE5();
            RsArchives();
            Weather();
            MeffAnimation();
            E5IconPairs("Item.e5", ImageCategory.ItemIcon);
            E5IconPairs("Mtem.e5", ImageCategory.StrategyIcon);
            DllIcons("Itemicon.dll", ImageCategory.ItemIcon);
            DllIcons("Mgcicon.dll", ImageCategory.StrategyIcon);
            CmdIcons();
            FaceTou();
            MapPair();
            foreach (var source in SourceHashes)
                Require(HistoryService.HashFile(source.Key) == source.Value, "矩阵误写原始样本：" + source.Key);
            Console.WriteLine($"RESOURCE_WRITEBACK_MATRIX_TESTS_OK：{_transactions} 次 Commit→磁盘复读→Undo→Redo；原始样本SHA不变。");
        }
        finally
        {
            var resolved = Path.GetFullPath(_temporaryRoot);
            Require(Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(resolved).StartsWith("ImageManagerWritebackMatrix_", StringComparison.Ordinal), "测试清理目录不安全。");
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(resolved, true);
        }
    }

    private static void GenericE5()
    {
        var root = Folder("普通E5");
        var path = Path.Combine(root, "Logo.e5");
        File.WriteAllBytes(path, Archive([ImageBytes(16, 16, Color.Red), ImageBytes(16, 16, Color.Blue)]));
        var project = Project(root, ("Logo.e5", path));
        var resource = Resource("Logo.e5", ImageCategory.Scene, ResourceKind.E5Indexed, [path]);
        var codec = new GenericE5ResourceCodec();
        var asset = codec.ReadAssets(project, resource)[0];
        var untouched = E5Archive.OpenFresh(path).ReadStored(2);
        var input = Path.Combine(root, "green.png"); File.WriteAllBytes(input, ImageBytes(16, 16, Color.Lime));
        var service = Planner(root);
        void GreenFirst()
        {
            var frames = codec.DecodeFrames(project, asset);
            try { Require(frames.Single().Bitmap.GetPixel(5, 5).G > 240, "普通E5替换/像素没有复读到新颜色。"); }
            finally { Dispose(frames); }
            Equal(untouched, E5Archive.OpenFresh(path).ReadStored(2), "普通E5误改其他条目");
        }
        RoundTrip(project, service.PlanReplace(project, [asset], [input]), "普通E5/替换", GreenFirst);
        using var edited = Solid(16, 16, Color.Lime);
        RoundTrip(project, service.PlanReplaceSingleFrame(project, asset, asset.Frames[0], edited), "普通E5/像素", GreenFirst);
        RoundTrip(project, service.PlanAppend(project, resource, [input]), "普通E5/追加", () =>
        {
            Require(E5Archive.OpenFresh(path).Entries.Count == 3, "普通E5追加数量错误。");
            var added = codec.ReadAssets(project, resource)[^1];
            var frames = codec.DecodeFrames(project, added);
            try { Require(frames.Single().Bitmap.GetPixel(5, 5).G > 240, "普通E5追加后不能重新解码。"); }
            finally { Dispose(frames); }
        });
        RoundTrip(project, service.PlanReplaceSingleFrame(project, asset, asset.Frames[0], edited, appendCopy: true), "普通E5/像素复制追加", () =>
        {
            var archive = E5Archive.OpenFresh(path);
            Require(archive.Entries.Count == 3, "普通E5像素复制追加没有扩容。");
            using var image = BmpPayloadService.Decode(archive.ReadDecoded(3));
            Require(image.GetPixel(5, 5).G > 240, "普通E5复制追加像素错误。");
        });
        RoundTrip(project, service.PlanDelete(project, [asset]), "普通E5/删除", () =>
        {
            var archive = E5Archive.OpenFresh(path);
            Require(archive.Entries.Count == 1, "普通E5删除数量错误。");
            Equal(untouched, archive.ReadStored(1), "普通E5删除后后续载荷错误");
            var frames = codec.DecodeFrames(project, codec.ReadAssets(project, resource).Single());
            try { Require(frames.Single().Bitmap.GetPixel(5, 5).B > 240, "普通E5删除后复读条目错误。"); }
            finally { Dispose(frames); }
        });
    }

    private static void RsArchives()
    {
        var root = Folder("RS");
        var palette = Palette(root);
        var rPath = Path.Combine(root, "Pmapobj.e5");
        File.WriteAllBytes(rPath, Archive([Strip(48, 64, 20, 30), Strip(48, 64, 20, 60),
            Strip(48, 64, 20, 90), Strip(48, 64, 20, 100)]));
        var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Spalet.e5"] = palette, ["Pmapobj.e5"] = rPath };
        var rFrames = new List<FrameDescriptor>();
        for (var side = 0; side < 2; side++)
        for (var frame = 0; frame < 20; frame++)
            rFrames.Add(new("Pmapobj.e5", rPath, side + 1, frame, 1, "r-action", side == 0 ? "front" : "back", 48, 64));
        var sFrames = new List<FrameDescriptor>();
        foreach (var spec in new[] { (Name: "mov", Size: 48, Count: 11), (Name: "atk", Size: 64, Count: 12), (Name: "spc", Size: 48, Count: 5) })
        {
            var name = "Unit_" + spec.Name + ".e5"; var path = Path.Combine(root, name); resources[name] = path;
            File.WriteAllBytes(path, Archive(Enumerable.Range(1, 244)
                .Select(number => Strip(spec.Size, spec.Size, spec.Count, 81 + (number - 1) % 4)).ToArray()));
            for (var frame = 0; frame < spec.Count; frame++)
                sFrames.Add(new(name, path, 242, frame, 1, spec.Name, "front", spec.Size, spec.Size));
        }
        var project = new ProjectProfile(root, "确定性RS", ProjectVersion.Ccz65, false, resources, []);
        var r = new LogicalImageAsset("Pmapobj.e5", 0, "R000", ImageCategory.CharacterR, ResourceKind.RComposite, rFrames, [], false, false, "");
        var s = new LogicalImageAsset("SComposite", 242, "S_242", ImageCategory.CharacterS, ResourceKind.SComposite, sFrames, [], false, false, "")
        { SMapping = new(242, false, [new(1, 242)], 244) };
        var catalog = new ImageCatalogService(); var service = Planner(root); var importer = new RsImportService();
        var colors = new PaletteService().RequireWritePalette(project);

        // Produce and edit an atlas locally. No clipboard attachment or early
        // successful return can bypass the three-file import transaction.
        var originalFrames = catalog.DecodeFrames(project, s);
        try
        {
            var atlas = new ReferenceAtlasService();
            var exported = atlas.Export(Path.Combine(root, "确定性AI输入"), s, originalFrames, ReferenceAtlasService.ResolveKeyColor(colors));
            var layout = new ReferenceAtlasLayoutPlanner().Plan(s, originalFrames);
            var editedPath = Path.Combine(root, "确定性AI输入", "edited.png");
            var selected = layout.Placements.Where(p => p.Frame.Descriptor.FrameIndex == 0).ToArray();
            Require(selected.Length == 3, "确定性S参考图没有三类动作。");
            using (var image = new Bitmap(exported.PngPath))
            {
                using var graphics = Graphics.FromImage(image);
                foreach (var placement in selected)
                {
                    using var brush = new SolidBrush(colors[230]);
                    graphics.FillRectangle(brush, placement.Bounds.X + 8, placement.Bounds.Y + 8, 8, 8);
                }
                image.Save(editedPath, ImageFormat.Png);
            }
            using var normalized = atlas.Normalize(editedPath, s, originalFrames, new AiAtlasNormalizationOptions());
            Require(!normalized.HasBlockingIssues, "确定性参考图归一化阻止了导入。");
            var plan = importer.PlanImport(project, s, normalized.Frames);
            Require(plan.Files.Count == 3, "S参考图导入没有包含三包。");
            RoundTrip(project, plan, "S三包/确定性参考图导入", () =>
            {
                foreach (var placement in selected)
                {
                    var descriptor = placement.Frame.Descriptor;
                    var archive = E5Archive.OpenFresh(descriptor.SourcePath);
                    Require(archive.ReadDecoded(242)[8 * descriptor.Width + 8] == 230, "S导入磁盘像素错误：" + descriptor.Action);
                    Require(archive.ReadDecoded(1)[0] == 81 && archive.ReadDecoded(241)[0] == 81 &&
                        archive.ReadDecoded(243)[0] == 83 && archive.ReadDecoded(244)[0] == 84,
                        "S参考图导入误改其他套/逻辑资源。");
                }
                AssertDecoded(project, s, 28);
            });
        }
        finally { Dispose(originalFrames); }

        foreach (var asset in new[] { r, s })
        {
            var descriptor = asset.Frames.First(frame => frame.FrameIndex == 3);
            var frames = catalog.DecodeFrames(project, asset);
            try
            {
                using var edited = new Bitmap(frames.Single(frame => frame.Descriptor == descriptor).Bitmap);
                edited.SetPixel(5, 6, colors[210]);
                RoundTrip(project, service.PlanReplaceSingleFrame(project, asset, descriptor, edited), asset.DisplayName + "/像素", () =>
                {
                    var bytes = E5Archive.OpenFresh(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
                    Require(bytes[descriptor.FrameIndex * descriptor.Width * descriptor.Height + 6 * descriptor.Width + 5] == 210,
                        asset.DisplayName + "像素没有落盘。");
                    AssertDecoded(project, asset, asset.Frames.Count);
                });
                var before = asset.Frames.GroupBy(frame => frame.SourcePath).ToDictionary(group => group.Key,
                    group => E5Archive.OpenFresh(group.Key).Entries.Count, StringComparer.OrdinalIgnoreCase);
                RoundTrip(project, importer.PlanAppendCopy(project, asset, frames, preferredTurn: 2), asset.DisplayName + "/复制追加", () =>
                {
                    foreach (var pair in before)
                    {
                        var archive = E5Archive.OpenFresh(pair.Key);
                        var added = asset.Kind == ResourceKind.RComposite ? 2 : 1;
                        Require(archive.Entries.Count == pair.Value + added, asset.DisplayName + "追加数量错误。");
                        var sourceNumber = asset.Kind == ResourceKind.RComposite ? 1 : 242;
                        Equal(archive.ReadDecoded(sourceNumber), archive.ReadDecoded(pair.Value + 1), "R/S复制追加载荷错误");
                    }
                    var appended = AppendedRsAsset(asset, before);
                    AssertDecoded(project, appended, appended.Frames.Count);
                });
            }
            finally { Dispose(frames); }

            var strips = Path.Combine(root, asset.DisplayName + "动作条");
            new RsStripExchangeService().Export(project, asset, strips, preferredTurn: 2);
            var stem = asset.Kind == ResourceKind.RComposite ? "front" : "mov";
            var input = asset.Kind == ResourceKind.RComposite
                ? Path.Combine(strips, asset.DisplayName, stem + ".bmp")
                : Path.Combine(strips, "将领", "S_242", stem + ".bmp");
            using (var image = new Bitmap(input))
            {
                image.SetPixel(2, 2, colors[220]); image.Save(input + ".new.bmp", ImageFormat.Bmp);
            }
            File.Move(input + ".new.bmp", input, true);
            var replacement = new RsStripExchangeService().PlanReplace(project, asset, strips, preferredTurn: 2);
            RoundTrip(project, replacement, asset.DisplayName + "/动作条替换", () =>
            {
                var descriptor = asset.Frames.First(frame =>
                    asset.Kind == ResourceKind.RComposite ? frame.Direction == "front" : frame.Action == "mov");
                var payload = E5Archive.OpenFresh(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
                if (asset.Kind == ResourceKind.RComposite)
                {
                    Require(payload.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                        "RAW目标R的文件导入没有转为PNG存储。");
                    using var stream = new MemoryStream(payload, false);
                    using var image = new Bitmap(stream);
                    Require(Image.GetPixelFormatSize(image.PixelFormat) == 32 && image.Size == new Size(48, 1280),
                        "R文件导入没有生成32位标准竖条。");
                    Require(image.GetPixel(2, 2).ToArgb() == colors[220].ToArgb(), "R动作条替换的真实PNG像素没有落盘。");
                }
                else
                {
                    Require(payload.Length == 48 * 48 * 11 + 2 && payload[2 * descriptor.Width + 2] == 220 &&
                        payload[^2] == 0x12 && payload[^1] == 0x34, "S动作条替换没有保持RAW索引像素及尾部。");
                    var decoded = catalog.DecodeFrames(project, asset);
                    try
                    {
                        Require(decoded.Single(frame => frame.Descriptor.Action == "mov" && frame.Descriptor.FrameIndex == 0)
                            .Bitmap.GetPixel(2, 2).ToArgb() == colors[220].ToArgb(), "S动作条替换的真实解码像素没有落盘。");
                    }
                    finally { Dispose(decoded); }
                }
                AssertDecoded(project, asset, asset.Frames.Count);
            });
            var paths = asset.Frames.Select(frame => frame.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var counts = paths.ToDictionary(path => path, path => E5Archive.OpenFresh(path).Entries.Count);
            var resource = Resource(asset.ResourceKey, asset.Category, asset.Kind, paths);
            RoundTrip(project, importer.PlanAppend(project, resource, strips), asset.DisplayName + "/动作条追加", () =>
            {
                foreach (var path in paths)
                    Require(E5Archive.OpenFresh(path).Entries.Count == counts[path] + (asset.Kind == ResourceKind.RComposite ? 2 : 1),
                        "R/S动作条追加数量错误。");
                var appended = AppendedRsAsset(asset, counts);
                AssertDecoded(project, appended, appended.Frames.Count);
            });
            RoundTrip(project, service.PlanDelete(project, [asset]), asset.DisplayName + "/删除", () =>
            {
                foreach (var path in paths)
                {
                    var archive = E5Archive.OpenFresh(path);
                    Require(archive.Entries.Count == (asset.Kind == ResourceKind.RComposite ? 2 : 243), "R/S删除没有同步重建目录。");
                    Require(archive.ReadDecoded(1)[0] == (asset.Kind == ResourceKind.RComposite ? 90 : 81), "R/S删除误改未选资源。");
                    if (asset.Kind == ResourceKind.SComposite)
                        Require(archive.ReadDecoded(241)[0] == 81 && archive.ReadDecoded(242)[0] == 83 && archive.ReadDecoded(243)[0] == 84,
                            "单图S删除未只前移所选槽后面的图片。");
                }
                var remaining = asset.Kind == ResourceKind.RComposite ? asset
                    : asset with { Frames = asset.Frames.Where(frame => frame.Turn == 1).ToArray(), SMapping = null };
                AssertDecoded(project, remaining, remaining.Frames.Count);
            });
        }
        var job = s with { LogicalId = 1, DisplayName = "S_001", SMode = SImageMode.Job, IsFixedSlot = true,
            Frames = s.Frames.Select(frame => frame with { ImageNumber = 1 }).ToArray(), SMapping = new(1, false, [new(1, 1)], 244) };
        RoundTrip(project, service.PlanDelete(project, [job]), "兵种S/固定槽清空", () =>
        {
            foreach (var path in job.Frames.Select(frame => frame.SourcePath).Distinct())
            {
                var archive = E5Archive.OpenFresh(path); var bytes = archive.ReadDecoded(1);
                Require(archive.Entries.Count == 244 && bytes.AsSpan(0, bytes.Length - 2).ToArray().All(value => value == 0), "兵种S没有原位清空。");
                Require(bytes[^2] == 0x12 && bytes[^1] == 0x34 && archive.ReadDecoded(2)[0] == 82, "兵种S清空误改尾部或后续槽。");
            }
            AssertDecoded(project, job, 28);
        });
    }

    private static void Weather()
    {
        var root = Folder("Weather"); var path = Path.Combine(root, "Weather.e5"); var palettePath = Palette(root);
        var payload = Enumerable.Range(1, 4).SelectMany(index => Enumerable.Repeat((byte)index, 216 * 50)).ToArray();
        File.WriteAllBytes(path, Archive([payload]));
        var project = Project(root, ("Weather.e5", path), ("Spalet.e5", palettePath));
        var resource = Resource("Weather.e5", ImageCategory.Dynamic, ResourceKind.WeatherSpecial, [path]);
        var codec = new WeatherResourceCodec(); var asset = codec.ReadAssets(project, resource).Single();
        var frames = codec.DecodeFrames(project, asset); var colors = new PaletteService().RequireWritePalette(project); var service = Planner(root);
        try
        {
            using var edited = new Bitmap(frames[2].Bitmap); edited.SetPixel(5, 6, colors[200]);
            RoundTrip(project, service.PlanReplaceSingleFrame(project, asset, frames[2].Descriptor, edited), "Weather/第三帧像素", () =>
            {
                var after = E5Archive.OpenFresh(path).ReadDecoded(1); var offset = 2 * 216 * 50 + 6 * 216 + 5;
                Require(after.Length == 43200 && after[offset] == 200 && Enumerable.Range(0, after.Length).All(i => i == offset || after[i] == payload[i]),
                    "Weather像素写回破坏其他像素/帧。");
                AssertFrames(codec.DecodeFrames(project, codec.ReadAssets(project, resource).Single()), 4);
            });
            using var strip = ImageRendering.DecodeIndexed(payload, 216, 200, colors); strip.SetPixel(9, 155, colors[201]);
            var input = Path.Combine(root, "weather.png"); strip.Save(input, ImageFormat.Png);
            RoundTrip(project, service.PlanReplace(project, [asset], [input]), "Weather/完整动作条替换", () =>
            {
                var after = E5Archive.OpenFresh(path).ReadDecoded(1);
                Require(after.Length == 43200 && after[155 * 216 + 9] == 201, "Weather整条替换复读错误。");
                AssertFrames(codec.DecodeFrames(project, codec.ReadAssets(project, resource).Single()), 4);
            });
        }
        finally { Dispose(frames); }
    }

    private static void E5IconPairs(string name, ImageCategory category)
    {
        var root = Folder(name + "图标");
        var path = CopySample("新改曹操傳6.6修正版", Path.Combine("E5", name), root);
        var project = Project(root, (name, path)); var resource = Resource(name, category, ResourceKind.E5Indexed, [path]);
        var codec = new PairedE5IconCodec(); var asset = codec.ReadAssets(project, resource)[0];
        var count = E5Archive.OpenFresh(path).Entries.Count; var nextPayload = E5Archive.OpenFresh(path).ReadStored(3);
        var frames = codec.DecodeFrames(project, asset);
        Size size;
        try { size = frames.Single(frame => frame.Descriptor.Action == "large").Bitmap.Size; }
        finally { Dispose(frames); }
        using var edited = Solid(size.Width, size.Height, Color.Red);
        var input = Path.Combine(root, "red.png"); edited.Save(input, ImageFormat.Png); var service = Planner(root);
        void VerifyAsset(LogicalImageAsset value)
        {
            var decoded = codec.DecodeFrames(project, value);
            try
            {
                Require(decoded.Count == 2 && decoded.All(frame => frame.Bitmap.GetPixel(frame.Bitmap.Width / 2, frame.Bitmap.Height / 2).R > 200),
                    name + "大小图没有同步落盘。");
            }
            finally { Dispose(decoded); }
        }
        RoundTrip(project, service.PlanReplace(project, [asset], [input]), name + "/替换", () => VerifyAsset(codec.ReadAssets(project, resource)[0]));
        RoundTrip(project, service.PlanItemIconEdit(project, asset, edited, false), name + "/像素", () => VerifyAsset(codec.ReadAssets(project, resource)[0]));
        RoundTrip(project, service.PlanAppend(project, resource, [input]), name + "/追加", () =>
        {
            Require(E5Archive.OpenFresh(path).Entries.Count == count + 2, name + "追加大小图数量错误。");
            VerifyAsset(codec.ReadAssets(project, resource)[^1]);
        });
        RoundTrip(project, service.PlanItemIconEdit(project, asset, edited, true), name + "/像素复制追加", () =>
        {
            Require(E5Archive.OpenFresh(path).Entries.Count == count + 2, name + "复制追加数量错误。");
            VerifyAsset(codec.ReadAssets(project, resource)[^1]);
        });
        RoundTrip(project, service.PlanDelete(project, [asset]), name + "/删除", () =>
        {
            var archive = E5Archive.OpenFresh(path); Require(archive.Entries.Count == count - 2, name + "删除未移除完整对。");
            Equal(nextPayload, archive.ReadStored(1), name + "删除改变后续原始载荷");
            AssertFrames(codec.DecodeFrames(project, codec.ReadAssets(project, resource)[0]), 2);
        });
    }

    private static void MeffAnimation()
    {
        var root = Folder("Meff动画包"); var path = Path.Combine(root, "Meff.e5"); var palette = Palette(root);
        const int width = 8, height = 6, recordLength = 80, planeSize = width * height;
        var original = new byte[recordLength + planeSize * 2 + 3];
        original[0] = 3; original[1] = 2; original[3] = 0x12;
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(4, 4), 80);
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(12, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(16, 4), 77);
        for (var record = 1; record <= 3; record++)
        { original[record * 20] = (byte)(record % 2); original[record * 20 + 1] = 255; }
        Array.Fill(original, (byte)20, recordLength, planeSize);
        Array.Fill(original, (byte)30, recordLength + planeSize, planeSize);
        original[^3] = 0xAA; original[^2] = 0xBB; original[^1] = 0xCC;
        var guard = (byte[])original.Clone(); guard[recordLength] = 60;
        File.WriteAllBytes(path, Archive([original, guard]));
        var project = Project(root, ("Meff.e5", path), ("Spalet.e5", palette));
        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(project).Single(value => value.Key == "Meff.e5");
        Require(resource.CanMutate, "确定性Meff动画包没有开放写回。");
        var codec = new StrategyAnimationResourceCodec(); var asset = codec.ReadAssets(project, resource)[0];
        var packageCodec = new StrategyAnimationPackageCodec();
        using var package = packageCodec.Decode(project, original);
        Require(package.PlaybackSequence.SequenceEqual(new[] { 1, 0, 1 }), "Meff播放顺序fixture不正确。");
        using var edited = new Bitmap(package.PlaneFrames[package.PlaybackSequence[0]]);
        edited.SetPixel(3, 2, new PaletteService().RequireWritePalette(project)[230]);
        var changed = packageCodec.RebuildFramePlanes(project, package,
            new Dictionary<int, Bitmap> { [package.PlaybackSequence[0]] = edited });
        var expectedOffset = recordLength + planeSize + 2 * width + 3;
        Require(changed[expectedOffset] == 230 && Enumerable.Range(0, original.Length).All(index =>
            index == expectedOffset || changed[index] == original[index]), "Meff重建误改首播放平面外的字节。");
        var service = Planner(root);
        var incoming = Path.Combine(root, "incoming.animation"); File.WriteAllBytes(incoming, changed);
        MutationPlan PackagePlan(byte[] candidate, MutationKind kind, string summary)
            => new(Guid.NewGuid().ToString("N"), kind, summary, [new(path, candidate, "保留动画记录、尾部及其他E5条目")], [], false);
        void VerifyChanged(int imageNumber, int expectedCount)
        {
            var archive = E5Archive.OpenFresh(path);
            Require(archive.Entries.Count == expectedCount, "Meff写回条目数错误。");
            Equal(changed, archive.ReadDecoded(imageNumber), "Meff包像素或保留字节复读错误");
            Equal(guard, archive.ReadDecoded(2), "Meff写回误改其他动画条目");
            if (imageNumber != 1) Equal(original, archive.ReadDecoded(1), "Meff复制追加误改原动画包");
            var after = codec.ReadAssets(project, resource).Single(value => value.Frames.Single().ImageNumber == imageNumber);
            AssertFrames(codec.DecodeFrames(project, after), 3);
            using var reopened = packageCodec.Decode(project, archive.ReadDecoded(imageNumber));
            Require(reopened.PlaybackSequence.SequenceEqual(package.PlaybackSequence) && reopened.Flags == package.Flags &&
                reopened.SoundParameter == package.SoundParameter, "Meff动画记录、标志或声音参数变化。");
            Equal(package.RecordBytes, reopened.RecordBytes, "Meff记录区变化");
            Equal(package.TailBytes, reopened.TailBytes, "Meff未知尾部变化");
        }
        var replaced = E5Archive.OpenFresh(path).Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [1] = changed });
        RoundTrip(project, service.PlanReplace(project, [asset], [incoming]), "Meff/结构校验整包替换", () => VerifyChanged(1, 2));
        var appended = E5Archive.OpenFresh(path).Rebuild(appendDecoded: [changed]);
        RoundTrip(project, service.PlanAppend(project, resource, [incoming]), "Meff/结构校验包追加", () => VerifyChanged(3, 3));
        RoundTrip(project, PackagePlan(replaced, MutationKind.PixelEdit, "Meff首播放平面像素"), "Meff/首播放平面像素", () => VerifyChanged(1, 2));
        RoundTrip(project, PackagePlan(appended, MutationKind.Append, "Meff像素复制追加"), "Meff/像素复制追加", () => VerifyChanged(3, 3));
        RoundTrip(project, service.PlanDelete(project, [asset]), "Meff/删除动画包", () =>
        {
            var archive = E5Archive.OpenFresh(path); Require(archive.Entries.Count == 1, "Meff删除条目数错误。");
            Equal(guard, archive.ReadDecoded(1), "Meff删除误改后续动画载荷");
            AssertFrames(codec.DecodeFrames(project, codec.ReadAssets(project, resource).Single()), 3);
        });
        var before = File.ReadAllBytes(path);
        var wrongPng = Path.Combine(root, "not-an-animation.png"); File.WriteAllBytes(wrongPng, ImageBytes(16, 16, Color.Red));
        var malformed = (byte[])original.Clone(); malformed[20] = 2;
        var badPackage = Path.Combine(root, "bad.animation"); File.WriteAllBytes(badPackage, malformed);
        foreach (var invalid in new[] { wrongPng, badPackage })
        {
            Reject(() => service.PlanAppend(project, resource, [invalid]), "Meff追加规划接受了普通PNG或越界播放记录。");
            Reject(() => service.PlanReplace(project, [asset], [invalid]), "Meff替换规划接受了普通PNG或越界播放记录。");
        }
        var rejectedHistory = new HistoryService(Path.Combine(root, "拒绝候选历史"));
        foreach (var invalid in new[] { File.ReadAllBytes(wrongPng), malformed })
        {
            var candidate = E5Archive.OpenFresh(path).Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [1] = invalid });
            var result = new ImageMutationService(rejectedHistory).Commit(project,
                PackagePlan(candidate, MutationKind.Replace, "伪造无效Meff候选"));
            Require(!result.Success && result.HistoryId == null && rejectedHistory.List(project).Count == 0,
                "Meff直接提交未阻断无效动画包，或产生了虚假历史。");
            Equal(before, File.ReadAllBytes(path), "Meff无效动画包候选改变了原文件");
        }
        Console.WriteLine("[RESOURCE_WRITEBACK_MATRIX_GUARD_PASS] Meff普通PNG/越界播放记录：追加与替换规划、伪造候选Commit均拒绝，文件与历史不变。");

        static void Reject(Action action, string message)
        {
            var rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, message);
        }
    }

    private static void DllIcons(string name, ImageCategory category)
    {
        var root = Folder(name); var path = CopySample("加强版6.5未加密版", name, root);
        var project = Project(root, (name, path)); var resource = Resource(name, category, ResourceKind.DllBitmap, [path]);
        var codec = new DllBitmapResourceCodec(); var assets = codec.ReadAssets(project, resource); var asset = assets[0];
        Require(asset.Frames.Count == 2, name + "没有完整图标对。");
        var large = asset.Frames.Single(frame => frame.Action == "large");
        using var edited = Solid(large.Width, large.Height, Color.Red);
        var input = Path.Combine(root, "red.png"); edited.Save(input, ImageFormat.Png); var service = Planner(root);
        void RedAsset(LogicalImageAsset value)
        {
            var frames = codec.DecodeFrames(project, value);
            try { Require(frames.Count == 2 && frames.All(frame => frame.Bitmap.GetPixel(0, 0).R > 200), name + "位图对未落盘。"); }
            finally { Dispose(frames); }
        }
        RoundTrip(project, service.PlanReplace(project, [asset], [input]), name + "/替换", () => RedAsset(codec.ReadAssets(project, resource)[0]));
        var pixelPlan = category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon
            ? service.PlanItemIconEdit(project, asset, edited, false)
            : new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit, "命令图标像素",
                [new(path, DllBitmapMutation.BuildReplacementCandidate(path, asset.Frames.Select(frame =>
                    (frame.ImageNumber, frame.SourceOffset, edited, frame.Width, frame.Height)).ToArray()), "RT_BITMAP像素")], [], false);
        RoundTrip(project, pixelPlan, name + "/像素", () => RedAsset(codec.ReadAssets(project, resource)[0]));
        RoundTrip(project, service.PlanAppend(project, resource, [input]), name + "/追加", () =>
        {
            var after = codec.ReadAssets(project, resource); Require(after.Count == assets.Count + 1, name + "追加图标数错误。"); RedAsset(after[^1]);
        });
        if (category is ImageCategory.ItemIcon or ImageCategory.StrategyIcon)
            RoundTrip(project, service.PlanItemIconEdit(project, asset, edited, true), name + "/像素复制追加", () =>
            {
                var after = codec.ReadAssets(project, resource); Require(after.Count == assets.Count + 1, name + "复制追加数错误。"); RedAsset(after[^1]);
            });
        RoundTrip(project, service.PlanDelete(project, [asset]), name + "/删除", () =>
        {
            var after = codec.ReadAssets(project, resource); Require(after.Count == assets.Count - 1, name + "删除完整图标对错误。");
            AssertFrames(codec.DecodeFrames(project, after[0]), after[0].Frames.Count);
        });
    }

    private static void CmdIcons()
    {
        const string name = "Cmdicon.dll";
        var root = Folder(name); var path = CopySample("加强版6.5未加密版", name, root);
        var project = Project(root, (name, path)); var resource = Resource(name, ImageCategory.Interface, ResourceKind.DllBitmap, [path]);
        var codec = new DllBitmapResourceCodec(); var assets = codec.ReadAssets(project, resource);
        var original = DllBitmapArchive.Read(path, requireAllBitmapRecords: true).ToDictionary(entry => (entry.Id, entry.Language));
        var expectedIds = new[] { 1, 2, 3 }.Concat(Enumerable.Range(21, 4)).Concat(Enumerable.Range(31, 22))
            .Concat(Enumerable.Range(61, 3)).Concat(Enumerable.Range(100, 9)).ToArray();
        Require(assets.Count == 41 && assets.All(asset => asset.Frames.Count == 1) &&
            assets.Select(asset => asset.LogicalId).SequenceEqual(expectedIds), "Cmdicon没有完整枚举41个稀疏原生ID。");
        AssertFrames(codec.DecodeFrames(project, assets.Single(asset => asset.LogicalId == 21)), 1);
        AssertFrames(codec.DecodeFrames(project, assets.Single(asset => asset.LogicalId == 108)), 1);
        var asset = assets.Single(value => value.LogicalId == 21); var target = asset.Frames.Single();
        using var edited = Solid(target.Width, target.Height, Color.Red);
        using var custom = Solid(33, 17, Color.Red);
        var input = Path.Combine(root, "red.png"); edited.Save(input, ImageFormat.Png);
        var customInput = Path.Combine(root, "custom.png"); custom.Save(customInput, ImageFormat.Png);
        var service = Planner(root);

        void VerifyOldResources(bool targetChanged = false, bool targetDeleted = false)
        {
            var after = DllBitmapArchive.Read(path, requireAllBitmapRecords: true).ToDictionary(entry => (entry.Id, entry.Language));
            foreach (var pair in original)
            {
                if (targetDeleted && pair.Key.Id == target.ImageNumber)
                { Require(!after.ContainsKey(pair.Key), "Cmdicon删除仍保留所选原生ID。"); continue; }
                Require(after.TryGetValue(pair.Key, out var entry), "Cmdicon误删或移动其他原生ID。");
                if (targetChanged && pair.Key == (target.ImageNumber, target.SourceOffset)) continue;
                Equal(pair.Value.Dib, entry!.Dib, "Cmdicon改变非目标原生资源载荷");
            }
        }
        void RedSingle(LogicalImageAsset value, int width, int height)
        {
            Require(value.Frames.Count == 1 && value.Frames[0].Width == width && value.Frames[0].Height == height,
                "Cmdicon单项尺寸被配对模板改写。");
            var decoded = codec.DecodeFrames(project, value);
            try { Require(decoded.Single().Bitmap.GetPixel(0, 0).R > 200, "Cmdicon新像素未落盘。"); }
            finally { Dispose(decoded); }
        }
        void VerifyTarget()
        {
            Require(codec.ReadAssets(project, resource).Count == 41, "Cmdicon替换改变原生项数。");
            VerifyOldResources(targetChanged: true);
            RedSingle(codec.ReadAssets(project, resource).Single(value => value.LogicalId == 21), target.Width, target.Height);
        }
        void VerifyAppend(int width, int height)
        {
            var after = codec.ReadAssets(project, resource);
            Require(after.Count == 42 && after[^1].LogicalId == 109 &&
                DllBitmapArchive.Read(path, true).Count == original.Count + 1, "Cmdicon追加没有生成一个独立原生ID=109。");
            VerifyOldResources(); RedSingle(after[^1], width, height);
        }
        RoundTrip(project, service.PlanReplace(project, [asset], [input]), "Cmdicon/原生ID21替换", VerifyTarget);
        var pixel = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit, "命令图标像素",
            [new(path, DllBitmapMutation.BuildReplacementCandidate(path,
                [(target.ImageNumber, target.SourceOffset, edited, target.Width, target.Height)]), "原生ID21像素")], [], false);
        RoundTrip(project, pixel, "Cmdicon/原生ID21像素", VerifyTarget);
        var nativeTemplate = original.Values.OrderBy(entry => entry.Id).First();
        RoundTrip(project, service.PlanAppend(project, resource, [customInput]), "Cmdicon/任意33x17素材按模板画布追加",
            () => VerifyAppend(nativeTemplate.Width, nativeTemplate.Height));
        var copy = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.Append, "命令图标像素复制追加",
            [new(path, DllBitmapMutation.BuildAppendNativeSinglesCandidate(path, [edited]), "独立位图复制追加")], [], false);
        RoundTrip(project, copy, "Cmdicon/像素复制追加", () => VerifyAppend(target.Width, target.Height));
        RoundTrip(project, service.PlanDelete(project, [asset]), "Cmdicon/仅删ID21保持其他稀疏ID", () =>
        {
            var after = codec.ReadAssets(project, resource);
            Require(after.Count == 40 && after.Select(value => value.LogicalId).SequenceEqual(expectedIds.Where(id => id != 21)),
                "Cmdicon删除压缩或重排了稀疏原生ID。");
            VerifyOldResources(targetDeleted: true);
            AssertFrames(codec.DecodeFrames(project, after.Single(value => value.LogicalId == 22)), 1);
            AssertFrames(codec.DecodeFrames(project, after.Single(value => value.LogicalId == 108)), 1);
        });
    }

    private static void FaceTou()
    {
        var root = Folder("FaceTou"); var face = CopySample("东吴霸王传", "Face.e5", root); var tou = CopySample("东吴霸王传", "Tou.dll", root);
        var project = Project(root, ("Face.e5", face), ("Tou.dll", tou));
        var resource = Resource("Face", ImageCategory.Portrait, ResourceKind.FaceComposite, [face, tou]);
        var codec = new FaceResourceCodec(); var assets = codec.ReadAssets(project, resource); var asset = assets[1];
        Require(asset.Frames.Any(frame => frame.SourceKey == "Tou.dll"), "头像样本没有Tou配对。");
        var count = E5Archive.OpenFresh(face).Entries.Count; var service = Planner(root);
        var originalTou = DllBitmapArchive.Read(tou, true).ToDictionary(record => (record.Id, record.Language));
        using var edited = Solid(96, 96, Color.Red); var input = Path.Combine(root, "red.png"); edited.Save(input, ImageFormat.Png);
        void Reopen(LogicalImageAsset value)
        {
            var frames = codec.DecodeFrames(project, value);
            try
            {
                Require(frames.Count == value.Frames.Count && frames.Any(frame => frame.Descriptor.SourceKey == "Tou.dll" &&
                    frame.Bitmap.GetPixel(frame.Bitmap.Width / 2, frame.Bitmap.Height / 2).R > 200),
                    "Face/Tou写回后不能复读配对图像。");
            }
            finally { Dispose(frames); }
        }
        void ReopenFileImport(LogicalImageAsset value)
        {
            var frames = codec.DecodeFrames(project, value);
            var currentTou = DllBitmapArchive.Read(tou, true).ToDictionary(record => (record.Id, record.Language));
            try
            {
                Require(frames.Count == value.Frames.Count && frames.Any(frame => frame.Descriptor.SourceKey == "Tou.dll"),
                    "Face/Tou文件导入没有保留可解码的配对图像。");
                foreach (var frame in frames.Where(frame => frame.Descriptor.SourceKey == "Tou.dll"))
                {
                    var key = (frame.Descriptor.ImageNumber, frame.Descriptor.SourceOffset);
                    var originalKey = originalTou.ContainsKey(key) ? key : (count + 300, frame.Descriptor.SourceOffset);
                    var template = originalTou[originalKey]; var actual = currentTou[key];
                    Require(DibBits(actual.Dib) == DibBits(template.Dib) && actual.Width == template.Width && actual.Height == template.Height,
                        "Tou文件导入没有保持原目标/追加模板的DIB色深与画布。");
                    var expected = RedInOriginalPalette(template.Dib);
                    var pixel = frame.Bitmap.GetPixel(frame.Bitmap.Width / 2, frame.Bitmap.Height / 2);
                    Require(pixel.R == expected.R && pixel.G == expected.G && pixel.B == expected.B,
                        $"Tou文件导入没有落盘符合原DIB调色板的真实像素：期待{expected}，实际{pixel}。");
                }
            }
            finally { Dispose(frames); }
        }
        RoundTrip(project, service.PlanReplace(project, [asset], [input]), "FaceTou/替换", () => ReopenFileImport(codec.ReadAssets(project, resource)[1]));
        RoundTrip(project, service.PlanAppend(project, resource, [input]), "FaceTou/追加", () =>
        {
            Require(E5Archive.OpenFresh(face).Entries.Count == count + 1, "Face追加条目错误。");
            ReopenFileImport(codec.ReadAssets(project, resource)[^1]);
        });
        foreach (var copy in new[] { false, true })
            RoundTrip(project, FacePixelPlan(project, asset, edited, copy), "FaceTou/像素" + (copy ? "复制追加" : ""), () =>
            {
                Require(E5Archive.OpenFresh(face).Entries.Count == count + (copy ? 1 : 0), "Face像素/复制追加数量错误。");
                Reopen(copy ? codec.ReadAssets(project, resource)[^1] : codec.ReadAssets(project, resource)[1]);
            });
        RoundTrip(project, service.PlanDelete(project, [asset]), "FaceTou/删除", () =>
        {
            Require(E5Archive.OpenFresh(face).Entries.Count == count - 1 && codec.ReadAssets(project, resource).Count == assets.Count - 1,
                "FaceTou删除没有同步减少条目。");
            AssertFrames(codec.DecodeFrames(project, codec.ReadAssets(project, resource)[1]), 2);
        });
        // The first logical portrait is eight fixed expression entries. Edit
        // its first frame by the same public payload/DLL APIs used by the UI.
        var expression = assets[0];
        RoundTrip(project, FacePixelPlan(project, expression, edited, false), "FaceTou/固定八表情首帧像素", () => Reopen(codec.ReadAssets(project, resource)[0]));
    }

    private static int DibBits(byte[] dib)
        => BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(dib) == 12 ? 10 : 14));

    private static Color RedInOriginalPalette(byte[] dib)
    {
        if (DibBits(dib) != 8) return Color.Red;
        var header = BinaryPrimitives.ReadInt32LittleEndian(dib); var core = header == 12;
        var count = core ? 256 : (int)BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (count == 0) count = 256;
        var stride = core ? 3 : 4; var best = Color.Empty; var distance = int.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var at = header + index * stride;
            var candidate = Color.FromArgb(dib[at + 2], dib[at + 1], dib[at]);
            var dr = 255 - candidate.R;
            var current = dr * dr + candidate.G * candidate.G + candidate.B * candidate.B;
            if (current >= distance) continue;
            distance = current; best = candidate;
        }
        return best;
    }

    private static MutationPlan FacePixelPlan(ProjectProfile project, LogicalImageAsset asset, Bitmap edited, bool append)
    {
        var frame = asset.Frames.First(value => value.SourceKey == "Face.e5");
        var archive = E5Archive.OpenFresh(frame.SourcePath);
        var payload = FacePayloadService.Rebuild(project, archive.ReadDecoded(frame.ImageNumber), edited);
        var candidate = append ? archive.Rebuild(appendDecoded: [payload])
            : archive.Rebuild(replacementDecoded: new Dictionary<int, byte[]> { [frame.ImageNumber] = payload });
        var files = new List<MutationFile> { new(frame.SourcePath, candidate, "Face像素") };
        var tou = asset.Frames.First(value => value.SourceKey == "Tou.dll");
        var touCandidate = append ? DllBitmapMutation.BuildAppendSinglesCandidate(tou.SourcePath, [edited])
            : DllBitmapMutation.BuildReplacementCandidate(tou.SourcePath, [(tou.ImageNumber, tou.SourceOffset, edited, tou.Width, tou.Height)]);
        files.Add(new(tou.SourcePath, touCandidate, "Tou同步像素"));
        return new(Guid.NewGuid().ToString("N"), append ? MutationKind.Append : MutationKind.PixelEdit, "FaceTou像素", files, [], false);
    }

    private static void MapPair()
    {
        var root = Folder("MapPair"); var map = Path.Combine(root, "M000.JPG"); var hex = Path.Combine(root, "Hexzmap.e5");
        File.WriteAllBytes(map, ImageBytes(96, 96, Color.Blue, ImageFormat.Jpeg));
        File.WriteAllBytes(hex, Archive([new byte[] { 0x12, 0x34, 1, 2, 3, 4 }, new byte[] { 8, 9, 10 }]));
        var project = Project(root, ("MapDirectory", root), ("Hexzmap.e5", hex));
        var resource = Resource("MapPairs", ImageCategory.Scene, ResourceKind.MapPair, [map]);
        var codec = new MapPairResourceCodec(); var asset = codec.ReadAssets(project, resource).Single(); var service = new MapPairService();
        var output = Path.Combine(root, "import"); var exported = service.Export(project, asset, output);
        File.WriteAllBytes(exported.JpegPath, ImageBytes(96, 96, Color.Red, ImageFormat.Jpeg));
        var terrain = File.ReadAllBytes(exported.BmpPath); terrain[^4] = 7; File.WriteAllBytes(exported.BmpPath, terrain);
        var originalHex = File.ReadAllBytes(hex);
        RoundTrip(project, service.PlanImport(project, asset, output), "地图对/底图及地形替换", () =>
        {
            var frames = codec.DecodeFrames(project, codec.ReadAssets(project, resource).Single());
            try { Require(frames.Single().Bitmap.GetPixel(5, 5).R > 200, "地图底图没有落盘。"); }
            finally { Dispose(frames); }
            Equal([0x12, 0x34, 7, 2, 3, 4], E5Archive.OpenFresh(hex).ReadDecoded(1), "地图地形格或原前缀错误");
            Equal([8, 9, 10], E5Archive.OpenFresh(hex).ReadDecoded(2), "地图修改其他地形条目");
        });
        var pixel = new MutationPlan(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit, "地图底图像素",
            [new(map, ImageBytes(96, 96, Color.Lime, ImageFormat.Jpeg), "仅改地图底图")], [], false);
        RoundTrip(project, pixel, "地图对/底图像素", () =>
        {
            using var image = new Bitmap(map); Require(image.GetPixel(5, 5).G > 200, "地图像素没有落盘。");
            Equal(originalHex, File.ReadAllBytes(hex), "地图底图像素误改地形层");
        });
    }

    private static void RoundTrip(ProjectProfile project, MutationPlan plan, string label, Action verify)
    {
        Require(plan.Files.Count > 0, label + "没有写入计划。");
        var scope = Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar;
        foreach (var file in plan.Files)
            Require(Path.GetFullPath(file.TargetPath).StartsWith(scope, StringComparison.OrdinalIgnoreCase), "矩阵目标超出隔离目录：" + file.TargetPath);
        var before = plan.Files.ToDictionary(file => file.TargetPath, file => File.Exists(file.TargetPath) ? File.ReadAllBytes(file.TargetPath) : null,
            StringComparer.OrdinalIgnoreCase);
        Require(plan.Files.Any(file => file.DeleteTarget ? before[file.TargetPath] != null
            : before[file.TargetPath] is not { } old || !old.AsSpan().SequenceEqual(file.CandidateBytes)), label + "只有无操作候选，不能视为写回验收。");
        var history = new HistoryService(Path.Combine(project.RootPath, "矩阵历史", (++_transactions).ToString("D3")));
        var result = new ImageMutationService(history).Commit(project, plan);
        Require(result.Success && result.WrittenPaths.Count == plan.Files.Count && result.HistoryId != null,
            label + "提交失败：" + string.Join("；", result.Diagnostics));
        CheckAfter(); verify();
        var undo = history.Undo(project, result.HistoryId!); Require(undo.Success, label + "撤回失败：" + string.Join("；", undo.Diagnostics)); CheckBefore();
        var redo = history.Redo(project, result.HistoryId!); Require(redo.Success, label + "前进失败：" + string.Join("；", redo.Diagnostics)); CheckAfter(); verify();
        var restore = history.Undo(project, result.HistoryId!); Require(restore.Success, label + "最终复原失败。"); CheckBefore();
        Console.WriteLine("[RESOURCE_WRITEBACK_MATRIX_PASS] " + label);

        void CheckAfter()
        {
            foreach (var file in plan.Files)
                if (file.DeleteTarget) Require(!File.Exists(file.TargetPath), label + "文件未删除。");
                else Equal(file.CandidateBytes, File.ReadAllBytes(file.TargetPath), label + "磁盘完整字节不符");
        }
        void CheckBefore()
        {
            foreach (var item in before)
                if (item.Value == null) Require(!File.Exists(item.Key), label + "没有恢复原不存在状态。");
                else Equal(item.Value, File.ReadAllBytes(item.Key), label + "未逐字节恢复原文件");
        }
    }

    private static void AssertDecoded(ProjectProfile project, LogicalImageAsset asset, int count)
        => AssertFrames(new ImageCatalogService().DecodeFrames(project, asset), count);
    private static LogicalImageAsset AppendedRsAsset(LogicalImageAsset asset, IReadOnlyDictionary<string, int> oldCounts)
        => asset with { Frames = asset.Frames
            .Select(frame => frame with { ImageNumber = oldCounts[frame.SourcePath] + (asset.Kind == ResourceKind.RComposite ? frame.ImageNumber : 1), Turn = 1 }).ToArray(),
            SMapping = null };
    private static void AssertFrames(IReadOnlyList<DecodedFrame> frames, int count)
    {
        try { Require(frames.Count == count && frames.All(frame => frame.Bitmap.Width > 0 && frame.Bitmap.Height > 0), "磁盘资源帧重新解码失败。"); }
        finally { Dispose(frames); }
    }
    private static void Dispose(IEnumerable<DecodedFrame> frames) { foreach (var frame in frames) frame.Dispose(); }
    private static ImageMutationService Planner(string root) => new(new HistoryService(Path.Combine(root, "候选历史")));
    private static ImageResourceDescriptor Resource(string key, ImageCategory category, ResourceKind kind, string[] paths)
        => new(key, key, category, kind, paths, 0, true, true, false, "隔离验收", "");
    private static ProjectProfile Project(string root, params (string Key, string Path)[] files)
        => new(root, "矩阵fixture", ProjectVersion.Ccz66, false, files.ToDictionary(file => file.Key, file => file.Path, StringComparer.OrdinalIgnoreCase), []);
    private static string Folder(string name) { var path = Path.Combine(_temporaryRoot, name); Directory.CreateDirectory(path); return path; }
    private static string CopySample(string sample, string relative, string destination)
    {
        var source = Path.Combine(_sampleRoot, sample, relative);
        if (!File.Exists(source)) throw new FileNotFoundException("资源写回矩阵缺少必需样本，不能静默跳过。", source);
        SourceHashes.TryAdd(source, HistoryService.HashFile(source));
        var target = Path.Combine(destination, Path.GetFileName(relative)); File.Copy(source, target);
        File.SetAttributes(target, FileAttributes.Normal); return target;
    }
    private static string FindSamples(string start)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(start)); directory != null; directory = directory.Parent)
        {
            if (directory.Name == "基底") return directory.FullName;
            var candidate = Path.Combine(directory.FullName, "基底"); if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("无法定位资源写回矩阵的基底目录：" + start);
    }
    private static string Palette(string root)
    {
        var bytes = new byte[768];
        for (var i = 0; i < 256; i++) bytes[i * 3] = bytes[i * 3 + 1] = bytes[i * 3 + 2] = (byte)i;
        bytes[0] = 255; bytes[1] = 255; bytes[2] = 0;
        var path = Path.Combine(root, "Spalet.e5"); File.WriteAllBytes(path, Archive([bytes])); return path;
    }
    private static byte[] Strip(int width, int height, int count, int value)
    {
        var bytes = Enumerable.Repeat((byte)value, width * height * count + 2).ToArray(); bytes[^2] = 0x12; bytes[^1] = 0x34; return bytes;
    }
    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var i = 0; i < payloads.Count; i++)
        {
            var at = E5Archive.IndexOffset + i * E5Archive.EntrySize;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at, 4), (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 4, 4), (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 8, 4), (uint)offset);
            payloads[i].CopyTo(bytes, offset); offset += payloads[i].Length;
        }
        return bytes;
    }
    private static Bitmap Solid(int width, int height, Color color)
    {
        var image = new Bitmap(width, height); using var graphics = Graphics.FromImage(image); graphics.Clear(color); return image;
    }
    private static byte[] ImageBytes(int width, int height, Color color, ImageFormat? format = null)
    {
        using var image = Solid(width, height, color); using var stream = new MemoryStream(); image.Save(stream, format ?? ImageFormat.Png); return stream.ToArray();
    }
    private static void Equal(byte[] expected, byte[] actual, string message) => Require(expected.AsSpan().SequenceEqual(actual), message);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
