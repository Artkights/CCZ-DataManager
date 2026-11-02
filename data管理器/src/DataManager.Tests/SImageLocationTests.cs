using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using DataManager.App;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class SImageLocationTests
{
    public static void Run()
    {
        CheckCandidates();
        var root = Path.Combine(Path.GetTempPath(), "DataManagerSLocation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new Dictionary<string, string>();
            foreach (var (action, width, count) in new[] { ("mov", 48, 11), ("atk", 64, 12), ("spc", 48, 5) })
            {
                var name = $"Unit_{action}.e5";
                paths[name] = Path.Combine(root, name);
                using var strip = new Bitmap(width, width * count);
                using (var graphics = Graphics.FromImage(strip)) { graphics.Clear(Color.Magenta); graphics.FillRectangle(Brushes.Blue, 16, 16, 16, 24); }
                using var stream = new MemoryStream(); strip.Save(stream, ImageFormat.Png);
                File.WriteAllBytes(paths[name], Archive(Enumerable.Repeat(stream.ToArray(), 360).ToArray()));
            }
            var profile = new ProjectProfile(root, "定位测试", ProjectVersion.Ccz65, false, paths, []);
            var descriptor = new ImageResourceDescriptor("SComposite", "S形象", ImageCategory.CharacterS,
                ResourceKind.SComposite, paths.Values.ToArray(), 360, true, true, false, "", "");
            var codec = new SResourceCodec();
            var asset = codec.ReadAssets(profile, descriptor).Single(item => item.LogicalId == 242);
            CheckSearch(asset, codec.ReadJobAssets(profile, descriptor)[1]);
            CheckGuard(profile, asset);
            var originals = paths.ToDictionary(item => item.Key, item => File.ReadAllBytes(item.Value));
            CheckGallery(profile, descriptor);
            TestSupport.Assert(paths.All(item => File.ReadAllBytes(item.Value).SequenceEqual(originals[item.Key])),
                "定位或搜索改变了游戏资源。");
            Console.WriteLine("S_IMAGE_LOCATION_TESTS_OK");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckCandidates()
    {
        foreach (var (s, expected) in new[] { (1, new[] { 241, 242, 243 }), (32, new[] { 334, 335, 336 }),
                     (33, new[] { 337 }), (242, new[] { 546 }) })
            TestSupport.Assert(SImageLocationCandidates.Create(s, 0).Select(item => item.PhysicalImageNumber).SequenceEqual(expected),
                $"人物紧凑 S={s} 定位到错误物理图号。");
        foreach (var job in new[] { 0, 59, 60, 79 })
            TestSupport.Assert(SImageLocationCandidates.Create(0, job).Select(item => item.PhysicalImageNumber)
                .SequenceEqual(Enumerable.Range(job * 3 + 1, 3)), "默认 S 的阵营定位错误或重复计算转职。");
        Reject(() => SImageLocationCandidates.Create(0, 80), "越界兵种仍可定位。");
    }

    private static void CheckSearch(LogicalImageAsset personAsset, LogicalImageAsset jobAsset)
    {
        var index = new SImageSearchIndex([new(7, "曹操", 1, [241, 242, 243]), new(8, "默认人物", 0, [1, 2, 3])]);
        foreach (var query in new[] { "曹操", "人物:7", "S:1", "紧凑S=1", "Unit:242", "S_242", "曹操 Unit:242" })
            TestSupport.Assert(index.Matches(personAsset, query), "无法按人物/编号定位：" + query);
        foreach (var query in new[] { "人物:8", "S:242", "S_002", "Unit:2", "曹操 Unit:241" })
            TestSupport.Assert(!index.Matches(personAsset, query), "混淆了紧凑编号和物理编号：" + query);
        TestSupport.Assert(index.Matches(jobAsset, "友军") && index.Matches(jobAsset, "S:0") && index.Matches(jobAsset, "默认人物"),
            "兵种阵营或默认S人物引用不能搜索。");
        TestSupport.Assert(index.DescribeReferences(personAsset).Contains("当前草稿引用"), "人物引用未标明草稿语义。");
    }

    private static void CheckGuard(ProjectProfile profile, LogicalImageAsset asset)
    {
        SReferenceTargetGuard.Validate(profile, asset);
        Reject(() => SReferenceTargetGuard.Validate(profile, asset with { PhysicalImageNumber = 243 }), "接受了冲突物理身份。");
        Reject(() => SReferenceTargetGuard.Validate(profile, asset with { Frames = asset.Frames.Skip(1).ToArray() }), "接受了缺帧目标。");
        var folder = Path.Combine(profile.RootPath, "E5"); Directory.CreateDirectory(folder);
        var duplicate = Path.Combine(folder, "Unit_atk.e5"); File.Copy(profile.TryGetResource("Unit_atk.e5")!, duplicate);
        Reject(() => SReferenceTargetGuard.Validate(profile, asset), "根目录与E5重复目标未在预检拒绝。");
        File.Delete(duplicate);
        var spc = profile.TryGetResource("Unit_spc.e5")!;
        var bytes = File.ReadAllBytes(spc);
        var archive = E5Archive.OpenFresh(spc);
        File.WriteAllBytes(spc, archive.Rebuild(deleteImageNumbers: [360]));
        Reject(() => SReferenceTargetGuard.Validate(profile, asset), "三档案数量不一致未在预检拒绝。");
        File.WriteAllBytes(spc, bytes); E5Archive.Invalidate(spc);
    }

    private static void CheckGallery(ProjectProfile profile, ImageResourceDescriptor descriptor)
    {
        using var previews = new ImagePreviewService();
        var history = new HistoryService(Path.Combine(profile.RootPath, "history"));
        using var page = new ImageGridPage("S", new ImageCatalogService(), new ImageMutationService(history),
            new ReferenceAtlasService(), new RsImportService(), new BatchRenameService(history), previews, ImagePageCapabilities.CharacterS);
        page.SAssignmentReferencesProvider = () => [new(7, "曹操", 1, [241, 242, 243])];
        using var host = new Form { Size = new Size(750, 500), Location = new Point(-30000, -30000), ShowInTaskbar = false };
        host.Controls.Add(page);
        Exception? failure = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(() =>
        {
            try
            {
                page.LoadProject(profile, [descriptor]); Wait(page.LoadingTask);
                page.SetSearch("曹操");
                var gallery = (VirtualImageGallery)typeof(ImageGridPage).GetField("_gallery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                TestSupport.Assert(gallery.ItemCount == 3, "人物名未筛选到三转物理图。");
                var locate = page.LocateSPhysicalImageAsync(359); Wait(locate);
                TestSupport.Assert(locate.Result && gallery.SelectedItems.Single().LogicalId == 359 && gallery.FocusedAssetId == gallery.SelectedItems[0].StablePhysicalId,
                    "定位没有独占选择并高亮目标。");
                TestSupport.Assert(gallery.AutoScrollPosition.Y < 0 && gallery.RealizedControls.Any(card => card.Tag is LogicalImageAsset item && item.LogicalId == 359),
                    "定位未滚动并实现屏幕外目标卡片。");
                locate = page.LocateSPhysicalImageAsync(2); Wait(locate);
                TestSupport.Assert(locate.Result && gallery.SelectedItems.Single().SMode == SImageMode.Job && gallery.SelectedItems[0].LogicalId == 2,
                    "定位兵种未正确切换图库或混入将领物理号。");
            }
            catch (Exception ex) { failure = ex; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (failure != null) throw new InvalidOperationException("S定位界面回归失败。", failure);
    }

    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(5); }
        TestSupport.Assert(task.IsCompleted, "S定位异步加载超时。"); task.GetAwaiter().GetResult(); Application.DoEvents();
    }

    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize;
        var bytes = new byte[offset + payloads.Sum(item => item.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * E5Archive.EntrySize, E5Archive.EntrySize);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }
}
