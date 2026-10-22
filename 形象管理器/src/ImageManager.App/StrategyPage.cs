using ImageManager.Core;

namespace ImageManager.App;

public sealed class StrategyPage : UserControl
{
    private readonly IImageCatalogService _catalog;
    private readonly IStrategyRowService _rows = new StrategyRowService();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly PictureBox _iconSmall = IconBox();
    private readonly PictureBox _iconLarge = IconBox();
    private readonly Label _iconSmallTitle = new() { Text = "小图", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _iconLargeTitle = new() { Text = "大图", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TableLayoutPanel _iconGrid = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private readonly AnimatedPreviewPanel _meff = new("Meff 小动画");
    private readonly AnimatedPreviewPanel _mcall = new("Mcall 大动画");
    private readonly TableLayoutPanel _rootLayout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private ProjectProfile? _project;
    private ImageResourceDescriptor? _iconResource;

    public StrategyPage(ImageGridPage imageGrid, IImageCatalogService catalog)
    {
        ImageGrid = imageGrid;
        _catalog = catalog;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "策略名", DataPropertyName = nameof(StrategyRow.Name), Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图标", DataPropertyName = nameof(StrategyRow.IconId), Width = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Meff", DataPropertyName = nameof(StrategyRow.MeffId), Width = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mcall", DataPropertyName = nameof(StrategyRow.McallId), Width = 55 });
        _grid.SelectionChanged += (_, _) => PreviewSelected();
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        left.Controls.Add(_grid);
        left.Controls.Add(new Label { Text = "策略行（共享图标保留多行）", Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 5, 0, 0) });

        var previews = new TableLayoutPanel { Dock = DockStyle.Top, Height = 245, ColumnCount = 3, Padding = new Padding(6) };
        for (var index = 0; index < 3; index++) previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        previews.Controls.Add(BuildIconPanel(), 0, 0);
        previews.Controls.Add(_meff, 1, 0);
        previews.Controls.Add(_mcall, 2, 0);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(ImageGrid);
        right.Controls.Add(previews);
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootLayout.Controls.Add(left, 0, 0);
        _rootLayout.Controls.Add(right, 1, 0);
        Controls.Add(_rootLayout);
        Layout += (_, _) => UpdateLeftWidth();
        DpiChangedAfterParent += (_, _) => UpdateLeftWidth();
    }

    public ImageGridPage ImageGrid { get; }

    private void UpdateLeftWidth()
    {
        var columns = _grid.Columns.Cast<DataGridViewColumn>().Sum(column => column.Width);
        var chrome = SystemInformation.VerticalScrollBarWidth + _grid.Margin.Horizontal + 18;
        _rootLayout.ColumnStyles[0].Width = Math.Max(330, columns + chrome);
    }

    public void LoadProject(ProjectProfile project, IEnumerable<ImageResourceDescriptor> iconResources,
        IReadOnlyList<ImageResourceDescriptor> allResources)
    {
        _project = project;
        var icons = iconResources.ToList();
        _iconResource = icons.FirstOrDefault();
        ConfigureIconMode(_iconResource?.Kind == ResourceKind.DllBitmap);
        ImageGrid.LoadProject(project, icons);
        try { _grid.DataSource = _rows.Read(project).ToList(); }
        catch (Exception ex)
        {
            _grid.DataSource = null;
            _meff.SetMessage("策略表读取失败：" + ex.Message);
            _mcall.Clear();
        }
        PreviewSelected();
    }

    private void PreviewSelected()
    {
        if (_project == null || _grid.CurrentRow?.DataBoundItem is not StrategyRow row) return;
        ReplaceImage(_iconSmall, null);
        ReplaceImage(_iconLarge, null);
        if (_iconResource != null)
        {
            try
            {
                var asset = _catalog.ReadAssets(_project, _iconResource).FirstOrDefault(item => item.LogicalId == row.IconId);
                if (asset != null)
                {
                    var frames = _catalog.DecodeFrames(_project, asset);
                    try
                    {
                        if (_iconResource.Kind == ResourceKind.DllBitmap)
                        {
                            ReplaceImage(_iconSmall, CopyFrame(frames, "small", 0));
                            ReplaceImage(_iconLarge, CopyFrame(frames, "large", 1));
                        }
                        else ReplaceImage(_iconSmall, frames.Count == 0 ? null : new Bitmap(frames[0].Bitmap));
                    }
                    finally { foreach (var frame in frames) frame.Dispose(); }
                }
            }
            catch (Exception) { }
        }
        PreviewMeff(row.MeffId);
        PreviewMcall(row.McallId);
    }

    private void PreviewMeff(int value)
    {
        if (_project == null) return;
        if (value == 255) { _meff.SetMessage("字段 255：无小动画"); return; }
        var path = _project.TryGetResource("Meff.e5");
        if (path == null) { _meff.SetMessage("缺少 Meff.e5"); return; }
        try
        {
            var archive = E5Archive.Open(path);
            if (value + 1 > archive.Entries.Count) throw new InvalidDataException($"字段 {value} 超出 Meff 范围。");
            using var animation = StrategyAnimationDecoder.Decode(_project, archive.ReadDecoded(value + 1));
            _meff.SetAnimation(animation, $"字段 {value} → 图号 #{value + 1}");
        }
        catch (Exception ex) { _meff.SetMessage(ex.Message); }
    }

    private void PreviewMcall(int value)
    {
        if (_project == null) return;
        if (value == 255 || value < 100) { _mcall.SetMessage($"字段 {value}：无大动画"); return; }
        var number = value - 100;
        var path = _project.ResourcePaths.Values.FirstOrDefault(candidate =>
        {
            var name = Path.GetFileNameWithoutExtension(candidate);
            if (!name.StartsWith("Mcall", StringComparison.OrdinalIgnoreCase)) return false;
            var digits = new string(name.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var parsed) && parsed == number;
        });
        if (path == null) { _mcall.SetMessage($"缺少 Mcall{number:00}.e5"); return; }
        try
        {
            using var animation = StrategyAnimationDecoder.Decode(_project, File.ReadAllBytes(path));
            _mcall.SetAnimation(animation, $"字段 {value} → {Path.GetFileName(path)}");
        }
        catch (Exception ex) { _mcall.SetMessage(ex.Message); }
    }

    private static Control Wrap(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4), BorderStyle = BorderStyle.FixedSingle };
        panel.Controls.Add(content);
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 25, TextAlign = ContentAlignment.MiddleCenter });
        return panel;
    }

    private Control BuildIconPanel()
    {
        _iconGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _iconGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var small = new Panel { Dock = DockStyle.Fill };
        small.Controls.Add(_iconSmall);
        small.Controls.Add(_iconSmallTitle);
        var large = new Panel { Dock = DockStyle.Fill };
        large.Controls.Add(_iconLarge);
        large.Controls.Add(_iconLargeTitle);
        _iconGrid.Controls.Add(small, 0, 0);
        _iconGrid.Controls.Add(large, 1, 0);
        return Wrap("策略图标", _iconGrid);
    }

    private void ConfigureIconMode(bool paired)
    {
        _iconSmallTitle.Text = paired ? "小图 16x16" : "单图";
        _iconLargeTitle.Text = "大图 32x32";
        _iconGrid.ColumnStyles[0].Width = paired ? 50 : 100;
        _iconGrid.ColumnStyles[1].Width = paired ? 50 : 0;
        _iconGrid.GetControlFromPosition(1, 0)!.Visible = paired;
    }

    private static Bitmap? CopyFrame(IReadOnlyList<DecodedFrame> frames, string action, int fallback)
    {
        var frame = frames.FirstOrDefault(item => item.Descriptor.Action == action) ?? frames.ElementAtOrDefault(fallback);
        return frame == null ? null : new Bitmap(frame.Bitmap);
    }

    private static PictureBox IconBox() => new()
        { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(232, 234, 236), Margin = new Padding(3) };

    private static void ReplaceImage(PictureBox box, Image? image)
    {
        var old = box.Image;
        box.Image = image;
        old?.Dispose();
    }
}

