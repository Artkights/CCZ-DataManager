using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using ImageManager.Core;

namespace ImageManager.Tests;

/// <summary>Special containers retain their verified headers, grids and frame protocols during file conversion.</summary>
public static class SpecialFileImportTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageManager.SpecialFileImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Fixture(root);
            WeatherConversion(fixture);
            PmapConversion(fixture);
            MapBottomImageConversion(fixture);
            DirectoryMapPairConversion(fixture);
            UnknownStructuresRemainReadOnly(fixture);
            RequestedFormatsKeepNativeStructures(fixture);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WeatherConversion(TestFixture fixture)
    {
        var resource = fixture.Catalog.Single(item => item.Kind == ResourceKind.WeatherSpecial);
        Require(resource.CanMutate, "验证通过的天气档案未开放文件导入。");
        var asset = fixture.Service.ReadAssets(fixture.Project, resource).First();
        // A gallery can supply all four descriptors rather than only its first descriptor.
        var expanded = asset with { Frames = Enumerable.Range(0, 4).Select(index => asset.Frames[0] with { FrameIndex = index }).ToArray() };
        var source = Save(fixture.Root, "weather source.gif", 37, 71, ImageFormat.Gif);
        using var replacement = Prepare(fixture, resource, ImageImportOperation.Replace, new ImageImportAssignment(expanded, "image", source));
        Require(replacement.CanCommit, Errors(replacement));
        var candidate = Candidate(replacement, "Weather.e5");
        Require(candidate.Entries.Count == 2 && candidate.ReadDecoded(1).Length == 43200, "天气文件替换破坏四帧协议。");
        Require(candidate.ReadDecoded(1).All(value => value == 1), "天气文件未按项目调色板量化。");
        Require(candidate.ReadStored(2).SequenceEqual(E5Archive.OpenFresh(fixture.Paths["Weather.e5"]).ReadStored(2)), "天气替换修改了其他条目。");
        Require(replacement.Rows.Single().After!.Size == new Size(216, 200), "天气预览没有显示重编码的整条四帧画布。");
        CommitCycle(fixture, replacement.Plan!, () => VerifyWeatherCatalog(fixture, 2, 0));
        using var append = Prepare(fixture, resource, ImageImportOperation.Append, new ImageImportAssignment(null, "image", source));
        Require(append.CanCommit, Errors(append));
        var appended = Candidate(append, "Weather.e5");
        Require(appended.Entries.Count == 3 && appended.ReadDecoded(3).Length == 43200, "天气新增未按模板保持固定帧协议。");
        CommitCycle(fixture, append.Plan!, () => VerifyWeatherCatalog(fixture, 3, 2));
        Require(E5Archive.OpenFresh(fixture.Paths["Weather.e5"]).Entries.Count == 2, "天气预览阶段写入真实文件。");
        var sourceBefore = File.ReadAllBytes(source);
        File.WriteAllBytes(source, sourceBefore.Concat(new byte[] { 17 }).ToArray());
        RejectStalePlan(fixture, replacement.Plan!, "素材变更后天气计划仍被提交。");
        File.WriteAllBytes(source, sourceBefore);
    }

    private static void RequestedFormatsKeepNativeStructures(TestFixture fixture)
    {
        var source = Save(fixture.Root, "format-lock.png", 19, 23, ImageFormat.Png);
        foreach (var kind in new[] { ResourceKind.WeatherSpecial, ResourceKind.PmapSpecial, ResourceKind.MapPair })
        {
            var resource = fixture.Catalog.Single(item => item.Kind == kind);
            var target = fixture.Service.ReadAssets(fixture.Project, resource).First();
            var support = ImageImportFormatPolicy.GetSupport(resource);
            Require(support.IsLocked && support.AllowedFormats.SequenceEqual(new[] { ImageImportOutputFormat.Preserve }),
                "专用结构错误开放BMP/PNG存储选择。");
            foreach (var format in Enum.GetValues<ImageImportOutputFormat>())
            {
                using var prepared = new ImageImportPreparationService().PrepareAsync(new(fixture.Project, resource, ImageImportOperation.Replace,
                    [new(target, "image", source)], format)).GetAwaiter().GetResult();
                Require(prepared.CanCommit, Errors(prepared));
                if (kind == ResourceKind.MapPair)
                {
                    var payload = prepared.Plan!.Files.Single().CandidateBytes;
                    Require(payload[0] == 255 && payload[1] == 216 && prepared.Rows.Single().OutputFormat == "JPG24",
                        "格式选择改变了地图固定JPG编码。");
                }
                else
                {
                    var payload = Candidate(prepared, kind == ResourceKind.WeatherSpecial ? "Weather.e5" : "Pmap.e5").ReadDecoded(1);
                    Require(kind == ResourceKind.WeatherSpecial ? payload.Length == 43200 :
                        PmapResourceCodec.IsVerifiedPayload(payload) && PmapResourceCodec.PayloadSize(payload) == new Size(8, 6),
                        "格式选择把专用像素结构覆盖成BMP/PNG。");
                }
            }
        }
    }

    private static void PmapConversion(TestFixture fixture)
    {
        var resource = fixture.Catalog.Single(item => item.Kind == ResourceKind.PmapSpecial);
        Require(resource.CanMutate, "已验证Pmap平面未开放文件导入。");
        var assets = fixture.Service.ReadAssets(fixture.Project, resource);
        var source = Save(fixture.Root, "pmap arbitrary.tif", 23, 9, ImageFormat.Tiff);
        using var replacement = Prepare(fixture, resource, ImageImportOperation.Replace, new ImageImportAssignment(assets[0], "image", source));
        Require(replacement.CanCommit, Errors(replacement));
        var original = E5Archive.OpenFresh(fixture.Paths["Pmap.e5"]);
        var candidate = Candidate(replacement, "Pmap.e5");
        var payload = candidate.ReadDecoded(1);
        Require(PmapResourceCodec.IsVerifiedPayload(payload) && PmapResourceCodec.PayloadSize(payload) == new Size(8, 6), "Pmap重编码画布变化。");
        Require(payload.AsSpan(0, 16).SequenceEqual(original.ReadDecoded(1).AsSpan(0, 16)), "Pmap16字节头未原样保留。");
        Require(payload.AsSpan(16).ToArray().All(value => value == 1), "Pmap重编码像素未使用项目调色板。");
        Require(candidate.ReadStored(2).SequenceEqual(original.ReadStored(2)), "Pmap替换修改了其他条目。");
        CommitCycle(fixture, replacement.Plan!, () => VerifyPmapCatalog(fixture, 2, 0, new Size(8, 6)));
        using var append = Prepare(fixture, resource, ImageImportOperation.Append, new ImageImportAssignment(null, "image", source));
        Require(append.CanCommit, Errors(append));
        var added = Candidate(append, "Pmap.e5").ReadDecoded(3);
        Require(added.AsSpan(0, 16).SequenceEqual(original.ReadDecoded(2).AsSpan(0, 16)) && PmapResourceCodec.PayloadSize(added) == new Size(3, 7),
            "Pmap新增没有继承末条验证模板的头和画布。");
        CommitCycle(fixture, append.Plan!, () => VerifyPmapCatalog(fixture, 3, 2, new Size(3, 7)));
        using var wrongSize = new Bitmap(9, 6);
        try { _ = PmapResourceCodec.RebuildPayload(fixture.Project, original.ReadDecoded(1), wrongSize); throw new InvalidOperationException("Pmap公开编码器接受了错误画布。"); }
        catch (InvalidDataException) { }
    }

    private static void MapBottomImageConversion(TestFixture fixture)
    {
        var resource = fixture.Catalog.Single(item => item.Kind == ResourceKind.MapPair);
        var asset = fixture.Service.ReadAssets(fixture.Project, resource).Single();
        var source = Save(fixture.Root, "map arbitrary.png", 19, 73, ImageFormat.Png);
        var terrainBefore = File.ReadAllBytes(fixture.Paths["Hexzmap.e5"]);
        using var result = Prepare(fixture, resource, ImageImportOperation.Replace, new ImageImportAssignment(asset, "image", source));
        Require(result.CanCommit, Errors(result));
        Require(result.Plan!.Files.Count == 1, "普通地图底图替换修改了地形层。");
        var file = result.Plan.Files.Single();
        Require(file.CandidateBytes[0] == 0xff && file.CandidateBytes[1] == 0xd8, "地图底图没有转为JPG。");
        using var stream = new MemoryStream(file.CandidateBytes);
        using var decoded = new Bitmap(stream);
        Require(decoded.Size == new Size(48, 96), "地图底图没有适配原地图画布。");
        Require(result.Plan.SourceSnapshots!.ContainsKey(fixture.Paths["Hexzmap.e5"]) && terrainBefore.SequenceEqual(File.ReadAllBytes(fixture.Paths["Hexzmap.e5"])),
            "普通地图导入未捕获地形校验依赖或改变真实地形。");
        CommitCycle(fixture, result.Plan!, () => VerifyMapCatalog(fixture));
        File.WriteAllBytes(fixture.Paths["Hexzmap.e5"], Archive([new byte[] { 0x6a, 0x6b, 1, 8 }]));
        RejectStalePlan(fixture, result.Plan!, "地图预览后变更地形未使候选失效。");
        File.WriteAllBytes(fixture.Paths["Hexzmap.e5"], terrainBefore);
        using var append = Prepare(fixture, resource, ImageImportOperation.Append, new ImageImportAssignment(null, "image", source));
        Require(!append.CanCommit && append.Rows.Any(row => row.Error != null), "地图在没有地形槽分配协议时开放了新增。");
    }

    private static void DirectoryMapPairConversion(TestFixture fixture)
    {
        var resource = fixture.Catalog.Single(item => item.Kind == ResourceKind.MapPair);
        var asset = fixture.Service.ReadAssets(fixture.Project, resource).Single();
        var folder = Path.Combine(fixture.Root, "paired");
        var service = new MapPairService();
        var exported = service.Export(fixture.Project, asset, folder);
        File.Delete(exported.JpegPath);
        var source = Save(folder, "M000_image.bmp", 83, 17, ImageFormat.Bmp);
        var terrain = File.ReadAllBytes(exported.BmpPath);
        terrain[^2] = 9; terrain[^1] = 10;
        File.WriteAllBytes(exported.BmpPath, terrain);
        var plan = service.PlanImport(fixture.Project, asset, folder);
        Require(plan.Files.Count == 2 && plan.SourceSnapshots!.ContainsKey(source) && plan.SourceSnapshots.ContainsKey(exported.BmpPath),
            "地图对导入没有捕获视觉图与地形素材依赖。");
        var hex = Candidate(plan, "Hexzmap.e5").ReadDecoded(1);
        Require(hex.SequenceEqual(new byte[] { 0x6a, 0x6b, 9, 10 }), "地图对导入没有保留目标前缀与更新严格地形格。");
        var image = plan.Files.Single(file => Path.GetFileName(file.TargetPath) == "M000.JPG");
        using var stream = new MemoryStream(image.CandidateBytes);
        using var decoded = new Bitmap(stream);
        Require(decoded.Size == new Size(48, 96), "地图对导入没有适配原画布。");
        CommitCycle(fixture, plan, () =>
        {
            VerifyMapCatalog(fixture);
            Require(E5Archive.OpenFresh(fixture.Paths["Hexzmap.e5"]).ReadDecoded(1).SequenceEqual(hex), "地图对提交后地形复读不一致。");
        });
        Save(folder, "M000.png", 3, 4, ImageFormat.Png);
        try { _ = service.PlanImport(fixture.Project, asset, folder); throw new InvalidOperationException("多个底图候选未被拒绝。"); }
        catch (InvalidDataException) { }
        File.Delete(Path.Combine(folder, "M000.png"));
        Save(folder, "M000.BMP", 1, 2, ImageFormat.Bmp);
        try { _ = service.PlanImport(fixture.Project, asset, folder); throw new InvalidOperationException("普通BMP误当成严格地形格式。"); }
        catch (InvalidDataException) { }
    }

    private static void UnknownStructuresRemainReadOnly(TestFixture fixture)
    {
        var invalid = Path.Combine(fixture.Root, "bad-pmap.e5");
        File.WriteAllBytes(invalid, Archive([new byte[40]]));
        var paths = new Dictionary<string, string>(fixture.Paths, StringComparer.OrdinalIgnoreCase) { ["Pmap.e5"] = invalid };
        var project = fixture.Project with { ResourcePaths = paths };
        var catalog = fixture.Service.BuildCatalog(project);
        var resource = catalog.Single(item => item.Kind == ResourceKind.PmapSpecial);
        Require(!resource.CanMutate, "未验证Pmap头部仍开放写入。");
        Require(!catalog.Single(item => item.Kind == ResourceKind.DtAnimation).CanMutate, "DT动画结构被作为静态图开放写入。");
        var source = Save(fixture.Root, "blocked.png", 10, 10, ImageFormat.Png);
        using var blocked = new ImageImportPreparationService().PrepareAsync(new(project, resource, ImageImportOperation.Append,
            [new(null, "image", source)])).GetAwaiter().GetResult();
        Require(!blocked.CanCommit, "只读专用结构仍生成候选。");
    }

    private static TestFixture Fixture(string root)
    {
        var palette = new byte[1024];
        palette[4 + 2] = 100;
        File.WriteAllBytes(Path.Combine(root, "tsb"), palette);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, payloads) in new (string, byte[][])[] {
            ("Weather.e5", [Enumerable.Repeat((byte)2, 43200).ToArray(), Enumerable.Repeat((byte)3, 43200).ToArray()]),
            ("Pmap.e5", [Pmap(8, 6, 7), Pmap(3, 7, 9)]),
            ("DT.e5", [new byte[64]]), ("Hexzmap.e5", [new byte[] { 0x6a, 0x6b, 1, 2 }]) })
        {
            var path = Path.Combine(root, name); File.WriteAllBytes(path, Archive(payloads)); paths[name] = path;
        }
        var maps = Path.Combine(root, "Maps"); Directory.CreateDirectory(maps);
        Save(maps, "M000.JPG", 48, 96, ImageFormat.Jpeg); paths["MapDirectory"] = maps;
        var project = new ProjectProfile(root, "专用图片导入测试", ProjectVersion.Ccz65, false, paths, []);
        var service = new ImageCatalogService();
        return new(root, project, paths, service, service.BuildCatalog(project));
    }

    private static byte[] Pmap(int width, int height, byte seed)
    {
        var bytes = Enumerable.Repeat(seed, 16 + width * height).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), (ushort)height);
        return bytes;
    }

    private static void VerifyWeatherCatalog(TestFixture fixture, int expectedCount, int logicalId)
    {
        var service = new ImageCatalogService();
        var resource = service.BuildCatalog(fixture.Project).Single(item => item.Kind == ResourceKind.WeatherSpecial);
        var assets = service.ReadAssets(fixture.Project, resource);
        Require(resource.CanMutate && assets.Count == expectedCount, "提交后的天气目录不可复读或数量错误。");
        var frames = service.DecodeFrames(fixture.Project, assets.Single(asset => asset.LogicalId == logicalId));
        try
        {
            Require(frames.Count == 4 && frames.All(frame => frame.Bitmap.Size == new Size(216, 50)), "提交后的天气公开codec没有读出四个216×50帧。");
            Require(frames.All(frame => frame.Bitmap.GetPixel(30, 20).ToArgb() == Color.FromArgb(255, 100, 0, 0).ToArgb()), "提交后的天气公开codec颜色不正确。");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void VerifyPmapCatalog(TestFixture fixture, int expectedCount, int logicalId, Size size)
    {
        var service = new ImageCatalogService();
        var resource = service.BuildCatalog(fixture.Project).Single(item => item.Kind == ResourceKind.PmapSpecial);
        var assets = service.ReadAssets(fixture.Project, resource);
        Require(resource.CanMutate && assets.Count == expectedCount, "提交后的Pmap目录不可复读或数量错误。");
        var frames = service.DecodeFrames(fixture.Project, assets.Single(asset => asset.LogicalId == logicalId));
        try
        {
            Require(frames.Count == 1 && frames[0].Bitmap.Size == size && frames[0].Descriptor.Width == size.Width && frames[0].Descriptor.Height == size.Height,
                "提交后的Pmap公开codec尺寸不正确。");
            Require(frames[0].Bitmap.GetPixel(1, 1).ToArgb() == Color.FromArgb(255, 100, 0, 0).ToArgb(), "提交后的Pmap公开codec颜色不正确。");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void VerifyMapCatalog(TestFixture fixture)
    {
        var service = new ImageCatalogService();
        var resource = service.BuildCatalog(fixture.Project).Single(item => item.Kind == ResourceKind.MapPair);
        var frames = service.DecodeFrames(fixture.Project, service.ReadAssets(fixture.Project, resource).Single());
        try { Require(frames.Count == 1 && frames[0].Bitmap.Size == new Size(48, 96), "提交后地图公开codec画布变化。"); }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CommitCycle(TestFixture fixture, MutationPlan plan, Action verifyPublicRead)
    {
        var before = plan.Files.ToDictionary(file => file.TargetPath, file => File.ReadAllBytes(file.TargetPath), StringComparer.OrdinalIgnoreCase);
        var history = new HistoryService(Path.Combine(fixture.Root, "history"));
        var committed = new ImageMutationService(history).Commit(fixture.Project, plan);
        Require(committed.Success && !string.IsNullOrWhiteSpace(committed.HistoryId), "专用导入实际提交失败：" + string.Join("；", committed.Diagnostics));
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(file.CandidateBytes), "提交后磁盘字节与预览候选不一致。");
        verifyPublicRead();
        Require(history.Undo(fixture.Project, committed.HistoryId!).Success, "专用导入撤回失败。");
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(before[file.TargetPath]), "撤回未逐字节恢复原始资源。");
        Require(history.Redo(fixture.Project, committed.HistoryId!).Success, "专用导入重做失败。");
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(file.CandidateBytes), "重做后磁盘字节与预览候选不一致。");
        verifyPublicRead();
        Require(history.Undo(fixture.Project, committed.HistoryId!).Success, "专用导入最终恢复失败。");
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(before[file.TargetPath]), "最终撤回未恢复测试前资源。");
    }

    private static void RejectStalePlan(TestFixture fixture, MutationPlan plan, string failure)
    {
        var before = plan.Files.ToDictionary(file => file.TargetPath, file => File.ReadAllBytes(file.TargetPath), StringComparer.OrdinalIgnoreCase);
        var history = new HistoryService(Path.Combine(fixture.Root, "history"));
        var historyCount = history.List(fixture.Project).Count;
        var result = new ImageMutationService(history).Commit(fixture.Project, plan);
        Require(!result.Success && result.WrittenPaths.Count == 0, failure);
        foreach (var file in plan.Files) Require(File.ReadAllBytes(file.TargetPath).SequenceEqual(before[file.TargetPath]), "过期计划失败后资源仍被写入。");
        Require(history.List(fixture.Project).Count == historyCount, "过期计划失败仍生成了历史提交。");
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize + 4;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = E5Archive.IndexOffset + index * E5Archive.EntrySize;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(entry, 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(entry + 4, 4), payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(entry + 8, 4), offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }

    private static PreparedImageImport Prepare(TestFixture fixture, ImageResourceDescriptor resource, ImageImportOperation operation,
        params ImageImportAssignment[] assignments)
        => new ImageImportPreparationService().PrepareAsync(new(fixture.Project, resource, operation, assignments)).GetAwaiter().GetResult();
    private static E5Archive Candidate(PreparedImageImport result, string name) => Candidate(result.Plan!, name);
    private static E5Archive Candidate(MutationPlan plan, string name)
    {
        var file = plan.Files.Single(item => Path.GetFileName(item.TargetPath).Equals(name, StringComparison.OrdinalIgnoreCase));
        return E5Archive.Parse(file.TargetPath, file.CandidateBytes);
    }
    private static string Save(string root, string name, int width, int height, ImageFormat format)
    {
        var path = Path.Combine(root, name);
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.FromArgb(255, 100, 0, 0));
        image.Save(path, format); return path;
    }
    private static string Errors(PreparedImageImport result) => string.Join("；", result.Rows.Select(row => row.Error).Where(error => error != null));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record TestFixture(string Root, ProjectProfile Project, Dictionary<string, string> Paths,
        ImageCatalogService Service, IReadOnlyList<ImageResourceDescriptor> Catalog);
}
