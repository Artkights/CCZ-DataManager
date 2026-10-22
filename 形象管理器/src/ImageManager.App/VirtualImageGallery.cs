using ImageManager.Core;

namespace ImageManager.App;

public sealed class VirtualImageGallery : UserControl
{
    private const int CardWidth = 222;
    private const int CardHeight = 188;
    private const int HorizontalGap = 10;
    private const int VerticalGap = 10;
    private const int BufferRows = 2;
    private readonly Dictionary<int, Control> _realized = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Panel _canvas = new BufferedGalleryPanel { Location = Point.Empty, Margin = Padding.Empty };
    private IReadOnlyList<LogicalImageAsset> _items = Array.Empty<LogicalImageAsset>();
    private Func<LogicalImageAsset, Control>? _cardFactory;
    private int _realizeQueued;
    private bool _updatingSelection;
    private string? _focusedId;

    public event Action? SelectionChanged;

    public VirtualImageGallery()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        DoubleBuffered = true;
        Padding = new Padding(8);
        Controls.Add(_canvas);
        Scroll += (_, _) => QueueRealize();
        Resize += (_, _) => { UpdateScrollExtent(); QueueRealize(); };
        VisibleChanged += (_, _) => { if (Visible) QueueRealize(); };
    }

    public IReadOnlyList<LogicalImageAsset> SelectedItems
        => _items.Where(item => _selected.Contains(item.StablePhysicalId)).ToArray();
    public int ItemCount => _items.Count;
    public int RealizedCount => _realized.Count;
    public IReadOnlyList<Control> RealizedControls => _realized.Values.ToArray();
    public string? FocusedAssetId
    {
        get => _focusedId;
        set
        {
            if (_focusedId == value) return;
            _focusedId = value;
            foreach (var card in _realized.Values) UpdateFocus(card);
        }
    }

    public void RefreshViewport() { UpdateScrollExtent(); QueueRealize(); }

    public void ScrollToItem(int index)
    {
        if (index < 0 || index >= _items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        UpdateScrollExtent();
        var target = Math.Min(index / ColumnCount() * (CardHeight + VerticalGap),
            Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));
        AutoScrollPosition = new Point(0, target);
        RealizeVisibleRows();
    }

    public void SetItems(IReadOnlyList<LogicalImageAsset> items, Func<LogicalImageAsset, Control> cardFactory,
        bool preserveSelection, bool reuseCards = true)
    {
        var scroll = -AutoScrollPosition.Y;
        var anchorIndex = Math.Min(_items.Count - 1, scroll / (CardHeight + VerticalGap) * ColumnCount());
        var anchor = anchorIndex >= 0 ? _items[anchorIndex].StablePhysicalId : null;
        if (!preserveSelection) _selected.Clear();
        else _selected.IntersectWith(items.Select(item => item.StablePhysicalId));
        var retained = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _realized.ToArray())
        {
            _realized.Remove(pair.Key);
            retained[((LogicalImageAsset)pair.Value.Tag!).StablePhysicalId] = pair.Value;
        }
        _items = items;
        _cardFactory = cardFactory;
        for (var index = 0; index < items.Count; index++)
        {
            if (!retained.TryGetValue(items[index].StablePhysicalId, out var card)) continue;
            if (!reuseCards || !Equals(card.Tag, items[index])) continue;
            retained.Remove(items[index].StablePhysicalId);
            _realized[index] = card;
            SetCardChecked(card, _selected.Contains(items[index].StablePhysicalId));
        }
        foreach (var card in retained.Values) DisposeCard(card);
        UpdateScrollExtent();
        var anchorPosition = preserveSelection && anchor != null
            ? Enumerable.Range(0, items.Count).FirstOrDefault(index => items[index].StablePhysicalId == anchor, -1) : -1;
        var desiredScroll = !preserveSelection ? 0 : anchorPosition >= 0
            ? anchorPosition / ColumnCount() * (CardHeight + VerticalGap) + scroll % (CardHeight + VerticalGap) : scroll;
        AutoScrollPosition = new Point(0, Math.Min(desiredScroll, Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height)));
        QueueRealize();
        SelectionChanged?.Invoke();
    }

    public void ClearItems()
    {
        _items = Array.Empty<LogicalImageAsset>();
        _selected.Clear();
        FocusedAssetId = null;
        RecycleAll();
        AutoScrollMinSize = Size.Empty;
        _canvas.Size = Size.Empty;
        SelectionChanged?.Invoke();
    }

    public void SelectAll(bool selected)
    {
        if (selected) _selected.UnionWith(_items.Select(item => item.StablePhysicalId));
        else foreach (var item in _items) _selected.Remove(item.StablePhysicalId);
        foreach (var card in _realized.Values) SetCardChecked(card, selected);
        SelectionChanged?.Invoke();
    }

    private void SetCardChecked(Control card, bool value)
    {
        if (Selector(card) is not { } check) return;
        _updatingSelection = true;
        try { check.Checked = value; }
        finally { _updatingSelection = false; }
    }

    private static CheckBox? Selector(Control card)
        => card.Controls.OfType<CheckBox>().FirstOrDefault(control => Equals(control.Tag, "selector"));

    protected override void Dispose(bool disposing)
    {
        if (disposing) RecycleAll();
        base.Dispose(disposing);
    }

    private void UpdateScrollExtent()
    {
        var columns = ColumnCount();
        var rows = (_items.Count + columns - 1) / columns;
        var desired = new Size(Padding.Horizontal + columns * CardWidth + (columns - 1) * HorizontalGap,
            Padding.Vertical + rows * (CardHeight + VerticalGap));
        if (_canvas.Size != desired) _canvas.Size = desired;
        if (AutoScrollMinSize != desired) AutoScrollMinSize = desired;
    }

    private void RealizeVisibleRows()
    {
        if (_cardFactory == null || _items.Count == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0 || !Visible) return;
        var columns = ColumnCount();
        var scrollY = -AutoScrollPosition.Y;
        var rowHeight = CardHeight + VerticalGap;
        var firstRow = Math.Max(0, scrollY / rowHeight - BufferRows);
        var lastRow = Math.Min((_items.Count - 1) / columns, (scrollY + ClientSize.Height) / rowHeight + BufferRows);
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(_items.Count - 1, (lastRow + 1) * columns - 1);
        _canvas.SuspendLayout();
        try
        {
            foreach (var index in _realized.Keys.Where(index => index < firstIndex || index > lastIndex).ToArray()) Recycle(index);
            for (var index = firstIndex; index <= lastIndex; index++)
            {
                var isNew = !_realized.TryGetValue(index, out var card);
                var asset = _items[index];
                if (isNew)
                {
                    card = _cardFactory(asset);
                    card.Tag = asset;
                    SetCardChecked(card, _selected.Contains(asset.StablePhysicalId));
                    if (Selector(card) is { } check)
                        check.CheckedChanged += (_, _) =>
                        {
                            if (_updatingSelection) return;
                            if (check.Checked) _selected.Add(asset.StablePhysicalId);
                            else _selected.Remove(asset.StablePhysicalId);
                            SelectionChanged?.Invoke();
                        };
                    _realized[index] = card;
                    UpdateFocus(card);
                }
                var bounds = new Rectangle(Padding.Left + index % columns * (CardWidth + HorizontalGap),
                    Padding.Top + index / columns * rowHeight, CardWidth, CardHeight);
                if (card!.Bounds != bounds) card.Bounds = bounds;
                if (isNew) _canvas.Controls.Add(card);
            }
        }
        finally { _canvas.ResumeLayout(false); }
    }

    private void UpdateFocus(Control card)
    {
        card.BackColor = card.Tag is LogicalImageAsset asset && asset.StablePhysicalId == _focusedId
            ? Color.FromArgb(190, 219, 255) : Color.White;
        card.Invalidate();
    }

    private int ColumnCount()
        => Math.Max(1, (Math.Max(CardWidth, ClientSize.Width - Padding.Horizontal) + HorizontalGap) / (CardWidth + HorizontalGap));

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueRealize(); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        UpdateScrollExtent();
        base.OnLayout(e);
        if (IsHandleCreated) QueueRealize();
    }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); QueueRealize(); }

    private void QueueRealize()
    {
        if (!IsHandleCreated || IsDisposed || Interlocked.Exchange(ref _realizeQueued, 1) == 1) return;
        BeginInvoke((Action)(() =>
        {
            Interlocked.Exchange(ref _realizeQueued, 0);
            if (!IsDisposed) RealizeVisibleRows();
        }));
    }

    private void RecycleAll() { foreach (var index in _realized.Keys.ToArray()) Recycle(index); }
    private void Recycle(int index)
    {
        if (_realized.Remove(index, out var card)) DisposeCard(card);
    }

    private void DisposeCard(Control card)
    {
        _canvas.Controls.Remove(card);
        DisposeImages(card);
        card.Dispose();
    }

    private static void DisposeImages(Control root)
    {
        if (root is PictureBox { Image: { } image } picture) { picture.Image = null; image.Dispose(); }
        foreach (Control child in root.Controls) DisposeImages(child);
    }
}

internal sealed class BufferedGalleryPanel : Panel
{
    public BufferedGalleryPanel() { DoubleBuffered = true; ResizeRedraw = true; }
}
