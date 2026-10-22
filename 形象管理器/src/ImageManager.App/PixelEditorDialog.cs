using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ImageManager.App;

public enum PixelSaveMode { WriteCurrent, AppendCopy }

public sealed record PixelEditorSection(string Name, Rectangle Bounds, int FrameHeight, string SourceFile);

public sealed class PixelEditorDialog : Form
{
    private readonly PixelCanvas _canvas;
    private readonly IReadOnlyList<Color>? _replacementPalette;
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(55, 57, 60) };
    private readonly Button _colorButton = new() { Width = 36, Height = 30, BackColor = Color.Black, FlatStyle = FlatStyle.Flat };
    private readonly NumericUpDown _zoom = new() { Minimum = 25, Maximum = 3200, Increment = 25, Value = 100, Width = 82 };
    public Bitmap? ResultBitmap { get; private set; }
    public PixelSaveMode SaveMode { get; private set; }

    public PixelEditorDialog(Bitmap source, string title)
        : this(source, title, [new PixelEditorSection("图片", new Rectangle(Point.Empty, source.Size), 0, string.Empty)]) { }

    public PixelEditorDialog(Bitmap source, string title, IReadOnlyList<PixelEditorSection> sections,
        IReadOnlyList<Color>? replacementPalette = null, bool allowAppendCopy = true)
    {
        Text = "像素编辑 - " + title;
        ClientSize = WorkingSize(1180, 820);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        _replacementPalette = replacementPalette;
        _canvas = new PixelCanvas(source, sections) { Location = Point.Empty };
        _scroll.Controls.Add(_canvas);
        Controls.Add(_scroll);
        Controls.Add(BuildBottomBar(allowAppendCopy));
        Controls.Add(BuildToolbar());
        _canvas.ColorPicked += color => { _colorButton.BackColor = color; _canvas.DrawingColor = color; };
        _canvas.ZoomChanged += zoom =>
        {
            var percent = Math.Clamp((decimal)Math.Round(zoom * 100), _zoom.Minimum, _zoom.Maximum);
            if (_zoom.Value != percent) _zoom.Value = percent;
        };
        _canvas.MouseWheel += CanvasMouseWheel;
        Shown += (_, _) => FitInitial();
    }

    private Control BuildToolbar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 46),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(7), WrapContents = true
        };
        foreach (var item in new[]
                 {
                     ("铅笔", PixelTool.Pencil), ("橡皮", PixelTool.Eraser), ("取色", PixelTool.Eyedropper),
                     ("填充", PixelTool.Fill), ("直线", PixelTool.Line), ("矩形", PixelTool.Rectangle)
                 })
        {
            var button = ToolButton(item.Item1);
            button.Click += (_, _) => _canvas.Tool = item.Item2;
            panel.Controls.Add(button);
        }
        var undo = ToolButton("撤回");
        var redo = ToolButton("重做");
        undo.Click += (_, _) => _canvas.Undo();
        redo.Click += (_, _) => _canvas.Redo();
        panel.Controls.Add(undo);
        panel.Controls.Add(redo);
        _colorButton.Click += (_, _) => ChooseColor(_colorButton);
        panel.Controls.Add(_colorButton);
        var replace = ToolButton("换色");
        replace.Click += (_, _) => OpenColorReplacement();
        panel.Controls.Add(replace);
        var grid = new CheckBox { Text = "网格", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 2, 0) };
        grid.CheckedChanged += (_, _) => _canvas.ShowGrid = grid.Checked;
        panel.Controls.Add(grid);
        panel.Controls.Add(new Label { Text = "缩放 %", AutoSize = true, Margin = new Padding(8, 8, 2, 0) });
        _zoom.ValueChanged += (_, _) => SetZoom((double)_zoom.Value / 100, null);
        panel.Controls.Add(_zoom);
        var fit = ToolButton("适合宽度");
        fit.Click += (_, _) => FitWidth();
        var actual = ToolButton("100%");
        actual.Click += (_, _) => SetZoom(1, null);
        panel.Controls.Add(fit);
        panel.Controls.Add(actual);
        return panel;
    }

    private Control BuildBottomBar(bool allowAppendCopy)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 50), FlowDirection = FlowDirection.RightToLeft,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true
        };
        var write = CommandButton("写回当前（立即保存）");
        var append = CommandButton("复制完整形象并追加（立即保存）");
        var cancel = CommandButton("取消");
        write.Click += (_, _) => Finish(PixelSaveMode.WriteCurrent);
        append.Click += (_, _) => Finish(PixelSaveMode.AppendCopy);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        panel.Controls.Add(write);
        if (allowAppendCopy) panel.Controls.Add(append);
        panel.Controls.Add(cancel);
        return panel;
    }

    private void OpenColorReplacement()
    {
        using var dialog = new PixelColorReplaceDialog(_canvas.CountColor, _replacementPalette);
        if (dialog.ShowDialog(this) == DialogResult.OK) _canvas.ReplaceColors(dialog.Pairs);
    }

    private void CanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != Keys.Control) return;
        var oldZoom = _canvas.Zoom;
        var factor = e.Delta > 0 ? 1.25 : .8;
        var newZoom = Math.Clamp(oldZoom * factor, .25, 32);
        var sourceX = e.X / oldZoom;
        var sourceY = e.Y / oldZoom;
        SetZoom(newZoom, new PointF((float)sourceX, (float)sourceY), e.Location);
    }

    private void FitWidth()
    {
        var available = Math.Max(1, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);
        SetZoom(Math.Clamp((double)available / _canvas.SourceSize.Width, .25, 32), null);
    }

    private void FitInitial()
    {
        var available = Math.Max(1, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 12);
        SetZoom(Math.Clamp(Math.Min(1, (double)available / _canvas.SourceSize.Width), .25, 32), null);
    }

    private void SetZoom(double zoom, PointF? anchor, Point? mouse = null)
    {
        var old = _canvas.Zoom;
        if (Math.Abs(old - zoom) < .0001) return;
        var scrollX = -_scroll.AutoScrollPosition.X;
        var scrollY = -_scroll.AutoScrollPosition.Y;
        var viewport = mouse ?? new Point(_scroll.ClientSize.Width / 2, _scroll.ClientSize.Height / 2);
        var source = anchor ?? new PointF((float)((scrollX + viewport.X) / old), (float)((scrollY + viewport.Y) / old));
        _canvas.Zoom = zoom;
        var targetX = Math.Max(0, (int)Math.Round(source.X * zoom - viewport.X));
        var targetY = Math.Max(0, (int)Math.Round(source.Y * zoom - viewport.Y));
        _scroll.AutoScrollPosition = new Point(targetX, targetY);
    }

    private void Finish(PixelSaveMode mode)
    {
        ResultBitmap?.Dispose();
        ResultBitmap = _canvas.Snapshot();
        SaveMode = mode;
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z)) { _canvas.Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { _canvas.Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ChooseColor(Button button)
    {
        using var dialog = new ColorDialog { Color = button.BackColor, FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        button.BackColor = dialog.Color;
        if (button == _colorButton) _canvas.DrawingColor = dialog.Color;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ResultBitmap?.Dispose();
        base.Dispose(disposing);
    }

    private static Button ToolButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(58, 30), Margin = new Padding(2) };
    private static Button CommandButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(88, 32), Margin = new Padding(3) };

    private static Size WorkingSize(int width, int height)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, height);
        return new Size(Math.Min(width, (int)(area.Width * .9)), Math.Min(height, (int)(area.Height * .9)));
    }
}

