using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

/// <summary>External file preparation exercises real encodings, physical targets, and native geometry.</summary>
public static class RsFileImportTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.RsFileImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Fixture(root);
            ReplacePhysicalS(fixture);
            ReplaceAndAppendR(fixture);
            AppendSAndRejectBrokenGroups(fixture);
            RejectBadGeometryAndMalformedFiles(fixture);
            RejectUnknownStandardTargetTails(fixture);
            CompatibilityDirectoryImports(fixture);
            PaletteChangesPreservingFileMetadata(fixture);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ReplacePhysicalS(TestFixture fixture)
    {
        var mov = Save(fixture.Root, "任意移动素材.bin", 48, 528, ImageFormat.Png,
            bitmap =>
            {
                bitmap.SetPixel(0, 0, Color.FromArgb(128, 110, 20, 30));
                bitmap.SetPixel(1, 0, Color.FromArgb(255, 247, 0, 255));
                bitmap.SetPixel(2, 0, Color.FromArgb(255, 248, 0, 255));
                bitmap.SetPixel(3, 0, Color.Transparent);
            });
        var atk = Save(fixture.Root, "attack 任意名字.jpg", 64, 768, ImageFormat.Jpeg);
        var spc = Save(fixture.Root, "special.bmp", 48, 240, ImageFormat.Png,
            bitmap =>
            {
                bitmap.SetPixel(1, 0, Color.FromArgb(255, 247, 0, 255));
                bitmap.SetPixel(2, 0, Color.FromArgb(128, 240, 40, 80));
            });
        using var preview = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace,
            new(fixture.S, "mov", mov), new(fixture.S, "atk", atk), new(fixture.S, "spc", spc));
        Require(preview.CanCommit, Errors(preview));
        Require(preview.Plan!.Files.Count == 3, "S 三动作替换没有组合为三档候选。");
        var movement = Candidate(preview, "Unit_mov.e5");
        var raw = movement.ReadDecoded(242);
        Require(raw.Length == 48 * 528 + 2 && raw[^2] == 0x5a && raw[^1] == 0xa5, "RAW8 两字节尾部丢失。");
        Require(raw[0] == 1 && raw[1] == 4 && raw[2] == 0 && raw[3] == 0,
            "RAW8 量化或透明阈值不符合整合包规则：" + string.Join(",", raw.Take(4)));
        Require(movement.ReadDecoded(241).SequenceEqual(E5Archive.OpenFresh(fixture.Paths[0]).ReadDecoded(241)) &&
                movement.ReadDecoded(243).SequenceEqual(E5Archive.OpenFresh(fixture.Paths[0]).ReadDecoded(243)),
            "S_242 替换展开到了相邻转数。");
        var movRow = preview.Rows.Single(row => row.Role == "mov");
        Require(movRow.After!.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 100, 0, 0).ToArgb(),
            "RAW8 预览展示的是量化前素材。");
        Require(movRow.After.GetPixel(1, 0).A == 255 && movRow.After.GetPixel(2, 0).A == 0,
            "RAW8 的 F700FF/阈值透明预览错误。");
        Require(movRow.Notes.Contains(Path.Combine(fixture.Root, "tsb"), StringComparison.Ordinal), "结果未记录实际调色板来源。");
        var attack = Candidate(preview, "Unit_atk.e5").ReadDecoded(242);
        Require(attack[0] == 'B' && attack[1] == 'M' && BinaryPrimitives.ReadUInt16LittleEndian(attack.AsSpan(28, 2)) == 32,
            "BMP 目标没有按整合包规则重新编码为 BMP32。");
        var special = Candidate(preview, "Unit_spc.e5").ReadDecoded(242);
        Require(IsPng(special) && preview.Rows.Single(row => row.Role == "spc").After!.GetPixel(1, 0).A == 0,
            "PNG 目标输出或真彩 F700FF 透明阈值错误。");
        Require(preview.Rows.Single(row => row.Role == "spc").After!.GetPixel(2, 0).ToArgb() == Color.FromArgb(128, 240, 40, 80).ToArgb(),
            "真彩素材半透明像素的RGB或alpha在转换中发生舍入。");
        Require(E5Archive.OpenFresh(fixture.Paths[0]).ReadDecoded(242)[0] == 7, "预览过程写入了实际资源。");

        using var partial = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace, new ImageImportAssignment(fixture.S, "mov", mov));
        Require(partial.CanCommit && partial.Plan!.Files.Count == 1, "仅提供移动素材却写入了其他动作档案。");
        var indexed = Path.Combine(fixture.Root, "一位图.bmp");
        using (var bitmap = new Bitmap(48, 528, PixelFormat.Format1bppIndexed)) bitmap.Save(indexed, ImageFormat.Bmp);
        using var indexedResult = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace, new ImageImportAssignment(fixture.S, "mov", indexed));
        Require(indexedResult.CanCommit, "有效 1bpp BMP 素材未能解码规范化：" + Errors(indexedResult));

        var ambiguous = fixture.S with { PhysicalImageNumber = 243 };
        using var badTarget = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace, new ImageImportAssignment(ambiguous, "mov", mov));
        Require(!badTarget.CanCommit && badTarget.Rows.Any(row => row.Error != null), "物理目标身份冲突未被拒绝。");
        using var duplicates = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace,
            new(fixture.S, "mov", mov), new(fixture.S, "mov", mov));
        Require(!duplicates.CanCommit && duplicates.Rows.All(row => row.Error != null), "重复目标素材未逐行报错。");
    }

    private static void ReplaceAndAppendR(TestFixture fixture)
    {
        var front = Save(fixture.Root, "正面任意文件.png", 48, 1280, ImageFormat.Jpeg);
        var back = Save(fixture.Root, "背面.png", 48, 1280, ImageFormat.Png);
        using var replaced = Prepare(fixture, fixture.RResource, ImageImportOperation.Replace, new ImageImportAssignment(fixture.R, "front", front));
        Require(replaced.CanCommit, Errors(replaced));
        var archive = Candidate(replaced, "Pmapobj.e5");
        Require(IsPng(archive.ReadDecoded(3)), "R RAW 替换没有转为 PNG32。");
        Require(archive.ReadDecoded(4).SequenceEqual(E5Archive.OpenFresh(fixture.RPath).ReadDecoded(4)),
            "只替换 R 正面却改写了背面。");
        Require(replaced.Rows.Single().OutputFormat == "PNG32", "R 输出格式说明错误。");
        using var appended = Prepare(fixture, fixture.RResource, ImageImportOperation.Append,
            new(null, "back", back, 9), new(null, "front", front, 9),
            new(null, "front", front, 2), new(null, "back", back, 2));
        Require(appended.CanCommit, Errors(appended));
        var appendedArchive = Candidate(appended, "Pmapobj.e5");
        Require(appendedArchive.Entries.Count == 8 && Enumerable.Range(5, 4).All(index => IsPng(appendedArchive.ReadDecoded(index))),
            "R 新增没有按组顺序成对追加 PNG32。");
        using var missing = Prepare(fixture, fixture.RResource, ImageImportOperation.Append, new ImageImportAssignment(null, "front", front));
        Require(!missing.CanCommit && missing.Rows.Any(row => row.Role == "back" && row.Error != null),
            "R 缺背面时未明确报错。");
        var oddPath = Path.Combine(fixture.Root, "odd.e5");
        File.WriteAllBytes(oddPath, Archive([new byte[48 * 1280]]));
        var oddResource = fixture.RResource with { Paths = [oddPath], EntryCount = 1 };
        using var odd = Prepare(fixture, oddResource, ImageImportOperation.Append,
            new(null, "front", front), new(null, "back", back));
        Require(!odd.CanCommit && odd.Rows.All(row => row.Error?.Contains("偶数", StringComparison.Ordinal) == true),
            "原 R 条目奇数未阻断追加。");
    }

    private static void AppendSAndRejectBrokenGroups(TestFixture fixture)
    {
        var mov = Save(fixture.Root, "新增移动.png", 48, 528, ImageFormat.Png);
        var atk = Save(fixture.Root, "新增攻击.png", 64, 768, ImageFormat.Png);
        var spc = Save(fixture.Root, "新增特技.png", 48, 240, ImageFormat.Png);
        using var appended = Prepare(fixture, fixture.SResource, ImageImportOperation.Append,
            new(null, "mov", mov), new(null, "atk", atk), new(null, "spc", spc));
        Require(appended.CanCommit && appended.Plan!.Files.Count == 3, Errors(appended));
        foreach (var file in appended.Plan!.Files)
        {
            var archive = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            Require(archive.Entries.Count == 244 && IsPng(archive.ReadDecoded(244)), "S 新增未同步追加一个 PNG32 物理条目。");
        }
        using var incomplete = Prepare(fixture, fixture.SResource, ImageImportOperation.Append,
            new(null, "mov", mov), new(null, "atk", atk));
        Require(!incomplete.CanCommit && incomplete.Rows.Any(row => row.Role == "spc" && row.Error != null),
            "S 新增缺动作时未阻断。");
        var badPath = Path.Combine(fixture.Root, "misaligned", "Unit_spc.e5");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        File.WriteAllBytes(badPath, Archive(Enumerable.Range(0, 242).Select(_ => new byte[48 * 240]).ToArray()));
        var misaligned = fixture.SResource with { Paths = [fixture.Paths[0], fixture.Paths[1], badPath] };
        using var bad = Prepare(fixture, misaligned, ImageImportOperation.Append,
            new(null, "mov", mov), new(null, "atk", atk), new(null, "spc", spc));
        Require(!bad.CanCommit && bad.Rows.All(row => row.Error?.Contains("一致", StringComparison.Ordinal) == true),
            "S 三包计数不齐未阻断新增。");
    }

    private static void RejectBadGeometryAndMalformedFiles(TestFixture fixture)
    {
        var badSize = Save(fixture.Root, "错误尺寸.bmp", 48, 527, ImageFormat.Bmp);
        var valid = Save(fixture.Root, "合法特技.png", 48, 240, ImageFormat.Png);
        using var mixed = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace,
            new(fixture.S, "mov", badSize), new(fixture.S, "spc", valid));
        Require(!mixed.CanCommit && mixed.Rows.Count == 2 && mixed.Rows.Single(row => row.Role == "mov").Error != null &&
            mixed.Rows.Single(row => row.Role == "spc").After != null, "错误未逐行保留，或合法预览被丢失。");
        var truncated = Path.Combine(fixture.Root, "截断.png");
        File.WriteAllBytes(truncated, [137, 80, 78, 71, 13, 10, 26, 10]);
        using var broken = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace, new ImageImportAssignment(fixture.S, "mov", truncated));
        Require(!broken.CanCommit && broken.Rows.Single().Error != null, "仅 PNG 文件头被当作完整图片接受。");
        var missingEnd = Path.Combine(fixture.Root, "缺IEND.png");
        File.WriteAllBytes(missingEnd, File.ReadAllBytes(valid)[..^12]);
        using var noEnd = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace, new ImageImportAssignment(fixture.S, "spc", missingEnd));
        Require(!noEnd.CanCommit && noEnd.Rows.Single().Error != null, "缺失 IEND 的 PNG 素材未阻断。");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            new ImageImportPreparationService().PrepareAsync(new(fixture.Project, fixture.SResource,
                ImageImportOperation.Replace, [new(fixture.S, "spc", valid)]), cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("取消请求未停止预览生成。");
        }
        catch (OperationCanceledException) { }
    }

    private static PreparedImageImport Prepare(TestFixture fixture, ImageResourceDescriptor resource,
        ImageImportOperation operation, params ImageImportAssignment[] assignments)
        => new ImageImportPreparationService().PrepareAsync(new(fixture.Project, resource, operation, assignments)).GetAwaiter().GetResult();

    private static void RejectUnknownStandardTargetTails(TestFixture fixture)
    {
        foreach (var (role, path, width, height) in new[]
                 {
                     ("atk", fixture.Paths[1], 64, 768), ("spc", fixture.Paths[2], 48, 240),
                     ("front", fixture.RPath, 48, 1280)
                 })
        {
            var snapshot = File.ReadAllBytes(path);
            try
            {
                var original = E5Archive.OpenFresh(path);
                var number = role == "front" ? 3 : 242;
                var payload = role == "front" ? Standard(width, height, ImageFormat.Png) : original.ReadDecoded(number);
                File.WriteAllBytes(path, original.Rebuild(replacementDecoded:
                    new Dictionary<int, byte[]> { [number] = payload.Concat(new byte[] { 0x42, 0x71 }).ToArray() }));
                var source = Save(fixture.Root, "未知尾部验证-" + role + ".png", width, height, ImageFormat.Png);
                using var result = Prepare(fixture, role == "front" ? fixture.RResource : fixture.SResource,
                    ImageImportOperation.Replace, new ImageImportAssignment(role == "front" ? fixture.R : fixture.S, role, source));
                Require(!result.CanCommit && result.Rows.Single().Error?.Contains("未知尾部", StringComparison.Ordinal) == true,
                    role + " 标准目标附加结构被文件导入静默丢弃。");
            }
            finally { File.WriteAllBytes(path, snapshot); E5Archive.Invalidate(path); }
        }
    }

    private static void CompatibilityDirectoryImports(TestFixture fixture)
    {
        var rDirectory = Path.Combine(fixture.Root, "旧目录R");
        Directory.CreateDirectory(rDirectory);
        Save(rDirectory, "x.jpg", 48, 1280, ImageFormat.Jpeg);
        Save(rDirectory, "2.jpeg", 48, 1280, ImageFormat.Jpeg);
        var rReplace = new RsStripExchangeService().PlanReplace(fixture.Project, fixture.R, rDirectory);
        var rCandidate = E5Archive.Parse(fixture.RPath, rReplace.Files.Single().CandidateBytes);
        Require(IsPng(rCandidate.ReadDecoded(3)) && IsPng(rCandidate.ReadDecoded(4)), "旧R目录JPG aliases没有复用新的PNG32替换规则。");
        var rAppend = new RsImportService().PlanAppend(fixture.Project, fixture.RResource, rDirectory);
        var rNew = E5Archive.Parse(fixture.RPath, rAppend.Files.Single().CandidateBytes);
        Require(rNew.Entries.Count == 6 && IsPng(rNew.ReadDecoded(5)) && IsPng(rNew.ReadDecoded(6)),
            "旧R目录aliases新增没有成对编码为PNG32。");
        Save(rDirectory, "front.png", 48, 1280, ImageFormat.Png);
        RequireRejected(() => new RsStripExchangeService().PlanReplace(fixture.Project, fixture.R, rDirectory),
            "旧目录多个front候选未要求明确映射。");

        var sRoot = Path.Combine(fixture.Root, "旧编号目录");
        var sDirectory = Path.Combine(sRoot, "S002");
        Directory.CreateDirectory(sDirectory);
        Save(sDirectory, "mov.jpg", 48, 528, ImageFormat.Jpeg);
        Save(sDirectory, "atk.jpeg", 64, 768, ImageFormat.Jpeg);
        Save(sDirectory, "spc.png", 48, 240, ImageFormat.Png);
        var sReplace = new RsStripExchangeService().PlanReplace(fixture.Project, fixture.S, sRoot, naming: RsImportNaming.Legacy);
        Require(sReplace.Files.Count == 3 && sReplace.Files.All(file =>
                E5Archive.Parse(file.TargetPath, file.CandidateBytes).ReadDecoded(241)
                    .SequenceEqual(E5Archive.OpenFresh(file.TargetPath).ReadDecoded(241))),
            "显式旧S002目录未精确替换物理242或改了相邻图号。");
        var sAppend = new RsImportService().PlanAppend(fixture.Project, fixture.SResource, sDirectory);
        Require(sAppend.Files.Count == 3 && sAppend.Files.All(file =>
                IsPng(E5Archive.Parse(file.TargetPath, file.CandidateBytes).ReadDecoded(244))),
            "旧目录S新增没有同步编码为PNG32。");
    }

    private static void PaletteChangesPreservingFileMetadata(TestFixture fixture)
    {
        var path = Path.Combine(fixture.Root, "Spalet.e5");
        var colors = new byte[768];
        for (var index = 0; index < 256; index++) colors[index * 3] = 200;
        colors[3] = 0; colors[4] = 100; colors[5] = 0;
        File.WriteAllBytes(path, Archive([colors]));
        var before = new PaletteService().Load(fixture.Project);
        Require(before[1].R == 100 && before[1].B == 0, "Spalet初始调色板未加载。");
        var timestamp = File.GetLastWriteTimeUtc(path);
        (colors[3], colors[4], colors[5]) = (200, 0, 0);
        (colors[6], colors[7], colors[8]) = (0, 100, 0);
        File.WriteAllBytes(path, Archive([colors]));
        File.SetLastWriteTimeUtc(path, timestamp);
        var input = Save(fixture.Root, "保长保时间palette验证.png", 48, 528, ImageFormat.Png);
        using var preview = Prepare(fixture, fixture.SResource, ImageImportOperation.Replace,
            new ImageImportAssignment(fixture.S, "mov", input));
        Require(preview.CanCommit, Errors(preview));
        var payload = Candidate(preview, "Unit_mov.e5").ReadDecoded(242);
        Require(payload[0] == 2 && preview.Rows.Single().After!.GetPixel(0, 0).ToArgb() == Color.FromArgb(100, 0, 0).ToArgb(),
            "同长度同修改时间的Spalet更新仍使用了旧缓存调色板。");
    }

    private static void RequireRejected(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static E5Archive Candidate(PreparedImageImport preview, string name)
    {
        var file = preview.Plan!.Files.Single(file => Path.GetFileName(file.TargetPath) == name);
        return E5Archive.Parse(file.TargetPath, file.CandidateBytes);
    }

    private static string Errors(PreparedImageImport preview) => string.Join("；", preview.Rows.Where(row => row.Error != null)
        .Select(row => row.TargetLabel + "/" + row.Role + ": " + row.Error));
    private static bool IsPng(byte[] bytes) => bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static string Save(string root, string name, int width, int height, ImageFormat format, Action<Bitmap>? edit = null)
    {
        var path = Path.Combine(root, name);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(255, 100, 0, 0));
        edit?.Invoke(bitmap);
        bitmap.Save(path, format);
        return path;
    }

    private static byte[] Standard(int width, int height, ImageFormat format)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Blue);
        using var stream = new MemoryStream();
        bitmap.Save(stream, format);
        return stream.ToArray();
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var dataStart = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize + 4;
        var bytes = new byte[dataStart + payloads.Sum(payload => payload.Length)];
        var offset = dataStart;
        for (var index = 0; index < payloads.Count; index++)
        {
            var directory = E5Archive.IndexOffset + index * E5Archive.EntrySize;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(directory, 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(directory + 4, 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(directory + 8, 4), offset);
            payloads[index].CopyTo(bytes, offset);
            offset += payloads[index].Length;
        }
        return bytes;
    }

    private sealed record TestFixture(string Root, ProjectProfile Project, string[] Paths, string RPath,
        ImageResourceDescriptor SResource, LogicalImageAsset S, ImageResourceDescriptor RResource, LogicalImageAsset R);

    private static TestFixture Fixture(string root)
    {
        var palette = new byte[1024];
        foreach (var (index, color) in new[] { (1, Color.FromArgb(100, 0, 0)), (2, Color.FromArgb(0, 200, 0)),
                     (3, Color.FromArgb(0, 0, 200)), (4, Color.FromArgb(247, 0, 255)) })
        { palette[index * 4] = color.B; palette[index * 4 + 1] = color.G; palette[index * 4 + 2] = color.R; }
        File.WriteAllBytes(Path.Combine(root, "tsb"), palette);
        var paths = new[] { "mov", "atk", "spc" }.Select(role => Path.Combine(root, "Unit_" + role + ".e5")).ToArray();
        var mov = Enumerable.Repeat((byte)7, 48 * 528).Concat(new byte[] { 0x5a, 0xa5 }).ToArray();
        var atk = Standard(64, 768, ImageFormat.Bmp);
        var spc = Standard(48, 240, ImageFormat.Png);
        for (var i = 0; i < paths.Length; i++)
        {
            var payload = new[] { mov, atk, spc }[i];
            File.WriteAllBytes(paths[i], Archive(Enumerable.Repeat(payload, 243).ToArray()));
        }
        var rPath = Path.Combine(root, "Pmapobj.e5");
        File.WriteAllBytes(rPath, Archive(Enumerable.Range(1, 4).Select(value => Enumerable.Repeat((byte)value, 48 * 1280).ToArray()).ToArray()));
        var project = new ProjectProfile(root, "R/S 文件导入测试", ProjectVersion.Ccz65, false,
            paths.Append(rPath).ToDictionary(path => Path.GetFileName(path), path => path), []);
        var sResource = new ImageResourceDescriptor("S", "S", ImageCategory.CharacterS, ResourceKind.SComposite,
            paths, 243, true, true, false, "", "");
        var sFrames = new[] { ("mov", 48, 11), ("atk", 64, 12), ("spc", 48, 5) }.SelectMany((spec, i) =>
            Enumerable.Range(0, spec.Item3).Select(frame => new FrameDescriptor(Path.GetFileName(paths[i]), paths[i], 242,
                frame, 1, spec.Item1, "", spec.Item2, spec.Item2))).ToArray();
        var s = new LogicalImageAsset("S", 242, "S_242", ImageCategory.CharacterS, ResourceKind.SComposite,
            sFrames, [], false, false, "") { PhysicalImageNumber = 242 };
        var rResource = new ImageResourceDescriptor("R", "R", ImageCategory.CharacterR, ResourceKind.RComposite,
            [rPath], 4, true, true, false, "", "");
        var rFrames = new[] { ("front", 3), ("back", 4) }.SelectMany(spec => Enumerable.Range(0, 20)
            .Select(frame => new FrameDescriptor("Pmapobj.e5", rPath, spec.Item2, frame, 1, "", spec.Item1, 48, 64))).ToArray();
        var r = new LogicalImageAsset("R", 1, "R001", ImageCategory.CharacterR, ResourceKind.RComposite,
            rFrames, [], false, false, "");
        return new(root, project, paths, rPath, sResource, s, rResource, r);
    }
}
