using System.Drawing.Drawing2D;
using System.Collections.Concurrent;

namespace ImageManager.Core;

public sealed class ThumbnailCache : IThumbnailCache
{
    internal static ThumbnailCache Shared { get; } = new(512, ImageResourceCoordinator.DefaultPixelBudget);
    private readonly int _maximumCount;
    private readonly long _maximumBytes;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _currentBytes;

    public ThumbnailCache(int maximumCount = 256, long maximumBytes = 128L * 1024 * 1024)
    {
        if (maximumCount <= 0 || maximumBytes <= 0) throw new ArgumentOutOfRangeException();
        _maximumCount = maximumCount;
        _maximumBytes = maximumBytes;
    }

    public Bitmap? TryGetClone(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            entry.Lease?.Touch();
            // GDI+'s Bitmap copy zeroes the hidden RGB of fully transparent
            // pixels; clone raw ARGB so a cached preview matches a fresh decode.
            return BmpPayloadService.CopyArgb(entry.Bitmap);
        }
    }

    public void Store(string key, Bitmap bitmap, string sourcePath)
    {
        var copy = BmpPayloadService.CopyArgb(bitmap);
        var bytes = checked((long)copy.Width * copy.Height * 4);
        lock (_sync)
        {
            RemoveCore(key);
            var node = _lru.AddFirst(key);
            var entry = new CacheEntry(copy, Path.GetFullPath(sourcePath), bytes, node);
            _entries[key] = entry;
            entry.Lease = ImageResourceCoordinator.Shared.RegisterPixels(bytes, "thumbnail-cache", () =>
            { lock (_sync) if (_entries.TryGetValue(key, out var live) && ReferenceEquals(live, entry)) RemoveCore(key); });
            _currentBytes += bytes;
            while (_entries.Count > _maximumCount || _currentBytes > _maximumBytes)
            {
                var last = _lru.Last;
                if (last == null) break;
                RemoveCore(last.Value);
            }
        }
        ImageResourceCoordinator.Shared.TrimPixels();
    }

    public void InvalidatePath(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        lock (_sync)
        {
            foreach (var key in _entries.Where(pair => pair.Value.SourcePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key).ToArray()) RemoveCore(key);
        }
    }

    internal void RetainRoot(string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (_sync)
            foreach (var key in _entries.Where(pair => !pair.Value.SourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToArray()) RemoveCore(key);
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values) { entry.Lease?.Dispose(); entry.Bitmap.Dispose(); }
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    public void Dispose() => Clear();

    private void RemoveCore(string key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node);
        _currentBytes -= entry.EstimatedBytes;
        entry.Lease?.Dispose();
        entry.Bitmap.Dispose();
    }

    private sealed record CacheEntry(Bitmap Bitmap, string SourcePath, long EstimatedBytes, LinkedListNode<string> Node)
    { public ImageResourceCoordinator.PixelLease? Lease { get; set; } }
}

public sealed class ImagePreviewService : IImagePreviewService
{
    public static ImagePreviewService Shared { get; } = new();
    private readonly IThumbnailCache _cache;
    private readonly bool _ownsCache;
    private static long _scopeSequence;
    private readonly long _cacheScope = Interlocked.Increment(ref _scopeSequence);
    private readonly ConcurrentDictionary<string, SharedPreviewRequest> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifetimeGate = new();
    private readonly Dictionary<string, int> _starting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> PathRevisions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ImagePreviewService(IThumbnailCache? cache = null)
    { _cache = cache ?? ThumbnailCache.Shared; _ownsCache = cache != null; }

    public void RetainRoot(string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (_lifetimeGate)
        {
            if (_cache is ThumbnailCache thumbnails) thumbnails.RetainRoot(root);
        }
    }

