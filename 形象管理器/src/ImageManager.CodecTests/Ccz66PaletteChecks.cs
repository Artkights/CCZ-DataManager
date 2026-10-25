using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using ImageManager.Core;

internal static class Ccz66PaletteChecks
{
    public static int Run(string testRoot, string repositoryRoot, bool writeEvidence = false)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        var codec = new StrategyAnimationPackageCodec();
        var syntheticRoot = Path.Combine(testRoot, "Ccz66Palette");
        Directory.CreateDirectory(syntheticRoot);
        var tsb = new byte[1024];
        tsb[0] = 255; tsb[2] = 255;
        tsb[4] = 30; tsb[5] = 110; tsb[6] = 210;
        tsb[8] = 220; tsb[9] = 160; tsb[10] = 20;
        File.WriteAllBytes(Path.Combine(syntheticRoot, "tsb"), tsb);
        var project66 = new ProjectProfile(syntheticRoot, "6.6色表专项", ProjectVersion.Ccz66, false,
            new Dictionary<string, string>(), []);
        var raw = SyntheticPackage();
        using (var full = codec.Decode(project66, raw))
        {
            Check(full.PlaneFrames[0].GetPixel(0, 0).ToArgb() == Color.FromArgb(210, 110, 30).ToArgb(),
                "6.6 RAW原生平面错误套用遗留低位尾色表");
            Check(full.Frames[0].GetPixel(0, 0).A == 255,
                "6.6记录只有selector/尾色表时不应启用透明混合");
            Check(full.Frames[1].GetPixel(0, 0).A == 132 && full.Frames[2].GetPixel(0, 0).A == 86,
                "6.6辅助启用字段或辅助引用仍应保留52%/34%透明混合");
            Check(full.Frames.All(frame => frame.GetPixel(1, 0).A == 0), "6.6 RAW索引0应透明");
            for (var index = 0; index < full.Frames.Count; index++)
            {
                using var preview = codec.DecodePreviewFrame(project66, raw, index);
                Check(Same(preview, full.Frames[index]), "6.6低位尾色表单帧预览与完整播放不一致");
            }
            using var edited = new Bitmap(full.PlaneFrames[0]);
            edited.SetPixel(0, 0, Color.FromArgb(20, 160, 220));
            var rewritten = codec.RebuildFramePlanes(project66, full, new Dictionary<int, Bitmap> { [0] = edited });
            Check(rewritten[80] == 2 && rewritten.Zip(raw).Count(pair => pair.First != pair.Second) == 1,
                "6.6 RAW写回未按项目色表只更改一个索引");
            Check(rewritten.AsSpan(0, 80).SequenceEqual(raw.AsSpan(0, 80)) &&
                  rewritten.AsSpan(82).SequenceEqual(raw.AsSpan(82)), "6.6 RAW写回改变记录或遗留尾色表");
        }
        foreach (var version in new[] { ProjectVersion.Ccz64, ProjectVersion.Ccz65 })
        {
            using var legacy = codec.Decode(project66 with { Version = version }, raw);
            Check(legacy.PlaneFrames[0].GetPixel(0, 0).ToArgb() == Color.FromArgb(14, 6, 2).ToArgb() &&
                  legacy.Frames[0].GetPixel(0, 0).A == 56,
                version + "既有附加色表或selector混合兼容规则改变");
        }
        var multiple = raw.Concat(raw.AsSpan(82, 768).ToArray()).ToArray();
        multiple[41] = 1;
        using (var full = codec.Decode(project66, multiple))
        using (var edited = new Bitmap(full.PlaneFrames[0]))
        {
            edited.SetPixel(0, 0, Color.FromArgb(20, 160, 220));
            var rewritten = codec.RebuildFramePlanes(project66, full, new Dictionary<int, Bitmap> { [0] = edited });
            Check(rewritten[80] == 2, "6.6遗留多个selector不应导致RAW项目色表写回被误拒绝");
        }
        using (var legacy = codec.Decode(project66 with { Version = ProjectVersion.Ccz65 }, multiple))
        {
            try
            {
                _ = codec.RebuildFramePlanes(project66 with { Version = ProjectVersion.Ccz65 }, legacy,
                    new Dictionary<int, Bitmap> { [0] = legacy.PlaneFrames[0] });
                throw new InvalidOperationException("6.5多色表写回门禁被放开");
            }
            catch (InvalidDataException) { checks++; }
        }

