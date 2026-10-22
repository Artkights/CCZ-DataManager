using ImageManager.Core;

namespace ImageManager.App;

public sealed class AnimationDialog : Form
{
    private readonly IReadOnlyList<Bitmap> _frames;
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(36, 38, 40) };
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Button _toggle = new() { Text = "暂停", AutoSize = true, MinimumSize = new Size(82, 32) };
    private readonly Button _next = new() { Text = "下一帧", AutoSize = true, MinimumSize = new Size(82, 32), Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private int _index;

    public AnimationDialog(string title, IReadOnlyList<Bitmap> frames, int interval)
    {
        Text = "播放动作 - " + title;
        ClientSize = WorkingSize(620, 620);
        MinimumSize = new Size(480, 420);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        _frames = frames;
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 48),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true
        };
        var speed = new NumericUpDown
            { Minimum = 40, Maximum = 2000, Value = Math.Clamp(interval, 40, 2000), Increment = 20, Width = 90 };
        _toggle.Click += (_, _) => TogglePlayback();
        _next.Click += (_, _) => ShowNext();
        speed.ValueChanged += (_, _) => _timer.Interval = (int)speed.Value;
        controls.Controls.Add(_toggle);
        controls.Controls.Add(_next);
        controls.Controls.Add(new Label { Text = "间隔(ms)", AutoSize = true, Margin = new Padding(10, 8, 3, 0) });
        controls.Controls.Add(speed);
        controls.Controls.Add(_status);
        Controls.Add(PreviewImageLayout.CreateViewport(_picture));
        Controls.Add(controls);
        _timer.Interval = (int)speed.Value;
        _timer.Tick += (_, _) => ShowNext();
        if (_frames.Count > 0) _picture.Image = _frames[0];
        _timer.Enabled = _frames.Count > 1;
        UpdateStatus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space) { TogglePlayback(); return true; }
        if (keyData == Keys.Right && !_timer.Enabled) { ShowNext(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void TogglePlayback()
    {
        if (_frames.Count <= 1) return;
        _timer.Enabled = !_timer.Enabled;
        _toggle.Text = _timer.Enabled ? "暂停" : "播放";
        _next.Enabled = !_timer.Enabled;
    }

    private void ShowNext()
    {
        if (_frames.Count == 0) return;
        _index = (_index + 1) % _frames.Count;
        _picture.Image = _frames[_index];
        UpdateStatus();
    }

    private void UpdateStatus() => _status.Text = _frames.Count == 0 ? "无帧" : $"帧 {_index + 1}/{_frames.Count}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _picture.Image = null;
            foreach (var frame in _frames) frame.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Size WorkingSize(int width, int height)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, height);
        return new Size(Math.Min(width, (int)(area.Width * .9)), Math.Min(height, (int)(area.Height * .9)));
    }
}

