namespace DataManager.App;

/// <summary>
/// Keeps the editable/searchable range combo beside its live image, so typing
/// and browsing never hides either the current text or the corresponding range.
/// </summary>
public sealed class RangePreviewPicker : UserControl
{
    private readonly SearchableIdComboBox _combo;
    private readonly RangePreviewSurface _preview = new();
    private readonly ToolTip _previewTips = new() { AutoPopDelay = 20000, InitialDelay = 250 };
    private Func<int, CancellationToken, Task<Bitmap?>>? _loader;
    private CancellationTokenSource _previewCancellation = new();
    private int _previewRevision;
    private int? _requestedId;
    private bool _releaseCompleted;

    public string FieldName { get; set; } = "范围";
    public int MinimumId { get; set; }
    public int MaximumId { get; set; } = 255;

    public RangePreviewPicker(SearchableIdComboBox combo)
    {
        _combo = combo ?? throw new ArgumentNullException(nameof(combo));
        Dock = DockStyle.Fill;
        Padding = Padding.Empty;
        MinimumSize = new Size(1, 36);

        _combo.Dock = DockStyle.None;
        Controls.Add(_combo);
        Controls.Add(_preview);

        _preview.Click += (_, _) => BeginEdit();
        _combo.DropDownClosed += (_, _) => RefreshPreview();
        _combo.TextChanged += (_, _) => RefreshPreview();
        _combo.SelectionChangeCommitted += (_, _) => RefreshPreview();
        _combo.UnknownValueSubmitted += (_, _) => RefreshPreview();
        _combo.Validating += (_, _) => RefreshPreview();
        SizeChanged += (_, _) => ArrangePreview();
        FontChanged += (_, _) => ArrangePreview();
        DpiChangedAfterParent += (_, _) => ArrangePreview();
        HandleCreated += (_, _) => BeginInvoke((Action)ArrangePreview);
        ArrangePreview();
    }

    public SearchableIdComboBox Combo => _combo;

    /// <summary>Exposes the image surface for deterministic UI smoke tests.</summary>
    public Control PreviewBox => _preview;

    public bool IsPreviewVisible => _preview.Visible;

    public void SetPreviewLoader(Func<int, CancellationToken, Task<Bitmap?>>? loader)
    {
        _previewCancellation.Cancel();
        _previewCancellation.Dispose();
        _previewCancellation = new CancellationTokenSource();
        _loader = loader;
        _previewRevision++;
        _requestedId = null;
        ClearPreviewImage();
        _preview.Visible = loader != null;
        ArrangePreview();
        if (loader != null) RefreshPreview();
    }

    /// <summary>Re-reads the current combo value after programmatic binding.</summary>
    public void RefreshPreview()
    {
        if (_loader == null || _releaseCompleted) return;
        _preview.Visible = true;
        if (!_combo.TryGetSelectedId(out var id) || id < MinimumId || id > MaximumId)
        {
            // Search text and partial numbers are not resource IDs. Retain the
            // most recent valid preview rather than flashing an unrelated image.
            if (_requestedId == null)
            {
                _preview.SetStatus("请输入编号");
                _previewTips.SetToolTip(_preview, $"{FieldName}：未选择有效 ID");
            }
            return;
        }
        if (_requestedId == id) return;
        _requestedId = id;
        _preview.SetStatus("加载中…");
        _previewTips.SetToolTip(_preview, $"{FieldName}：ID {id}");
        RequestPreview(id);
    }

    public void BeginEdit()
    {
        _combo.Focus();
        _combo.SelectionStart = _combo.Text.Length;
        _combo.SelectionLength = 0;
    }

    private void ArrangePreview()
    {
        var gap = Math.Max(3, 4 * DeviceDpi / 96);
        var previewWidth = Math.Min(Math.Max(40 * DeviceDpi / 96, Font.Height + 12), Math.Max(1, ClientSize.Width / 3));
        var comboWidth = Math.Max(1, ClientSize.Width - previewWidth - gap);
        _combo.Bounds = new Rectangle(0, Math.Max(0, (ClientSize.Height - _combo.PreferredHeight) / 2), comboWidth, _combo.PreferredHeight);
        _preview.Bounds = new Rectangle(comboWidth + gap, 0, previewWidth, Math.Max(1, ClientSize.Height));
    }

    private async void RequestPreview(int id)
    {
        var loader = _loader;
        if (loader == null) return;
        _previewCancellation.Cancel();
        _previewCancellation.Dispose();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        var revision = ++_previewRevision;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await loader(id, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing ||
                revision != _previewRevision || !ReferenceEquals(cancellation, _previewCancellation)) return;
            _preview.SetImage(bitmap);
            _preview.SetStatus(bitmap == null ? "无对应图片" : null);
            bitmap = null;
            _preview.Visible = true;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cancellation.IsCancellationRequested && !IsDisposed && revision == _previewRevision)
            {
                ClearPreviewImage();
                _preview.SetStatus("预览不可用");
                _preview.Visible = true;
            }
        }
        finally { bitmap?.Dispose(); }
    }

    private void ClearPreviewImage()
    {
        _preview.SetImage(null);
    }

    private void ReleaseResources()
    {
        if (_releaseCompleted) return;
        _releaseCompleted = true;
        _previewRevision++;
        _previewCancellation.Cancel();
        _previewCancellation.Dispose();
        _previewTips.Dispose();
        ClearPreviewImage();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseResources();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Paints a small range image without exposing it as a resource PictureBox.
/// Resource preview layout tests intentionally inspect PictureBox controls;
/// this compact selector is a different visual surface with a fixed combo-box
/// footprint, so it draws its own centred, proportional image instead.
/// </summary>
internal sealed class RangePreviewSurface : Control
{
    private Bitmap? _image;
    private string? _status;

    public RangePreviewSurface()
    {
        BackColor = SystemColors.Window;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetImage(Bitmap? image)
    {
        if (ReferenceEquals(_image, image)) return;
        var previous = _image;
        _image = image;
        previous?.Dispose();
        Invalidate();
    }

    public void SetStatus(string? status)
    {
        if (string.Equals(_status, status, StringComparison.Ordinal)) return;
        _status = status;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_image == null)
        {
            if (!string.IsNullOrWhiteSpace(_status))
                TextRenderer.DrawText(e.Graphics, _status, Font, ClientRectangle, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        var scale = Math.Min((float)ClientSize.Width / _image.Width, (float)ClientSize.Height / _image.Height);
        var width = Math.Max(1, (int)Math.Round(_image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(_image.Height * scale));
        var destination = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(_image, destination);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var image = _image;
            _image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
