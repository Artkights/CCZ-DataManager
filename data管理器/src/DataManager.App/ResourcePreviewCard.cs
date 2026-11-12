using ImageManager.Core;
using DataManager.Images;

namespace DataManager.App;

/// <summary>A persistent preview viewport. Selection changes only restart a different resource request.</summary>
internal sealed class ResourcePreviewCard : Panel
{
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Control, Margin = Padding.Empty };
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, Margin = Padding.Empty };
    private readonly Label _state = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ToolTip _tips;
    private readonly object _pendingGate = new();
    private CancellationTokenSource? _cancellation;
    private Func<CancellationToken, Task<ResourcePreviewAnimation>>? _loader;
    private ResourcePreviewAnimation? _animation, _pending;
    private string? _key;
    private long _generation;
    private bool _started, _animate, _released, _loaded;
    private bool _previewActive = true;
    private int _frameIndex, _resumeFrame;
    private bool _windowLoading;
    private ImageResourceCoordinator.PixelLease? _pixelLease;

    internal bool HasLoadedResource => _loaded;

    internal void SetPreviewActive(bool active)
    {
        if (_released || _previewActive == active) return;
        _previewActive = active;
        if (!active) PauseHiddenPreview();
        else { _pixelLease?.Touch(); UpdatePlayback(); Start(); }
    }

    public ResourcePreviewCard(ToolTip tips)
    {
        _tips = tips;
        Dock = DockStyle.Fill; Margin = new Padding(3); Padding = Padding.Empty;
        DoubleBuffered = true;
        _picture.BackColor = _body.BackColor;
        _body.Controls.Add(_picture);
        Controls.Add(_body); Controls.Add(_state);
        _state.Height = Math.Max(20, Font.Height + 6);
        _body.SizeChanged += (_, _) => ArrangePicture();
        FontChanged += (_, _) => _state.Height = Math.Max(20, Font.Height + 6);
        HandleCreated += (_, _) => { UpdatePlayback(); Start(); };
        HandleDestroyed += (_, _) =>
        {
            if (_released) return;
            _timer.Stop();
            if (!_loaded) { CancelPending(); _started = false; }
        };
        VisibleChanged += (_, _) =>
        {
            if (!Visible && (!_loaded || (_animation?.PixelBytes ?? 0) > 1024 * 1024 || _animation?.LoadWindowAsync != null)) SuspendPreview();
            else { UpdatePlayback(); if (Visible) Start(); }
        };
        _picture.Click += (_, _) => Start(); _state.Click += (_, _) => Start();
        _timer.Tick += (_, _) => Advance();
    }

    public void Bind(string key, string title, Func<CancellationToken, Task<ResourcePreviewAnimation>> loader, bool animate)
    {
        Text = title;
        AccessibleName = title;
        _tips.SetToolTip(_picture, title);
        if (string.Equals(_key, key, StringComparison.Ordinal)) { UpdatePlayback(); Start(); return; }
        CancelPending();
        _key = key; _loader = loader; _animate = animate; _started = false; _loaded = false; _resumeFrame = 0;
        _state.Text = "加载中…";
        _state.Visible = _picture.Image == null;
        _timer.Stop();
        Start();
    }

    public void InvalidateResource() { CancelPending(); _key = null; _started = false; _loaded = false; _timer.Stop(); }

    private void Start()
    {
        if (_released || !_previewActive || _started || _loader == null || !IsHandleCreated || !Visible) return;
        _started = true;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var generation = _generation;
        var loader = _loader;
        _ = LoadAsync(loader, token, generation);
    }

    private async Task LoadAsync(Func<CancellationToken, Task<ResourcePreviewAnimation>> loader, CancellationToken token, long generation)
    {
        ResourcePreviewAnimation? decoded = null;
        string? error = null;
        try
        {
            decoded = await loader(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) { decoded.Dispose(); return; }
            if (_resumeFrame > 0 && decoded.LoadWindowAsync != null &&
                (_resumeFrame < decoded.WindowStart || _resumeFrame >= decoded.WindowStart + decoded.Frames.Count))
            {
                var initial = decoded; decoded = null;
                try { decoded = ResourcePreviewAnimation.FromWindow(await initial.LoadWindowAsync(_resumeFrame, token).ConfigureAwait(false), initial.Mapping); }
                finally { initial.Dispose(); }
            }
        }
        catch (OperationCanceledException) { decoded?.Dispose(); return; }
        catch (Exception ex) { decoded?.Dispose(); decoded = null; error = ex.Message; }
        lock (_pendingGate)
        {
            if (_released || !_previewActive || token.IsCancellationRequested || generation != _generation)
            { decoded?.Dispose(); return; }
            _pending = decoded;
        }
        void Apply()
        {
            ResourcePreviewAnimation? ready;
            lock (_pendingGate)
            {
                if (_released || token.IsCancellationRequested || generation != _generation) return;
                ready = _pending; _pending = null;
            }
            if (!_previewActive || !Visible)
            { ready?.Dispose(); SuspendPreview(); return; }
            if (ready == null)
            {
                ReleaseOwnedPixels();
                _state.Text = "预览不可用"; _state.Visible = true; _tips.SetToolTip(_state, error);
                return;
            }
            Publish(ready, _resumeFrame);
            if (_animation == null) return;
            _loaded = true;
            _state.Text = ready.Frames.Count == 0 ? "无对应图片" : string.Empty;
            _state.Visible = ready.Frames.Count == 0;
            _tips.SetToolTip(this, ready.Mapping); _tips.SetToolTip(_state, ready.Mapping);
            _tips.SetToolTip(_picture, Text + Environment.NewLine + ready.Mapping);
            _timer.Interval = Math.Clamp(ready.IntervalMs, 40, 2000);
            UpdatePlayback();
        }
        try { BeginInvoke((Action)Apply); }
        catch (InvalidOperationException)
        {
            lock (_pendingGate)
            {
                if (generation == _generation) { _pending?.Dispose(); _pending = null; }
            }
        }
    }

    private void Publish(ResourcePreviewAnimation ready, int absoluteFrame)
    {
        ReleaseOwnedPixels(); _animation = ready;
        _frameIndex = Math.Clamp(absoluteFrame - ready.WindowStart, 0, Math.Max(0, ready.Frames.Count - 1));
        _picture.Image = ready.Frames.Count == 0 ? null : ready.Frames[_frameIndex]; ArrangePicture();
        _pixelLease = ImageResourceCoordinator.Shared.RegisterPixels(ready.PixelBytes, "business-preview", () =>
        {
            void Reclaim() { if (!_released && ReferenceEquals(_animation, ready)) SuspendPreview(); }
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)Reclaim); } catch (InvalidOperationException) { } }
            else Reclaim();
        });
        ImageResourceCoordinator.Shared.TrimPixels();
    }

    private void ReleaseOwnedPixels()
    {
        _picture.Image = null; _pixelLease?.Dispose(); _pixelLease = null;
        _animation?.Dispose(); _animation = null;
    }

    private void SuspendPreview()
    {
        _timer.Stop();
        if (_animation != null) _resumeFrame = _animation.WindowStart + _frameIndex;
        CancelPending(); _started = false; _loaded = false; _windowLoading = false;
        ReleaseOwnedPixels();
        _state.Text = "点击加载预览"; _state.Visible = true;
    }

    private void PauseHiddenPreview()
    {
        // Completed small still images already count against the shared pixel
        // budget. Keep their lease so returning to a business tab can reuse
        // them; eviction, replacement and disposal still release the bitmap.
        if (_loaded && _animation is { TotalFrameCount: <= 1, LoadWindowAsync: null } image &&
            image.PixelBytes <= 1024 * 1024)
        {
            _timer.Stop();
            CancelPending();
            return;
        }
        SuspendPreview();
    }

    private async void Advance()
    {
        var animation = _animation;
        if (_released || !_previewActive || !Visible) { _timer.Stop(); return; }
        if (animation == null || animation.TotalFrameCount < 2 || _windowLoading) return;
        _pixelLease?.Touch();
        var next = (animation.WindowStart + _frameIndex + 1) % animation.TotalFrameCount;
        if (next >= animation.WindowStart && next < animation.WindowStart + animation.Frames.Count)
        { _frameIndex = next - animation.WindowStart; _picture.Image = animation.Frames[_frameIndex]; ArrangePicture(); return; }
        if (animation.LoadWindowAsync == null) return;
        _windowLoading = true; var generation = _generation;
        var token = _cancellation?.Token ?? CancellationToken.None;
        AnimationFrameWindow? window = null;
        try
        {
            window = await animation.LoadWindowAsync(next, token);
            if (_released || !_previewActive || token.IsCancellationRequested || generation != _generation || !Visible) return;
            var ready = ResourcePreviewAnimation.FromWindow(window, animation.Mapping); window = null;
            Publish(ready, next); _loaded = _animation != null; UpdatePlayback();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (!_released && _previewActive && Visible && generation == _generation) { _tips.SetToolTip(_state, error.Message); _timer.Stop(); } }
        finally { window?.Dispose(); if (generation == _generation) _windowLoading = false; }
    }

    private void UpdatePlayback() => _timer.Enabled = !_released && _previewActive && IsHandleCreated && _loaded && Visible && _animate && _animation?.TotalFrameCount > 1;

    private void ArrangePicture()
    {
        var source = _picture.Image?.Size ?? Size.Empty;
        if (source.Width <= 0 || source.Height <= 0) { _picture.Bounds = Rectangle.Empty; return; }
        var scale = Math.Min(_body.ClientSize.Width / (double)source.Width, _body.ClientSize.Height / (double)source.Height);
        var size = new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        var bounds = new Rectangle((_body.ClientSize.Width - size.Width) / 2, (_body.ClientSize.Height - size.Height) / 2, size.Width, size.Height);
        if (_picture.Bounds != bounds) _picture.Bounds = bounds;
    }

    private void CancelPending()
    {
        lock (_pendingGate)
        {
            _generation++;
            _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
            _pending?.Dispose(); _pending = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_released)
        {
            lock (_pendingGate) _released = true;
            _loader = null;
            CancelPending();
            _timer.Dispose(); ReleaseOwnedPixels();
        }
        base.Dispose(disposing);
    }
}
