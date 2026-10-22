using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DataManager.App;
using DataManager.Images;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationBootstrap.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            var report = new Dictionary<string, object?> { ["Label"] = args[0], ["Timestamp"] = DateTimeOffset.Now,
                ["Source"] = Path.GetFullPath(args[1]), ["Picker"] = Picker(), ["Frames"] = Frames(args[1]),
                ["Lifecycle"] = Lifecycle() };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
            File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static object Lifecycle()
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        using var combo = new SearchableIdComboBox(); form.Controls.Add(combo); form.Show(); Application.DoEvents();
        var request = typeof(SearchableIdComboBox).GetMethod("RequestThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<int, Bitmap?>)typeof(SearchableIdComboBox).GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(combo)!;
        var pending = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        combo.SetPreviewLoader((_, token) => { oldToken = token; return pending.Task; });
        request.Invoke(combo, [1]);
        combo.SetPreviewLoader((_, _) => Task.FromResult<Bitmap?>(null));
        using var stale = new Bitmap(48, 48); pending.SetResult(stale);
        PumpUntil(() => IsReleased(stale));
        Assert(oldToken.IsCancellationRequested && cache.Count == 0, "旧加载器晚到结果污染新缓存。");
        for (var id = 0; id < 1024; id++) request.Invoke(combo, [id]);
        var negativeCount = cache.Count;
        combo.SetPreviewLoader((_, _) => Task.FromResult<Bitmap?>(new Bitmap(512, 512)));
        for (var id = 0; id < 20; id++) request.Invoke(combo, [id]);
        var byteBudgetCount = cache.Count;
        var byteBudgetBytes = cache.Values.Where(image => image != null).Sum(image => (long)image!.Width * image.Height * 4);
        if (HasBoundedPicker()) Assert(negativeCount <= 96 && byteBudgetCount == 4 && byteBudgetBytes == 4L * 1024 * 1024,
            "空图缓存或字节预算无效。");
        var pendingClose = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        combo.SetPreviewLoader((_, _) => pendingClose.Task); request.Invoke(combo, [1]);
        combo.Dispose(); using var late = new Bitmap(48, 48); pendingClose.SetResult(late);
        PumpUntil(() => IsReleased(late));
        form.Close();
        return new { NegativeCacheCount = negativeCount, ByteBudgetCount = byteBudgetCount, ByteBudgetBytes = byteBudgetBytes,
            LoaderCancellationDisposesLateResult = true, ClosingDisposesLateResult = true };
    }

    private static object Picker()
    {
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
        using var combo = new SearchableIdComboBox(); form.Controls.Add(combo); form.Show(); Application.DoEvents();
        var request = typeof(SearchableIdComboBox).GetMethod("RequestThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cacheField = typeof(SearchableIdComboBox).GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<int, Bitmap?>)cacheField.GetValue(combo)!;
        var loads = 0;
        Bitmap Icon(int id)
        {
            loads++; var bitmap = new Bitmap(48, 48, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.FromArgb(255, id % 256, id / 256, 73));
            return bitmap;
        }
        combo.SetPreviewLoader((id, _) => Task.FromResult<Bitmap?>(Icon(id)));
        Collect(); var process = Process.GetCurrentProcess(); process.Refresh(); var memoryBefore = process.PrivateMemorySize64;
        var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        Bitmap? first = null;
        for (var id = 0; id < 1024; id++) { request.Invoke(combo, [id]); if (id == 0) first = cache[0]; }
        clock.Stop(); var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var retainedCount = cache.Count;
        var retainedSurfaceBytes = cache.Values.Where(image => image != null).Sum(image => (long)image!.Width * image.Height * 4);
        if (HasBoundedPicker()) Assert(retainedCount <= 96 && retainedSurfaceBytes <= 4L * 1024 * 1024, "缩略图缓存超过预算。");
        Collect(); process.Refresh(); var privateDelta = process.PrivateMemorySize64 - memoryBefore;
        var firstReleased = IsReleased(first!);
        var recent = cache[1023]; request.Invoke(combo, [1023]);
        Assert(ReferenceEquals(recent, cache[1023]) && loads == 1024, "最近缩略图命中仍解码。");
        request.Invoke(combo, [0]);
        Assert(cache[0]!.GetPixel(1, 1).ToArgb() == Color.FromArgb(255, 0, 0, 73).ToArgb(), "被淘汰缩略图重载像素改变。");
        var reloadedFirst = loads == 1025;
        var owned = cache.Values.Where(image => image != null).Cast<Bitmap>().ToArray();
        combo.SetPreviewLoader(null);
        Assert(cache.Count == 0 && owned.All(IsReleased), "更换解码器未释放全部缩略图。");
        form.Close();
        return new { Requests = 1024, IconWidth = 48, IconHeight = 48, RetainedCount = retainedCount, RetainedSurfaceBytes = retainedSurfaceBytes,
            ElapsedMs = clock.Elapsed.TotalMilliseconds, AllocatedBytes = allocatedBytes, PrivateBytesDelta = privateDelta,
            FirstEvictedAndDisposed = firstReleased, RecentCacheHit = true, EvictedReloadMatchesPixels = true, ReloadedFirst = reloadedFirst,
            LoaderReplacementDisposesAll = true };
    }

    private static object Frames(string root)
    {
        using var adapter = new ResourceCatalogAdapter();
        var paths = adapter.Scan(root).Resources.SelectMany(resource => resource.Paths).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = paths.ToDictionary(path => path, HashFile, StringComparer.OrdinalIgnoreCase);
        const int logicalId = 361;
        var warm = adapter.DecodeFrames(root, "Pmapobj.e5", logicalId);
        Assert(warm.Count > 0, "没有R形象基准。");
        var hashes = warm.Select(HashBitmap).ToArray(); var surfaceBytes = warm.Sum(frame => (long)frame.Width * frame.Height * 4);
        warm[0].SetPixel(0, 0, Color.Lime);
        foreach (var image in warm) image.Dispose();
        var measurements = new List<double>(); Collect(); long allocatedBytes = 0;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var allocationBefore = GC.GetTotalAllocatedBytes(true);
            var clock = Stopwatch.StartNew(); var frames = adapter.DecodeFrames(root, "Pmapobj.e5", logicalId); clock.Stop();
            allocatedBytes += GC.GetTotalAllocatedBytes(true) - allocationBefore;
            measurements.Add(clock.Elapsed.TotalMilliseconds);
            try { Assert(frames.Select(HashBitmap).SequenceEqual(hashes), "解码像素/颜色或帧顺序改变。"); }
            finally { foreach (var image in frames) image.Dispose(); }
        }
        Assert(before.All(pair => HashFile(pair.Key) == pair.Value), "只读基准改变了源资源。");
        measurements.Sort();
        return new { Resource = "Pmapobj.e5", LogicalId = logicalId, Samples = measurements.Count, FrameCount = hashes.Length, FrameSurfaceBytes = surfaceBytes,
            P50Ms = measurements[9], P95Ms = measurements[18], MaxMs = measurements[^1], AllocatedBytes = allocatedBytes,
            PixelHashes = hashes, CallerOwnershipIsolated = true, SourceHashesUnchanged = true };
    }

    private static string HashBitmap(Bitmap bitmap)
    {
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(bits.Stride) * bits.Height]; Marshal.Copy(bits.Scan0, bytes, 0, bytes.Length);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { bitmap.UnlockBits(bits); }
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool HasBoundedPicker() => typeof(SearchableIdComboBox).GetField("MaximumThumbnailCount", BindingFlags.Static | BindingFlags.NonPublic) != null;
    private static void PumpUntil(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(1); }
        Assert(ready(), "后台缩略图取消或关闭后没有释放。");
    }
    private static bool IsReleased(Bitmap bitmap) { try { _ = bitmap.Width; return false; } catch (ArgumentException) { return true; } }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
