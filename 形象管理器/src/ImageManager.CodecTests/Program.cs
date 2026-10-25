using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

var checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
void Same(byte[] expected, byte[] actual, string message) => Check(expected.AsSpan().SequenceEqual(actual), message);
ProjectProfile Project(string root) => new(root, "专项测试", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
var temporary = Path.Combine(Path.GetTempPath(), "CczCodecTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    if (args.Contains("--ccz66-palette-only", StringComparer.Ordinal))
    {
        var repositoryRoot = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            ?? Directory.GetCurrentDirectory();
        checks += Ccz66PaletteChecks.Run(temporary, repositoryRoot,
            args.Contains("--ccz66-palette-evidence", StringComparer.Ordinal));
        Console.WriteLine($"全部通过：{checks} 个断言。");
        return 0;
    }
    if (args.Contains("--raw-icon-investigation", StringComparer.Ordinal))
    {
        var sampleRoot = Path.Combine(Directory.GetCurrentDirectory(), "基底", "厚黑6.6测（7.9水杯版）");
        var archive = E5Archive.Open(Path.Combine(sampleRoot, "E5", "Item.e5"));
        Console.WriteLine("Item entries=" + archive.Entries.Count + "; lengths=" + string.Join(',', archive.Entries.GroupBy(entry => entry.DecodedLength).Select(group => $"{group.Key}:{group.Count()}")));
        foreach (var entry in archive.Entries.Take(10))
        {
            var bytes = archive.ReadDecoded(entry.ImageNumber);
            Console.WriteLine($"Item #{entry.ImageNumber} length={bytes.Length}, standard={ImageRendering.IsStandardImage(bytes)}, head={Convert.ToHexString(bytes[..Math.Min(12,bytes.Length)])}");
        }
        var profile = new ProjectProfileService().Detect(sampleRoot);
        var catalog = new ImageCatalogService(); var resource = catalog.BuildCatalog(profile).Single(item => item.Key == "Itemicon.dll");
        var dll = catalog.ReadAssets(profile,resource);
        var dllFrames = dll.SelectMany(item=>catalog.DecodeFrames(profile,item)).ToArray();
        try { Console.WriteLine("DLL icons=" + dll.Count + "; dimensions=" + string.Join(',', dllFrames.GroupBy(entry => entry.Bitmap.Size).Select(group => $"{group.Key}:{group.Count()}"))); }
        finally { foreach(var frame in dllFrames) frame.Dispose(); }
        Console.WriteLine("Profile=" + profile.Version + "; " + string.Join(';',profile.Diagnostics));
        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(sampleRoot,"Ekd5.exe"));
        Console.WriteLine("EXE FileVersion=" + versionInfo.FileVersion + "; FilePrivatePart=" + versionInfo.FilePrivatePart);
        Check(profile.Version == ProjectVersion.Ccz66, "混合资源项目由 PE 固定版本资源识别为6.6");
        Check(profile.TryGetResource("Item.e5") == null && resource.Key == "Itemicon.dll", "未验证 RAW Item 不覆盖可读 DLL 图标");
        Check(dll.Count == 171, "混合版171对DLL宝物图标可用");
        foreach (var name in new[] { "复刻版6.6山贼", "复刻版6.6山贼 国庆", "新改曹操傳6.6修正版", "曹操传6.6-V7.16 解压密码：66", "烈焰同盟6.6x(新修版)黄线完结+红线5关" })
            Check(new ProjectProfileService().Detect(Path.Combine(Directory.GetCurrentDirectory(), "基底", name)).Version == ProjectVersion.Ccz66,
                "6.6 PE/资源独立探测：" + name);
        Console.WriteLine("混合版及六套6.6 Profile专项通过，断言=" + checks);
        return 0;
    }
    foreach (var bits in new[] { 4, 8, 24, 32 })
    foreach (var topDown in new[] { false, true })
    foreach (var core in new[] { false, true }.Where(core => !core || !topDown && bits != 32))
    {
        var original = Bmp(bits, topDown, core);
        using var image = BmpPayloadService.Decode(original);
        Check(image.Width == 3 && image.Height == 2, "BMP 尺寸");
        Same(original, BmpPayloadService.Rebuild(original, image), $"BMP{bits}/{topDown}/{core} 无修改应逐字节一致");
        if (bits <= 8)
        {
            Check(image.GetPixel(0, 0).R == 200, "BMP 自身色表不得受项目调色板影响");
            var palette = BmpPayloadService.TryGetEmbeddedPalette(original)!;
            Check(palette.All(color => color.A == 255), "BMP 保留字节不是透明度");
            var icons = new ItemIconDerivationService();
            Same(original, icons.EncodeStandardImageLike(original, image, Color.Magenta),
                "图标写入链路无修改仍保留BMP4/8原位深、重复索引、色表和尾部");
            image.SetPixel(0, 0, palette[2]);
            var rebuilt = BmpPayloadService.Rebuild(original, image);
            using var reread = BmpPayloadService.Decode(rebuilt);
            Check(reread.GetPixel(0, 0).ToArgb() == palette[2].ToArgb(), "BMP 单像素重建");
            Check(rebuilt.Zip(original).Count(pair => pair.First != pair.Second) == 1, "BMP 单像素只改一个索引字节（重复颜色索引保留）");
            var iconBytes = icons.EncodeStandardImageLike(original, image, Color.Magenta);
            var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(10));
            Same(original[..pixelOffset], iconBytes[..pixelOffset], "图标编辑不改BMP头、位深和原调色板");
            Same(original[^3..], iconBytes[^3..], "图标编辑保留未知尾部");
            Check(iconBytes.Zip(original).Count(pair => pair.First != pair.Second) == 1,
                "图标索引像素编辑不将图片重编码为24bpp");
        }
    }
    Console.WriteLine("BMP4/8/24/32、CORE12、top-down、padding、重复palette、未知尾部通过。");

    var tsb = new byte[1024]; tsb[4] = 10; tsb[5] = 20; tsb[6] = 30;
    File.WriteAllBytes(Path.Combine(temporary, "tsb"), tsb);
    var paletteService = new PaletteService();
    Check(paletteService.RequireWritePalette(Project(temporary))[1].R == 30, "项目 tsb 优先于内置");
    var priorTime = File.GetLastWriteTimeUtc(Path.Combine(temporary, "tsb"));
    tsb[6] = 90; File.WriteAllBytes(Path.Combine(temporary, "tsb"), tsb); File.SetLastWriteTimeUtc(Path.Combine(temporary, "tsb"), priorTime);
    Check(paletteService.RequireWritePalette(Project(temporary))[1].R == 90, "相同长度/mtime外改仍由SHA使palette缓存失效");
    var missing = Project(Path.Combine(temporary, "missing"));
    Check(paletteService.Resolve(missing).Source == "内置 tsb" && paletteService.Resolve(missing).CanWrite, "无项目palette使用嵌入tsb而非灰度");

    checks += RepresentativeFrameChecks.Run(temporary);
    if (args.Contains("--representative-only", StringComparer.Ordinal)) return 0;

    checks += Ccz66PaletteChecks.Run(temporary,
        args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());

    var codec = new StrategyAnimationPackageCodec();
    var raw = AnimationHeader(2, 1, 2, 1);
    raw = raw.Concat(new byte[] { 1, 2, 2, 1, 3, 3 }).Concat(new byte[768]).Concat(new byte[] { 0xfa, 0xfb }).ToArray();
    using (var decoded = codec.Decode(Project(temporary), raw))
    {
        Check(decoded.ActualFrameCount == 3 && decoded.Frames.Count == 3 && decoded.TailBytes.Length == 770, "RAW 主/辅助平面与768色表尾分界");
        Check(decoded.PlaybackSequence.SequenceEqual(new[] { 1, 0, 1 }), "按记录引用播放，不把辅助平面顺序播放");
        Same(raw, codec.RebuildFramePlanes(Project(temporary), decoded, new Dictionary<int, Bitmap>()), "RAW无修改原样");
        using var edit = new Bitmap(decoded.PlaneFrames[0]); edit.SetPixel(0, 0, Color.FromArgb(90, 20, 10));
        var result = codec.RebuildFramePlanes(Project(temporary), decoded, new Dictionary<int, Bitmap> { [0] = edit });
        Same(raw[..80], result[..80], "RAW记录不变"); Same(raw[^770..], result[^770..], "RAW palette/tail不变");
    }
    foreach (var format in new[] { ImageFormat.Bmp, ImageFormat.Png })
    {
        using var strip = new Bitmap(2, 3, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(strip)) { graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Green, 0, 1, 2, 1); graphics.FillRectangle(Brushes.Blue, 0, 2, 2, 1); }
        using var stream = new MemoryStream(); strip.Save(stream, format);
        var bytes = AnimationHeader(2, 1, 2, 1).Concat(stream.ToArray()).Concat(new byte[] { 0xa1, 0xa2 }).ToArray();
        using var package = codec.Decode(Project(temporary), bytes);
        Check(package.ActualFrameCount == 3 && package.Frames[0].GetPixel(0, 0).G == 128 && package.Frames[1].GetPixel(0, 0).R == 255, "嵌入标准长条分帧及记录播放");
        Same(bytes, codec.RebuildFramePlanes(Project(temporary), package, new Dictionary<int, Bitmap>()), "标准载荷无修改逐字节原样");
        using var edited = new Bitmap(package.PlaneFrames[0]); edited.SetPixel(0, 0, Color.Yellow);
        var rebuilt = codec.RebuildFramePlanes(Project(temporary), package, new Dictionary<int, Bitmap> { [0] = edited });
        Same(bytes[..80], rebuilt[..80], "嵌入图片记录保留"); Same(bytes[^2..], rebuilt[^2..], "嵌入图片未知尾部保留");
        using var reread = codec.Decode(Project(temporary), rebuilt);
        Check(reread.PlaneFrames[0].GetPixel(0, 0).R == 255 && reread.PlaneFrames[0].GetPixel(0, 0).G == 255, "嵌入图片编辑后复读");
    }
    var fading = (byte[])raw.Clone();
    fading[23] = 8; BinaryPrimitives.WriteInt32LittleEndian(fading.AsSpan(24), 1);
    BinaryPrimitives.WriteInt32LittleEndian(fading.AsSpan(28), 3); BinaryPrimitives.WriteInt32LittleEndian(fading.AsSpan(32), -1);
    using (var package = codec.Decode(Project(temporary), fading))
    {
        Check(package.Frames.All(frame => frame.Size == new Size(5, 2)), "记录坐标合并公共画布，播放不跳动");
        Check(package.Frames[0].GetPixel(3, 0).A == 132, "消费者6*n+4权重，8对应52%透明混合");
        Check(package.Frames[1].GetPixel(0, 1).A == 255 && package.Frames[0].GetPixel(0, 1).A == 0, "位置与透明底正确");
        Check(package.PlaneFrames[1].Size == new Size(2, 1) && package.PlaneFrames[1].GetPixel(0, 0).A == 255, "原始平面不混入播放透明度/位移");
    }
    Console.WriteLine("RAW8/BMP/PNG动画、主/辅助平面、记录引用、附加色表/未知尾部与写后复读通过。");

    var root = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory();
    foreach (var name in new[] { "加强版6.5未加密版", Path.Combine("18州", "十八州演义0.2版本"), Path.Combine("曹操传6.6-V10.1 【解压密码：66】", "复刻版6.6") })
    {
        var sampleRoot = Path.Combine(root, "基底", name);
        if (!Directory.Exists(sampleRoot)) { Console.WriteLine("跳过不存在样本 " + sampleRoot); continue; }
        var project = new ProjectProfileService().Detect(sampleRoot);
        var catalog = new ImageCatalogService();
        var resources = catalog.BuildCatalog(project);
        var weather = resources.Single(resource => resource.Kind == ResourceKind.WeatherSpecial);
        var assets = catalog.ReadAssets(project, weather);
        Check(assets.Count == 5, "真实天气5条");
        foreach (var asset in assets)
        {
            var original = E5Archive.Open(weather.Paths.Single()).ReadDecoded(asset.Frames.Single().ImageNumber);
            var frames = catalog.DecodeFrames(project, asset);
            try
            {
                Check(frames.Count == 4 && frames.All(frame => frame.Bitmap.Size == new Size(216, 50)), "真实天气216×50四帧");
                Same(original, frames.SelectMany(frame => frame.PaletteIndexes!).ToArray(), "真实天气按纵向原始索引切割");
                using var strip = new Bitmap(216, 200);
                using (var graphics = Graphics.FromImage(strip))
                    foreach (var frame in frames) graphics.DrawImageUnscaled(frame.Bitmap, 0, frame.Descriptor.FrameIndex * 50);
                Same(original, WeatherResourceCodec.RebuildPayload(project, original, strip), "真实天气导出导入原样");
                using var edited = new Bitmap(frames[1].Bitmap); edited.SetPixel(0, 0, Color.Yellow);
                var changed = WeatherResourceCodec.ReplaceFrame(project, original, 1, edited);
                Same(original[..10800], changed[..10800], "天气帧0不变"); Same(original[21600..], changed[21600..], "天气帧2/3不变");
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        var weatherTestRoot = Path.Combine(temporary, "WeatherCommit", name);
        Directory.CreateDirectory(weatherTestRoot);
        var weatherTestPath = Path.Combine(weatherTestRoot, "Weather.e5");
        byte[] sourceWeather;
        using (var input = new FileStream(weather.Paths.Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream()) { input.CopyTo(memory); sourceWeather = memory.ToArray(); }
        File.WriteAllBytes(weatherTestPath, sourceWeather);
        var testResources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Weather.e5"] = weatherTestPath };
        if (project.TryGetResource("Spalet.e5") is { } spalet) testResources["Spalet.e5"] = spalet;
        var weatherTestProject = new ProjectProfile(weatherTestRoot, "天气副本事务", project.Version, false, testResources, []);
        var weatherTestCatalog = new ImageCatalogService();
        var weatherTestResource = weatherTestCatalog.BuildCatalog(weatherTestProject).Single(resource => resource.Kind == ResourceKind.WeatherSpecial);
        Check(weatherTestResource.CanMutate, "真实天气正确几何通过Catalog写入门禁");
        var weatherTestAsset = weatherTestCatalog.ReadAssets(weatherTestProject, weatherTestResource)[0];
        var testFrames = weatherTestCatalog.DecodeFrames(weatherTestProject, weatherTestAsset);
        try
        {
            var history = new HistoryService(Path.Combine(weatherTestRoot, "backup"));
            var mutations = new ImageMutationService(history);
            using var replacementStrip = new Bitmap(216, 200);
            using (var graphics = Graphics.FromImage(replacementStrip))
                foreach (var frame in testFrames) graphics.DrawImageUnscaled(frame.Bitmap, 0, frame.Descriptor.FrameIndex * 50);
            var testPalette = new PaletteService().RequireWritePalette(weatherTestProject);
            var changeColor = testPalette.First(color => color.ToArgb() != replacementStrip.GetPixel(0, 0).ToArgb());
            replacementStrip.SetPixel(0, 0, changeColor);
            var incoming = Path.Combine(weatherTestRoot, "天气条.png"); replacementStrip.Save(incoming, ImageFormat.Png);
            var plan = mutations.PlanReplace(weatherTestProject, [weatherTestAsset], [incoming]);
            var committed = mutations.Commit(weatherTestProject, plan);
            Check(committed.Success, "真实天气条替换Commit失败：" + string.Join(';', committed.Diagnostics));
            var after = File.ReadAllBytes(weatherTestPath);
            Check(!after.AsSpan().SequenceEqual(sourceWeather), "天气Commit应真正改变副本文件");
            Check(history.Undo(weatherTestProject, committed.HistoryId!).Success, "真实天气替换Undo");
            Same(sourceWeather, File.ReadAllBytes(weatherTestPath), "真实天气Undo恢复原档案SHA/字节");
            Check(history.Redo(weatherTestProject, committed.HistoryId!).Success, "真实天气替换Redo");
            Same(after, File.ReadAllBytes(weatherTestPath), "真实天气Redo恢复写后字节");
            var beforeSingle = E5Archive.Open(weatherTestPath).ReadDecoded(weatherTestAsset.Frames.Single().ImageNumber);
            using var lastFrame = new Bitmap(testFrames[3].Bitmap);
            lastFrame.SetPixel(0, 0, testPalette.First(color => color.ToArgb() != lastFrame.GetPixel(0, 0).ToArgb()));
            var singlePlan = mutations.PlanReplaceSingleFrame(weatherTestProject, weatherTestAsset, testFrames[3].Descriptor, lastFrame);
            var singleCommit = mutations.Commit(weatherTestProject, singlePlan);
            Check(singleCommit.Success, "天气单帧Commit：" + string.Join(';', singleCommit.Diagnostics));
            var afterSingle = E5Archive.Open(weatherTestPath).ReadDecoded(weatherTestAsset.Frames.Single().ImageNumber);
            Same(beforeSingle[..32400], afterSingle[..32400], "天气单帧Commit保留前三帧");
            Check(!beforeSingle.AsSpan(32400).SequenceEqual(afterSingle.AsSpan(32400)), "天气单帧Commit改变第4帧");
            Check(history.Undo(weatherTestProject, singleCommit.HistoryId!).Success, "天气单帧Undo");
            Same(after, File.ReadAllBytes(weatherTestPath), "天气单帧Undo恢复上一次完整条状态");
            Check(history.Redo(weatherTestProject, singleCommit.HistoryId!).Success, "天气单帧Redo");
            Same(afterSingle, E5Archive.Open(weatherTestPath).ReadDecoded(weatherTestAsset.Frames.Single().ImageNumber), "天气单帧Redo复读");
            using var sourceReread = new FileStream(weather.Paths.Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sourceMemory = new MemoryStream(); sourceReread.CopyTo(sourceMemory);
            Same(sourceWeather, sourceMemory.ToArray(), "天气事务测试不改变原始基底任何源字节");
            Console.WriteLine($"真实天气事务 {name}：完整条替换/单帧像素 Commit→Undo→Redo 全部通过，原基底字节不变。");
        }
        finally { foreach (var frame in testFrames) frame.Dispose(); }
        var animationCount = 0;
        foreach (var resource in resources.Where(resource => resource.Kind == ResourceKind.McallAnimation || resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase) || resource.Kind == ResourceKind.DtAnimation))
        foreach (var asset in catalog.ReadAssets(project, resource))
        {
            var descriptor = asset.Frames.Single();
            if (asset.Status.Contains("保留的 Meff 尾部条目不是可播放动画", StringComparison.Ordinal))
            {
                Check(resource.Key.Equals("Meff.e5", StringComparison.OrdinalIgnoreCase) && descriptor.ImageNumber > 60,
                    "只有已诊断的旧版Meff保留尾部条目允许跳过播放矩阵");
                continue;
            }
            var original = StrategyAnimationResourceCodec.ReadPackageBytes(descriptor);
            using var package = codec.Decode(project, original);
            if (args.Contains("--diagnostics", StringComparer.Ordinal) && (package.UsesSecondaryLayer || package.SecondaryPlaneCount > 0 || package.TailBytes.Length >= 768))
            {
                var recordModes = Enumerable.Range(1, package.RecordCount - 1)
                    .Select(i => $"{package.RecordBytes[i*20+1]:X2}/{package.RecordBytes[i*20+2]:X2}/{package.RecordBytes[i*20+3]:X2}/{BinaryPrimitives.ReadInt32LittleEndian(package.RecordBytes.AsSpan(i*20+4,4))}")
                    .Distinct().ToArray();
                Console.WriteLine($"DETAIL {name} {Path.GetFileName(descriptor.SourcePath)} #{descriptor.ImageNumber}: {package.Diagnostics}; {string.Join(' ', recordModes)}");
            }
            Check(package.Frames.Count == package.RecordCount - 1 && package.PlaneFrames.Count == package.ActualFrameCount, "真实动画帧数");
            foreach (var index in new[] { 0, package.Frames.Count - 1 }.Distinct())
            {
                using var preview = codec.DecodePreviewFrame(project, original, index);
                Check(preview.Size == package.Frames[index].Size && BmpPayloadService.ReadPixels(preview)
                    .AsSpan().SequenceEqual(BmpPayloadService.ReadPixels(package.Frames[index])),
                    $"真实动画代表帧必须与全量播放一致：{name}/{asset.StablePhysicalId}/{index}");
            }
            Same(original, codec.RebuildFramePlanes(project, package, new Dictionary<int, Bitmap>()), "真实动画原样往返");
            if (package.PayloadFormat == "BMP")
            {
                Same(original, codec.RebuildFramePlanes(project, package, new Dictionary<int, Bitmap> { [0] = package.PlaneFrames[0] }), "真实嵌入BMP原帧重写保持header/stride/tail");
                using var edit = new Bitmap(package.PlaneFrames[0]); edit.SetPixel(0, 0, Color.Yellow);
                var rewritten = codec.RebuildFramePlanes(project, package, new Dictionary<int, Bitmap> { [0] = edit });
                using var reread = codec.Decode(project, rewritten);
                Check(reread.PlaneFrames[0].GetPixel(0, 0).R == 255 && reread.PlaneFrames[0].GetPixel(0, 0).G == 255, "真实BMP编辑后目标像素复读");
                Same(package.RecordBytes, rewritten[..package.RecordBytes.Length], "真实BMP编辑保留记录");
            }
            animationCount++;
        }
        Console.WriteLine($"真实样本 {name}：Weather全部5条，动画全部{animationCount}条解码/逐字节无修改重建通过。");
    }
    Console.WriteLine($"全部通过：{checks} 个断言。");
    return 0;
}
finally
{
    // Only the explicitly created isolated test directory is removed.
    Directory.Delete(temporary, true);
}

static byte[] AnimationHeader(int width, int height, byte primary, byte secondary)
{
    var bytes = new byte[80]; bytes[0] = 3; bytes[1] = primary; bytes[2] = secondary;
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 80);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), height);
    bytes[20] = 1; bytes[40] = 0; bytes[60] = 1; bytes[21] = bytes[41] = bytes[61] = 255;
    return bytes;
}

