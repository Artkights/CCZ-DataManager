using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class MutationSemanticTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManagerSemantics_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Logo.e5");
            var profile = new ProjectProfile(root, "语义验收", ProjectVersion.Ccz65, false,
                new Dictionary<string, string> { ["Logo.e5"] = path }, []);
            var historyPath = Path.Combine(root, "history");
            var history = new HistoryService(historyPath);
            var service = new ImageMutationService(history);
            var red = Image(Color.Red, ImageFormat.Bmp);
            var original = Archive(red, Image(Color.Blue, ImageFormat.Bmp));
            File.WriteAllBytes(path, original);

            var identical = service.Commit(profile, Plan(path, original));
            NoChange(identical, "完全相同候选");
            Require(!Directory.Exists(historyPath), "无变化操作创建了历史目录。");
            var container = (byte[])original.Clone(); container[8] ^= 1;
            NoChange(service.Commit(profile, Plan(path, container)), "仅容器头变化");
            var recoded = E5Archive.OpenFresh(path).Rebuild(replacementDecoded:
                new Dictionary<int, byte[]> { [1] = Image(Color.Red, ImageFormat.Png) });
            NoChange(service.Commit(profile, Plan(path, recoded)), "相同像素更换压缩/图片编码");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "无变化操作触碰了物理文件。");

            var target = new FrameDescriptor("Logo.e5", path, 1, 0, 1, "image", "none", 4, 4);
            using var green = Solid(Color.Lime);
            var expected = MutationImageExpectation.FromBitmap(target, ResourceKind.E5Indexed, green);
            var missing = service.Commit(profile, Plan(path, original) with { ExpectedImages = [expected] });
            Require(missing.Outcome == MutationOutcome.Failed && !missing.Success && missing.HistoryId == null,
                "候选丢失编辑像素仍被当成无变化或成功。");
            Require(!Directory.Exists(historyPath), "写前语义验证失败创建了历史。");

            var wrongSlot = E5Archive.OpenFresh(path).Rebuild(replacementDecoded:
                new Dictionary<int, byte[]> { [2] = Image(Color.Lime, ImageFormat.Bmp) });
            var wrong = service.Commit(profile, Plan(path, wrongSlot) with { ExpectedImages = [expected] });
            Require(wrong.Outcome == MutationOutcome.Failed && File.ReadAllBytes(path).SequenceEqual(original),
                "写错物理图号没有被独立目标预期拒绝。");

            var changed = E5Archive.OpenFresh(path).Rebuild(replacementDecoded:
                new Dictionary<int, byte[]> { [1] = Image(Color.Lime, ImageFormat.Bmp) });
            var applied = service.Commit(profile, Plan(path, changed) with { ExpectedImages = [expected] });
            Require(applied.Outcome == MutationOutcome.Applied && applied.Success && applied.HistoryId != null,
                "有效像素修改没有保存：" + string.Join("；", applied.Diagnostics));
            using (var actual = BmpPayloadService.Decode(E5Archive.OpenFresh(path).ReadDecoded(1)))
                Require(actual.GetPixel(2, 2).ToArgb() == Color.Lime.ToArgb(), "保存后新开档案的像素不符。");
            Require(history.Undo(profile, applied.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(original),
                "语义验证事务不能撤回。");
            Require(history.Redo(profile, applied.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(changed),
                "语义验证事务不能重做。");

            var terrain = Path.Combine(root, "Pmap.e5");
            File.WriteAllBytes(terrain, Archive([1, 2, 3, 4]));
            var terrainPlan = Plan(terrain, Archive([1, 2, 9, 4])) with { Kind = MutationKind.TerrainEdit };
            Require(service.Commit(profile, terrainPlan).Success, "有效地形/非图片载荷变化被误判无变化。");

            var pngPath = Path.Combine(root, "OpaquePng.e5");
            File.WriteAllBytes(pngPath, Archive(Image(Color.Magenta, ImageFormat.Png)));
            using var transparent = Solid(Color.FromArgb(0, 0, 0, 0));
            var transparentPng = Image(Color.FromArgb(0, 0, 0, 0), ImageFormat.Png);
            var pngTarget = target with { SourceKey = "OpaquePng.e5", SourcePath = pngPath };
            var pngPlan = Plan(pngPath, Archive(transparentPng)) with
            { ExpectedImages = [MutationImageExpectation.FromBitmap(pngTarget, ResourceKind.E5Indexed, transparent)] };
            Require(service.Commit(profile, pngPlan).Success, "PNG不透明洋红被错误视为透明无变化。");

            var palette = new byte[1024];
            for (var index = 0; index < 256; index++)
            { palette[index * 4] = (byte)index; palette[index * 4 + 1] = 30; palette[index * 4 + 2] = 40; }
            File.WriteAllBytes(Path.Combine(root, "tsb"), palette);
            var rawPath = Path.Combine(root, "Hitarea.e5");
            File.WriteAllBytes(rawPath, Archive(Enumerable.Repeat((byte)1, 16).ToArray()));
            var raw = Enumerable.Repeat((byte)1, 16).ToArray(); raw[0] = 0;
            using var rawExpected = Solid(Color.FromArgb(40, 30, 1)); rawExpected.SetPixel(0, 0, Color.Transparent);
            var rawTarget = target with { SourceKey = "Hitarea.e5", SourcePath = rawPath };
            var rawPlan = Plan(rawPath, Archive(raw)) with
            { ExpectedImages = [MutationImageExpectation.FromBitmap(rawTarget, ResourceKind.E5Indexed, rawExpected)] };
            Require(service.Commit(profile, rawPlan).Success, "非洋红项目色表的RAW透明0号复读失败。");
            CheckWholeFileRepair(root, profile, history, service);
            Console.WriteLine("MUTATION_SEMANTIC_TESTS_OK no-op container recode independent-pixels physical-slot disk undo redo terrain corrupt-ls-meff-repair");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static void CheckWholeFileRepair(string root, ProjectProfile profile, HistoryService history, ImageMutationService service)
    {
        var path = Path.Combine(root, "Repair.e5");
        var damaged = Archive(new byte[] { 0 });
        // The directory is valid, but this one-byte LS stream cannot produce 32 output bytes.
        BinaryPrimitives.WriteInt32BigEndian(damaged.AsSpan(E5Archive.IndexOffset + 4), 32);
        File.WriteAllBytes(path, damaged);
        Require(E5Archive.OpenFresh(path).Entries.Count == 1, "损坏LS测试夹具的目录无效。");
        var historyBefore = history.List(profile).Count;
        var source = Path.Combine(root, "repair-source.bin");
        var valid = Archive(Image(Color.Gold, ImageFormat.Bmp)); File.WriteAllBytes(source, valid);
        var local = service.Commit(profile, Plan(path, valid));
        Require(local.Outcome == MutationOutcome.Failed && File.ReadAllBytes(path).SequenceEqual(damaged) &&
                history.List(profile).Count == historyBefore, "局部操作放过了损坏旧LS载荷，或失败产生了修改/历史。");
        var result = service.Commit(profile, service.PlanWholeFileReplace(profile, path, source));
        Require(result.Success && File.ReadAllBytes(path).SequenceEqual(valid),
            "完整有效资源不能修复目录合法但LS损坏的旧包：" + string.Join("；", result.Diagnostics));
        using (var actual = BmpPayloadService.Decode(E5Archive.OpenFresh(path).ReadDecoded(1)))
            Require(actual.GetPixel(0, 0).ToArgb() == Color.Gold.ToArgb(), "整包修复没有复读到正确像素。");
        Require(history.Undo(profile, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(damaged) &&
                history.Redo(profile, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(valid),
            "损坏旧包修复的历史不能精确撤回/重做。");
        var failed = service.Commit(profile, Plan(path, damaged) with { Kind = MutationKind.WholeFileReplace });
        Require(failed.Outcome == MutationOutcome.Failed && File.ReadAllBytes(path).SequenceEqual(valid),
            "整包修复放松了新候选LS流的校验。");
        var unterminated = damaged.ToArray(); unterminated[^1] = 255; File.WriteAllBytes(path, unterminated);
        var unterminatedRepair = service.Commit(profile, service.PlanWholeFileReplace(profile, path, source));
        Require(unterminatedRepair.Success && File.ReadAllBytes(path).SequenceEqual(valid),
            "没有终止位的旧LS码流不能通过整文件替换修复。");

        var meffPath = Path.Combine(root, "Meff.e5");
        var badAnimation = Archive(new byte[40]); File.WriteAllBytes(meffPath, badAnimation);
        var package = new byte[41]; package[0] = 1; package[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(4), 100);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), 1);
        package[21] = 255; package[40] = 1;
        var validAnimation = Archive(package); File.WriteAllBytes(source, validAnimation);
        _ = StrategyAnimationPackageCodec.Probe(package);
        var meffLocal = service.Commit(profile, Plan(meffPath, validAnimation));
        Require(meffLocal.Outcome == MutationOutcome.Failed && File.ReadAllBytes(meffPath).SequenceEqual(badAnimation),
            "局部操作放过了损坏旧Meff动画载荷。");
        var repaired = service.Commit(profile, service.PlanWholeFileReplace(profile, meffPath, source));
        Require(repaired.Success && File.ReadAllBytes(meffPath).SequenceEqual(validAnimation),
            "完整有效Meff不能修复旧动画载荷：" + string.Join("；", repaired.Diagnostics));
        using (var animation = new StrategyAnimationPackageCodec().Decode(profile, E5Archive.OpenFresh(meffPath).ReadDecoded(1)))
            Require(animation.PlaneFrames.Count == 1 && animation.Width == 1 && animation.Height == 1,
                "Meff整包修复后物理平面无法重新解码。");
        var invalidMeff = service.Commit(profile, Plan(meffPath, badAnimation) with { Kind = MutationKind.WholeFileReplace });
        Require(invalidMeff.Outcome == MutationOutcome.Failed && File.ReadAllBytes(meffPath).SequenceEqual(validAnimation),
            "整包修复放松了新Meff候选动画结构校验。");
    }

    private static MutationPlan Plan(string path, byte[] candidate)
        => new(Guid.NewGuid().ToString("N"), MutationKind.PixelEdit, "语义验收", [new(path, candidate, "图片")], [], false);
    private static void NoChange(MutationResult result, string label)
        => Require(result.Outcome == MutationOutcome.NoChange && !result.Success && result.HistoryId == null && result.WrittenPaths.Count == 0,
            label + "没有返回独立的无变化结果：" + string.Join("；", result.Diagnostics));
    private static Bitmap Solid(Color color)
    {
        var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap); graphics.Clear(color); return bitmap;
    }
    private static byte[] Image(Color color, ImageFormat format)
    {
        using var bitmap = Solid(color); using var stream = new MemoryStream(); bitmap.Save(stream, format); return stream.ToArray();
    }
    private static byte[] Archive(params byte[][] payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Length * E5Archive.EntrySize + 4;
        var result = new byte[offset + payloads.Sum(payload => payload.Length)];
        "Ls12"u8.CopyTo(result);
        for (var index = 0; index < payloads.Length; index++)
        {
            var at = E5Archive.IndexOffset + index * E5Archive.EntrySize;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at + 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(at + 8), offset);
            payloads[index].CopyTo(result, offset); offset += payloads[index].Length;
        }
        return result;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
