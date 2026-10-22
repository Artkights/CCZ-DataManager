using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class ImageImportPreparationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CczImageFilePrepare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CandidatePreviewAndHistory(root);
            DependenciesAndCancellation(root);
            InvalidRowsAreAtomic(root);
            CrossResourceCompatibility(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CandidatePreviewAndHistory(string root)
    {
        var path = Path.Combine(root, "Logo.e5");
        var original = Archive(Png(4, 4, Color.Red), Png(3, 5, Color.Blue));
        File.WriteAllBytes(path, original);
        var source = Path.Combine(root, "随意命名的数据.bmp");
        var png = Png(17, 11, Color.Magenta);
        File.WriteAllBytes(source, png); // Content wins over extension; ordinary images keep magenta.
        var (project, resource, asset) = Context(root, path);
        var history = new HistoryService(Path.Combine(root, "history-preview"));
        var mutations = new ImageMutationService(history);
        var request = new ImageImportRequest(project, resource, ImageImportOperation.Replace,
            [new(asset, "image", source)]);
        using var prepared = Prepare(request);
        Require(prepared.CanCommit, Errors(prepared));
        Require(File.ReadAllBytes(path).SequenceEqual(original), "准备预览改动了项目文件。");
        Require(history.List(project).Count == 0, "准备预览写入了历史。");
        var row = prepared.Rows.Single();
        Require(row.SourceFormat.Contains("PNG") && row.SourceSize == new Size(17, 11), "未按实际内容报告素材格式/尺寸。");
        Require(row.After!.Size == new Size(4, 4) && row.After.GetPixel(2, 2).ToArgb() == Color.Magenta.ToArgb(),
            "普通图片未适配目标画布或误用了洋红透明规则。");
        var plan = prepared.Plan!;
        var candidate = E5Archive.Parse(path, plan.Files.Single().CandidateBytes);
        using (var candidateImage = BmpPayloadService.Decode(candidate.ReadDecoded(1)))
            Require(candidateImage.Size == new Size(4, 4) && candidateImage.GetPixel(2, 2).ToArgb() == Color.Magenta.ToArgb(), "普通整图替换没有按目标规格生成候选。");
        Require(candidate.ReadDecoded(2).SequenceEqual(E5Archive.Parse(path, original).ReadDecoded(2)), "候选误改相邻物理条目。");
        prepared.Dispose();
        Require(!prepared.CanCommit, "已释放的预览仍报告可交互提交。");
        var result = mutations.Commit(project, plan);
        Require(result.Success, string.Join("；", result.Diagnostics));
        using (var decoded = BmpPayloadService.Decode(E5Archive.OpenFresh(path).ReadDecoded(1)))
            Require(decoded.Size == new Size(4, 4) && decoded.GetPixel(2, 2).ToArgb() == Color.Magenta.ToArgb(), "写回复读与预览不一致。");
        var committed = File.ReadAllBytes(path);
        Require(history.Undo(project, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(original), "导入撤回没有逐字节恢复。");
        Require(history.Redo(project, result.HistoryId!).Success && File.ReadAllBytes(path).SequenceEqual(committed), "导入重做没有逐字节恢复。");
        Require(history.Undo(project, result.HistoryId!).Success, "清理事务状态失败。");

        // Existing file APIs must reach the import path, while bitmap edits retain their original geometry.
        var compatible = mutations.PlanReplace(project, [asset], [source]);
        Require(E5Archive.Parse(path, compatible.Files.Single().CandidateBytes).ReadDecoded(1).SequenceEqual(candidate.ReadDecoded(1)), "兼容替换接口未接入新导入规则。");
        using var wrongSize = new Bitmap(17, 11);
        var pixelBlocked = false;
        try { _ = mutations.PlanReplaceSingleFrame(project, asset, asset.Frames[0], wrongSize); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { pixelBlocked = true; }
        Require(pixelBlocked, "放宽文件导入连带放宽了原像素编辑尺寸。");
    }

    private static void DependenciesAndCancellation(string root)
    {
        var path = Path.Combine(root, "Dependency.e5");
        var original = Archive(Png(4, 4, Color.Red));
        File.WriteAllBytes(path, original);
        var source = Path.Combine(root, "dependency.png");
        File.WriteAllBytes(source, Png(8, 8, Color.Green));
        var (project, resource, asset) = Context(root, path);
        var history = new HistoryService(Path.Combine(root, "history-dependency"));
        var mutations = new ImageMutationService(history);
        var request = new ImageImportRequest(project, resource, ImageImportOperation.Replace, [new(asset, "image", source)]);
        using (var prepared = Prepare(request))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var timestamp = File.GetLastWriteTimeUtc(source);
            File.WriteAllBytes(source, Png(8, 8, Color.Yellow));
            File.SetLastWriteTimeUtc(source, timestamp);
            Require(!mutations.Commit(project, prepared.Plan!).Success, "外部素材变更后仍提交旧预览。");
        }
        Require(File.ReadAllBytes(path).SequenceEqual(original) && history.List(project).Count == 0, "过期素材改动了资源或历史。");
        using (var prepared = Prepare(request))
        {
            Require(prepared.CanCommit, Errors(prepared));
            File.WriteAllBytes(Path.Combine(root, "tsb"), new byte[1024]);
            Require(!mutations.Commit(project, prepared.Plan!).Success, "准备后新出现的高优先级调色板没有使候选失效。");
        }
        Require(File.ReadAllBytes(path).SequenceEqual(original), "过期调色板候选写入了资源。");
        using (var prepared = Prepare(request))
        {
            Require(prepared.CanCommit, Errors(prepared));
            var external = Archive(Png(6, 6, Color.Blue));
            File.WriteAllBytes(path, external);
            Require(!mutations.Commit(project, prepared.Plan!).Success && File.ReadAllBytes(path).SequenceEqual(external),
                "准备后的外部目标改动被覆盖。");
        }
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try { _ = new ImageImportPreparationService().PrepareAsync(request, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "已取消的导入任务仍生成候选。");
    }

    private static void InvalidRowsAreAtomic(string root)
    {
        var path = Path.Combine(root, "Atomic.e5");
        var original = Archive(Png(4, 4, Color.Red), Png(4, 4, Color.Blue));
        File.WriteAllBytes(path, original);
        var source = Path.Combine(root, "valid.png");
        var invalid = Path.Combine(root, "broken.png");
        File.WriteAllBytes(source, Png(13, 7, Color.Green));
        File.WriteAllBytes(invalid, [137, 80, 78, 71, 13, 10, 26, 10]);
        var (project, resource, asset) = Context(root, path);
        var second = asset with { LogicalId = 2, DisplayName = "图2", Frames = [asset.Frames[0] with { ImageNumber = 2 }] };
        var request = new ImageImportRequest(project, resource, ImageImportOperation.Replace,
            [new(asset, "image", source), new(second, "image", invalid)]);
        using (var prepared = Prepare(request))
            Require(!prepared.CanCommit && prepared.Plan == null && prepared.Rows.Any(row => row.Error != null), "批量含错误时仍生成可提交计划。");
        using (var prepared = Prepare(request with { Assignments = [new(asset, "image", source), new(second, "image", invalid, Included: false)] }))
            Require(prepared.CanCommit && prepared.Rows.Count == 1, "显式排除错误行后没有正确重建候选。");
        using (var prepared = Prepare(request with { Project = project with { IsReadOnly = true } }))
            Require(!prepared.CanCommit, "只读项目允许生成导入计划。");
        Require(File.ReadAllBytes(path).SequenceEqual(original), "准备或取消批量操作改动了目标。");
    }

    private static void CrossResourceCompatibility(string root)
    {
        var firstPath = Path.Combine(root, "cross-one.e5");
        var secondPath = Path.Combine(root, "cross-two.e5");
        var firstBytes = Archive(Png(7, 3, Color.Red));
        var secondBytes = Archive(Png(5, 8, Color.Blue));
        File.WriteAllBytes(firstPath, firstBytes); File.WriteAllBytes(secondPath, secondBytes);
        var first = Context(root, firstPath); var second = Context(root, secondPath);
        var source = Path.Combine(root, "cross-source.any"); File.WriteAllBytes(source, Png(21, 19, Color.Orange));
        var history = new HistoryService(Path.Combine(root, "history-cross"));
        var mutations = new ImageMutationService(history);
        var plan = mutations.PlanReplace(first.Item1, [first.Item3, second.Item3], [source, source]);
        Require(plan.Files.Count == 2 && plan.SourceSnapshots!.ContainsKey(source), "跨资源兼容替换未使用共同素材快照。");
        var result = mutations.Commit(first.Item1, plan);
        Require(result.Success, string.Join("；", result.Diagnostics));
        foreach (var (path, size) in new[] { (firstPath, new Size(7, 3)), (secondPath, new Size(5, 8)) })
        {
            using var image = BmpPayloadService.Decode(E5Archive.OpenFresh(path).ReadDecoded(1));
            Require(image.Size == size && image.GetPixel(1, 1).ToArgb() == Color.Orange.ToArgb(), "跨资源替换回落到原尺寸严格导入。");
        }
        Require(history.Undo(first.Item1, result.HistoryId!).Success && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes) &&
            File.ReadAllBytes(secondPath).SequenceEqual(secondBytes), "跨资源导入撤回没有同时恢复两个档案。");
    }

    private static PreparedImageImport Prepare(ImageImportRequest request)
        => new ImageImportPreparationService().PrepareAsync(request).GetAwaiter().GetResult();
    private static string Errors(PreparedImageImport prepared) => string.Join("；", prepared.Rows.Select(row => row.Error));
    private static (ProjectProfile, ImageResourceDescriptor, LogicalImageAsset) Context(string root, string path)
    {
        var key = Path.GetFileName(path);
        var project = new ProjectProfile(root, "素材准备验收", ProjectVersion.Ccz65, false, new Dictionary<string, string> { [key] = path }, []);
        var resource = new ImageResourceDescriptor(key, "普通图", ImageCategory.Interface, ResourceKind.E5Indexed, [path], 2, true, true, false, "", "");
        var asset = new LogicalImageAsset(key, 1, "图1", ImageCategory.Interface, ResourceKind.E5Indexed,
            [new(key, path, 1, 0, 1, "image", "none", 4, 4)], [], false, false, "");
        return (project, resource, asset);
    }
    private static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
    private static byte[] Archive(params byte[][] payloads)
    {
        var bytes = new byte[E5Archive.IndexOffset + payloads.Length * E5Archive.EntrySize + payloads.Sum(payload => payload.Length)];
        var offset = E5Archive.IndexOffset + payloads.Length * E5Archive.EntrySize;
        for (var i = 0; i < payloads.Length; i++)
        {
            var record = E5Archive.IndexOffset + i * E5Archive.EntrySize;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(record), payloads[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(record + 4), payloads[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(record + 8), offset);
            payloads[i].CopyTo(bytes, offset); offset += payloads[i].Length;
        }
        return bytes;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
