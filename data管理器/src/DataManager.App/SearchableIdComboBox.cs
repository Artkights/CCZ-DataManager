using System.Globalization;
using System.Runtime.InteropServices;

namespace DataManager.App;

/// <summary>
/// 可输入的 ID/名称下拉框。输入任意数字串会按 ID 的字符包含关系过滤，
/// 名称仍使用不区分大小写的包含匹配；回车允许提交列表之外的数字。
/// </summary>
public sealed class SearchableIdComboBox : ComboBox
{
    private readonly List<IdNameOption> _options = new();
    private readonly Dictionary<int, Bitmap?> _thumbnails = new();
    private readonly LinkedList<int> _thumbnailLru = new();
    private readonly Dictionary<int, LinkedListNode<int>> _thumbnailNodes = new();
    private long _thumbnailBytes;
    private const int MaximumThumbnailCount = 96;
    private const long MaximumThumbnailBytes = 4L * 1024 * 1024;
    private readonly HashSet<int> _loading = new();
    private Func<int, CancellationToken, Task<Bitmap?>>? _previewLoader;
    private CancellationTokenSource _previewCancellation = new();
    private bool _updating;
    private bool _releaseCompleted;
    private bool _bindingSelectionPending;
    private bool _bindingSelectionQueued;
    private bool _boundValueUntouched;
    private int _interactionRevision;
    private bool _idOnly;