        var goldenPath = Path.Combine(repositoryRoot, "基底", "曹操传6.6-V7.16 解压密码：66", "E5", "Mcall00.e5");
        var sampleRoots = new[] { "复刻版6.6山贼", "复刻版6.6山贼 国庆" }
            .Select(name => Path.Combine(repositoryRoot, "基底", name)).ToArray();
        if (!File.Exists(goldenPath) || sampleRoots.Any(root => !File.Exists(Path.Combine(root, "E5", "Mcall00.e5"))))
        {
            Console.WriteLine($"6.6色表合成专项通过，真实山贼/BMP黄金样本不存在，跳过真实矩阵；断言={checks}。");
            return checks;
        }
        var evidenceRoot = Path.Combine(repositoryRoot, "验证", "图片导入与调色板修正-20261023");
        if (writeEvidence) Directory.CreateDirectory(evidenceRoot);
        var sourcePaths = sampleRoots.SelectMany(root => new[] { Path.Combine(root, "Ekd5.exe"),
                Path.Combine(root, "E5", "Mcall00.e5"), Path.Combine(root, "E5", "Spalet.e5") })
            .Append(goldenPath).ToArray();
        var beforeHashes = sourcePaths.ToDictionary(path => path, FileHash);
        var goldenBytes = File.ReadAllBytes(goldenPath);
        Check(FileHash(goldenPath) == "FD7FE1364D991F5B9D5423EF109D056F0C7DD724ACB67DCD6350A85E8D663662",
            "6.6真彩黄金样本内容改变，请重新核验参照来源");
        var goldenProject = new ProjectProfileService().Detect(Path.GetDirectoryName(Path.GetDirectoryName(goldenPath))!);
        using var golden = codec.Decode(goldenProject, goldenBytes);
        using var goldenStrip = ImageRendering.DecodeStandardImage(golden.EncodedImageBytes);
        var goldenPixels = BmpPayloadService.ReadPixels(goldenStrip);
        var evidence = new List<object>();
        foreach (var sampleRoot in sampleRoots)
        {
            var project = new ProjectProfileService().Detect(sampleRoot);
            Check(project.Version == ProjectVersion.Ccz66, "山贼项目应由引擎证据识别为6.6");
            var path = Path.Combine(sampleRoot, "E5", "Mcall00.e5");
            var bytes = File.ReadAllBytes(path);
            Check(FileHash(path) == "D124ACA9A1ADFD0F89C7FE96F91F6853645B2BE5E14EBE4108BC189FD1DB88D4",
                "真实RAW山贼样本内容改变，请重新核验黄金对应关系");
            using var package = codec.Decode(project, bytes);
            Check(package.PlaneFrames.Count == 50 && package.Frames.Count == 25 && package.TailBytes.Length == 768,
                "山贼Mcall00主/辅助平面、播放步或尾色表分界错误");
            var palette = new PaletteService().Load(project);
            var goldenMismatch = 0;
            var decodedMismatch = 0;
            var transparentPixels = 0;
            var planeSize = package.Width * package.Height;
            for (var plane = 0; plane < package.PlaneFrames.Count; plane++)
            {
                var actual = BmpPayloadService.ReadPixels(package.PlaneFrames[plane]);
                for (var pixel = 0; pixel < planeSize; pixel++)
                {
                    var index = package.PlaneBytes[plane * planeSize + pixel];
                    var color = palette[index];
                    var expectedAt = (plane * planeSize + pixel) * 4;
                    var actualAt = pixel * 4;
                    if (goldenPixels[expectedAt] != color.B || goldenPixels[expectedAt + 1] != color.G ||
                        goldenPixels[expectedAt + 2] != color.R) goldenMismatch++;
                    var transparent = index == 0 || color.B == 255 && color.G == 0 && color.R is 247 or 255;
                    if (transparent)
                    {
                        transparentPixels++;
                        if (actual[actualAt + 3] != 0) decodedMismatch++;
                    }
                    else if (actual[actualAt] != goldenPixels[expectedAt] ||
                             actual[actualAt + 1] != goldenPixels[expectedAt + 1] ||
                             actual[actualAt + 2] != goldenPixels[expectedAt + 2] || actual[actualAt + 3] != 255)
                        decodedMismatch++;
                }
            }
            Check(package.PlaneBytes.Length == 2_880_000 && goldenMismatch == 0,
                $"山贼RAW索引按项目色表展开与真彩BMP黄金参照不一致：{goldenMismatch}像素");
            Check(decodedMismatch == 0, $"山贼实际主/辅助平面颜色或透明度与黄金参照不一致：{decodedMismatch}像素");
            for (var index = 0; index < package.Frames.Count; index++)
            {
                Check(Same(package.Frames[index], golden.Frames[index]), $"山贼播放第{index}帧与真彩黄金参照不一致");
                using var preview = codec.DecodePreviewFrame(project, bytes, index);
                Check(Same(preview, golden.Frames[index]), $"山贼单帧预览第{index}帧与真彩黄金参照不一致");
            }
            var peakFrame = Enumerable.Range(0, package.Frames.Count)
                .First(index => package.RecordBytes[(index + 1) * 20 + 3] == 16);
            var maxAlphas = new[] { 0, peakFrame, package.Frames.Count - 1 }.Select(index => MaxAlpha(package.Frames[index])).ToArray();
            Check(maxAlphas.SequenceEqual(new byte[] { 56, 255, 25 }), "山贼首/峰/末帧22%/100%/10%透明度错误");
            var editedPixel = Array.IndexOf(package.PlaneBytes, (byte)175, 0, planeSize);
            Check(editedPixel >= 0, "山贼测试修改索引175像素不存在");
            using var edited = new Bitmap(package.PlaneFrames[0]);
            edited.SetPixel(editedPixel % package.Width, editedPixel / package.Width, palette[47]);
            var rewritten = codec.RebuildFramePlanes(project, package, new Dictionary<int, Bitmap> { [0] = edited });
            var changedAt = package.RecordBytes.Length + editedPixel;
            Check(rewritten[changedAt] == 47 && rewritten.AsSpan(0, changedAt).SequenceEqual(bytes.AsSpan(0, changedAt)) &&
                  rewritten.AsSpan(changedAt + 1).SequenceEqual(bytes.AsSpan(changedAt + 1)),
                "山贼单像素写回未使用项目色表，或改变了其他平面/记录/尾部");
            using var reread = codec.Decode(project, rewritten);
            Check(reread.PlaneFrames[0].GetPixel(editedPixel % package.Width, editedPixel / package.Width).ToArgb() == palette[47].ToArgb(),
                "山贼单像素写后复读颜色错误");
            Check(codec.RebuildFramePlanes(project, package, new Dictionary<int, Bitmap>()).AsSpan().SequenceEqual(bytes),
                "山贼无修改动画重建不再逐字节一致");
            evidence.Add(new
            {
                sample = Path.GetFileName(sampleRoot), sourceSha256 = beforeHashes[path], sourceBytes = bytes.Length,
                recordBytes = package.RecordBytes.Length, primaryPlanes = package.DeclaredFrameCount,
                secondaryPlanes = package.SecondaryPlaneCount, package.Width, package.Height,
                tailOffset = package.RecordBytes.Length + package.PlaneBytes.Length,
                tailBytes = package.TailBytes.Length, tailChannelMinimum = package.TailBytes.Min(),
                tailChannelMaximum = package.TailBytes.Max(), projectPalette = new PaletteService().Resolve(project).Source,
                goldenSha256 = beforeHashes[goldenPath], comparedPixels = package.PlaneBytes.Length,
                goldenMismatch, decodedMismatch, transparentPixels, firstPeakLastAlpha = maxAlphas.Select(value => (int)value).ToArray(),
                singlePixelChangedAt = changedAt, singlePixelWrittenIndex = rewritten[changedAt], changedByteCount = 1
            });
            if (writeEvidence && sampleRoot == sampleRoots[0])
                WriteVisualEvidence(evidenceRoot, codec, project, bytes, package, peakFrame);
            Console.WriteLine($"6.6真实色表 {Path.GetFileName(sampleRoot)}：2,880,000像素与BMP黄金参照零差异，50原生平面、25播放步/单帧、56/255/25 alpha、单像素项目色表写回及记录/尾部保护通过。");
        }
        foreach (var path in sourcePaths)
            Check(FileHash(path) == beforeHashes[path], "只读色表专项改变原始基底：" + path);
        if (writeEvidence)
            File.WriteAllText(Path.Combine(evidenceRoot, "ccz66-palette-evidence.json"),
                JsonSerializer.Serialize(new { samples = evidence, sourceHashesUnchanged = true, assertions = checks },
                    new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"6.6调色板专项全部通过；断言={checks}；原始基底SHA保持不变。");
        return checks;
    }

