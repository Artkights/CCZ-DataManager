using System.Drawing.Drawing2D;

namespace RangeManager.App;

public enum ShapeLayer { Attack, Pierce }

public sealed class ShapeCellEditEventArgs(CellOffset cell, bool selected) : EventArgs
{
    public CellOffset Cell { get; } = cell;
    public bool Selected { get; } = selected;
}

/// <summary>A geometry-only grid. A range image never supplies its active cells.</summary>
public sealed class ShapeGridControl : Control
{
    public static readonly Color OriginColor = Color.Blue;
    public static readonly Color AttackColor = Color.Red;
    public static readonly Color PierceColor = Color.Lime;
    public static readonly Color GridColor = Color.FromArgb(64, 64, 64);
    public static readonly Color BorderColor = Color.FromArgb(112, 112, 112);
    private HashSet<CellOffset> _attack = [];
    private HashSet<CellOffset> _pierce = [];
    private CellOffset[] _hoverCells = [];
    private RangePixelTiles? _tiles;
    private int _columns = 9, _rows = 9, _cellSize = 20;
    private readonly ToolTip _tip = new();
    private CellOffset? _hover;
    public CellOffset? TargetMarker { get; set; }

    public bool Editable { get; set; }
    public bool ShowingImage => false;
    public CellOffset EditingOrigin { get; set; } = new(0, 0);
    public int Columns => _columns;
    public int Rows => _rows;
    public IReadOnlyList<CellOffset> HoverCellsForTest => _hoverCells;
    public int CellPixelSize => Math.Max(16, _cellSize * DeviceDpi / 96);
    public override Size GetPreferredSize(Size proposedSize) =>
        new(_columns * CellPixelSize + 24, _rows * CellPixelSize + 24);

    public event EventHandler<ShapeCellEditEventArgs>? CellEditRequested;
    public event EventHandler<CellOffset>? CellHovered;
    public event EventHandler? HoverCleared;
    // Kept for the legacy modal editor; the main pages use explicit set/remove events.
    public event EventHandler<CellOffset>? CellClicked;

    public ShapeGridControl()
    {
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public void SetGrid(int columns, int rows, int cellSize = 20)
    {
        _columns = Math.Clamp(columns | 1, 3, 511);
        _rows = Math.Clamp(rows | 1, 3, 511);
        _cellSize = Math.Clamp(cellSize, 16, 64);
        Invalidate();
    }

    public void SetLayers(IEnumerable<CellOffset> attack, IEnumerable<CellOffset> pierce, string caption = "", RangePixelTiles? tiles = null)
    {
        _attack = attack.ToHashSet();
        _pierce = pierce.ToHashSet();
        _tiles = tiles;
        _hover = null;
        Invalidate();
    }

    public void SetHoverCells(IEnumerable<CellOffset> cells)
    {
        _hoverCells = cells.ToArray();
        Invalidate();
    }

    public void SetShape(IEnumerable<CellOffset> cells, ShapeLayer layer, int radius, string caption)
    {
        var values = cells.ToArray();
        EditingOrigin = new(0, 0);
        SetLayers(layer == ShapeLayer.Attack ? values : [], layer == ShapeLayer.Pierce ? values : [], caption);
        SetRadius(radius);
    }

    public void SetRadius(int radius) => SetGrid(Math.Clamp(radius, 1, 255) * 2 + 1,
        Math.Clamp(radius, 1, 255) * 2 + 1, _cellSize);

    /// <summary>Compatibility API: bitmap previews have intentionally been retired.</summary>
    public void SetImage(Image? image, string caption) { }

    public RangeGridLayout GridLayout
    {
        get
        {
            int width = _columns * CellPixelSize, height = _rows * CellPixelSize;
            return new(new Rectangle(Math.Max(12, (ClientSize.Width - width) / 2),
                Math.Max(12, (ClientSize.Height - height - 24) / 2), width, height),
                _columns, _rows, CellPixelSize);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.None;
        var layout = GridLayout;
        var bounds = layout.Bounds;
        RangeBitmapRenderer.Draw(e.Graphics, layout, _attack, _pierce, TargetMarker, tiles: _tiles);
        using var hoverPen = new Pen(Color.White) { DashStyle = DashStyle.Dot };
        foreach (var cell in _hoverCells)
        {
            var rect = Rectangle.Inflate(layout.CellRectangle(cell), -2, -2);
            e.Graphics.DrawRectangle(hoverPen, rect);
        }
        using var border = new Pen(_tiles?.Frame?.GetPixel(0, 0) ?? BorderColor);
        e.Graphics.DrawRectangle(border, bounds);
    }

    public bool TryHitTest(Point location, out CellOffset cell)
    {
        return GridLayout.TryHitTest(location, out cell);
    }

    public Point CellCenterForTest(CellOffset cell)
    {
        var rect = GridLayout.CellRectangle(cell);
        return new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Editable || e.Button is not (MouseButtons.Left or MouseButtons.Right) || !TryHitTest(e.Location, out var world)) return;
        Focus();
        CellEditRequested?.Invoke(this, new(world, e.Button == MouseButtons.Left));
        if (e.Button == MouseButtons.Left) CellClicked?.Invoke(this, world);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Editable ? Cursors.Cross : Cursors.Default;
        if (!TryHitTest(e.Location, out var cell))
        {
            if (_hover != null) { _hover = null; SetHoverCells([]); }
            return;
        }
        if (cell == _hover) return;
        _hover = cell;
        _tip.SetToolTip(this, $"坐标 ({cell.Dx}, {cell.Dy})；左键选中，右键取消");
        if (Editable) CellHovered?.Invoke(this, cell);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        SetHoverCells([]);
        HoverCleared?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
