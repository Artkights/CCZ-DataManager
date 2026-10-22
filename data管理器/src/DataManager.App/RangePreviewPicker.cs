namespace DataManager.App;

/// <summary>
/// Keeps the editable/searchable range combo while replacing its closed edit
/// area with the corresponding range image.  The native combo remains the
/// source of truth for text input, filtering, keyboard navigation and the
/// owner-drawn drop-down; the preview layer is only visible while the combo
/// is closed and unfocused.
/// </summary>
public sealed class RangePreviewPicker : UserControl
{
    private readonly SearchableIdComboBox _combo;
    private readonly RangePreviewSurface _preview = new();
    private readonly ToolTip _previewTips = new() { AutoPopDelay = 20000, InitialDelay = 250 };
    private Func<int, CancellationToken, Task<Bitmap?>>? _loader;
    private CancellationTokenSource _previewCancellation = new();
    private int _previewRevision;
    private bool _releaseCompleted;

    public string FieldName { get; set; } = "范围";

    public RangePreviewPicker(SearchableIdComboBox combo)
    {
        _combo = combo ?? throw new ArgumentNullException(nameof(combo));
        Dock = DockStyle.Fill;
        Padding = Padding.Empty;
        MinimumSize = new Size(1, 32);

        _combo.Dock = DockStyle.Fill;
        Controls.Add(_combo);
        Controls.Add(_preview);

        _preview.Click += (_, _) => BeginEdit();
        _combo.Enter += (_, _) => HidePreview();
        _combo.DropDown += (_, _) => HidePreview();
        _combo.DropDownClosed += (_, _) => RefreshPreview();
        _combo.TextChanged += (_, _) =>
        {
            if (_combo.Focused || _combo.DroppedDown) HidePreview();
        };
        _combo.SelectionChangeCommitted += (_, _) => RefreshPreview();
        _combo.UnknownValueSubmitted += (_, _) => RefreshPreview();
        _combo.Validating += (_, _) => RefreshPreview();
        SizeChanged += (_, _) => ArrangePreview();
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
        ClearPreviewImage();
        _preview.Visible = loader != null;
        ArrangePreview();
        if (loader != null) RefreshPreview();
    }

    /// <summary>Re-reads the current combo value after programmatic binding.</summary>
    public void RefreshPreview()
    {
        if (_loader == null) return;
        if (_combo.Focused || _combo.DroppedDown)
        {
            HidePreview();
            return;
        }

        if (!_combo.TryGetSelectedId(out var id))
        {
            ClearPreviewImage();
            _preview.SetStatus("无图");
            _previewTips.SetToolTip(_preview, $"{FieldName}：未选择有效 ID");
            _preview.Visible = true;
            return;
        }

        _preview.Visible = true;
        _preview.SetStatus("加载中…");
        _previewTips.SetToolTip(_preview, $"{FieldName}：ID {id}");
        RequestPreview(id);
    }

    public void BeginEdit()
    {
        HidePreview();
        _combo.Focus();
        _combo.SelectionStart = _combo.Text.Length;
        _combo.SelectionLength = 0;
    }

    private void HidePreview()
    {
        _preview.Visible = false;
    }

    private void ArrangePreview()
    {
        // ComboBoxInfo's native button rectangle is not available until the
        // combo handle exists.  The scaled scrollbar width is the same native
        // width used by the standard WinForms combo and leaves its arrow fully
        // clickable at every supported DPI.
        var buttonWidth = Math.Max(18, (int)Math.Round(18d * DeviceDpi / 96d));
        var width = Math.Max(1, ClientSize.Width - buttonWidth - 2);
        var height = Math.Max(1, ClientSize.Height - 2);
        _preview.Bounds = new Rectangle(1, 1, width, height);
        _preview.BringToFront();
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
            _preview.SetStatus(null);
            bitmap = null;
            _preview.Visible = !_combo.Focused && !_combo.DroppedDown;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cancellation.IsCancellationRequested && !IsDisposed && revision == _previewRevision)
            {
                ClearPreviewImage();
                _preview.SetStatus("无图");
                _preview.Visible = !_combo.Focused && !_combo.DroppedDown;
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
