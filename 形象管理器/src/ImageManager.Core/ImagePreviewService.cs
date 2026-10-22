using System.Drawing.Drawing2D;
using System.Collections.Concurrent;

namespace ImageManager.Core;

public sealed class ThumbnailCache : IThumbnailCache
{
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
            return new Bitmap(entry.Bitmap);
        }
    }

    public void Store(string key, Bitmap bitmap, string sourcePath)
    {
        var copy = new Bitmap(bitmap);
        var bytes = checked((long)copy.Width * copy.Height * 4);
        lock (_sync)
        {
            RemoveCore(key);
            var node = _lru.AddFirst(key);
            _entries[key] = new CacheEntry(copy, Path.GetFullPath(sourcePath), bytes, node);
            _currentBytes += bytes;
            while (_entries.Count > _maximumCount || _currentBytes > _maximumBytes)
            {
                var last = _lru.Last;
                if (last == null) break;
                RemoveCore(last.Value);
            }
        }
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

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values) entry.Bitmap.Dispose();
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
        entry.Bitmap.Dispose();
    }

    private sealed record CacheEntry(Bitmap Bitmap, string SourcePath, long EstimatedBytes, LinkedListNode<string> Node);
}

public sealed class ImagePreviewService : IImagePreviewService
{
    private readonly IThumbnailCache _cache;
    private readonly SemaphoreSlim _decodeSlots = new(4, 4);
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifetimeGate = new();
    private long _revision;
    private bool _disposed;

    public ImagePreviewService(IThumbnailCache? cache = null) => _cache = cache ?? new ThumbnailCache();

    public async Task<Bitmap?> GetThumbnailAsync(ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifetimeGate) ObjectDisposedException.ThrowIf(_disposed, this);
        // Key construction checks source and palette dependencies. It must not
        // probe archives or hash a palette on the WinForms message thread.
        var sourceKey = await Task.Run(() => BuildKey(project, asset, descriptor, size, showBackground),
            cancellationToken).ConfigureAwait(false);
        string key;
        Task populate;
        lock (_lifetimeGate)
        {
            // A request already in progress completes empty when its owner is
            // disposed; new requests still fail the check at method entry.
            if (_disposed) return null;
            // An invalidation starts a new generation even when file metadata
            // is unchanged. Completed or late old tasks cannot serve it.
            var revision = _revision;
            key = revision + "|" + sourceKey;
            if (_cache.TryGetClone(key) is { } cached) return cached;
            populate = _inflight.GetOrAdd(key, _ => PopulateAsync(key, project, catalog, asset, descriptor, size,
                showBackground, revision, _shutdown.Token));
            // Population outlives an individual waiter. Retire it even when
            // every waiter cancels before decoding completes.
            _ = populate.ContinueWith(completed =>
            {
                _ = completed.Exception;
                _inflight.TryRemove(new KeyValuePair<string, Task>(key, completed));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try
        {
            await populate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lifetimeGate) return _disposed ? null : _cache.TryGetClone(key);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _shutdown.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (populate.IsCompleted) _inflight.TryRemove(new KeyValuePair<string, Task>(key, populate));
        }
    }

    public void InvalidatePaths(IEnumerable<string> paths)
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _revision++;
            foreach (var path in paths)
            {
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
            var pending = _inflight.Values.ToArray();
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
        _cache.Dispose();
        _shutdown.Dispose();
        _decodeSlots.Dispose();
    }

    private async Task PopulateAsync(string key, ProjectProfile project, IImageCatalogService catalog,
        LogicalImageAsset asset, FrameDescriptor descriptor, Size size, bool showBackground,
        long revision, CancellationToken cancellationToken)
    {
        await _decodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetClone(key) is { } raced)
            {
                raced.Dispose();
                return;
            }
            using var thumbnail = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var decoded = DecodeOne(project, catalog, asset, descriptor, cancellationToken);
                if (decoded == null) return null;
                using var display = showBackground
                    ? new Bitmap(decoded.Bitmap)
                    : ImageRendering.ApplyKeyTransparency(decoded.Bitmap, false, asset.Kind == ResourceKind.DllBitmap);
                return Resize(display, size);
            }, cancellationToken).ConfigureAwait(false);
            if (thumbnail != null)
            {
                lock (_lifetimeGate)
                    if (!_disposed && revision == _revision) _cache.Store(key, thumbnail, descriptor.SourcePath);
            }
        }
        finally { _decodeSlots.Release(); }
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
        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }
}
