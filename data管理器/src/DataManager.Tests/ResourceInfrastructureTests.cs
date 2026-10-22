using System.Buffers.Binary;
using System.Drawing.Imaging;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class ResourceInfrastructureTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerResource_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Item.e5"), BuildArchive([Png(Color.Red), Png(Color.Red)]));
            File.WriteAllBytes(Path.Combine(root, "Mtem.e5"), BuildArchive([Png(Color.Red)]));
            File.WriteAllBytes(Path.Combine(root, "Hitarea.e5"), BuildArchive(Enumerable.Range(0, 33)
                .Select(index => Png(index == 0 ? Color.Red : index == 1 ? Color.Green : Color.Blue)).ToArray()));
            File.WriteAllBytes(Path.Combine(root, "Effarea.e5"), BuildArchive(Enumerable.Repeat(Png(Color.Yellow), 12).ToArray()));
            File.WriteAllBytes(Path.Combine(root, "Logo.e5"), BuildArchive([Png(Color.Purple)]));
            using var adapter = new ResourceCatalogAdapter();
            var first = adapter.Scan(root);
            TestSupport.Assert(ReferenceEquals(first, adapter.Scan(root)), "未变化的资源目录没有复用项目快照");
            var assets = adapter.ReadAssets(root, "Hitarea.e5");
            TestSupport.Assert(ReferenceEquals(assets, adapter.ReadAssets(root, "Hitarea.e5")), "逻辑资源索引没有复用缓存");
            AssertRange(adapter, root, true, 0, 1, 33);
            AssertRange(adapter, root, true, 32, 33, 33);
            AssertRange(adapter, root, false, 0, 1, 12);
            AssertRange(adapter, root, false, 11, 12, 12);
            foreach (var (attack, value) in new[] { (true, -1), (true, 33), (false, 12), (true, 255), (true, int.MaxValue) })
            {
                var range = adapter.ResolveRangePreview(root, attack, value);
                TestSupport.Assert(!range.CanPreview, "越界范围字段错误地关联到图片");
                using var absent = adapter.DecodeRangeFirstFrameAsync(root, attack, value, new Size(16, 16)).GetAwaiter().GetResult();
                TestSupport.Assert(absent == null, "越界范围应显示缺图占位");
            }
            using (var image = adapter.DecodeRangeFirstFrameAsync(root, true, 0, new Size(16, 16)).GetAwaiter().GetResult())
                TestSupport.Assert(image?.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "范围字段0没有读取图号1");
            using (var image = adapter.DecodeRangeFirstFrameAsync(root, true, 1, new Size(16, 16)).GetAwaiter().GetResult())
                TestSupport.Assert(image?.GetPixel(0, 0).ToArgb() == Color.Green.ToArgb(), "范围字段1没有读取图号2");
            using (var image = adapter.DecodeRangeFirstFrameAsync(root, true, 0, new Size(16, 16)).GetAwaiter().GetResult())
                TestSupport.Assert(image?.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "缩略图缓存向调用者泄露了可共同释放的实例");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    adapter.DecodeFirstFrameAsync(root, "Hitarea.e5", 1, new Size(16, 16), cancellation.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("已取消的预览请求仍然完成");
                }
                catch (OperationCanceledException) { }
            }
            TestDisposeDuringDecode(root);
            TestInvalidationWithCompletedDecode(root);
            TestInvalidationDuringDecode(root);
            TestCanceledThumbnailWaiterRetiresDecode(root);
            using (var panel = new IntegratedResourcePanel(adapter))
            {
                using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                    ShowInTaskbar = false, Size = new Size(1100, 740) };
                host.Controls.Add(panel); host.Show();
                panel.LoadProject(root);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!panel.LoadingTask.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(15))
                { Application.DoEvents(); Thread.Sleep(5); }
                TestSupport.Assert(panel.LoadingTask.IsCompleted, "资源目录后台加载超时");
                panel.LoadingTask.GetAwaiter().GetResult();
                TestSupport.Assert(panel.ProjectCategories.Any(category => category.Key == "Resource:Logo.e5" && category.Name == "背景插图（Logo.e5）"),
                    "其他图片没有按中文名称展开到项目分类");
                TestSupport.Assert(!panel.ProjectCategories.Any(category => category.Key is "History" or "Other" or "Animation"),
                    "资源列表仍然含重复历史或合并大类");
                foreach (var category in new[] { "AttackRange", "PierceRange", "Animation", "Other", "History" }) panel.ShowCategory(category);
                host.Close();
            }
            TestResourceHistory(root, adapter);
            var e5 = Path.Combine(root, "E5");
            Directory.CreateDirectory(e5);
            File.Copy(Path.Combine(root, "Logo.e5"), Path.Combine(e5, "Logo.e5"));
            File.WriteAllBytes(Path.Combine(root, "Foreign.e5"), BuildArchive([Png(Color.White)]));
            File.Copy(Path.Combine(root, "Foreign.e5"), Path.Combine(e5, "Foreign.e5"));
            adapter.Invalidate(root);
            var collision = adapter.Scan(root);
            var logo = collision.Resources.Single(resource => resource.Key == "Logo.e5");
            TestSupport.Assert(!logo.CanPreview && !logo.CanMutate && logo.Status.Contains("同名资源", StringComparison.Ordinal),
                "根目录/E5 同名资源没有阻止歧义读写");
            TestSupport.Assert(collision.Resources.Count(resource => resource.Key == "Discovered:Foreign.e5") == 1,
                "同名自动发现资源产生了重复键");
            try
            {
                adapter.Append(root, "Logo.e5", [Path.Combine(root, "素材.png")]);
                throw new InvalidOperationException("同名冲突资源仍然允许写入");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("资源当前不可修改", StringComparison.Ordinal)) { }

            // Meff.e5 is a strategy-animation archive whose canonical storage
            // is E5/Meff.e5.  A byte-identical root copy must not disable the
            // read path or make the data manager choose the wrong archive.
            var meffBytes = BuildArchive([Png(Color.Cyan)]);
            File.WriteAllBytes(Path.Combine(root, "Meff.e5"), meffBytes);
            File.WriteAllBytes(Path.Combine(e5, "Meff.e5"), meffBytes);
            adapter.Invalidate(root);
            var meff = adapter.Scan(root).Resources.Single(resource => resource.Key == "Meff.e5");
            TestSupport.Assert(meff.CanPreview && meff.EntryCount == 1 && meff.Paths.Count == 1 &&
                meff.Paths[0].StartsWith(Path.Combine(root, "E5"), StringComparison.OrdinalIgnoreCase) &&
                !meff.CanMutate && meff.Status.Contains("规范路径", StringComparison.Ordinal),
                "Meff 根目录同内容副本没有合并到 E5 规范路径，只读预览仍被错误阻断");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("DataManagerResource_", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录清理范围无效。");
            Directory.Delete(resolved, true);
        }
    }

    private static void TestResourceHistory(string root, ResourceCatalogAdapter adapter)
    {
        var path = Path.Combine(root, "Logo.e5");
        var before = HistoryService.HashFile(path);
        var source = Path.Combine(root, "素材.png");
        File.WriteAllBytes(source, Png(Color.Orange));
        var result = adapter.Append(root, "Logo.e5", [source]);
        TestSupport.Assert(result.Success && result.HistoryId != null, "资源追加没有保留原历史事务");
        TestSupport.Assert(adapter.Scan(root).Resources.Single(resource => resource.Key == "Logo.e5").EntryCount == 2 &&
                           adapter.ReadAssets(root, "Logo.e5").Count == 2, "资源写入后目录或索引缓存没有失效");
        var after = HistoryService.HashFile(path);
        var history = new ResourceHistoryAdapter(adapter);
        TestSupport.Assert(history.List(root).Single().CanUndo, "统一资源历史没有显示可撤回的原记录");
        TestSupport.Assert(history.Undo(root, result.HistoryId!).Success && HistoryService.HashFile(path) == before,
            "统一资源历史撤回没有恢复原文件");
        var entries = history.List(root);
        TestSupport.Assert(entries.Single(entry => !entry.IsTransition).CanRedo &&
                           entries.Where(entry => entry.IsTransition).All(entry => !entry.CanUndo && !entry.CanRedo),
            "历史转换记录不应当成为可撤回的原事务");
        TestSupport.Assert(history.Redo(root, result.HistoryId!).Success && HistoryService.HashFile(path) == after,
            "统一资源历史重做没有恢复写后文件");
        var directory = Path.Combine(root, ".data管理器形象历史");
        TestSupport.Assert(Directory.EnumerateFiles(directory, "history.jsonl", SearchOption.AllDirectories).Count() == 1,
            "统一历史不应复制或迁移原资源 journal");
        File.AppendAllText(path, "外部修改");
        TestSupport.Assert(!history.Undo(root, result.HistoryId!).Success, "资源历史没有检查外部文件 SHA 冲突");
    }

    private static void TestDisposeDuringDecode(string root)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var profile = new ProjectProfileService().Detect(root);
        var descriptor = new FrameDescriptor("Hitarea.e5", Path.Combine(root, "Hitarea.e5"), 1, 0, 1, "image", "none", 16, 16);
        var asset = new LogicalImageAsset("Hitarea.e5", 1, "攻击范围", ImageCategory.Range, ResourceKind.E5Indexed,
            [descriptor], [], false, false, "");
        var service = new ImagePreviewService();
        try
        {
            var pending = service.GetThumbnailAsync(profile, new BlockingCatalog(entered, release), asset, descriptor, new Size(16, 16), true);
            TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "异步预览未进入后台解码");
            // Dispose must return while the decoder is still running. Waiting
            // here would deadlock a UI shutdown that owns the release signal.
            service.Dispose();
            release.Set();
            using var result = pending.GetAwaiter().GetResult();
            TestSupport.Assert(result == null, "已关闭的预览服务仍回填缩略图");
        }
        finally { release.Set(); service.Dispose(); }
    }

    private static void TestInvalidationWithCompletedDecode(string root)
    {
        var profile = new ProjectProfileService().Detect(root);
        var descriptor = new FrameDescriptor("Hitarea.e5", Path.Combine(root, "Hitarea.e5"), 1, 0, 1, "image", "none", 16, 16);
        var asset = new LogicalImageAsset("Hitarea.e5", 1, "攻击范围", ImageCategory.Range, ResourceKind.E5Indexed,
            [descriptor], [], false, false, "");
        using var cache = new RecordingThumbnailCache();
        using var service = new ImagePreviewService(cache);
        using (var first = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Red), asset, descriptor, new Size(16, 16), true).GetAwaiter().GetResult())
            TestSupport.Assert(first != null, "缓存竞态回归没有完成首次解码");
        // Recreate the precise scheduling window after Populate completes but
        // before its caller's finally removes the completed in-flight task.
        var inflight = (System.Collections.Concurrent.ConcurrentDictionary<string, Task>)typeof(ImagePreviewService)
            .GetField("_inflight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        inflight[cache.LastStoredKey!] = Task.CompletedTask;
        service.InvalidatePaths([descriptor.SourcePath]);
        using var refreshed = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Blue), asset, descriptor, new Size(16, 16), true).GetAwaiter().GetResult();
        TestSupport.Assert(refreshed?.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(),
            "缓存失效后复用了已经完成的旧解码任务，导致新请求返回空图或旧图");
    }

    private static void TestInvalidationDuringDecode(string root)
    {
        var profile = new ProjectProfileService().Detect(root);
        var descriptor = new FrameDescriptor("Hitarea.e5", Path.Combine(root, "Hitarea.e5"), 1, 0, 1, "image", "none", 16, 16);
        var asset = new LogicalImageAsset("Hitarea.e5", 1, "攻击范围", ImageCategory.Range, ResourceKind.E5Indexed,
            [descriptor], [], false, false, "");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var service = new ImagePreviewService();
        var old = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Red, entered, release), asset, descriptor, new Size(16, 16), true);
        try
        {
            TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "旧缩略图请求未进入可控后台解码");
            service.InvalidatePaths([descriptor.SourcePath]);
            var fresh = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Blue), asset, descriptor, new Size(16, 16), true);
            TestSupport.Assert(fresh.Wait(TimeSpan.FromSeconds(5)), "缓存失效后的新请求仍在等待旧一代解码");
            using (var refreshed = fresh.GetAwaiter().GetResult())
                TestSupport.Assert(refreshed?.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(), "新一代解码没有返回新图片");
            release.Set();
            old.GetAwaiter().GetResult();
            using var cached = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Yellow), asset, descriptor, new Size(16, 16), true).GetAwaiter().GetResult();
            TestSupport.Assert(cached?.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(), "迟到的旧解码覆盖了新一代缓存");
        }
        finally { release.Set(); using var oldResult = old.GetAwaiter().GetResult(); }
    }

    private static void TestCanceledThumbnailWaiterRetiresDecode(string root)
    {
        var profile = new ProjectProfileService().Detect(root);
        var descriptor = new FrameDescriptor("Hitarea.e5", Path.Combine(root, "Hitarea.e5"), 1, 0, 1, "image", "none", 16, 16);
        var asset = new LogicalImageAsset("Hitarea.e5", 1, "攻击范围", ImageCategory.Range, ResourceKind.E5Indexed,
            [descriptor], [], false, false, "");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var service = new ImagePreviewService();
        var pending = service.GetThumbnailAsync(profile, new SolidCatalog(Color.Red, entered, release), asset, descriptor,
            new Size(16, 16), true, cancellation.Token);
        var inflight = (System.Collections.Concurrent.ConcurrentDictionary<string, Task>)typeof(ImagePreviewService)
            .GetField("_inflight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        try
        {
            TestSupport.Assert(entered.Wait(TimeSpan.FromSeconds(5)), "取消请求回归未进入可控后台解码");
            TestSupport.Assert(SpinWait.SpinUntil(() => inflight.Count == 1, TimeSpan.FromSeconds(5)),
                "可控后台解码没有登记共享任务");
            var population = inflight.Values.Single();
            cancellation.Cancel();
            try
            {
                using var unexpected = pending.GetAwaiter().GetResult();
                throw new InvalidOperationException("已取消的缩略图等待者仍返回图片");
            }
            catch (OperationCanceledException) { }
            TestSupport.Assert(!population.IsCompleted && inflight.Count == 1, "等待者取消时后台共享解码意外结束");
            release.Set();
            population.GetAwaiter().GetResult();
            TestSupport.Assert(SpinWait.SpinUntil(() => inflight.IsEmpty, TimeSpan.FromSeconds(5)),
                "所有等待者取消后已完成的缩略图解码永久残留在inflight中");
        }
        finally { release.Set(); }
    }

    private sealed class RecordingThumbnailCache : IThumbnailCache
    {
        private readonly ThumbnailCache _inner = new();
        public string? LastStoredKey { get; private set; }
        public Bitmap? TryGetClone(string key) => _inner.TryGetClone(key);
        public void Store(string key, Bitmap bitmap, string sourcePath)
        { _inner.Store(key, bitmap, sourcePath); LastStoredKey = key; }
        public void InvalidatePath(string sourcePath) => _inner.InvalidatePath(sourcePath);
        public void Clear() => _inner.Clear();
        public void Dispose() => _inner.Dispose();
    }

    private sealed class SolidCatalog(Color color, ManualResetEventSlim? entered = null, ManualResetEventSlim? release = null) : IImageCatalogService
    {
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => [];
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        {
            entered?.Set();
            if (release != null && !release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("测试旧解码释放信号超时。");
            var bitmap = new Bitmap(16, 16);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
            return [new DecodedFrame(asset.Frames[0], bitmap)];
        }
    }

    private sealed class BlockingCatalog(ManualResetEventSlim entered, ManualResetEventSlim release) : IImageCatalogService
    {
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => [];
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource) => [];
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试解码释放信号超时。");
            return [new DecodedFrame(asset.Frames[0], new Bitmap(16, 16))];
        }
    }

    private static void AssertRange(ResourceCatalogAdapter adapter, string root, bool attack, int value, int imageNumber, int count)
    {
        var range = adapter.ResolveRangePreview(root, attack, value);
        TestSupport.Assert(range.CanPreview && range.FieldValue == value && range.PhysicalImageNumber == imageNumber &&
                           range.EntryCount == count, "范围字段到物理图号的映射不正确");
    }

    private static byte[] Png(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] BuildArchive(IReadOnlyList<byte[]> payloads)
    {
        var firstOffset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize;
        var bytes = new byte[firstOffset + payloads.Sum(payload => payload.Length)];
        var offset = firstOffset;
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * E5Archive.EntrySize, E5Archive.EntrySize);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset);
            offset += payloads[index].Length;
        }
        return bytes;
    }
}
