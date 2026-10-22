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
    private int _frameIndex;

    internal bool HasLoadedResource => _loaded;

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
        VisibleChanged += (_, _) => { UpdatePlayback(); if (Visible) Start(); };
        _timer.Tick += (_, _) =>
        {
            if (_animation == null || _animation.Frames.Count < 2) return;
            _frameIndex = (_frameIndex + 1) % _animation.Frames.Count;
            _picture.Image = _animation.Frames[_frameIndex];
            ArrangePicture();
        };
    }

    public void Bind(string key, string title, Func<CancellationToken, Task<ResourcePreviewAnimation>> loader, bool animate)
    {
        Text = title;
        AccessibleName = title;
        _tips.SetToolTip(_picture, title);
        if (string.Equals(_key, key, StringComparison.Ordinal)) { UpdatePlayback(); return; }
        CancelPending();
        _key = key; _loader = loader; _animate = animate; _started = false; _loaded = false;
        _state.Text = "加载中…";
        _state.Visible = _picture.Image == null;
        _timer.Stop();
        Start();
    }

    public void InvalidateResource() { CancelPending(); _key = null; _started = false; _loaded = false; _timer.Stop(); }

    private void Start()
    {
        if (_released || _started || _loader == null || !IsHandleCreated || !Visible) return;
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
        try { decoded = await loader(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ex.Message; }
        lock (_pendingGate)
        {
            if (_released || token.IsCancellationRequested || generation != _generation)
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
            if (ready == null)
            {
                _picture.Image = null; _animation?.Dispose(); _animation = null;
                _state.Text = "预览不可用"; _state.Visible = true; _tips.SetToolTip(_state, error);
                return;
            }
            _picture.Image = null;
            _animation?.Dispose(); _animation = ready; _frameIndex = 0;
            _loaded = true;
            _picture.Image = ready.Frames.FirstOrDefault(); ArrangePicture();
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

    private void UpdatePlayback() => _timer.Enabled = !_released && IsHandleCreated && _loaded && Visible && _animate && _animation?.Frames.Count > 1;

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
            CancelPending();
            _timer.Dispose(); _picture.Image = null;
            _animation?.Dispose(); _animation = null;
        }
        base.Dispose(disposing);
    }
}
