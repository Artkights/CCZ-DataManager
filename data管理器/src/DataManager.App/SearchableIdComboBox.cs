using ImageManager.Core;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
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
    private readonly Dictionary<int, long> _thumbnailVersions = new();
    private readonly LinkedList<int> _thumbnailLru = new();
    private readonly Dictionary<int, LinkedListNode<int>> _thumbnailNodes = new();
    private long _thumbnailBytes;
    private readonly Dictionary<int, ImageResourceCoordinator.PixelLease> _thumbnailLeases = new();
    private const int MaximumThumbnailCount = 96;
    private const long MaximumThumbnailBytes = 4L * 1024 * 1024;
    private readonly HashSet<int> _loading = new();
    private Func<int, CancellationToken, Task<Bitmap?>>? _previewLoader;
    private CancellationTokenSource _previewCancellation = new();
    private bool _updating;
    private bool _releaseCompleted;
    private bool _previewActive = true;
    private bool _bindingSelectionPending;
    private bool _bindingSelectionQueued;
    private bool _boundValueUntouched;
    private int _interactionRevision;
    private long _editBindingRevision;
    private SuspendedEdit? _suspendedEdit;
    private bool _suspendedEditRestoreQueued;
    private bool _idOnly;
    private bool _rangePreviewMode;
    private string _displayFilterText = string.Empty;
    private long _optionsVersion;
    private (long Version, string Query, bool Range, bool Display)? _itemsBinding;
    private int _boundItemCount;

    private const int RangePreviewImageLogicalSize = 64;

    public SearchableIdComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDown;
        AutoCompleteMode = AutoCompleteMode.None;
        AutoCompleteSource = AutoCompleteSource.None;
        IntegralHeight = false;
        MaxDropDownItems = 18;
    }

    public IReadOnlyList<IdNameOption> Options => _options;

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            // Explicit managed assignments do not raise OnTextUpdate. They
            // supersede any edit snapshot awaiting restoration after showing.
            if (!_updating) { _editBindingRevision++; _suspendedEdit = null; }
            base.Text = value;
        }
    }

    /// <summary>Match the literal ID：name label, including its separator and unnamed label.</summary>
    public bool MatchDisplayText { get; set; }

    /// <summary>Show only the numeric ID while retaining names for filtering.</summary>
    public bool IdOnly
    {
        get => _idOnly;
        set { _idOnly = value; FormattingEnabled = value || _rangePreviewMode; UpdateRangeDropDownMetrics(); Invalidate(); }
    }

    /// <summary>
    /// Presents a range field as a numeric editor while retaining the decoded
    /// range image in each popup row. Names and diagnostic labels remain in the
    /// option model but are intentionally not painted in this mode.
    /// </summary>
    public bool RangePreviewMode
    {
        get => _rangePreviewMode;
        set
        {
            if (_rangePreviewMode == value) return;
            _rangePreviewMode = value;
            FormattingEnabled = _idOnly || value;
            UpdateRangeDropDownMetrics();
            Invalidate();
        }
    }

    public string GetDisplayText(IdNameOption option)
        => IdOnly || RangePreviewMode ? option.Id.ToString(CultureInfo.InvariantCulture) : option.Display;

    public event EventHandler<int>? UnknownValueSubmitted;

    internal void SetPreviewActive(bool active)
    {
        if (_releaseCompleted || _previewActive == active) return;
        _previewActive = active;
        if (!active) { CaptureSuspendedEdit(); SuspendPreviews(); }
        else { QueueSuspendedEditRestore(); RefreshPreviewWindow(); }
    }

    private sealed record SuspendedEdit(string Text, int Start, int Length, int Interaction, long Binding, long Options);

    private void CaptureSuspendedEdit()
    {
        _suspendedEdit ??= new(Text, SelectionStart, SelectionLength, _interactionRevision, _editBindingRevision, _optionsVersion);
    }

    private void QueueSuspendedEditRestore()
    {
        if (_suspendedEdit == null || _suspendedEditRestoreQueued || !_previewActive || !Visible ||
            !IsHandleCreated || _releaseCompleted || IsDisposed || Disposing) return;
        var edit = _suspendedEdit;
        _suspendedEditRestoreQueued = true;
        // Native layout can replace a typed ID prefix with the selected item's
        // label while this view is inactive. Restore only this unchanged edit,
        // after showing/layout, without binding or submitting a new value.
        BeginInvoke((Action)(() =>
        {
            _suspendedEditRestoreQueued = false;
            if (_releaseCompleted || IsDisposed || Disposing || !_previewActive || !Visible) return;
            if (!ReferenceEquals(edit, _suspendedEdit)) { QueueSuspendedEditRestore(); return; }
            if (edit.Interaction != _interactionRevision || edit.Binding != _editBindingRevision || edit.Options != _optionsVersion)
            { _suspendedEdit = null; return; }
            var wasUpdating = _updating;
            _updating = true;
            try
            {
                if (Text != edit.Text) Text = edit.Text;
                SelectionStart = Math.Min(edit.Start, Text.Length);
                SelectionLength = Math.Min(edit.Length, Text.Length - SelectionStart);
                _bindingSelectionPending = false;
                _suspendedEdit = null;
            }
            finally { _updating = wasUpdating; }
        }));
    }

    private void SuspendPreviews()
    {
        _previewCancellation.Cancel(); _previewCancellation.Dispose(); _previewCancellation = new CancellationTokenSource();
        _loading.Clear(); ClearThumbnails();
    }

    protected override void OnFormat(ListControlConvertEventArgs e)
    {
        base.OnFormat(e);
        if ((IdOnly || RangePreviewMode) && e.ListItem is IdNameOption option) e.Value = GetDisplayText(option);
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
        UpdateRangeDropDownMetrics();
        Invalidate();
    }

    private void UpdateRangeDropDownMetrics()
    {
        if (!_rangePreviewMode || _previewLoader == null) return;
        var image = RangePreviewImagePixels;
        DropDownWidth = Math.Max(Width, image + 84 * DeviceDpi / 96);
    }

    private int RangePreviewImagePixels
        => Math.Max(1, (int)Math.Round(RangePreviewImageLogicalSize * DeviceDpi / 96d));

    public void InvalidatePreviewIds(IEnumerable<int> ids)
    {
        foreach (var id in ids.Distinct())
        {
            _thumbnailVersions[id] = _thumbnailVersions.GetValueOrDefault(id) + 1;
            RemoveThumbnail(id);
            _loading.Remove(id);
        }
        RefreshPreviewWindow();
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
        _optionsVersion++;
        RebuildItems(text);
        if (preserveEditSelection && Focused)
        {
            SelectionStart = Math.Min(start, Text.Length);
            SelectionLength = Math.Min(length, Text.Length - SelectionStart);
        }
    }

    /// <summary>Programmatic binding must not leave the native edit box looking like selected user text.</summary>
    public void SetBoundValue(string value, bool preserveEditSelection = false, bool filterOptions = false)
    {
        _editBindingRevision++;
        _suspendedEdit = null;
        var start = SelectionStart;
        var length = SelectionLength;
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            if (filterOptions) RebuildItems(value);
            else Text = value;
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
        QueueSuspendedEditRestore();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible && !_releaseCompleted) CaptureSuspendedEdit();
        base.OnVisibleChanged(e);
        if (!Visible && !_releaseCompleted)
        {
            SuspendPreviews();
        }
        if (_boundValueUntouched && Visible && !Focused) _bindingSelectionPending = true;
        QueueBindingSelectionCleanup();
        QueueSuspendedEditRestore();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_boundValueUntouched && !Focused) _bindingSelectionPending = true;
        QueueBindingSelectionCleanup();
        QueueSuspendedEditRestore();
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
        _suspendedEdit = null;
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
        var start = SelectionStart;
        var length = SelectionLength;
        RebuildItems(text);
        if (!DroppedDown && Items.Count > 0)
        {
            DroppedDown = true;
            // Opening the native list can replace a typed ID prefix with its
            // first suggestion. A query must retain the user's actual input.
            if (MatchDisplayText) SetBoundValue(text, preserveEditSelection: true);
        }
        SelectionStart = Math.Min(start, Text.Length);
        SelectionLength = Math.Min(length, Text.Length - SelectionStart);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        MarkUserInteraction();
        if (MatchDisplayText && e.KeyCode == Keys.Enter)
        {
            // Merely typing a complete label can set SelectedItem. Only a
            // changed label from keyboard navigation commits a suggestion.
            if (SelectedItem is IdNameOption option && Text == option.Display && Text != _displayFilterText)
            {
                OnSelectionChangeCommitted(EventArgs.Empty);
                e.Handled = e.SuppressKeyPress = true;
            }
            else base.OnKeyDown(e);
            return;
        }
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
        MarkUserInteraction();
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
        if (!MatchDisplayText && _options.Any(option => GetDisplayText(option).Equals(Text, StringComparison.Ordinal)))
            RebuildItems(string.Empty, Text);
        else if (MatchDisplayText)
        {
            var text = _displayFilterText;
            var interaction = _interactionRevision;
            RebuildItems(text);
            // The native arrow/F4 path may also select the first prefix match
            // after OnDropDown returns. Do not overwrite later user actions.
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && !Disposing && DroppedDown && interaction == _interactionRevision)
                    SetBoundValue(text, preserveEditSelection: true);
            }));
        }
        base.OnDropDown(e);
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        // Closing without committing can copy the native highlight into the
        // edit box. Keep the typed query until an explicit selection is made.
        if (MatchDisplayText) SetBoundValue(_displayFilterText, preserveEditSelection: true);
        base.OnDropDownClosed(e);
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        if (_previewLoader == null || e.Index < 0)
        {
            e.ItemHeight = Font.Height + 8;
            return;
        }
        var image = _rangePreviewMode ? RangePreviewImagePixels : Math.Max(48, 48 * DeviceDpi / 96);
        e.ItemHeight = Math.Max(image + 6, Font.Height + 8);
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
            var imageSize = _rangePreviewMode ? RangePreviewImagePixels : Math.Max(48, 48 * DeviceDpi / 96);
            var bounds = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 3, imageSize, imageSize);
            var cached = TryGetThumbnail(option.Id, out var thumbnail);
            if (cached && thumbnail != null)
            {
                var scale = Math.Min((double)bounds.Width / thumbnail.Width, (double)bounds.Height / thumbnail.Height);
                var size = new Size(Math.Max(1, (int)Math.Round(thumbnail.Width * scale)),
                    Math.Max(1, (int)Math.Round(thumbnail.Height * scale)));
                var imageBounds = new Rectangle(bounds.Left + (bounds.Width - size.Width) / 2,
                    bounds.Top + (bounds.Height - size.Height) / 2, size.Width, size.Height);
                var interpolation = e.Graphics.InterpolationMode;
                var pixelOffset = e.Graphics.PixelOffsetMode;
                if (_rangePreviewMode)
                {
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                }
                e.Graphics.DrawImage(thumbnail, imageBounds);
                e.Graphics.InterpolationMode = interpolation;
                e.Graphics.PixelOffsetMode = pixelOffset;
            }
            else
            {
                using var brush = new SolidBrush(Color.FromArgb(235, 238, 242));
                e.Graphics.FillRectangle(brush, bounds);
                if (!_rangePreviewMode)
                    TextRenderer.DrawText(e.Graphics, cached ? "无图" : "加载中", Font, bounds, Color.DimGray,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                RequestThumbnail(option.Id);
            }
            var imageGap = Math.Max(6, 8 * DeviceDpi / 96);
            textBounds.X += imageSize + imageGap;
            textBounds.Width = Math.Max(0, textBounds.Width - imageSize - imageGap);
        }
        TextRenderer.DrawText(e.Graphics, GetDisplayText(option), Font, textBounds, e.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private async void RequestThumbnail(int id)
    {
        if (_releaseCompleted || !_previewActive || !Visible || _previewLoader == null || TryGetThumbnail(id, out _) || !_loading.Add(id)) return;
        var cancellation = _previewCancellation;
        var version = _thumbnailVersions.GetValueOrDefault(id);
        var loader = _previewLoader;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await loader(id, cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || Disposing || !_previewActive || !Visible ||
                !ReferenceEquals(cancellation, _previewCancellation) || version != _thumbnailVersions.GetValueOrDefault(id)) return;
            StoreThumbnail(id, bitmap);
            bitmap = null;
            RefreshPreviewWindow();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cancellation.IsCancellationRequested && !IsDisposed && !Disposing && _previewActive && Visible &&
                ReferenceEquals(cancellation, _previewCancellation) && version == _thumbnailVersions.GetValueOrDefault(id))
            {
                StoreThumbnail(id, null);
                RefreshPreviewWindow();
            }
        }
        finally
        {
            bitmap?.Dispose();
            if (ReferenceEquals(cancellation, _previewCancellation) && version == _thumbnailVersions.GetValueOrDefault(id)) _loading.Remove(id);
        }
    }

    private bool TryGetThumbnail(int id, out Bitmap? bitmap)
    {
        if (!_thumbnails.TryGetValue(id, out bitmap)) return false;
        var node = _thumbnailNodes[id];
        _thumbnailLru.Remove(node); _thumbnailLru.AddFirst(node);
        if (_thumbnailLeases.TryGetValue(id, out var lease)) lease.Touch();
        return true;
    }

    private void StoreThumbnail(int id, Bitmap? bitmap)
    {
        RemoveThumbnail(id);
        _thumbnails[id] = bitmap;
        _thumbnailNodes[id] = _thumbnailLru.AddFirst(id);
        if (bitmap != null)
        {
            var bytes = (long)bitmap.Width * bitmap.Height * 4; _thumbnailBytes += bytes;
            _thumbnailLeases[id] = ImageResourceCoordinator.Shared.RegisterPixels(bytes, "picker-thumbnail", () =>
            {
                void Reclaim() { if (_thumbnails.TryGetValue(id, out var live) && ReferenceEquals(live, bitmap)) { RemoveThumbnail(id); RefreshPreviewWindow(); } }
                if (IsDisposed) return;
                if (InvokeRequired) { try { BeginInvoke((Action)Reclaim); } catch (InvalidOperationException) { } } else Reclaim();
            });
        }
        // A custom loader may return one oversized image. Keep that one usable,
        // while normal 48x48 picker images remain bounded by both budgets.
        while (_thumbnails.Count > MaximumThumbnailCount ||
               _thumbnailBytes > MaximumThumbnailBytes && _thumbnails.Count > 1)
            RemoveThumbnail(_thumbnailLru.Last!.Value);
        ImageResourceCoordinator.Shared.TrimPixels();
    }

    private void RemoveThumbnail(int id)
    {
        if (!_thumbnails.Remove(id, out var bitmap)) return;
        _thumbnailLru.Remove(_thumbnailNodes[id]); _thumbnailNodes.Remove(id);
        if (_thumbnailLeases.Remove(id, out var lease)) lease.Dispose();
        if (bitmap == null) return;
        _thumbnailBytes -= (long)bitmap.Width * bitmap.Height * 4;
        bitmap.Dispose();
    }

    private void ClearThumbnails()
    {
        foreach (var lease in _thumbnailLeases.Values) lease.Dispose(); _thumbnailLeases.Clear();
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
        if (MatchDisplayText) _displayFilterText = query;
        var trimmed = query.Trim();
        var binding = (_optionsVersion, trimmed, RangePreviewMode, MatchDisplayText);
        if (_itemsBinding == binding && Items.Count == _boundItemCount)
        {
            var value = displayText ?? query;
            if (Text != value) Text = value;
            return;
        }
        var filtered = string.IsNullOrWhiteSpace(trimmed)
            ? _options
            : _options.Where(option => RangePreviewMode ? option.Id.ToString(CultureInfo.InvariantCulture).Contains(trimmed, StringComparison.OrdinalIgnoreCase) :
                                       MatchDisplayText ? option.Display.Contains(trimmed, StringComparison.OrdinalIgnoreCase) :
                                       option.Id.ToString(CultureInfo.InvariantCulture).Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                                       option.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        var wasUpdating = _updating;
        _updating = true;
        BeginUpdate();
        try
        {
            SelectedIndex = -1;
            Items.Clear();
            Items.AddRange(filtered.Cast<object>().ToArray());
            _itemsBinding = binding;
            _boundItemCount = Items.Count;
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
            _suspendedEdit = null;
            _previewLoader = null;
            _loading.Clear();
            _previewCancellation.Cancel();
            _previewCancellation.Dispose();
            ClearThumbnails();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            // Native UIA may retain the disposed ComboBox. Disposed handlers
            // must run first, then release delegates that capture its page.
            UnknownValueSubmitted = null;
            Events.Dispose();
        }
    }
}

public sealed record IdNameOption(int Id, string Name)
{
    public string Display => string.IsNullOrWhiteSpace(Name)
        ? $"{Id.ToString(CultureInfo.InvariantCulture)}：未确认"
        : $"{Id.ToString(CultureInfo.InvariantCulture)}：{Name}";

    public override string ToString() => Display;
}
