using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using DataManager.App;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;
using DataManager.Core.Binary;

namespace DataManager.Tests;

internal static class ImageResourceOptimizationTests
{
    public static void Run()
    {
        SchedulerAsync().GetAwaiter().GetResult();
        SchedulerTimingAsync().GetAwaiter().GetResult();
        PixelBudget();
        ArchiveCache();
        SharedWaiterCancellation();
        ProjectSwitches();
        PlaybackWindow();
        HiddenBusinessPreview();
        InactiveGalleryDoesNotRealize();
        HiddenGalleryLifecycle();
        ResourcePreviewCardTests.Run();
        Console.WriteLine("IMAGE_RESOURCE_OPTIMIZATION_TESTS_OK concurrency=2 queue=64 latest-owner nested-safe pixels=96MiB archive=128MiB window=8frames/16MiB");
    }

    private static async Task SchedulerAsync()
    {
        using var coordinator = new ImageResourceCoordinator();
        using var entered = new CountdownEvent(2); using var release = new ManualResetEventSlim();
        var active = 0; var maximum = 0;
        int Decode(CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            int before; do { before = maximum; } while (current > before && Interlocked.CompareExchange(ref maximum, current, before) != before);
            entered.Signal();
            try { release.Wait(token); return 1; } finally { Interlocked.Decrement(ref active); }
        }
        var first = coordinator.DecodeAsync(Decode, _ => { }); var second = coordinator.DecodeAsync(Decode, _ => { });
        TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "两个解码槽未启动");
        using var cancellation = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 80).Select(index => coordinator.DecodeAsync(_ => index, _ => { }, cancellation.Token)).ToArray();
        TestSupport.Assert(coordinator.Statistics.QueuedDecodes <= 64 && coordinator.Statistics.WaitingAdmissions == 0 &&
            coordinator.Statistics.CancelledDecodes >= 16 && coordinator.Statistics.ActiveDecodes == 2,
            "解码队列或并发超过容量");
        var owner = new object(); var old = coordinator.DecodeAsync(_ => 1, _ => { }, latestOwner: owner);
        var latest = coordinator.DecodeAsync(_ => 2, _ => { }, latestOwner: owner);
        cancellation.Cancel(); release.Set();
        await Task.WhenAll(first, second);
        try { await Task.WhenAll(waiting); } catch (OperationCanceledException) { }
        try { await old; throw new InvalidOperationException("旧owner请求未取消"); } catch (OperationCanceledException) { }
        TestSupport.Assert(await latest == 2 && maximum == 2, "最新owner请求未完成或解码并发失控");
        var nested = coordinator.DecodeAsync(_ => coordinator.DecodeAsync(_ => 42, _ => { }).GetAwaiter().GetResult(), _ => { });
        TestSupport.Assert(await nested.WaitAsync(TimeSpan.FromSeconds(5)) == 42, "遗留嵌套入口发生解码槽死锁");
        using var one = new ImageResourceCoordinator(concurrency: 1, queueCapacity: 3);
        using var blockerEntered = new ManualResetEventSlim(); using var unblock = new ManualResetEventSlim();
        var blocker = one.DecodeAsync(_ => { blockerEntered.Set(); unblock.Wait(); return 0; }, _ => { });
        TestSupport.Assert(blockerEntered.Wait(TimeSpan.FromSeconds(5)), "优先队列阻塞任务未开始");
        var order = new List<int>();
        var background = one.DecodeAsync(_ => { order.Add(3); return 3; }, _ => { }, priority: DecodeRequestPriority.Background);
        var thumb = one.DecodeAsync(_ => { order.Add(2); return 2; }, _ => { }, priority: DecodeRequestPriority.Thumbnail);
        var visible = one.DecodeAsync(_ => { order.Add(1); return 1; }, _ => { });
        unblock.Set(); await Task.WhenAll(blocker, background, thumb, visible);
        TestSupport.Assert(order.SequenceEqual([1, 2, 3]), "可见预览未优先于缩略图和后台任务");
    }

    private static async Task SchedulerTimingAsync()
    {
        using var coordinator = new ImageResourceCoordinator(concurrency: 2, queueCapacity: 4);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        using var cancelQueued = new CancellationTokenSource();
        int Block(CancellationToken token)
        {
            entered.Signal();
            release.Wait(token);
            return 1;
        }
        var first = coordinator.DecodeAsync(Block, _ => { });
        var second = coordinator.DecodeAsync(Block, _ => { });
        Task<int>? survivor = null, cancelled = null;
        var cancelledExecuted = 0;
        try
        {
            TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "计时验收的两个解码槽未启动");
            survivor = coordinator.DecodeAsync(_ => 42, _ => { });
            cancelled = coordinator.DecodeAsync(_ => { Interlocked.Increment(ref cancelledExecuted); return 0; },
                _ => { }, cancelQueued.Token);
            cancelQueued.Cancel();
            var blocked = coordinator.Statistics;
            TestSupport.Assert(blocked.ActiveDecodes == 2 && blocked.QueuedDecodes == 2 &&
                blocked.QueueWait.TotalSamples == 2 && blocked.DecodeExecution.TotalSamples == 0,
                "尚未执行的排队请求被计入执行耗时或没有保留两个在运行槽的等待样本");
            // Keep both slots occupied long enough to distinguish admitted
            // queue wait from the surviving request's very short delegate.
            await Task.Delay(30).ConfigureAwait(false);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second, survivor!).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await cancelled!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw new InvalidOperationException("排队时取消的请求仍然完成");
        }
        catch (OperationCanceledException) { }
        var measured = coordinator.Statistics;
        TestSupport.Assert(survivor!.Result == 42 && cancelledExecuted == 0 &&
            measured.QueueWait.TotalSamples == 3 && measured.QueueWait.RetainedSamples == 3 &&
            measured.DecodeExecution.TotalSamples == 3 && measured.DecodeExecution.RetainedSamples == 3,
            "实际执行请求没有一一对应耗时样本，或取消前未执行的请求产生了样本");
        TestSupport.Assert(measured.QueueWait.P95Ms >= 10 && measured.QueueWait.MaximumMs >= 10 &&
            measured.DecodeExecution.MaximumMs >= 10,
            "被阻塞的解码槽没有产生可区分的排队等待与执行耗时");

        var additional = ImageResourceCoordinator.TimingSampleCapacity + 17;
        for (var index = 0; index < additional; index++)
            await coordinator.DecodeAsync(_ => index, _ => { }).ConfigureAwait(false);
        var bounded = coordinator.Statistics;
        foreach (var timing in new[] { bounded.QueueWait, bounded.DecodeExecution })
            TestSupport.Assert(timing.TotalSamples == 3 + additional &&
                timing.RetainedSamples == ImageResourceCoordinator.TimingSampleCapacity &&
                timing.SampleCapacity == ImageResourceCoordinator.TimingSampleCapacity &&
                double.IsFinite(timing.P95Ms) && timing.P95Ms >= 0 && timing.P95Ms <= timing.MaximumMs,
                "耗时样本没有限制为固定窗口，或P95/最大值统计无效");
        TestSupport.Assert(bounded.QueueWait.MaximumMs >= measured.QueueWait.MaximumMs &&
            bounded.DecodeExecution.MaximumMs >= measured.DecodeExecution.MaximumMs,
            "耗时窗口覆盖旧样本后丢失了生命周期最大值");

        var nested = await coordinator.DecodeAsync(_ => coordinator.DecodeAsync(_ => 42, _ => { })
            .GetAwaiter().GetResult(), _ => { }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var final = coordinator.Statistics;
        TestSupport.Assert(nested == 42 && final.QueueWait.TotalSamples == bounded.QueueWait.TotalSamples + 1 &&
            final.DecodeExecution.TotalSamples == bounded.DecodeExecution.TotalSamples + 1,
            "嵌套遗留入口产生重复计时样本或解码槽死锁");
        Console.WriteLine($"DECODE_TIMING_BOUNDED_OK samples={final.QueueWait.TotalSamples} retained={final.QueueWait.RetainedSamples} capacity={final.QueueWait.SampleCapacity} blocked-wait>=10ms cancelled-before-start=excluded nested=one-sample");
    }

    private static void PixelBudget()
    {
        using var coordinator = new ImageResourceCoordinator(8 * 1024 * 1024);
        ImageResourceCoordinator.PixelLease? first = null, second = null, third = null;
        var firstReclaimed = false;
        first = coordinator.RegisterPixels(4 * 1024 * 1024, "one", () => { firstReclaimed = true; first!.Dispose(); });
        second = coordinator.RegisterPixels(4 * 1024 * 1024, "two", () => second!.Dispose());
        third = coordinator.RegisterPixels(4 * 1024 * 1024, "three", () => third!.Dispose());
        coordinator.TrimPixels();
        TestSupport.Assert(firstReclaimed && coordinator.Statistics.PixelBytes == 8 * 1024 * 1024,
            "共享像素预算没有回收最旧所有者");
        first.Dispose(); second.Dispose(); third.Dispose();
        TestSupport.Assert(coordinator.Statistics.PixelBytes == 0 && coordinator.Statistics.PixelSets == 0,
            "所有权租约重复释放或丢失计费");
    }

    private static void ArchiveCache()
    {
        using var owned = CreateWorkspace("ImageResourceCache");
        var root = owned.RootPath;
        try
        {
            E5Archive.ClearCache(); E5Archive.RetainRoot(root);
            var paths = new[] { "Pmapobj.e5", "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" }.Select(name => Path.Combine(root, name)).ToArray();
            const int payloadSize = 34 * 1024 * 1024;
            foreach (var path in paths) WriteArchive(path, payloadSize);
            var reads = E5Archive.CacheStatistics.FileReads;
            var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => E5Archive.Open(paths[0]))).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            TestSupport.Assert(tasks.All(task => ReferenceEquals(task.Result, tasks[0].Result)) && E5Archive.CacheStatistics.FileReads - reads == 1,
                "同源E5没有合并并发读取");
            foreach (var path in paths.Skip(1)) _ = E5Archive.Open(path);
            TestSupport.Assert(E5Archive.CacheStatistics.RetainedBytes <= E5Archive.MaximumCacheBytes && E5Archive.CacheStatistics.Archives == 3,
                "E5 LRU未遵守128MiB预算");
            var small = Path.Combine(root, "Item.e5"); WriteArchive(small, 1);
            var cached = E5Archive.Open(small); var stamp = File.GetLastWriteTimeUtc(small);
            using (var stream = new FileStream(small, FileMode.Open, FileAccess.Write)) { stream.Position = stream.Length - 1; stream.WriteByte(71); }
            File.SetLastWriteTimeUtc(small, stamp);
            TestSupport.Assert(E5Archive.Open(small).ReadDecoded(1)[0] == 0 && E5Archive.OpenFresh(small).ReadDecoded(1)[0] == 71,
                "OpenFresh未绕过同长度同时间戳的预览缓存");
            E5Archive.RetainRoot(Path.Combine(root, "different-project"));
            TestSupport.Assert(E5Archive.CacheStatistics.RetainedBytes == 0, "项目切换没有淘汰旧root的E5缓存");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
        finally { E5Archive.ClearCache(); }
    }

    private static void WriteArchive(string path, int payloadLength)
    {
        var offset = E5Archive.IndexOffset + E5Archive.EntrySize + 4;
        var header = new byte[offset];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(E5Archive.IndexOffset), payloadLength);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(E5Archive.IndexOffset + 4), payloadLength);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(E5Archive.IndexOffset + 8), offset);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write(header); stream.SetLength(offset + payloadLength);
    }

    private static void PlaybackWindow()
    {
        const int count = 200, width = 128, height = 128;
        var package = new byte[(count + 1) * 20 + width * height * 2];
        package[0] = count; package[1] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(4), 50);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), height);
        for (var step = 1; step <= count; step++) { package[step * 20] = (byte)(step % 2); package[step * 20 + 1] = 255; }
        package.AsSpan((count + 1) * 20, width * height).Fill(64);
        package.AsSpan((count + 1) * 20 + width * height).Fill(128);
        var project = new ProjectProfile(Path.GetTempPath(), "window", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        using var first = StrategyPreviewPlayback.OpenAsync(project, package).GetAwaiter().GetResult();
        TestSupport.Assert(first.TotalFrames == count && first.Frames.Count <= 8 && first.LoadWindowAsync != null && first.PixelBytes <= StrategyPreviewPlayback.MaximumWindowBytes,
            "较大策略动画未进入帧窗口模式");
        using var last = first.LoadWindowAsync!(195, CancellationToken.None).GetAwaiter().GetResult();
        var codec = new StrategyAnimationPackageCodec();
        for (var index = 0; index < last.Frames.Count; index++)
        {
            using var expected = codec.DecodePreviewFrame(project, package, last.Start + index);
            TestSupport.Assert(BmpPayloadService.ReadPixels(expected).SequenceEqual(BmpPayloadService.ReadPixels(last.Frames[index])),
                "窗口换帧改变透明像素、调色板或播放顺序");
        }
        TestSupport.Assert(last.Start == 195 && last.Frames.Count == 5, "末窗口没有按完整播放步截断");
    }

    private static void SharedWaiterCancellation()
    {
        using var owned = CreateWorkspace("SharedPreviewWaiters");
        var root = owned.RootPath;
        var path = Path.Combine(root, "shared.e5"); File.WriteAllBytes(path, [1]);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancelFirst = new CancellationTokenSource();
        var catalog = new BlockingPreviewCatalog(entered, release);
        var project = new ProjectProfile(root, "shared", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        var descriptor = new FrameDescriptor("shared", path, 1, 0, 1, "image", "none", 4, 4);
        var asset = new LogicalImageAsset("shared", 1, "shared", ImageCategory.Range, ResourceKind.E5Indexed, [descriptor], [], false, false, "");
        var service = ImagePreviewService.Shared;
        try
        {
            var beforeTiming = ImageResourceCoordinator.Shared.Statistics;
            var first = service.GetThumbnailAsync(project, catalog, asset, descriptor, new Size(4, 4), true, cancelFirst.Token);
            TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "共享缩略图未进入解码");
            var second = service.GetThumbnailAsync(project, catalog, asset, descriptor, new Size(4, 4), true);
            cancelFirst.Cancel();
            try { first.GetAwaiter().GetResult(); throw new InvalidOperationException("已取消等待者仍返回图像"); } catch (OperationCanceledException) { }
            release.Set();
            using var image = second.GetAwaiter().GetResult();
            TestSupport.Assert(image != null && catalog.Calls == 1, "取消一个等待者取消了其他共享消费者或重复解码");
            var afterTiming = ImageResourceCoordinator.Shared.Statistics;
            TestSupport.Assert(afterTiming.QueueWait.TotalSamples - beforeTiming.QueueWait.TotalSamples == 1 &&
                afterTiming.DecodeExecution.TotalSamples - beforeTiming.DecodeExecution.TotalSamples == 1,
                "取消一个共享等待者影响了实际解码计时，或同一共享解码产生重复样本");
            Console.WriteLine("SHARED_PREVIEW_CANCEL_ISOLATION_OK callers=2 decodes=1 queue-wait-samples=1 execution-samples=1");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
        finally { release.Set(); service.InvalidatePaths([path]); }
    }

    private sealed class BlockingPreviewCatalog(ManualResetEventSlim entered, ManualResetEventSlim release) : IImageCatalogService, IBoundedAnimationPreviewCatalog
    {
        public int Calls;
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => [];
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset) => [];
        public DecodedFrame DecodePreviewFrame(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor, CancellationToken token = default)
        { Interlocked.Increment(ref Calls); entered.Set(); release.Wait(token); return new(descriptor, new Bitmap(4, 4)); }
    }

    private static void ProjectSwitches()
    {
        using var owned = CreateWorkspace("ImageProjectSwitches");
        var temporary = owned.RootPath;
        var roots = new[] { Path.Combine(temporary, "red"), Path.Combine(temporary, "blue") };
        try
        {
            for (var project = 0; project < roots.Length; project++)
            {
                Directory.CreateDirectory(roots[project]);
                using var bitmap = new Bitmap(16, 16); using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(project == 0 ? Color.Red : Color.Blue);
                using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); var payload = stream.ToArray();
                var offset = E5Archive.IndexOffset + 2 * E5Archive.EntrySize + 4;
                var archive = new byte[offset + payload.Length * 2];
                for (var entry = 0; entry < 2; entry++)
                {
                    var at = E5Archive.IndexOffset + entry * E5Archive.EntrySize;
                    BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(at), payload.Length);
                    BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(at + 4), payload.Length);
                    BinaryPrimitives.WriteInt32BigEndian(archive.AsSpan(at + 8), offset + entry * payload.Length);
                    payload.CopyTo(archive, offset + entry * payload.Length);
                }
                File.WriteAllBytes(Path.Combine(roots[project], "Item.e5"), archive);
            }
            using var adapter = new ResourceCatalogAdapter();
            var catalogs = (System.Collections.IDictionary)typeof(ResourceCatalogAdapter).GetField("_catalogCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var root = roots[cycle % 2]; adapter.SetActiveRoot(root); adapter.Scan(root);
                using var image = adapter.DecodeFirstFrameAsync(root, "Item.e5", 0, new Size(16, 16)).GetAwaiter().GetResult();
                // Paired icon IDs are zero based, while physical E5 entries stay one based.
                TestSupport.Assert(image != null && image.GetPixel(0, 0).ToArgb() == (cycle % 2 == 0 ? Color.Red : Color.Blue).ToArgb(),
                    "项目切换后预览回填了旧项目像素");
                TestSupport.Assert(catalogs.Count == 1 && E5Archive.CacheStatistics.Archives <= 1 &&
                    ImageResourceCoordinator.Shared.Statistics.PixelBytes <= ImageResourceCoordinator.DefaultPixelBudget,
                    "20轮项目切换累积目录/档案/预览缓存");
            }
            Console.WriteLine("IMAGE_PROJECT_SWITCH_20_OK roots=2 catalog-cache=1 archive-cache<=1 source-colors-stable");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
        finally
        {
            foreach (var root in roots) ImagePreviewService.Shared.InvalidatePaths([Path.Combine(root, "Item.e5")]);
            E5Archive.ClearCache();
        }
    }

    private static OwnedTemporaryWorkspace CreateWorkspace(string name)
    {
        var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager." + name, Path.GetFullPath(AppContext.BaseDirectory));
        Console.WriteLine("IMAGE_OWNED_WORKSPACE " + owned.RootPath);
        return owned;
    }

    private static void RetainFailure(OwnedTemporaryWorkspace owned, Exception error)
    {
        File.WriteAllText(Path.Combine(owned.RootPath, "failure.txt"), error.ToString());
        Console.WriteLine("IMAGE_FAILED_WORKSPACE_RETAINED " + owned.RootPath);
    }

    private static void HiddenBusinessPreview()
    {
        using var tips = new ToolTip();
        using var form = new Form { Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Size = new Size(600, 400) };
        using var card = new ResourcePreviewCard(tips); form.Controls.Add(card); form.Show(); Application.DoEvents();
        var calls = 0;
        ResourcePreviewAnimation Load()
        { calls++; return new([new Bitmap(768, 768), new Bitmap(768, 768)], 2000, "large"); }
        card.Bind("large", "large", _ => Task.FromResult(Load()), true);
        WaitUntil(() => card.HasLoadedResource);
        var field = typeof(ResourcePreviewCard).GetField("_animation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (ResourcePreviewAnimation)field.GetValue(card)!;
        card.Visible = false;
        TestSupport.Assert(IsDisposed(first.Frames[0]) && field.GetValue(card) == null, "隐藏重动画未释放像素");
        card.Visible = true; WaitUntil(() => card.HasLoadedResource);
        TestSupport.Assert(calls == 2 && field.GetValue(card) != null, "重新显示重动画未重载");
    }

    private static void InactiveGalleryDoesNotRealize()
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(900, 650) };
        using var gallery = new VirtualImageGallery();
        form.Controls.Add(gallery); form.Show(); Application.DoEvents();
        var assets = GalleryLifecycleCatalog.Assets("inactive-gallery.e5");
        var created = 0;
        gallery.SetItems(assets, _ => { created++; return new Panel(); }, preserveSelection: false);
        WaitUntil(() => gallery.RealizedCount > 0);
        const int index = 36;
        gallery.SelectItem(assets[index].StablePhysicalId);
        gallery.ScrollToItem(index);
        var scroll = -gallery.AutoScrollPosition.Y;
        TestSupport.Assert(scroll > 0, "停用图库验收没有建立非零滚动位置");

        // A callback already queued while active must recheck the state when
        // delivered. Direct scroll, resize and refresh use the same barrier.
        gallery.RefreshViewport();
        gallery.SetActive(false);
        var stoppedAt = created;
        TestSupport.Assert(gallery.Visible && gallery.RealizedCount == 0, "显式停用仍可见图库没有回收全部卡片");
        var size = form.Size; form.Width++; form.Size = size;
        gallery.RefreshViewport(); gallery.ScrollToItem(index);
        DrainGalleryEvents();
        TestSupport.Assert(created == stoppedAt && gallery.RealizedCount == 0,
            "停用但仍可见时，已排队回调、Resize、RefreshViewport或ScrollToItem重新创建了卡片");
        TestSupport.Assert(gallery.ItemCount == assets.Count && gallery.SelectedItems.Single().StablePhysicalId == assets[index].StablePhysicalId &&
            gallery.FocusedAssetId == assets[index].StablePhysicalId && -gallery.AutoScrollPosition.Y == scroll,
            "停用图库丢失了逻辑项、选择、焦点或滚动位置");
        gallery.SetActive(true);
        WaitUntil(() => gallery.RealizedCount > 0);
        var resumedAt = created;
        gallery.SetActive(true); gallery.RefreshViewport(); DrainGalleryEvents();
        TestSupport.Assert(created == resumedAt && gallery.SelectedItems.Single().StablePhysicalId == assets[index].StablePhysicalId &&
            gallery.FocusedAssetId == assets[index].StablePhysicalId && -gallery.AutoScrollPosition.Y == scroll,
            "重复启用图库重建现有卡片或未恢复选择和滚动");
        Console.WriteLine("INACTIVE_GALLERY_BARRIER_OK active=false/visible=true queued/resize/refresh/scroll=no-realize selection/scroll=retained");
    }

    private static void HiddenGalleryLifecycle()
    {
        using var owned = CreateWorkspace("HiddenGalleryLifecycle");
        try
        {
            var path = Path.Combine(owned.RootPath, "lifecycle.e5"); File.WriteAllBytes(path, [1]);
            var catalog = new GalleryLifecycleCatalog(path);
            using var previews = new ControlledGalleryPreviews();
            var history = new HistoryService(Path.Combine(owned.RootPath, "history"));
            using var page = new ImageGridPage("隐藏图库生命周期", catalog, new ImageMutationService(history),
                new ReferenceAtlasService(), new RsImportService(), new BatchRenameService(history), previews,
                ImagePageCapabilities.Export | ImagePageCapabilities.BackgroundToggle);
            using var parent = new Panel { Dock = DockStyle.Fill };
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Size = new Size(1100, 740) };
            parent.Controls.Add(page); form.Controls.Add(parent); form.Show(); Application.DoEvents();
            var project = new ProjectProfile(owned.RootPath, "hidden-gallery", ProjectVersion.Ccz65, true,
                new Dictionary<string, string>(), []);
            page.LoadProject(project, catalog.BuildCatalog(project));
            var gallery = GalleryField<VirtualImageGallery>(page, "_gallery");
            WaitUntil(() => page.LoadingTask.IsCompleted && gallery.RealizedCount > 0 && previews.Calls.Count > 0);
            page.LoadingTask.GetAwaiter().GetResult();
            var selected = catalog.Items[36];
            gallery.SelectItem(selected.StablePhysicalId); gallery.ScrollToItem(36);
            WaitUntil(() => gallery.RealizedControls.Any(card => ((LogicalImageAsset)card.Tag!).StablePhysicalId == selected.StablePhysicalId) &&
                previews.Calls.Count(call => !call.Token.IsCancellationRequested) >= 3);
            var focused = gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).StablePhysicalId == selected.StablePhysicalId);
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(focused, [EventArgs.Empty]);
            var pending = previews.Calls.Where(call => !call.Token.IsCancellationRequested).ToArray();
            var hiddenLate = pending[0]; var resumedLate = pending[1];
            foreach (var call in pending.Skip(2)) call.Complete(Color.Blue);
            WaitUntil(() => GalleryPictures(gallery).Any(box => box.Image != null));
            var retained = GalleryPictures(gallery).Where(box => box.Image != null).Select(box => (Bitmap)box.Image!).ToArray();
            var oldCards = gallery.RealizedControls.ToArray();
            var scroll = -gallery.AutoScrollPosition.Y;
            TestSupport.Assert(scroll > 0, "父级隐藏验收没有建立非零滚动位置");

            // Match MainForm: hide the containing module, without calling any
            // gallery/page SetActive helper from the test.
            var visibilityEvents = new List<string>();
            page.VisibleChanged += (_, _) => visibilityEvents.Add($"page:{page.Visible}");
            gallery.VisibleChanged += (_, _) => visibilityEvents.Add($"gallery:{gallery.Visible}");
            parent.VisibleChanged += (_, _) => visibilityEvents.Add($"parent:{parent.Visible}");
            parent.Visible = false;
            TestSupport.Assert(!page.Visible && gallery.RealizedCount == 0 && oldCards.All(card => card.IsDisposed) &&
                hiddenLate.Token.IsCancellationRequested && resumedLate.Token.IsCancellationRequested,
                $"父级模块隐藏未回收图库卡片或取消待完成缩略图: pageVisible={page.Visible}, cards={gallery.RealizedCount}, disposed={oldCards.Count(card => card.IsDisposed)}/{oldCards.Length}, cancel={hiddenLate.Token.IsCancellationRequested}/{resumedLate.Token.IsCancellationRequested}, active={GalleryField<bool>(gallery, "_active")}, events={string.Join(',', visibilityEvents)}");
            TestSupport.Assert(retained.All(BitmapReleased) &&
                GalleryField<Dictionary<PictureBox, ImageResourceCoordinator.PixelLease>>(page, "_thumbnailLeases").Count == 0,
                "父级模块隐藏仍保留缩略图像素或计费租约");
            var hiddenCalls = previews.Calls.Count;
            gallery.RefreshViewport(); form.Width++; form.Width--; DrainGalleryEvents();
            hiddenLate.Complete(Color.Red);
            WaitUntil(() => hiddenLate.Result != null && BitmapReleased(hiddenLate.Result));
            TestSupport.Assert(gallery.RealizedCount == 0 && previews.Calls.Count == hiddenCalls,
                "隐藏模块的布局/迟到结果重新建卡或启动新缩略图请求");

            parent.Visible = true;
            WaitUntil(() => gallery.RealizedCount > 0 && previews.Calls.Count > hiddenCalls);
            var resumedCalls = previews.Calls.Where(call => !call.Token.IsCancellationRequested && !call.IsCompleted).ToArray();
            TestSupport.Assert(resumedCalls.Length > 0, "显示父级没有重新读取已回收的缩略图");
            foreach (var call in resumedCalls) call.Complete(Color.Lime);
            WaitUntil(() => GalleryPictures(gallery).Count > 0 && GalleryPictures(gallery).All(box => box.Image != null));
            resumedLate.Complete(Color.Red);
            WaitUntil(() => resumedLate.Result != null && BitmapReleased(resumedLate.Result));
            TestSupport.Assert(GalleryPictures(gallery).All(box => ((Bitmap)box.Image!).GetPixel(0, 0).ToArgb() == Color.Lime.ToArgb()) &&
                gallery.SelectedItems.Single().StablePhysicalId == selected.StablePhysicalId && gallery.FocusedAssetId == selected.StablePhysicalId &&
                -gallery.AutoScrollPosition.Y == scroll,
                "显示恢复后图片、选择、焦点或滚动未恢复，或旧请求覆盖了新缩略图");
            Console.WriteLine("HIDDEN_GALLERY_LIFECYCLE_OK parent-hide=cards0/pending-cancel/pixels-release late-hidden/late-resumed=disposed show=image/selection/focus/scroll-restored");
            owned.Complete();
        }
        catch (Exception error) { RetainFailure(owned, error); throw; }
    }

    private static T GalleryField<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static IReadOnlyList<PictureBox> GalleryPictures(Control parent)
        => GalleryDescendants(parent).OfType<PictureBox>().ToArray();

    private static IEnumerable<Control> GalleryDescendants(Control parent)
        => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(GalleryDescendants(child)));

    private static void DrainGalleryEvents()
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 80) { Application.DoEvents(); Thread.Sleep(1); }
    }

    private static bool BitmapReleased(Bitmap bitmap)
    {
        // Inspect managed ownership, avoiding a GDI call on an intentionally
        // disposed late result. This uses the same runtime probe as animation tests.
        var field = typeof(Image).GetField("_nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(Image).GetField("nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("此运行时没有可验证的 Image 原生句柄字段。");
        var value = field.GetValue(bitmap);
        var handle = value is IntPtr address ? address : value is Pointer
            ? (IntPtr)typeof(Pointer).GetMethod("GetPointerValue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!
            : throw new InvalidOperationException("Image 原生句柄字段的运行时表示未识别。");
        return handle == IntPtr.Zero;
    }

    private sealed class GalleryLifecycleCatalog(string path) : IImageCatalogService
    {
        public IReadOnlyList<LogicalImageAsset> Items { get; } = Assets(path);
        public static IReadOnlyList<LogicalImageAsset> Assets(string path) => Enumerable.Range(0, 80)
            .Select(id => new LogicalImageAsset("lifecycle", id, "缩略图" + id, ImageCategory.Range, ResourceKind.E5Indexed,
                [new FrameDescriptor("lifecycle", path, id, 0, 1, "image", "none", 4, 4)], [], false, false, "")).ToArray();
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project)
            => [new("lifecycle", "受控图库", ImageCategory.Range, ResourceKind.E5Indexed, [path], Items.Count, true, false, false, "", "")];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => Items;
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
            => throw new InvalidOperationException("生命周期测试不应绕过受控预览服务。");
    }

    private sealed class ControlledGalleryPreviews : IImagePreviewService
    {
        public System.Collections.Concurrent.ConcurrentQueue<GalleryThumbnailCall> Calls { get; } = new();
        public Task<Bitmap?> GetThumbnailAsync(ProjectProfile project, IImageCatalogService catalog, LogicalImageAsset asset,
            FrameDescriptor descriptor, Size size, bool showBackground, CancellationToken cancellationToken = default)
        {
            var call = new GalleryThumbnailCall(cancellationToken); Calls.Enqueue(call); return call.Task;
        }
        public void InvalidatePaths(IEnumerable<string> paths) { }
        public void Dispose() { foreach (var call in Calls) call.CancelRemaining(); }
    }

    private sealed class GalleryThumbnailCall(CancellationToken token)
    {
        private readonly TaskCompletionSource<Bitmap?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; } = token;
        public Task<Bitmap?> Task => _completion.Task;
        public bool IsCompleted => _completion.Task.IsCompleted;
        public Bitmap? Result { get; private set; }
        public void Complete(Color color)
        {
            var bitmap = new Bitmap(4, 4);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
            Result = bitmap;
            // Deliberately ignore cancellation: the caller must reject and
            // dispose a decoder that returns after its consumer went away.
            if (!_completion.TrySetResult(bitmap)) bitmap.Dispose();
        }
        public void CancelRemaining() => _completion.TrySetCanceled();
    }
    private static bool IsDisposed(Bitmap bitmap) { try { _ = bitmap.Width; return false; } catch (ArgumentException) { return true; } }
    private static void WaitUntil(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(1); } TestSupport.Assert(condition(), "预览生命周期等待超时"); }
}