internal sealed class AnimatedPreviewPanel : Panel
{
    private readonly Label _title;
    private readonly Label _status;
    private readonly PictureBox _picture;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<Bitmap> _frames = [];
    private int _index;

    public AnimatedPreviewPanel(string title)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(4);
        BorderStyle = BorderStyle.FixedSingle;
        _title = new Label { Text = title, Dock = DockStyle.Top, Height = 25, TextAlign = ContentAlignment.MiddleCenter };
        _status = new Label { Dock = DockStyle.Bottom, Height = 38, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
        _picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 37, 40) };
        Controls.Add(_picture);
        Controls.Add(_status);
        Controls.Add(_title);
        _timer.Tick += (_, _) => Next();
    }

    public void SetAnimation(StrategyAnimationData animation, string mapping)
    {
        ClearFrames();
        _frames.AddRange(animation.Frames.Select(frame => new Bitmap(frame)));
        _timer.Interval = Math.Clamp(animation.IntervalMs, 40, 2000);
        _status.Text = mapping + $"；{_frames.Count} 帧 / {_timer.Interval}ms";
        _picture.Image = _frames.FirstOrDefault();
        _timer.Enabled = _frames.Count > 1;
    }

    public void SetMessage(string message)
    {
        ClearFrames();
        _status.Text = message;
    }

    public void Clear() => SetMessage(string.Empty);

    private void Next()
    {
        if (_frames.Count == 0) return;
        _index = (_index + 1) % _frames.Count;
        _picture.Image = _frames[_index];
    }

    private void ClearFrames()
    {
        _timer.Enabled = false;
        _picture.Image = null;
        foreach (var frame in _frames) frame.Dispose();
        _frames.Clear();
        _index = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); ClearFrames(); }
        base.Dispose(disposing);
    }
}