public sealed class PixelColorReplaceDialog : Form
{
    private readonly Func<Color, int> _count;
    private readonly List<(CheckBox Enabled, Button From, Button To, Label Hits)> _rows = [];
    public IReadOnlyList<(Color From, Color To)> Pairs { get; private set; } = Array.Empty<(Color, Color)>();

    private readonly IReadOnlyList<Color>? _palette;

    public PixelColorReplaceDialog(Func<Color, int> count, IReadOnlyList<Color>? palette = null)
    {
        _count = count;
        _palette = palette;
        Text = "精确换色";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(430, 300);
        MaximumSize = new Size(760, 720);
        StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 8, Padding = new Padding(12) };
        if (_palette != null)
        {
            var note = new Label { Text = "索引资源：目标色必须从项目调色板选择；透明像素请使用橡皮工具。",
                AutoSize = true, ForeColor = Color.DarkSlateBlue, Margin = new Padding(0, 0, 0, 8) };
            root.Controls.Add(note, 0, 0);
            root.SetColumnSpan(note, 5);
        }
        var headerRow = _palette == null ? 0 : 1;
        root.Controls.Add(new Label { Text = "使用", AutoSize = true }, 0, headerRow);
        root.Controls.Add(new Label { Text = "原色", AutoSize = true }, 1, headerRow);
        root.Controls.Add(new Label { Text = "", AutoSize = true }, 2, headerRow);
        root.Controls.Add(new Label { Text = "目标色", AutoSize = true }, 3, headerRow);
        root.Controls.Add(new Label { Text = "命中", AutoSize = true }, 4, headerRow);
        for (var index = 0; index < 5; index++)
        {
            var enabled = new CheckBox { Checked = index == 0, AutoSize = true, Anchor = AnchorStyles.None };
            var from = Swatch(Color.Magenta);
            var to = Swatch(_palette?.FirstOrDefault() ?? Color.Transparent);
            var hits = new Label { AutoSize = true, Text = "0", Anchor = AnchorStyles.Left };
            from.Click += (_, _) => { Choose(from); UpdateHits(); };
            to.Click += (_, _) => ChooseTarget(to);
            enabled.CheckedChanged += (_, _) => UpdateHits();
            _rows.Add((enabled, from, to, hits));
            var row = headerRow + index + 1;
            root.Controls.Add(enabled, 0, row);
            root.Controls.Add(from, 1, row);
            root.Controls.Add(new Label { Text = "→", AutoSize = true, Anchor = AnchorStyles.None }, 2, row);
            root.Controls.Add(to, 3, row);
            root.Controls.Add(hits, 4, row);
        }
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var apply = CommandButton("同时替换");
        var cancel = CommandButton("取消");
        apply.Click += (_, _) =>
        {
            Pairs = _rows.Where(row => row.Enabled.Checked).Select(row => (row.From.BackColor, row.To.BackColor)).ToArray();
            if (Pairs.Count == 0) { MessageBox.Show(this, "至少启用一组换色。", "没有换色组"); return; }
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, headerRow + 6);
        root.SetColumnSpan(buttons, 5);
        Controls.Add(root);
        UpdateHits();
    }

    private void UpdateHits()
    {
        foreach (var row in _rows) row.Hits.Text = row.Enabled.Checked ? _count(row.From.BackColor).ToString() : "-";
    }

    private static void Choose(Button button)
    {
        using var dialog = new ColorDialog { Color = button.BackColor, FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog() == DialogResult.OK) button.BackColor = dialog.Color;
    }

    private void ChooseTarget(Button button)
    {
        if (_palette == null) { Choose(button); return; }
        using var dialog = new PalettePickerDialog(_palette, button.BackColor);
        if (dialog.ShowDialog(this) == DialogResult.OK) button.BackColor = dialog.SelectedColor;
    }

    private static Button Swatch(Color color) => new() { Width = 64, Height = 30, BackColor = color, FlatStyle = FlatStyle.Flat };
    private static Button CommandButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(88, 32) };
}