    private static byte[] SyntheticPackage()
    {
        var bytes = new byte[80 + 2 + 768];
        bytes[0] = 3; bytes[1] = 1; bytes[3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1);
        bytes[23] = 3;
        bytes[43] = 8; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), 1);
        bytes[62] = 1; bytes[63] = 5;
        bytes[80] = 1;
        bytes[85] = 2; bytes[86] = 14; bytes[87] = 6;
        return bytes;
    }

    private static byte MaxAlpha(Bitmap bitmap)
    {
        var pixels = BmpPayloadService.ReadPixels(bitmap);
        byte maximum = 0;
        for (var at = 3; at < pixels.Length; at += 4) maximum = Math.Max(maximum, pixels[at]);
        return maximum;
    }

    private static bool Same(Bitmap left, Bitmap right)
        => left.Size == right.Size && BmpPayloadService.ReadPixels(left).AsSpan().SequenceEqual(BmpPayloadService.ReadPixels(right));

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteVisualEvidence(string root, StrategyAnimationPackageCodec codec, ProjectProfile project,
        byte[] bytes, StrategyAnimationData package, int peakFrame)
    {
        var frameIndexes = new[] { 0, peakFrame, package.Frames.Count - 1 };
        using var sheet = new Bitmap(1100, 714);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(239, 242, 246));
        using var title = new Font("Microsoft YaHei UI", 18, FontStyle.Bold);
        using var label = new Font("Microsoft YaHei UI", 12);
        using var text = new SolidBrush(Color.FromArgb(35, 45, 58));
        graphics.DrawString("山贼 6.6 · Mcall00 调色板修正", title, text, 24, 15);
        graphics.DrawString("同一原始文件；保留 22% → 100% → 10% 透明度渐变", label, text, 24, 52);
        graphics.DrawString("修正前\n旧解析", label, text, 20, 175);
        graphics.DrawString("修正后\n项目色表", label, text, 20, 484);
        for (var column = 0; column < frameIndexes.Length; column++)
        {
            var frameIndex = frameIndexes[column];
            var left = 145 + column * 310;
            graphics.DrawString($"播放步 {frameIndex + 1} · {new[] { "22%", "100%", "10%" }[column]}", label, text, left + 20, 89);
            using var before = codec.DecodePreviewFrame(project with { Version = ProjectVersion.Ccz65 }, bytes, frameIndex);
            for (var row = 0; row < 2; row++)
            {
                var top = 120 + row * 297;
                var rectangle = new Rectangle(left, top, 276, 276);
                using var background = new SolidBrush(Color.FromArgb(250, 250, 250));
                graphics.FillRectangle(background, rectangle);
                using var check = new SolidBrush(Color.FromArgb(220, 225, 232));
                for (var y = 0; y < rectangle.Height; y += 12)
                for (var x = 0; x < rectangle.Width; x += 12)
                    if ((x / 12 + y / 12) % 2 == 0)
                        graphics.FillRectangle(check, rectangle.X + x, rectangle.Y + y, Math.Min(12, rectangle.Width - x), Math.Min(12, rectangle.Height - y));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(row == 0 ? before : package.Frames[frameIndex], rectangle);
            }
        }
        sheet.Save(Path.Combine(root, "mcall00-palette-before-after.png"), ImageFormat.Png);
        package.Frames[peakFrame].Save(Path.Combine(root, "mcall00-corrected-peak.png"), ImageFormat.Png);
    }
}