public sealed class AllFramesDialog : Form
{
    private readonly List<DecodedFrame> _frames;
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(37, 39, 42),
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly FlowLayoutPanel _thumbnails = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6), WrapContents = true };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(10, 8, 0, 0) };
    private readonly Stack<FrameChange> _undo = new();
    private readonly Stack<FrameChange> _redo = new();
    private int _selectedIndex;
    private bool _dirty;
    private bool _allowClose;

    public AllFramesDialog(string title, IReadOnlyList<DecodedFrame> frames)
    {
        Text = "全部帧数 - " + title;
        ClientSize = WorkingSize(1180, 780);
        MinimumSize = new Size(800, 560);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        _frames = frames.Select(frame => new DecodedFrame(frame.Descriptor, new Bitmap(frame.Bitmap),
            frame.PaletteIndexes?.ToArray(), frame.PaletteBgra?.ToArray())).ToList();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Size = new Size(Math.Max(800, ClientSize.Width), Math.Max(480, ClientSize.Height - 56)),
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 320,
            Panel2MinSize = 320,
            SplitterDistance = 510
        };
        split.Panel1.Controls.Add(PreviewImageLayout.CreateViewport(_preview));
        split.Panel2.Controls.Add(_thumbnails);
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 48),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true
        };
        AddButton(bar, "←", () => ShiftSelected(-1, 0, 1), "左移 1 像素");
        AddButton(bar, "→", () => ShiftSelected(1, 0, 1), "右移 1 像素");
        AddButton(bar, "↑", () => ShiftSelected(0, -1, 1), "上移 1 像素");
        AddButton(bar, "↓", () => ShiftSelected(0, 1, 1), "下移 1 像素");
        AddButton(bar, "撤回", Undo, "撤回帧位移");
        AddButton(bar, "重做", Redo, "重做帧位移");
        AddButton(bar, "保存写回", SaveAndClose, "把全部变更作为一个事务写回");
        AddButton(bar, "关闭", Close, "关闭窗口");
        bar.Controls.Add(_status);
        Controls.Add(split);
        Controls.Add(bar);
        RenderThumbnails();
        SelectFrame(0);
        FormClosing += OnFormClosing;
    }

    public IReadOnlyList<DecodedFrame> ResultFrames => _frames;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Alt) == Keys.Alt)
        {
            var amount = (keyData & Keys.Shift) == Keys.Shift ? 5 : 1;
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Left) { ShiftSelected(-1, 0, amount); return true; }
            if (key == Keys.Right) { ShiftSelected(1, 0, amount); return true; }
            if (key == Keys.Up) { ShiftSelected(0, -1, amount); return true; }
            if (key == Keys.Down) { ShiftSelected(0, 1, amount); return true; }
        }
        if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RenderThumbnails()
    {
        ClearThumbnails();
        string? currentGroup = null;
        for (var index = 0; index < _frames.Count; index++)
        {
            var frame = _frames[index];
            var group = GroupName(frame.Descriptor);
            if (group != currentGroup)
            {
                currentGroup = group;
                var heading = new Label
                {
                    Text = group, AutoSize = false, Width = Math.Max(300, _thumbnails.ClientSize.Width - 36), Height = 28,
                    Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4)
                };
                _thumbnails.Controls.Add(heading);
                _thumbnails.SetFlowBreak(heading, true);
            }
            var frameIndex = index;
            var panel = new Panel { Width = 118, Height = 128, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4), Tag = index };
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(235, 237, 239),
                Image = new Bitmap(frame.Bitmap)
            };
            picture.Click += (_, _) => SelectFrame(frameIndex);
            panel.Controls.Add(PreviewImageLayout.CreateViewport(picture));
            panel.Controls.Add(new Label
                { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleCenter, Text = $"帧 {frame.Descriptor.FrameIndex}" });
            _thumbnails.Controls.Add(panel);
        }
    }

    private void ClearThumbnails()
    {
        foreach (Control card in _thumbnails.Controls.Cast<Control>().ToArray())
        {
            foreach (var picture in card.Controls.OfType<Panel>().SelectMany(viewport => viewport.Controls.OfType<PictureBox>()))
            {
                var image = picture.Image; picture.Image = null; image?.Dispose();
            }
            card.Dispose();
        }
        _thumbnails.Controls.Clear();
    }

    private void SelectFrame(int index)
    {
        if (_frames.Count == 0) return;
        _selectedIndex = Math.Clamp(index, 0, _frames.Count - 1);
        _preview.Image = _frames[_selectedIndex].Bitmap;
        _status.Text = $"帧 {_selectedIndex + 1}/{_frames.Count}；Alt+方向键 1 像素，Shift+Alt+方向键 5 像素";
    }

    private void ShiftSelected(int dx, int dy, int amount)
    {
        if (_frames.Count == 0) return;
        var frame = _frames[_selectedIndex];
        var before = new Bitmap(frame.Bitmap);
        var after = Shift(frame.Bitmap, dx * amount, dy * amount);
        ReplaceFrame(_selectedIndex, after);
        _undo.Push(new FrameChange(_selectedIndex, before, new Bitmap(after)));
        ClearStack(_redo);
        _dirty = true;
        RefreshSelectedThumbnail();
    }

    private void Undo()
    {
        if (!_undo.TryPop(out var change)) return;
        var current = new Bitmap(_frames[change.Index].Bitmap);
        ReplaceFrame(change.Index, new Bitmap(change.Before));
        _redo.Push(new FrameChange(change.Index, new Bitmap(change.Before), current));
        _selectedIndex = change.Index;
        RefreshSelectedThumbnail();
    }

    private void Redo()
    {
        if (!_redo.TryPop(out var change)) return;
        var current = new Bitmap(_frames[change.Index].Bitmap);
        ReplaceFrame(change.Index, new Bitmap(change.After));
        _undo.Push(new FrameChange(change.Index, current, new Bitmap(change.After)));
        _selectedIndex = change.Index;
        RefreshSelectedThumbnail();
    }

    private void ReplaceFrame(int index, Bitmap bitmap)
    {
        var old = _frames[index];
        old.Dispose();
        _frames[index] = new DecodedFrame(old.Descriptor, bitmap, null, old.PaletteBgra);
        _preview.Image = bitmap;
    }

    private void RefreshSelectedThumbnail()
    {
        var panel = _thumbnails.Controls.OfType<Panel>().FirstOrDefault(control => Equals(control.Tag, _selectedIndex));
        if (panel?.Controls.OfType<Panel>().SelectMany(viewport => viewport.Controls.OfType<PictureBox>()).FirstOrDefault() is { } picture)
        {
            var old = picture.Image;
            picture.Image = new Bitmap(_frames[_selectedIndex].Bitmap);
            old?.Dispose();
        }
        SelectFrame(_selectedIndex);
    }

    private static Bitmap Shift(Bitmap source, int dx, int dy)
    {
        var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(source, dx, dy);
        return result;
    }

    private void SaveAndClose()
    {
        _allowClose = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || !_dirty) return;
        if (MessageBox.Show(this, "帧对齐尚未保存，确定关闭？", "未保存修改", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.No) e.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview.Image = null;
            ClearThumbnails();
            foreach (var frame in _frames) frame.Dispose();
            ClearStack(_undo);
            ClearStack(_redo);
        }
        base.Dispose(disposing);
    }

    private static string GroupName(FrameDescriptor frame)
    {
        if (frame.SourceKey.Equals("Pmapobj.e5", StringComparison.OrdinalIgnoreCase))
            return frame.Direction == "front" ? "正面" : "背面";
        var direction = frame.Direction == "none" ? string.Empty : " / " + frame.Direction;
        return $"{frame.Action}{direction} / {Path.GetFileName(frame.SourcePath)}";
    }

    private static void AddButton(Control parent, string text, Action action, string tooltip)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(64, 32), Margin = new Padding(2) };
        button.Click += (_, _) => action();
        new ToolTip().SetToolTip(button, tooltip);
        parent.Controls.Add(button);
    }

    private static void ClearStack(Stack<FrameChange> stack)
    {
        while (stack.TryPop(out var change))
        {
            change.Before.Dispose();
            change.After.Dispose();
        }
    }

    private static Size WorkingSize(int width, int height)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, height);
        return new Size(Math.Min(width, (int)(area.Width * .9)), Math.Min(height, (int)(area.Height * .9)));
    }

    private sealed record FrameChange(int Index, Bitmap Before, Bitmap After);
}