internal sealed class PalettePickerDialog : Form
{
    public PalettePickerDialog(IReadOnlyList<Color> palette, Color selected)
    {
        Text = "选择调色板颜色";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(530, 510);
        MinimumSize = new Size(430, 400);
        StartPosition = FormStartPosition.CenterParent;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
            Padding = new Padding(8) };
        var tip = new ToolTip();
        for (var index = 0; index < Math.Min(256, palette.Count); index++)
        {
            var color = palette[index];
            var swatch = new Button { Width = 27, Height = 27, Margin = new Padding(1), BackColor = color,
                FlatStyle = FlatStyle.Flat, TabStop = false, Tag = color };
            swatch.FlatAppearance.BorderSize = color.ToArgb() == selected.ToArgb() ? 3 : 1;
            tip.SetToolTip(swatch, $"索引 {index:000}  #{color.R:X2}{color.G:X2}{color.B:X2}");
            swatch.Click += (_, _) => { SelectedColor = (Color)swatch.Tag; DialogResult = DialogResult.OK; };
            flow.Controls.Add(swatch);
        }
        var cancelBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(88, 32) };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        cancelBar.Controls.Add(cancel);
        Controls.Add(flow);
        Controls.Add(cancelBar);
    }

    public Color SelectedColor { get; private set; }
}

internal enum PixelTool { Pencil, Eraser, Eyedropper, Fill, Line, Rectangle }

internal sealed class PixelCanvas : Control
{
    private Bitmap _bitmap;
    private readonly IReadOnlyList<PixelEditorSection> _sections;
    private readonly Stack<Bitmap> _undo = new();
    private readonly Stack<Bitmap> _redo = new();
    private double _zoom = 1;
    private bool _showGrid = true;
    private bool _drawing;
    private Point _start;
    private Point _last;
    private Rectangle _activeBounds;

    public PixelCanvas(Bitmap source, IReadOnlyList<PixelEditorSection> sections)
    {
        _bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(_bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(source, 0, 0);
        }
        _sections = sections.Count == 0
            ? [new PixelEditorSection("图片", new Rectangle(Point.Empty, source.Size), 0, string.Empty)]
            : sections;
        DoubleBuffered = true;
        Tool = PixelTool.Pencil;
        DrawingColor = Color.Black;
        UpdateSize();
    }