    public async Task<Bitmap?> GetThumbnailAsync(ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(descriptor.SourcePath);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _starting[sourcePath] = _starting.GetValueOrDefault(sourcePath) + 1;
        }
        // Key construction checks source and palette dependencies. It must not
        // probe archives or hash a palette on the WinForms message thread.
        string sourceKey;
        try
        {
            sourceKey = await Task.Run(() => BuildKey(project, asset, descriptor, size, showBackground),
                cancellationToken).ConfigureAwait(false);
        }
        catch { lock (_lifetimeGate) ReleaseStarting(sourcePath); throw; }
        // External/test catalog implementations may assign different decoding semantics to identical descriptors.
        // Native catalogs share one codec contract; custom catalogs get an identity scope.
        if (catalog is not IBoundedAnimationPreviewCatalog)
            sourceKey += "|decoder:" + _cacheScope;
        string key;
        SharedPreviewRequest request;
        lock (_lifetimeGate)
        {
            try
            {
            // A request already in progress completes empty when its owner is
            // disposed; new requests still fail the check at method entry.
            if (_disposed) return null;
            // An invalidation starts a new generation even when file metadata
            // is unchanged. Completed or late old tasks cannot serve it.
            var revision = PathRevision(descriptor.SourcePath);
            key = revision + "|" + sourceKey;
            if (_cache.TryGetClone(key) is { } cached) return cached;
            request = _inflight.GetOrAdd(key, _ =>
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                return new SharedPreviewRequest(sourcePath, cancellation,
                    PopulateAsync(key, project, catalog, asset, descriptor, size, showBackground, revision, cancellation.Token));
            });
            request.Waiters++;
            _ = request.Task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (_lifetimeGate)
                {
                    request.Completed = true;
                    _inflight.TryRemove(new KeyValuePair<string, SharedPreviewRequest>(key, request));
                    if (request.Waiters == 0) request.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            finally { ReleaseStarting(sourcePath); }
        }
        try
        {
            await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lifetimeGate) return _disposed ? null : _cache.TryGetClone(key);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_lifetimeGate)
                if (--request.Waiters == 0)
                {
                    if (request.Completed) request.Dispose();
                    if (request.Completed || _starting.GetValueOrDefault(sourcePath) == 0)
                    {
                        _inflight.TryRemove(new KeyValuePair<string, SharedPreviewRequest>(key, request));
                        if (!request.Completed) request.Cancellation.Cancel();
                    }
                }
        }
    }

    public void InvalidatePaths(IEnumerable<string> paths)
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            foreach (var path in paths)
            {
                PathRevisions.AddOrUpdate(Path.GetFullPath(path), 1, (_, previous) => previous + 1);
                _cache.InvalidatePath(path);
                E5Archive.Invalidate(path);
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            var pending = _inflight.Values.Select(request => request.Task).ToArray();
            if (pending.Length == 0) ReleaseResources();
            else _ = Task.WhenAll(pending).ContinueWith(completed =>
            {
                _ = completed.Exception;
                ReleaseResources();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void ReleaseResources()
    {
        if (_ownsCache) _cache.Dispose();
        else if (ReferenceEquals(this, Shared)) _cache.Clear();
        _shutdown.Dispose();

    }

    private async Task PopulateAsync(string key, ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground,
        long revision, CancellationToken cancellationToken)
    {
        if (_cache.TryGetClone(key) is { } raced) { raced.Dispose(); return; }
        using var thumbnail = await ImageResourceCoordinator.Shared.DecodeAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            using var decoded = DecodeOne(project, catalog, asset, descriptor, token);
            if (decoded == null) return null;
            using var display = showBackground
                ? BmpPayloadService.CopyArgb(decoded.Bitmap)
                : ImageRendering.ApplyKeyTransparency(decoded.Bitmap, false, asset.Kind == ResourceKind.DllBitmap);
            return Resize(display, size);
        }, bitmap => bitmap?.Dispose(), cancellationToken, priority: DecodeRequestPriority.Thumbnail).ConfigureAwait(false);
        if (thumbnail != null)
        {
            lock (_lifetimeGate)
                if (!_disposed && revision == PathRevision(descriptor.SourcePath))
                    _cache.Store(key, thumbnail, descriptor.SourcePath);
        }
    }

    private static long PathRevision(string path)
        => PathRevisions.TryGetValue(Path.GetFullPath(path), out var revision) ? revision : 0;

    private void ReleaseStarting(string sourcePath)
    {
        if (_starting.GetValueOrDefault(sourcePath) > 1) { _starting[sourcePath]--; return; }
        _starting.Remove(sourcePath);
        foreach (var pair in _inflight.Where(pair => pair.Value.SourcePath == sourcePath && pair.Value.Waiters == 0 && !pair.Value.Completed))
        {
            _inflight.TryRemove(pair);
            pair.Value.Cancellation.Cancel();
        }
    }

    private sealed class SharedPreviewRequest(string sourcePath, CancellationTokenSource cancellation, Task task) : IDisposable
    {
        public string SourcePath { get; } = sourcePath;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; } = task;
        public int Waiters { get; set; }
        public bool Completed { get; set; }
        private bool _released;
        public void Dispose() { if (_released) return; _released = true; Cancellation.Dispose(); }
    }

    private static string BuildKey(ProjectProfile project, LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground)
    {
        var info = new FileInfo(descriptor.SourcePath);
        var paletteVersion = new PaletteService().GetVersionKey(project);
        return string.Join('|', asset.StablePhysicalId, Path.GetFullPath(descriptor.SourcePath), info.Length, info.LastWriteTimeUtc.Ticks,
            paletteVersion, descriptor.ImageNumber, descriptor.FrameIndex, descriptor.SourceOffset,
            descriptor.Turn, descriptor.Action, descriptor.Direction, size.Width, size.Height, showBackground);
    }

    private static DecodedFrame? DecodeOne(ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asset.Kind is ResourceKind.RComposite or ResourceKind.SComposite)
        {
            var bytes = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
            var group = asset.Frames.Where(frame => frame.SourcePath.Equals(descriptor.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                                                    frame.ImageNumber == descriptor.ImageNumber).ToArray();
            var frameCount = group.Max(frame => frame.FrameIndex) + 1;
            using var strip = ImageRendering.IsStandardImage(bytes)
                ? ImageRendering.DecodeStandardImage(bytes)
                : ImageRendering.DecodeIndexed(bytes, descriptor.Width, descriptor.Height * frameCount,
                    new PaletteService().Load(project));
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = ImageRendering.Crop(strip, new Rectangle(0, descriptor.FrameIndex * descriptor.Height,
                descriptor.Width, descriptor.Height));
            return new DecodedFrame(descriptor, bitmap);
        }

        return catalog.DecodePreviewFrame(project, asset, descriptor, cancellationToken);
    }

    private static Bitmap Resize(Bitmap source, Size bounds)
    {
        var scale = Math.Min((double)bounds.Width / source.Width, (double)bounds.Height / source.Height);
        scale = Math.Min(1, scale);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        // GDI+ DrawImage composites source-over: fully transparent source pixels
        // (A=0) keep the destination's zeroed RGB, so a 1:1 "resize" silently
        // rewrote hidden colour channels. Copy the ARGB rows directly whenever
        // no scaling is needed; only genuinely resized previews use DrawImage.
        if (width == source.Width && height == source.Height) return BmpPayloadService.CopyArgb(source);
        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }
}
