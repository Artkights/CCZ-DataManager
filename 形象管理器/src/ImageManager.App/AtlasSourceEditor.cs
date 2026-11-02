using System.Drawing.Drawing2D;
using ImageManager.Core;

namespace ImageManager.App;

/// <summary>Edits rectangles in original-image coordinates; it never edits target descriptors.</summary>
internal sealed class AtlasSourceEditor : UserControl
{
    private readonly ComboBox _scope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
    private readonly ComboBox _frames = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 152 };
    private readonly NumericUpDown[] _coordinates = Enumerable.Range(0, 4)
        .Select(_ => new NumericUpDown { Minimum = 0, Maximum = 100000, Width = 76 }).ToArray();
    private readonly CheckBox _repairAspect = new() { Text = "恢复横纵拉伸", AutoSize = true };
    private readonly CheckBox _showGuides = new() { Text = "中心/格底参考线", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, Padding = new Padding(4), MaximumSize = new Size(1100, 0) };
    private readonly SourceCanvas _canvas;
    private readonly Stack<AtlasSourceMapping> _undo = new();
    private readonly Stack<AtlasSourceMapping> _redo = new();
    private readonly Button _undoButton = new() { Text = "撤回格位", AutoSize = true };
    private readonly Button _redoButton = new() { Text = "重做格位", AutoSize = true };
    private IReadOnlyList<FrameDescriptor> _descriptors = [];
    private AtlasSourceMapping? _mapping;
    private bool _loading;

    public event EventHandler<AtlasSourceMappingChangedEventArgs>? MappingChanged;
    public event EventHandler? SelectedFrameChanged;
    public string? SelectedFrameKey => _frames.SelectedItem is FrameChoice choice ? choice.Key : null;
    public AtlasSourceMapping? Mapping => _mapping?.Clone();

    public AtlasSourceEditor()
    {
        Dock = DockStyle.Fill;
        _canvas = new SourceCanvas(this) { Dock = DockStyle.Fill };
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(3) };
        _scope.Items.AddRange(["有效画布", "全部格位", "当前分区", "当前语义行", "当前帧"]);
        _scope.SelectedIndex = 0;
        tools.Controls.Add(new Label { Text = "编辑范围", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
        tools.Controls.Add(_scope);
        tools.Controls.Add(_frames);
        var labels = new[] { "X", "Y", "宽", "高" };
        for (var index = 0; index < 4; index++)
        {
            tools.Controls.Add(new Label { Text = labels[index], AutoSize = true, Margin = new Padding(6, 7, 2, 0) });
            tools.Controls.Add(_coordinates[index]);
        }
        AddButton(tools, "应用矩形", ApplyCoordinates);
        AddButton(tools, "确认本范围格位", ConfirmSelection);
        tools.Controls.Add(_repairAspect);
        tools.Controls.Add(_showGuides);
        tools.Controls.Add(_undoButton);
        tools.Controls.Add(_redoButton);
        AddButton(tools, "显示完整图片", _canvas.Fit);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(tools, 0, 0);
        table.Controls.Add(_canvas, 0, 1);
        table.Controls.Add(_hint, 0, 2);
        Controls.Add(table);
        SizeChanged += (_, _) => _hint.MaximumSize = new Size(Math.Max(100, ClientSize.Width - 12), 0);
        _scope.SelectedIndexChanged += (_, _) => RefreshSelection();
        _frames.SelectedIndexChanged += (_, _) => { RefreshSelection(); if (!_loading) SelectedFrameChanged?.Invoke(this, EventArgs.Empty); };
        _repairAspect.CheckedChanged += (_, _) =>
        {
            if (_loading || _mapping == null) return;
            BeginChange();
            _mapping.RepairAspectRatio = _repairAspect.Checked;
            Publish(false);
        };
        _undoButton.Click += (_, _) => Restore(_undo, _redo);
        _redoButton.Click += (_, _) => Restore(_redo, _undo);
        _showGuides.CheckedChanged += (_, _) => _canvas.Invalidate();
        RefreshSelection();
    }

    public void SetSource(Bitmap bitmap, AtlasSourceMapping mapping, IReadOnlyList<FrameDescriptor> descriptors)
    {
        var selected = SelectedFrameKey;
        _loading = true;
        try
        {
            _mapping = mapping.Clone();
            _descriptors = descriptors;
            _canvas.SetImage(bitmap);
            _frames.Items.Clear();
            foreach (var descriptor in descriptors)
                _frames.Items.Add(new FrameChoice(AiAtlasNormalizer.FrameKey(descriptor),
                    $"{descriptor.Action} {descriptor.FrameIndex:00} · {descriptor.Direction}"));
            var selectedIndex = _frames.Items.Cast<FrameChoice>().ToList().FindIndex(item => item.Key == selected);
            _frames.SelectedIndex = selectedIndex >= 0 ? selectedIndex : _frames.Items.Count > 0 ? 0 : -1;
            _repairAspect.Checked = mapping.RepairAspectRatio;
        }
        finally { _loading = false; }
        RefreshSelection();
    }

    public void SelectFrame(string key)
    {
        var index = _frames.Items.Cast<FrameChoice>().ToList().FindIndex(item => item.Key == key);
        if (index >= 0 && _frames.SelectedIndex != index) _frames.SelectedIndex = index;
    }

    public void ReplaceMapping(AtlasSourceMapping mapping)
    {
        BeginChange();
        var roiChanged = _mapping?.EffectiveBounds != mapping.EffectiveBounds;
        _mapping = mapping.Clone();
        Publish(roiChanged);
    }

    private IReadOnlyList<string> SelectedKeys()
    {
        if (_mapping == null || _scope.SelectedIndex == 0) return [];
        var selected = _descriptors.FirstOrDefault(item => AiAtlasNormalizer.FrameKey(item) == SelectedFrameKey);
        return _descriptors.Where(item => _mapping.FrameRectangles.ContainsKey(AiAtlasNormalizer.FrameKey(item)) &&
            (_scope.SelectedIndex == 1 || selected != null && item.Turn == selected.Turn &&
                (_scope.SelectedIndex == 2 ? (item.Action == "atk") == (selected.Action == "atk") :
                    _scope.SelectedIndex == 3 ? SemanticRow(item) == SemanticRow(selected) :
                    AiAtlasNormalizer.FrameKey(item) == SelectedFrameKey)))
            .Select(AiAtlasNormalizer.FrameKey).ToArray();
    }

    private static string SemanticRow(FrameDescriptor descriptor)
        => descriptor.Action switch
        {
            "mov" => "mov:" + (descriptor.FrameIndex < 6 ? 0 : 1),
            "spc" => "spc",
            "atk" => "atk:" + descriptor.FrameIndex / 4,
            _ => descriptor.Direction
        };

    private Rectangle SelectionBounds()
    {
        if (_mapping == null) return Rectangle.Empty;
        if (_scope.SelectedIndex == 0) return _mapping.EffectiveBounds;
        var rectangles = SelectedKeys().Select(key => _mapping.FrameRectangles[key]).Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0).ToArray();
        return rectangles.Length == 0 ? Rectangle.Empty : rectangles.Aggregate(Rectangle.Union);
    }

    private void RefreshSelection()
    {
        var bounds = SelectionBounds();
        var values = new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        for (var index = 0; index < 4; index++) _coordinates[index].Value = Math.Clamp(values[index], 0, 100000);
        _undoButton.Enabled = _undo.Count > 0;
        _redoButton.Enabled = _redo.Count > 0;
        _hint.Text = _scope.SelectedIndex == 0
            ? "拖动画布边框或角点可裁去外框和留白；空白处拖出新框。改变有效画布会清除旧格位和背景取样，重新自动识别。滚轮缩放，中键拖动。"
            : $"黄色框为当前范围，拖动内部平移，拖动边/角缩放；其他框可点击选帧。当前包含 {SelectedKeys().Count} 帧，数字为原图像素坐标；调整或确认后重新预览。";
        _canvas.Invalidate();
    }

    private void ApplyCoordinates()
    {
        var bounds = new Rectangle((int)_coordinates[0].Value, (int)_coordinates[1].Value,
            (int)_coordinates[2].Value, (int)_coordinates[3].Value);
        ApplyRectangle(bounds);
    }

    private void ApplyRectangle(Rectangle bounds)
    {
        if (_mapping == null || bounds.Width <= 0 || bounds.Height <= 0) return;
        var old = SelectionBounds();
        if (old.Width <= 0 || old.Height <= 0) return;
        var limit = _scope.SelectedIndex == 0 ? _canvas.ImageBounds : _mapping.EffectiveBounds;
        if (bounds.Size == old.Size && bounds.Width <= limit.Width && bounds.Height <= limit.Height)
            bounds.Location = new Point(Math.Clamp(bounds.X, limit.Left, limit.Right - bounds.Width),
                Math.Clamp(bounds.Y, limit.Top, limit.Bottom - bounds.Height));
        bounds = Rectangle.Intersect(bounds, limit);
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds == old) return;
        BeginChange();
        if (_scope.SelectedIndex == 0)
        {
            _mapping.EffectiveBounds = bounds;
            _mapping.FrameRectangles.Clear();
            _mapping.ConfirmedFrames.Clear();
        }
        else
        {
            foreach (var key in SelectedKeys())
            {
                var item = _mapping.FrameRectangles[key];
                var left = bounds.Left + (int)Math.Round((item.Left - old.Left) * bounds.Width / (double)old.Width);
                var top = bounds.Top + (int)Math.Round((item.Top - old.Top) * bounds.Height / (double)old.Height);
                var right = bounds.Left + (int)Math.Round((item.Right - old.Left) * bounds.Width / (double)old.Width);
                var bottom = bounds.Top + (int)Math.Round((item.Bottom - old.Top) * bounds.Height / (double)old.Height);
                _mapping.FrameRectangles[key] = Rectangle.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
                _mapping.ConfirmedFrames.Add(key);
            }
        }
        Publish(_scope.SelectedIndex == 0);
    }

    private void ConfirmSelection()
    {
        if (_mapping == null || SelectedKeys().Count == 0) return;
        BeginChange();
        foreach (var key in SelectedKeys()) _mapping.ConfirmedFrames.Add(key);
        Publish(false);
    }

    private void BeginChange()
    {
        if (_mapping != null) _undo.Push(_mapping.Clone());
        _redo.Clear();
    }

    private void Restore(Stack<AtlasSourceMapping> source, Stack<AtlasSourceMapping> destination)
    {
        if (source.Count == 0 || _mapping == null) return;
        destination.Push(_mapping.Clone());
        var next = source.Pop();
        var roiChanged = next.EffectiveBounds != _mapping.EffectiveBounds;
        _mapping = next;
        _loading = true;
        _repairAspect.Checked = next.RepairAspectRatio;
        _loading = false;
        Publish(roiChanged);
    }

    private void Publish(bool roiChanged)
    {
        RefreshSelection();
        if (_mapping != null) MappingChanged?.Invoke(this, new(_mapping.Clone(), roiChanged));
    }

    private void SelectAt(Point point)
    {
        if (_mapping == null || _scope.SelectedIndex == 0) return;
        var key = _mapping.FrameRectangles.Where(item => item.Value.Contains(point))
            .OrderBy(item => item.Value.Width * item.Value.Height).Select(item => item.Key).FirstOrDefault();
        if (key != null) SelectFrame(key);
    }

    private static void AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
    }

    private sealed record FrameChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class SourceCanvas(AtlasSourceEditor owner) : Control
    {
        private Bitmap? _image;
        private float _zoom = 1;
        private PointF _pan;
        private Point _startClient;
        private Point _startImage;
        private Rectangle _startBounds;
        private Rectangle? _dragBounds;
        private int _mode;
        private bool _panning;
        private PointF _startPan;
        public Rectangle ImageBounds => _image == null ? Rectangle.Empty : new Rectangle(Point.Empty, _image.Size);

        public void SetImage(Bitmap bitmap)
        {
            var resized = _image?.Size != bitmap.Size;
            _image?.Dispose();
            _image = new Bitmap(bitmap);
            if (resized) Fit();
            Invalidate();
        }

        public void Fit() { _zoom = 1; _pan = PointF.Empty; Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.FromArgb(38, 40, 43));
        protected override void OnPaint(PaintEventArgs e)
        {
            if (_image == null) return;
            var (scale, offset) = View();
            var graphics = e.Graphics;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_image, new RectangleF(offset.X, offset.Y, _image.Width * scale, _image.Height * scale));
            if (owner._mapping == null) return;
            using var outline = new Pen(Color.FromArgb(180, Color.Cyan), 1);
            foreach (var item in owner._mapping.FrameRectangles)
            {
                var client = ClientRectangleOf(item.Value, scale, offset);
                graphics.DrawRectangle(outline, client.X, client.Y, client.Width, client.Height);
                if (owner._showGuides.Checked)
                {
                    using var guide = new Pen(Color.FromArgb(150, Color.LimeGreen), 1) { DashStyle = DashStyle.Dash };
                    graphics.DrawLine(guide, client.Left + client.Width / 2, client.Top, client.Left + client.Width / 2, client.Bottom);
                    graphics.DrawLine(guide, client.Left, client.Bottom - 1, client.Right, client.Bottom - 1);
                }
                if (item.Key == owner.SelectedFrameKey && owner._scope.SelectedIndex != 0 && client.Width > 40 && client.Height > 20)
                {
                    var descriptor = owner._descriptors.FirstOrDefault(frame => AiAtlasNormalizer.FrameKey(frame) == item.Key);
                    if (descriptor != null)
                    {
                        var text = $"{descriptor.Action}{descriptor.FrameIndex}";
                        var size = graphics.MeasureString(text, Font);
                        if (client.Width >= size.Width + 4 && client.Height >= size.Height + 4)
                        {
                            graphics.FillRectangle(Brushes.Black, client.X + 1, client.Y + 1, size.Width, size.Height);
                            graphics.DrawString(text, Font, Brushes.White, client.X + 1, client.Y + 1);
                        }
                    }
                }
            }
            using var roiPen = new Pen(Color.Lime, 2) { DashStyle = DashStyle.Dash };
            var roi = ClientRectangleOf(owner._mapping.EffectiveBounds, scale, offset);
            graphics.DrawRectangle(roiPen, roi.X, roi.Y, roi.Width, roi.Height);
            var selected = ClientRectangleOf(_dragBounds ?? owner.SelectionBounds(), scale, offset);
            using var selectedPen = new Pen(Color.Gold, 2);
            graphics.DrawRectangle(selectedPen, selected.X, selected.Y, selected.Width, selected.Height);
            foreach (var point in new[] { new PointF(selected.Left, selected.Top), new PointF(selected.Right, selected.Top),
                         new PointF(selected.Left, selected.Bottom), new PointF(selected.Right, selected.Bottom) })
                graphics.FillRectangle(Brushes.Gold, point.X - 4, point.Y - 4, 8, 8);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_image == null) return;
            Focus();
            _startClient = e.Location;
            if (e.Button == MouseButtons.Middle)
            {
                _panning = true; _startPan = _pan; Capture = true; return;
            }
            if (e.Button != MouseButtons.Left || owner._mapping == null) return;
            _startImage = ImagePoint(e.Location);
            if (!ImageBounds.Contains(_startImage)) return;
            _startBounds = owner.SelectionBounds();
            var (scale, offset) = View();
            var client = ClientRectangleOf(_startBounds, scale, offset);
            const int threshold = 9;
            var nearX = e.X >= client.Left - threshold && e.X <= client.Right + threshold;
            var nearY = e.Y >= client.Top - threshold && e.Y <= client.Bottom + threshold;
            _mode = 0;
            if (nearY && Math.Abs(e.X - client.Left) <= threshold) _mode |= 1;
            else if (nearY && Math.Abs(e.X - client.Right) <= threshold) _mode |= 2;
            if (nearX && Math.Abs(e.Y - client.Top) <= threshold) _mode |= 4;
            else if (nearX && Math.Abs(e.Y - client.Bottom) <= threshold) _mode |= 8;
            if (_mode == 0 && _startBounds.Contains(_startImage)) _mode = 16;
            if (_mode == 0 && owner._scope.SelectedIndex != 0)
            {
                owner.SelectAt(_startImage);
                _startBounds = owner.SelectionBounds();
                if (_startBounds.Contains(_startImage)) _mode = 16;
            }
            if (_mode == 0) _mode = 32;
            _dragBounds = _startBounds;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning) { _pan = new(_startPan.X + e.X - _startClient.X, _startPan.Y + e.Y - _startClient.Y); Invalidate(); return; }
            if (_dragBounds == null) return;
            var point = ImagePoint(e.Location);
            point = new Point(Math.Clamp(point.X, 0, ImageBounds.Width), Math.Clamp(point.Y, 0, ImageBounds.Height));
            var dx = point.X - _startImage.X;
            var dy = point.Y - _startImage.Y;
            var left = _startBounds.Left; var top = _startBounds.Top;
            var right = _startBounds.Right; var bottom = _startBounds.Bottom;
            if (_mode == 32) { left = Math.Min(_startImage.X, point.X); top = Math.Min(_startImage.Y, point.Y); right = Math.Max(_startImage.X, point.X); bottom = Math.Max(_startImage.Y, point.Y); }
            else if (_mode == 16) { left += dx; right += dx; top += dy; bottom += dy; }
            else
            {
                if ((_mode & 1) != 0) left += dx;
                if ((_mode & 2) != 0) right += dx;
                if ((_mode & 4) != 0) top += dy;
                if ((_mode & 8) != 0) bottom += dy;
            }
            _dragBounds = Rectangle.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle) _panning = false;
            if (e.Button == MouseButtons.Left && _dragBounds is { } bounds)
            {
                _dragBounds = null;
                if (Math.Abs(e.X - _startClient.X) + Math.Abs(e.Y - _startClient.Y) > 3) owner.ApplyRectangle(bounds);
                Invalidate();
            }
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_image == null) return;
            var point = ImagePoint(e.Location);
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f), 1, 12);
            var (scale, offset) = View();
            _pan = new(_pan.X + e.X - (offset.X + point.X * scale), _pan.Y + e.Y - (offset.Y + point.Y * scale));
            Invalidate();
        }

        private (float Scale, PointF Offset) View()
        {
            if (_image == null) return (1, PointF.Empty);
            var scale = Math.Min(Math.Max(1, ClientSize.Width - 12) / (float)_image.Width,
                Math.Max(1, ClientSize.Height - 12) / (float)_image.Height) * _zoom;
            return (scale, new((ClientSize.Width - _image.Width * scale) / 2 + _pan.X,
                (ClientSize.Height - _image.Height * scale) / 2 + _pan.Y));
        }

        private Point ImagePoint(Point client)
        {
            var (scale, offset) = View();
            return new((int)Math.Round((client.X - offset.X) / scale), (int)Math.Round((client.Y - offset.Y) / scale));
        }

        private static RectangleF ClientRectangleOf(Rectangle rectangle, float scale, PointF offset)
            => new(offset.X + rectangle.X * scale, offset.Y + rectangle.Y * scale, rectangle.Width * scale, rectangle.Height * scale);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image?.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }
}

internal sealed class AtlasSourceMappingChangedEventArgs(AtlasSourceMapping mapping, bool effectiveBoundsChanged) : EventArgs
{
    public AtlasSourceMapping Mapping { get; } = mapping;
    public bool EffectiveBoundsChanged { get; } = effectiveBoundsChanged;
}