    public event Action<Color>? ColorPicked;
    public event Action<double>? ZoomChanged;
    public PixelTool Tool { get; set; }
    public Color DrawingColor { get; set; }
    public Size SourceSize => _bitmap.Size;
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, .25, 32);
            UpdateSize();
            Invalidate();
            ZoomChanged?.Invoke(_zoom);
        }
    }
    public bool ShowGrid { get => _showGrid; set { _showGrid = value; Invalidate(); } }

    public Bitmap Snapshot() => new(_bitmap);
    public int CountColor(Color color)
    {
        var count = 0;
        foreach (var section in _sections)
        for (var y = section.Bounds.Top; y < section.Bounds.Bottom; y++)
        for (var x = section.Bounds.Left; x < section.Bounds.Right; x++)
            if (_bitmap.GetPixel(x, y).ToArgb() == color.ToArgb()) count++;
        return count;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(new Bitmap(_bitmap));
        _bitmap.Dispose();
        _bitmap = _undo.Pop();
        Invalidate();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(new Bitmap(_bitmap));
        _bitmap.Dispose();
        _bitmap = _redo.Pop();
        Invalidate();
    }

    public void ReplaceColors(IReadOnlyList<(Color From, Color To)> pairs)
    {
        if (pairs.Count == 0) return;
        PushUndo();
        foreach (var section in _sections)
        for (var y = section.Bounds.Top; y < section.Bounds.Bottom; y++)
        for (var x = section.Bounds.Left; x < section.Bounds.Right; x++)
        {
            var original = _bitmap.GetPixel(x, y);
            var replacement = pairs.FirstOrDefault(pair => pair.From.ToArgb() == original.ToArgb());
            if (replacement != default) _bitmap.SetPixel(x, y, replacement.To);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var checker = CheckerBrush()) e.Graphics.FillRectangle(checker, ClientRectangle);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_bitmap, new Rectangle(0, 0, Scaled(_bitmap.Width), Scaled(_bitmap.Height)));

        using var gutterBrush = new HatchBrush(HatchStyle.DiagonalCross, Color.FromArgb(80, Color.DimGray), Color.FromArgb(30, Color.White));
        var editable = new Region(ClientRectangle);
        foreach (var section in _sections) editable.Exclude(Scale(section.Bounds));
        e.Graphics.FillRegion(gutterBrush, editable);
        editable.Dispose();

        using var boundaryPen = new Pen(Color.FromArgb(180, 255, 210, 60), Math.Max(1, (float)Math.Min(2, Zoom)));
        using var framePen = new Pen(Color.FromArgb(90, 255, 255, 255));
        using var labelBackground = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        foreach (var section in _sections)
        {
            var rectangle = Scale(section.Bounds);
            e.Graphics.DrawRectangle(boundaryPen, rectangle);
            if (section.FrameHeight > 0)
                for (var y = section.Bounds.Top + section.FrameHeight; y < section.Bounds.Bottom; y += section.FrameHeight)
                    e.Graphics.DrawLine(framePen, rectangle.Left, Scaled(y), rectangle.Right, Scaled(y));
            var label = $"{section.Name}  {Path.GetFileName(section.SourceFile)}";
            var measured = e.Graphics.MeasureString(label, Font);
            e.Graphics.FillRectangle(labelBackground, rectangle.Left + 2, rectangle.Top + 2, measured.Width + 8, measured.Height + 4);
            e.Graphics.DrawString(label, Font, Brushes.White, rectangle.Left + 6, rectangle.Top + 4);
        }

        if (ShowGrid && Zoom >= 6)
        {
            using var pen = new Pen(Color.FromArgb(55, 25, 25, 25));
            foreach (var section in _sections)
            {
                for (var x = section.Bounds.Left; x <= section.Bounds.Right; x++)
                    e.Graphics.DrawLine(pen, Scaled(x), Scaled(section.Bounds.Top), Scaled(x), Scaled(section.Bounds.Bottom));
                for (var y = section.Bounds.Top; y <= section.Bounds.Bottom; y++)
                    e.Graphics.DrawLine(pen, Scaled(section.Bounds.Left), Scaled(y), Scaled(section.Bounds.Right), Scaled(y));
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var point = ToPixel(e.Location);
        var section = _sections.FirstOrDefault(item => item.Bounds.Contains(point));
        if (section == null) return;
        _activeBounds = section.Bounds;
        if (Tool == PixelTool.Eyedropper)
        {
            DrawingColor = _bitmap.GetPixel(point.X, point.Y);
            ColorPicked?.Invoke(DrawingColor);
            return;
        }
        PushUndo();
        _drawing = true;
        _start = _last = point;
        if (Tool == PixelTool.Fill) { FloodFill(point, _bitmap.GetPixel(point.X, point.Y), DrawingColor); _drawing = false; }
        else if (Tool is PixelTool.Pencil or PixelTool.Eraser) SetPixel(point);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drawing || Tool is not (PixelTool.Pencil or PixelTool.Eraser)) return;
        var point = ToPixel(e.Location);
        point.X = Math.Clamp(point.X, _activeBounds.Left, _activeBounds.Right - 1);
        point.Y = Math.Clamp(point.Y, _activeBounds.Top, _activeBounds.Bottom - 1);
        DrawLine(_last, point, Tool == PixelTool.Eraser ? Color.Transparent : DrawingColor);
        _last = point;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drawing) return;
        _drawing = false;
        var point = ToPixel(e.Location);
        point.X = Math.Clamp(point.X, _activeBounds.Left, _activeBounds.Right - 1);
        point.Y = Math.Clamp(point.Y, _activeBounds.Top, _activeBounds.Bottom - 1);
        if (Tool == PixelTool.Line) DrawLine(_start, point, DrawingColor);
        if (Tool == PixelTool.Rectangle) DrawRectangle(_start, point, DrawingColor);
        Invalidate();
    }

    private void PushUndo()
    {
        _undo.Push(new Bitmap(_bitmap));
        foreach (var bitmap in _redo) bitmap.Dispose();
        _redo.Clear();
    }

    private void SetPixel(Point point)
    {
        if (_activeBounds.Contains(point)) _bitmap.SetPixel(point.X, point.Y, Tool == PixelTool.Eraser ? Color.Transparent : DrawingColor);
    }

    private void DrawLine(Point start, Point end, Color color)
    {
        var dx = Math.Abs(end.X - start.X);
        var sx = start.X < end.X ? 1 : -1;
        var dy = -Math.Abs(end.Y - start.Y);
        var sy = start.Y < end.Y ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            if (_activeBounds.Contains(start)) _bitmap.SetPixel(start.X, start.Y, color);
            if (start == end) break;
            var twice = 2 * error;
            if (twice >= dy) { error += dy; start.X += sx; }
            if (twice <= dx) { error += dx; start.Y += sy; }
        }
    }

    private void DrawRectangle(Point start, Point end, Color color)
    {
        DrawLine(new Point(start.X, start.Y), new Point(end.X, start.Y), color);
        DrawLine(new Point(end.X, start.Y), new Point(end.X, end.Y), color);
        DrawLine(new Point(end.X, end.Y), new Point(start.X, end.Y), color);
        DrawLine(new Point(start.X, end.Y), new Point(start.X, start.Y), color);
    }

    private void FloodFill(Point start, Color target, Color replacement)
    {
        if (target.ToArgb() == replacement.ToArgb()) return;
        var queue = new Queue<Point>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            if (!_activeBounds.Contains(point) || _bitmap.GetPixel(point.X, point.Y).ToArgb() != target.ToArgb()) continue;
            _bitmap.SetPixel(point.X, point.Y, replacement);
            queue.Enqueue(new Point(point.X - 1, point.Y));
            queue.Enqueue(new Point(point.X + 1, point.Y));
            queue.Enqueue(new Point(point.X, point.Y - 1));
            queue.Enqueue(new Point(point.X, point.Y + 1));
        }
    }

    private Point ToPixel(Point point) => new((int)(point.X / Zoom), (int)(point.Y / Zoom));
    private int Scaled(int value) => (int)Math.Round(value * Zoom);
    private Rectangle Scale(Rectangle rectangle) => new(Scaled(rectangle.X), Scaled(rectangle.Y), Scaled(rectangle.Width), Scaled(rectangle.Height));
    private void UpdateSize() => Size = new Size(Scaled(_bitmap.Width) + 1, Scaled(_bitmap.Height) + 1);

    private static TextureBrush CheckerBrush()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(225, 225, 225));
            using var brush = new SolidBrush(Color.FromArgb(195, 195, 195));
            graphics.FillRectangle(brush, 0, 0, 8, 8);
            graphics.FillRectangle(brush, 8, 8, 8, 8);
        }
        var result = new TextureBrush(bitmap, WrapMode.Tile);
        bitmap.Dispose();
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap.Dispose();
            foreach (var bitmap in _undo) bitmap.Dispose();
            foreach (var bitmap in _redo) bitmap.Dispose();
        }
        base.Dispose(disposing);
    }
}