    public SearchableIdComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDown;
        AutoCompleteMode = AutoCompleteMode.None;
        AutoCompleteSource = AutoCompleteSource.None;
        IntegralHeight = false;
        MaxDropDownItems = 18;
    }

    public IReadOnlyList<IdNameOption> Options => _options;

    /// <summary>Show only the numeric ID while retaining names for filtering.</summary>
    public bool IdOnly
    {
        get => _idOnly;
        set { _idOnly = value; FormattingEnabled = value; }
    }

    public string GetDisplayText(IdNameOption option)
        => IdOnly ? option.Id.ToString(CultureInfo.InvariantCulture) : option.Display;

    public event EventHandler<int>? UnknownValueSubmitted;

    protected override void OnFormat(ListControlConvertEventArgs e)
    {
        base.OnFormat(e);
        if (IdOnly && e.ListItem is IdNameOption option) e.Value = GetDisplayText(option);
    }

    /// <summary>The loader returns a caller-owned thumbnail; painting never performs file I/O.</summary>
    public void SetPreviewLoader(Func<int, CancellationToken, Task<Bitmap?>>? loader)
    {
        _previewCancellation.Cancel();
        _previewCancellation.Dispose();
        _previewCancellation = new CancellationTokenSource();
        ClearThumbnails();
        _loading.Clear();
        _previewLoader = loader;
        DrawMode = loader == null ? DrawMode.Normal : DrawMode.OwnerDrawVariable;
        MaxDropDownItems = loader == null ? 18 : 8;
        DropDownHeight = loader == null ? 360 : 440;
        Invalidate();
    }

    public void SetOptions(IEnumerable<IdNameOption> options, bool preserveEditSelection = false)
    {
        var updated = options as IReadOnlyList<IdNameOption> ?? options.ToArray();
        if (_options.SequenceEqual(updated)) return;
        if (DroppedDown) DroppedDown = false;
        var start = SelectionStart;
        var length = SelectionLength;
        var text = Text;
        _options.Clear();
        _options.AddRange(updated);
        RebuildItems(text);
        if (preserveEditSelection && Focused)
        {
            SelectionStart = Math.Min(start, Text.Length);
            SelectionLength = Math.Min(length, Text.Length - SelectionStart);
        }
    }

    /// <summary>Programmatic binding must not leave the native edit box looking like selected user text.</summary>
    public void SetBoundValue(string value, bool preserveEditSelection = false)
    {
        var start = SelectionStart;
        var length = SelectionLength;
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            Text = value;
            SelectionStart = preserveEditSelection && Focused ? Math.Min(start, Text.Length) : Text.Length;
            SelectionLength = preserveEditSelection && Focused ? Math.Min(length, Text.Length - SelectionStart) : 0;
            _bindingSelectionPending = !(preserveEditSelection && Focused);
            _boundValueUntouched = _bindingSelectionPending;
            QueueBindingSelectionCleanup();
        }
        finally { _updating = wasUpdating; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SelectionStart = Text.Length;
        SelectionLength = 0;
        QueueBindingSelectionCleanup();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_boundValueUntouched && Visible && !Focused) _bindingSelectionPending = true;
        QueueBindingSelectionCleanup();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_boundValueUntouched && !Focused) _bindingSelectionPending = true;
        QueueBindingSelectionCleanup();
    }

    private void QueueBindingSelectionCleanup()
    {
        if (!_bindingSelectionPending || _bindingSelectionQueued || !IsHandleCreated || IsDisposed || Disposing) return;
        _bindingSelectionQueued = true;
        var text = Text;
        var interaction = _interactionRevision;
        // Native DropDown combo boxes can select their text again when their
        // parent is first shown or auto-scaled, after OnHandleCreated returned.
        // One posted cleanup follows that layout. It only applies to this bound
        // value and never erases selection made by a user in the meantime.
        BeginInvoke((Action)(() =>
        {
            _bindingSelectionQueued = false;
            if (IsDisposed || Disposing || !_bindingSelectionPending || interaction != _interactionRevision) return;
            if (Text != text) { QueueBindingSelectionCleanup(); return; }
            if (Focused || DroppedDown) return;
            SelectionStart = Text.Length;
            SelectionLength = 0;
            _bindingSelectionPending = false;
        }));
    }

    private void MarkUserInteraction()
    {
        _interactionRevision++;
        _bindingSelectionPending = false;
        _boundValueUntouched = false;
    }

    protected override void OnEnter(EventArgs e)
    {
        MarkUserInteraction();
        base.OnEnter(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        MarkUserInteraction();
        base.OnMouseDown(e);
    }

    public bool TryGetSelectedId(out int value)
    {
        if (SelectedItem is IdNameOption option && string.Equals(Text.Trim(), GetDisplayText(option), StringComparison.Ordinal))
        {
            value = option.Id;
            return true;
        }
        var token = Text.Trim().Split(['：', ':', ' '], 2)[0];
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    protected override void OnTextUpdate(EventArgs e)
    {
        base.OnTextUpdate(e);
        if (_updating) return;
        MarkUserInteraction();
        var text = Text;
        RebuildItems(text);
        if (!DroppedDown && Items.Count > 0)
        {
            DroppedDown = true;
            SelectionStart = text.Length;
            SelectionLength = 0;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        MarkUserInteraction();
        if (e.KeyCode == Keys.Enter && TryGetSelectedId(out var id))
        {
            UnknownValueSubmitted?.Invoke(this, id);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnSelectionChangeCommitted(EventArgs e)
    {
        // Owner-drawn editable combos can still expose the previous typed
        // text when the native selection changes. Commit listeners parse
        // Text, so synchronize it before notifying them of the new selection.
        if (SelectedItem is IdNameOption option) SetBoundValue(GetDisplayText(option));
        base.OnSelectionChangeCommitted(e);
    }

    protected override void OnDropDown(EventArgs e)
    {
        // The selected display text is not a new search. Opening an already
        // selected value must expose all choices again.
        if (_options.Any(option => GetDisplayText(option).Equals(Text, StringComparison.Ordinal)))
            RebuildItems(string.Empty, Text);
        base.OnDropDown(e);
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = _previewLoader == null || e.Index < 0 ? Font.Height + 8 : Math.Max(54, Font.Height + 8);
        base.OnMeasureItem(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (_previewLoader == null) { base.OnDrawItem(e); return; }
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= Items.Count) return;
        if (Items[e.Index] is not IdNameOption option) return;
        var editing = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var textBounds = Rectangle.Inflate(e.Bounds, -4, 0);
        if (!editing)
        {
            var bounds = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 3, 48, 48);
            var cached = TryGetThumbnail(option.Id, out var thumbnail);
            if (cached && thumbnail != null)
            {
                var scale = Math.Min((double)bounds.Width / thumbnail.Width, (double)bounds.Height / thumbnail.Height);
                var size = new Size(Math.Max(1, (int)Math.Round(thumbnail.Width * scale)),
                    Math.Max(1, (int)Math.Round(thumbnail.Height * scale)));
                var imageBounds = new Rectangle(bounds.Left + (bounds.Width - size.Width) / 2,
                    bounds.Top + (bounds.Height - size.Height) / 2, size.Width, size.Height);
                e.Graphics.DrawImage(thumbnail, imageBounds);
            }
            else
            {
                using var brush = new SolidBrush(Color.FromArgb(235, 238, 242));
                e.Graphics.FillRectangle(brush, bounds);
                TextRenderer.DrawText(e.Graphics, cached ? "无图" : "加载中", Font, bounds, Color.DimGray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                RequestThumbnail(option.Id);
            }
            textBounds.X += 54;
            textBounds.Width = Math.Max(0, textBounds.Width - 54);
        }
        TextRenderer.DrawText(e.Graphics, GetDisplayText(option), Font, textBounds, e.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private async void RequestThumbnail(int id)
    {
        if (_previewLoader == null || TryGetThumbnail(id, out _) || !_loading.Add(id)) return;
        var cancellation = _previewCancellation;
        var loader = _previewLoader;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await loader(id, cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing) return;
            StoreThumbnail(id, bitmap);
            bitmap = null;
            RefreshPreviewWindow();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cancellation.IsCancellationRequested && !IsDisposed)
            {
                StoreThumbnail(id, null);
                RefreshPreviewWindow();
            }
        }
        finally
        {
            bitmap?.Dispose();
            if (ReferenceEquals(cancellation, _previewCancellation)) _loading.Remove(id);
        }
    }

    private bool TryGetThumbnail(int id, out Bitmap? bitmap)
    {
        if (!_thumbnails.TryGetValue(id, out bitmap)) return false;
        var node = _thumbnailNodes[id];
        _thumbnailLru.Remove(node); _thumbnailLru.AddFirst(node);
        return true;
    }

    private void StoreThumbnail(int id, Bitmap? bitmap)
    {
        RemoveThumbnail(id);
        _thumbnails[id] = bitmap;
        _thumbnailNodes[id] = _thumbnailLru.AddFirst(id);
        if (bitmap != null) _thumbnailBytes += (long)bitmap.Width * bitmap.Height * 4;
        // A custom loader may return one oversized image. Keep that one usable,
        // while normal 48x48 picker images remain bounded by both budgets.
        while (_thumbnails.Count > MaximumThumbnailCount ||
               _thumbnailBytes > MaximumThumbnailBytes && _thumbnails.Count > 1)
            RemoveThumbnail(_thumbnailLru.Last!.Value);
    }

    private void RemoveThumbnail(int id)
    {
        if (!_thumbnails.Remove(id, out var bitmap)) return;
        _thumbnailLru.Remove(_thumbnailNodes[id]); _thumbnailNodes.Remove(id);
        if (bitmap == null) return;
        _thumbnailBytes -= (long)bitmap.Width * bitmap.Height * 4;
        bitmap.Dispose();
    }

    private void ClearThumbnails()
    {
        foreach (var bitmap in _thumbnails.Values) bitmap?.Dispose();
        _thumbnails.Clear(); _thumbnailLru.Clear(); _thumbnailNodes.Clear(); _thumbnailBytes = 0;
    }

    private void RefreshPreviewWindow()
    {
        Invalidate();
        // A native ComboBox owns a separate popup HWND. Invalidating only the
        // edit control leaves newly decoded thumbnails invisible until hover.
        if (!IsHandleCreated || !DroppedDown) return;
        var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
        if (GetComboBoxInfo(Handle, ref info) && info.ListHandle != IntPtr.Zero)
            InvalidateRect(info.ListHandle, IntPtr.Zero, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRect Item;
        public NativeRect Button;
        public int ButtonState;
        public IntPtr ComboHandle;
        public IntPtr EditHandle;
        public IntPtr ListHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr window, ref ComboBoxInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    private void RebuildItems(string query, string? displayText = null)
    {
        var trimmed = query.Trim();
        var filtered = string.IsNullOrWhiteSpace(trimmed)
            ? _options
            : _options.Where(option => option.Id.ToString(CultureInfo.InvariantCulture).Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                                       option.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        var wasUpdating = _updating;
        _updating = true;
        BeginUpdate();
        try
        {
            SelectedIndex = -1;
            Items.Clear();
            foreach (var option in filtered) Items.Add(option);
            SelectedIndex = -1;
            Text = displayText ?? query;
            SelectionStart = Text.Length;
            SelectionLength = 0;
        }
        finally { EndUpdate(); _updating = wasUpdating; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_releaseCompleted)
        {
            _releaseCompleted = true;
            _previewCancellation.Cancel();
            _previewCancellation.Dispose();
            ClearThumbnails();
        }
        base.Dispose(disposing);
    }
}

public sealed record IdNameOption(int Id, string Name)
{
    public string Display => string.IsNullOrWhiteSpace(Name)
        ? $"{Id.ToString(CultureInfo.InvariantCulture)}：未确认"
        : $"{Id.ToString(CultureInfo.InvariantCulture)}：{Name}";

    public override string ToString() => Display;
}