static byte[] Bmp(int bits, bool topDown, bool core)
{
    const int width = 3, height = 2;
    var dib = core ? 12 : 40; var count = bits <= 8 ? 1 << bits : 0;
    var paletteEntrySize = core ? 3 : 4; var offset = 14 + dib + count * paletteEntrySize;
    var stride = (width * bits + 31) / 32 * 4;
    var bytes = Enumerable.Repeat((byte)0xab, offset + stride * height + 3).ToArray();
    Array.Clear(bytes, 0, offset); bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length - 3); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), offset);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), dib);
    if (core)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), width); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), (ushort)bits);
    }
    else
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), stride * height);
    }
    if (count > 0)
    {
        for (var i = 0; i < count; i++) { var at = 14 + dib + i * paletteEntrySize; bytes[at] = (byte)i; bytes[at + 1] = 30; bytes[at + 2] = (byte)(i == 3 ? 200 : 100); }
        bytes[14 + dib + paletteEntrySize + 2] = 200; bytes[14 + dib + 3 * paletteEntrySize] = 1; // duplicate colors 1 and 3
        for (var y = 0; y < height; y++)
        {
            var row = offset + (topDown ? y : height - 1 - y) * stride;
            if (bits == 8) { bytes[row] = 3; bytes[row + 1] = 1; bytes[row + 2] = 2; }
            else { bytes[row] = 0x31; bytes[row + 1] = 0x2f; }
        }
    }
    return bytes;
}
