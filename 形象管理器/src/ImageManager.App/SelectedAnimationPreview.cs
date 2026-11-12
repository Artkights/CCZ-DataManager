using ImageManager.Core;

namespace ImageManager.App;

/// <summary>A stable viewport. Its frame ownership is independent from gallery cards and checkboxes.</summary>
internal sealed class SelectedAnimationPreview : UserControl
{
    private const long MaximumCacheBytes = 32L * 1024 * 1024;
    private readonly long _maximumCacheBytes;
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(36, 38, 40) };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Text = "点击缩略图预览" };
    private readonly Label _status = new() { AutoSize = true, Text = "勾选仅用于批量操作" };
    private readonly Button _toggle = new() { Text = "播放", AutoSize = true };
    private readonly Button _next = new() { Text = "下一帧", AutoSize = true };
    private readonly NumericUpDown _interval = new() { Minimum = 30, Maximum = 2000, Value = 100, Increment = 20, Width = 68 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly LinkedList<PlaybackEntry> _cache = new();
    private IReadOnlyList<DecodedFrame> _frames = Array.Empty<DecodedFrame>();
    private CancellationTokenSource? _cancellation;
    private PlaybackEntry? _current;
    private string? _requestedKey;
    private long _revision, _bindingRequest;
    private int _index;
    private bool _loading;
    private bool _active = true;
    private bool _windowLoading;
    private CancellationTokenSource? _windowCancellation;
    private int _resumeIndex;
    private ProjectProfile? _restoreProject;
    private IImageCatalogService? _restoreCatalog;
    private LogicalImageAsset? _restoreAsset;

    public string? AssetId { get; private set; }
    public bool IsPlaying => _timer.Enabled;
    public int RetainedAnimationCount => _cache.Count + (_current != null && !_cache.Contains(_current) ? 1 : 0);
    public long RetainedBytes => _cache.Sum(entry => entry.Bytes) + (_current != null && !_cache.Contains(_current) ? _current.Bytes : 0);
    public event Action? CacheChanged;

    public SelectedAnimationPreview(long maximumCacheBytes = MaximumCacheBytes)
    {
        if (maximumCacheBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        _maximumCacheBytes = maximumCacheBytes;
        Dock = DockStyle.Fill;
        Padding = new Padding(6);
        DoubleBuffered = true;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(2) };
        controls.Controls.AddRange([_toggle, _next,
            new Label { Text = "毫秒", AutoSize = true, Margin = new Padding(4, 6, 2, 0) }, _interval, _status]);
        Controls.Add(PreviewImageLayout.CreateViewport(_picture));
        Controls.Add(controls);
        Controls.Add(_title);
        _timer.Tick += (_, _) => Advance();
        _toggle.Click += (_, _) => Toggle();
        _next.Click += (_, _) => { _timer.Stop(); Advance(); UpdateState(); };
        _interval.ValueChanged += (_, _) => _timer.Interval = (int)_interval.Value;
        VisibleChanged += (_, _) => { if (!Visible) SuspendPreview(); else ResumePreview(); };
        UpdateState();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active) SuspendPreview(); else ResumePreview();
    }
    private void SuspendPreview()
    {
        ++_bindingRequest; ++_revision; _cancellation?.Cancel(); _windowCancellation?.Cancel();
        Pause();
        if (_current == null && !_loading) return;
        if (_current != null) _resumeIndex = _current.WindowStart + _index;
        var project = _restoreProject; var catalog = _restoreCatalog; var asset = _restoreAsset;
        ReleaseAllFrames(); _windowLoading = false;
        _restoreProject = project; _restoreCatalog = catalog; _restoreAsset = asset;
    }
    private void ResumePreview()
    {
        if (_active && Visible && _current == null && !_loading && _restoreProject != null && _restoreCatalog != null && _restoreAsset != null)
            _ = BindAsync(_restoreProject, _restoreCatalog, _restoreAsset);
    }
    public void Pause() { _timer.Stop(); UpdateState(); }
    public void Toggle()
    {
        _timer.Enabled = !_timer.Enabled && _active && Visible && (_current?.TotalFrames ?? _frames.Count) > 1;
        UpdateState();
    }

    public async Task BindAsync(ProjectProfile project, IImageCatalogService catalog, LogicalImageAsset asset)
    {
        if (IsDisposed) return;
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var request = ++_bindingRequest;
        string key;
        try { key = await Task.Run(() => Fingerprint(project, asset)); }
        catch (Exception ex) { ShowFailure(asset, ex.Message); return; }
        if (IsDisposed || request != _bindingRequest) return;
        if (_requestedKey == key && (_loading || _current?.Key == key)) return;
        var revision = ++_revision;
        if (_restoreAsset?.StablePhysicalId != asset.StablePhysicalId) _resumeIndex = 0;
        _restoreProject = project; _restoreCatalog = catalog; _restoreAsset = asset;
        _cancellation?.Cancel();
        Pause();
        AssetId = asset.StablePhysicalId;
        _requestedKey = key;
        _title.Text = asset.DisplayName;
        _status.Text = "正在读取动画…";
        var cached = _cache.FirstOrDefault(entry => entry.Key == key);
        if (cached != null) { Display(cached); return; }
        _loading = true;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var decoded = await ImageResourceCoordinator.Shared.DecodeAsync(decodeToken =>
            {
                decodeToken.ThrowIfCancellationRequested();
                if (catalog is IBoundedAnimationPreviewCatalog && asset.Category == ImageCategory.StrategyAnimation && asset.Frames.Count == 1)
                {
                    var package = StrategyAnimationResourceCodec.ReadPackageBytes(asset.Frames[0]);
                    var window = StrategyPreviewPlayback.OpenAsync(project, package, decodeToken).GetAwaiter().GetResult();
                    if (_resumeIndex > 0 && window.LoadWindowAsync != null && _resumeIndex >= window.Start + window.Frames.Count)
                    {
                        var initial = window;
                        try { window = initial.LoadWindowAsync(_resumeIndex, decodeToken).GetAwaiter().GetResult(); }
                        finally { initial.Dispose(); }
                    }
                    return FromWindow(key, asset, window);
                }
                var frames = catalog.DecodeFrames(project, asset);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (frames.Count == 0) throw new InvalidDataException("该资源没有可播放的帧。");
                    var interval = 100;
                    if (asset.Category == ImageCategory.StrategyAnimation && asset.Frames.Count == 1)
                    {
                        var source = asset.Frames[0];
                        var bytes = StrategyAnimationResourceCodec.ReadPackageBytes(source);
                        if (bytes.Length >= 8)
                        {
                            var delay = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
                            if (delay is >= 30 and <= 2000) interval = delay;
                        }
                    }
                    return new PlaybackEntry(key, asset.StablePhysicalId, frames, interval,
                        asset.Frames.Select(frame => Path.GetFullPath(frame.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                }
                catch { foreach (var frame in frames) frame.Dispose(); throw; }
            }, entry => entry.Dispose(), token, this);
            if (IsDisposed || Disposing || revision != _revision || token.IsCancellationRequested)
            {
                decoded.Dispose();
                return;
            }
            Display(decoded, _resumeIndex);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && revision == _revision) ShowFailure(asset, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _loading = false; }
            cancellation.Dispose();
        }
    }

    private void Display(PlaybackEntry entry, int absoluteFrame = 0)
    {
        var previous = _current;
        _picture.Image = null;
        _current = entry;
        _frames = entry.Frames;
        UpdateState();
        _loading = false;
        _index = Math.Clamp(absoluteFrame - entry.WindowStart, 0, Math.Max(0, entry.Frames.Count - 1));
        _interval.Value = entry.Interval;
        _timer.Interval = entry.Interval;
        if (previous != null && previous != entry && previous.Key == entry.Key) { _cache.Remove(previous); previous.Dispose(); previous = null; }
        _cache.Remove(entry);
        if (entry.Lease == null)
            entry.Lease = ImageResourceCoordinator.Shared.RegisterPixels(entry.Bytes, "gallery-animation", () => Reclaim(entry));
        entry.Lease.Touch();
        if (entry.Bytes <= _maximumCacheBytes) _cache.AddFirst(entry);
        if (previous != null && previous != entry && !_cache.Contains(previous)) previous.Dispose();
        while (_cache.Count > 2 || _cache.Sum(item => item.Bytes) > _maximumCacheBytes)
        {
            var expired = _cache.Last!.Value;
            _cache.RemoveLast();
            if (expired != _current) expired.Dispose();
        }
        if (entry.Bytes > _maximumCacheBytes)
        {
            // The budget limits reusable frames. A large current animation can still play.
            foreach (var expired in _cache) expired.Dispose();
            _cache.Clear();
        }
        ShowCurrent();
        _timer.Enabled = _active && Visible && (_current?.TotalFrames ?? _frames.Count) > 1;
        UpdateState();
        CacheChanged?.Invoke();
        ImageResourceCoordinator.Shared.TrimPixels();
    }

    private void Reclaim(PlaybackEntry entry)
    {
        void Release()
        {
            if (_current == entry) SuspendPreview();
            else if (_cache.Remove(entry)) entry.Dispose();
        }
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)Release); } catch (InvalidOperationException) { } }
        else Release();
    }

    private static PlaybackEntry FromWindow(string key, LogicalImageAsset asset, AnimationFrameWindow window)
        => new(key, asset.StablePhysicalId, window.Frames.Select((bitmap, index) =>
            new DecodedFrame(asset.Frames[0] with { FrameIndex = window.Start + index, Width = bitmap.Width, Height = bitmap.Height }, bitmap)).ToArray(),
            window.IntervalMs, asset.Frames.Select(frame => Path.GetFullPath(frame.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            { WindowStart = window.Start, TotalFrames = window.TotalFrames, LoadWindowAsync = window.LoadWindowAsync };

    public void TrimCache(int maximumCount, long maximumBytes)
    {
        if (maximumCount <= 0 || (_current?.Bytes ?? 0) > maximumBytes) { ReleaseAllFrames(); return; }
        foreach (var entry in _cache.Reverse().ToArray())
        {
            if (RetainedAnimationCount <= maximumCount && RetainedBytes <= maximumBytes) break;
            if (entry == _current) continue;
            _cache.Remove(entry); entry.Dispose();
        }
    }

    public void ReleaseAllFrames()
    {
        Clear();
        foreach (var entry in _cache) entry.Dispose();
        _cache.Clear();
    }

    public void ShowFailure(LogicalImageAsset asset, string message)
    {
        _restoreProject = null; _restoreCatalog = null; _restoreAsset = null;
        ++_revision; ++_bindingRequest;
        _cancellation?.Cancel();
        Pause();
        ReleaseCurrent();
        AssetId = asset.StablePhysicalId;
        _requestedKey = null;
        _loading = false;
        _title.Text = asset.DisplayName;
        _status.Text = "无法预览：" + message;
        UpdateState();
    }

    public void Clear()
    {
        _restoreProject = null; _restoreCatalog = null; _restoreAsset = null;
        _windowCancellation?.Cancel();
        ++_revision; ++_bindingRequest;
        _cancellation?.Cancel();
        Pause();
        ReleaseCurrent();
        AssetId = null;
        _requestedKey = null;
        _loading = false;
        _title.Text = "点击缩略图预览";
        _status.Text = "勾选仅用于批量操作";
        UpdateState();
    }

    private void ReleaseCurrent()
    {
        _picture.Image = null;
        if (_current != null && !_cache.Contains(_current)) _current.Dispose();
        _current = null;
        _frames = Array.Empty<DecodedFrame>();
    }

    public void InvalidatePaths(IEnumerable<string> paths)
    {
        var changed = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_current?.Paths.Any(changed.Contains) == true) Clear();
        foreach (var entry in _cache.Where(entry => entry.Paths.Any(changed.Contains)).ToArray())
        {
            _cache.Remove(entry);
            entry.Dispose();
        }
    }

    private static string Fingerprint(ProjectProfile project, LogicalImageAsset asset)
        => project.RootPath + "|" + asset.StablePhysicalId + "|" + string.Join("|", asset.Frames.Select(frame => frame.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                var info = new FileInfo(path);
                return Path.GetFullPath(path) + ":" + (info.Exists ? info.Length : -1) + ":" + info.LastWriteTimeUtc.Ticks;
            }));

    private async void Advance()
    {
        var current = _current;
        if (current == null || _frames.Count == 0 || _windowLoading) return;
        current.Lease?.Touch();
        var next = (current.WindowStart + _index + 1) % current.TotalFrames;
        if (next >= current.WindowStart && next < current.WindowStart + _frames.Count)
        { _index = next - current.WindowStart; ShowCurrent(); return; }
        if (current.LoadWindowAsync == null || _restoreAsset == null) return;
        _windowLoading = true; var revision = _revision;
        var windowCancellation = new CancellationTokenSource(); _windowCancellation = windowCancellation;
        var token = windowCancellation.Token;
        AnimationFrameWindow? window = null;
        try
        {
            window = await current.LoadWindowAsync(next, token);
            if (IsDisposed || revision != _revision || !_active || !Visible) return;
            var entry = FromWindow(current.Key, _restoreAsset, window); window = null;
            Display(entry, next);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed && revision == _revision) { Pause(); _status.Text = "无法预览：" + error.Message; } }
        finally
        {
            window?.Dispose();
            if (ReferenceEquals(_windowCancellation, windowCancellation)) _windowCancellation = null;
            windowCancellation.Dispose(); if (revision == _revision) _windowLoading = false;
        }
    }
    private void ShowCurrent()
    {
        if (_frames.Count == 0) return;
        _picture.Image = _frames[_index].Bitmap;
        _status.Text = $"帧 {(_current?.WindowStart ?? 0) + _index + 1}/{_current?.TotalFrames ?? _frames.Count}";
    }

    private void UpdateState()
    {
        _toggle.Enabled = (_current?.TotalFrames ?? _frames.Count) > 1;
        _next.Enabled = (_current?.TotalFrames ?? _frames.Count) > 1;
        _toggle.Text = _timer.Enabled ? "暂停" : "播放";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseAllFrames();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record PlaybackEntry(string Key, string AssetId, IReadOnlyList<DecodedFrame> Frames, int Interval, string[] Paths) : IDisposable
    {
        public long Bytes { get; } = Frames.Sum(frame => (long)frame.Bitmap.Width * frame.Bitmap.Height * 4);
        public int WindowStart { get; init; }
        public int TotalFrames { get; init; } = Frames.Count;
        public Func<int, CancellationToken, Task<AnimationFrameWindow>>? LoadWindowAsync { get; init; }
        public ImageResourceCoordinator.PixelLease? Lease { get; set; }
        public void Dispose() { Lease?.Dispose(); Lease = null; foreach (var frame in Frames) frame.Dispose(); }
    }
}
