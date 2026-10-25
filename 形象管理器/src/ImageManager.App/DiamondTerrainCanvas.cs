using System.Drawing.Drawing2D;
using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Draws the scene and edits terrain cells without changing the scene bitmap.</summary>
public sealed class DiamondTerrainCanvas : Control
{
    private readonly SceneTerrainDocument _document;
    private readonly GraphicsPath _grid = new();
    private readonly PointF[] _centers;
    private double _zoom = 1;
    private bool _showTerrain = true;
    private bool _showGrid = true;
    private bool _editingEnabled = true;
    private MouseButtons _strokeButton;
    private PointF _lastSource;
    private Point? _hover;

    public DiamondTerrainCanvas(SceneTerrainDocument document)
    {
        _document = document;
        Name = "DiamondTerrainCanvas";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        BackColor = Color.FromArgb(55, 57, 60);
        _centers = new PointF[checked(document.Columns * document.Rows)];
        for (var y = 0; y < document.Rows; y++)
        for (var x = 0; x < document.Columns; x++)
        {
            _centers[y * document.Columns + x] = document.Geometry.CellCenter(x, y);
            _grid.AddPolygon(document.Geometry.CellPolygon(x, y));
        }
        UpdateSize();
    }

    public event Action<Point?>? HoverCellChanged;
    public event Action? DocumentChanged;
    public event Action<MouseEventArgs>? ZoomRequested;
    public Size SourceSize => _document.Background.Size;
    public double Zoom
    {
        get => _zoom;
        set
        {
            StopStroke();
            _zoom = Math.Clamp(value, .25, 8);
            UpdateSize();
            Invalidate();
        }
    }
    public bool ShowTerrain
    {
        get => _showTerrain;
        set { if (!value) StopStroke(); _showTerrain = value; UpdateCursor(); Invalidate(); }
    }
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; Invalidate(); }
    }
    public bool EditingEnabled
    {
        get => _editingEnabled;
        set { if (!value) StopStroke(); _editingEnabled = value; UpdateCursor(); }
    }

    public void Undo()
    {
        StopStroke();
        if (EditingEnabled && _document.Undo()) Changed();
    }

    public void Redo()
    {
        StopStroke();
        if (EditingEnabled && _document.Redo()) Changed();
    }

    public void StopStroke()
    {
        if (_strokeButton == MouseButtons.None) return;
        _strokeButton = MouseButtons.None;
        if (Capture) Capture = false;
        if (_document.EndStroke()) Changed();
    }

    public void RefreshHoverFromMouse()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var screenPoint = Cursor.Position;
        for (Control? control = this; control != null; control = control.Parent)
        {
            if (!control.Visible || !control.ClientRectangle.Contains(control.PointToClient(screenPoint)))
            {
                SetHover(null);
                return;
            }
        }
        SetHover(_document.Geometry.HitTest(ToSource(PointToClient(screenPoint))));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.ScaleTransform((float)Zoom, (float)Zoom);
        e.Graphics.DrawImage(_document.Background, new Rectangle(Point.Empty, SourceSize),
            0, 0, SourceSize.Width, SourceSize.Height, GraphicsUnit.Pixel);
        if (ShowGrid)
        {
            using var pen = new Pen(Color.FromArgb(145, 12, 12, 12), (float)(1 / Zoom));
            e.Graphics.DrawPath(pen, _grid);
        }
        if (ShowTerrain)
        {
            var visible = new RectangleF((float)(e.ClipRectangle.X / Zoom), (float)(e.ClipRectangle.Y / Zoom),
                (float)(e.ClipRectangle.Width / Zoom), (float)(e.ClipRectangle.Height / Zoom));
            var diameter = (float)Math.Max(4, 2 / Zoom);
            visible.Inflate(diameter, diameter);
            for (var y = 0; y < _document.Rows; y++)
            for (var x = 0; x < _document.Columns; x++)
            {
                var center = _centers[y * _document.Columns + x];
                if (_document.IsBlocked(x, y) && visible.Contains(center))
                    e.Graphics.FillRectangle(Brushes.Red, center.X - diameter / 2, center.Y - diameter / 2, diameter, diameter);
            }
        }
        if (_hover is { } cell)
        {
            using var pen = new Pen(Color.FromArgb(235, 255, 210, 45), (float)(1.5 / Zoom));
            e.Graphics.DrawPolygon(pen, _document.Geometry.CellPolygon(cell.X, cell.Y));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        StopStroke();
        var source = ToSource(e.Location);
        SetHover(_document.Geometry.HitTest(source));
        if (!ShowTerrain || !EditingEnabled || !_document.CanEdit || _hover == null ||
            e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        _document.BeginStroke();
        _strokeButton = e.Button;
        _lastSource = source;
        Capture = true;
        PaintCells(source, source);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var source = ToSource(e.Location);
        var cell = _document.Geometry.HitTest(source);
        SetHover(cell);
        if (cell == null) { StopStroke(); return; }
        if (_strokeButton == MouseButtons.None) return;
        if ((MouseButtons & _strokeButton) == 0) { StopStroke(); return; }
        PaintCells(_lastSource, source);
        _lastSource = source;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == _strokeButton)
        {
            var source = ToSource(e.Location);
            if (_document.Geometry.HitTest(source) != null) PaintCells(_lastSource, source);
            StopStroke();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) StopStroke();
    }

    protected override void OnLostFocus(EventArgs e) { StopStroke(); base.OnLostFocus(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        StopStroke();
        SetHover(null);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        StopStroke();
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            ZoomRequested?.Invoke(e);
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        StopStroke();
        // WinForms scales child bounds when the monitor DPI changes. Keep the
        // scroll surface tied to the same source-pixel zoom used for paint and hit tests.
        UpdateSize();
        Invalidate();
        RefreshHoverFromMouse();
    }

    private PointF ToSource(Point point) => new((float)(point.X / Zoom), (float)(point.Y / Zoom));
    private void PaintCells(PointF from, PointF to)
    {
        if (_strokeButton == MouseButtons.None) return;
        var changed = false;
        foreach (var cell in _document.Geometry.TraceCells(from, to))
            changed |= _document.SetCell(cell.X, cell.Y, _strokeButton == MouseButtons.Left);
        if (changed) Changed();
    }
    private void SetHover(Point? cell)
    {
        if (_hover == cell) return;
        _hover = cell;
        HoverCellChanged?.Invoke(cell);
        Invalidate();
    }
    private void Changed() { Invalidate(); DocumentChanged?.Invoke(); }
    private void UpdateSize() => Size = new Size(Math.Max(1, (int)Math.Ceiling(SourceSize.Width * Zoom)),
        Math.Max(1, (int)Math.Ceiling(SourceSize.Height * Zoom)));
    private void UpdateCursor() => Cursor = ShowTerrain && EditingEnabled && _document.CanEdit ? Cursors.Cross : Cursors.Default;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopStroke(); _grid.Dispose(); }
        base.Dispose(disposing);
    }
}
