using ImageManager.Core;

namespace ImageManager.App;

public sealed partial class AiAtlasImportDialog
{
    private readonly AtlasSourceEditor _sourceEditor = new();
    private readonly ComboBox _maskBackdrop = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox _animationScope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 114 };
    private readonly Label _manifestStatus = new() { Text = "未关联清单：自动识别动作格位", AutoSize = true, Padding = new Padding(5) };
    private readonly Stack<Point> _undoneBackgroundSamples = new();
    private readonly ToolTip _targetToolTip = new() { AutoPopDelay = 25000, InitialDelay = 250, ReshowDelay = 100 };
    private readonly CheckBox _showGuides = new() { Text = "中心/格底参考线", AutoSize = true };
    private readonly CheckBox _expandSource = new() { Text = "放大编辑区（隐藏逐帧诊断）", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
    private readonly TabControl _workspaceTabs = new() { Dock = DockStyle.Fill };
    private bool _synchronizingSelection;

    private Control BuildLockedTargetHeader()
    {
        var type = _asset.Kind == ResourceKind.SComposite
            ? (_asset.SMode == SImageMode.Job ? "兵种 S" : "将领 S") + $" · Unit #{_context.PhysicalImageNumber}"
            : _asset.DisplayName;
        var destinations = string.Join("；", _referenceFrames.GroupBy(frame => new { frame.Descriptor.SourceKey, frame.Descriptor.ImageNumber })
            .Select(group => $"{group.Key.SourceKey} #{group.Key.ImageNumber}（{group.Count()} 帧）"));
        var header = new Label
        {
            Text = $"锁定目标：{type}  →  {destinations}\n源图文件名和布局清单只辅助切图；确认后写入此处所列形象。",
            Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6), BackColor = Color.FromArgb(230, 241, 250),
            MaximumSize = new Size(Math.Max(200, ClientSize.Width - 30), 0)
        };
        SizeChanged += (_, _) => header.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 30), 0);
        _targetToolTip.SetToolTip(header, "当前写入文件（鼠标悬停核对完整位置）：\n" + string.Join("\n",
            _referenceFrames.GroupBy(frame => new { frame.Descriptor.SourcePath, frame.Descriptor.ImageNumber })
                .Select(group => $"{group.Key.SourcePath}  ·  物理图号 {group.Key.ImageNumber}")));
        return header;
    }

    private Control BuildWorkspace()
    {
        var tabs = _workspaceTabs;
        var source = new TabPage("裁剪与格位") { Padding = new Padding(3) };
        var sourceLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var manifestBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        AddButton(manifestBar, "关联布局清单…", AssociateManifest);
        AddButton(manifestBar, "重新自动识别", ResetSourceMapping);
        manifestBar.Controls.Add(_expandSource);
        manifestBar.Controls.Add(_manifestStatus);
        sourceLayout.Controls.Add(manifestBar, 0, 0);
        sourceLayout.Controls.Add(_sourceEditor, 0, 1);
        source.Controls.Add(sourceLayout);
        var pixels = new TabPage("像素、蒙版与动画") { Padding = new Padding(3) };
        pixels.Controls.Add(BuildPreviews());
        tabs.TabPages.Add(source);
        tabs.TabPages.Add(pixels);
        return tabs;
    }

    private void InitializeSourceSpace(TableLayoutPanel root)
    {
        void Apply()
        {
            var expand = _workspaceTabs.SelectedIndex <= 0 && _expandSource.Checked;
            root.SuspendLayout();
            _diagnostics.Visible = !expand;
            root.RowStyles[1].SizeType = SizeType.Percent;
            root.RowStyles[1].Height = expand ? 100 : 78;
            root.RowStyles[2].SizeType = expand ? SizeType.Absolute : SizeType.Percent;
            root.RowStyles[2].Height = expand ? 0 : 22;
            root.ResumeLayout(true);
        }
        _workspaceTabs.SelectedIndexChanged += (_, _) => Apply();
        _expandSource.CheckedChanged += (_, _) => Apply();
        Shown += (_, _) => Apply();
        Apply();
    }

    private void InitializeSourceEditing()
    {
        _maskBackdrop.Items.AddRange(["深灰", "白色", "棋盘"]);
        _maskBackdrop.SelectedIndex = 0;
        _maskBackdrop.SelectedIndexChanged += (_, _) => UpdateMaskBackdrop();
        _animationScope.Items.AddRange(["当前动作组", "全套检查"]);
        _animationScope.SelectedIndex = 0;
        _animationScope.SelectedIndexChanged += (_, _) => { _animationIndex = 0; ShowAnimationFrame(); };
        _sourceEditor.MappingChanged += (_, args) =>
        {
            _options.SourceMapping = args.Mapping.Clone();
            if (args.EffectiveBoundsChanged)
            {
                _options.BackgroundSample = null;
                _options.BackgroundSamples.Clear();
                _undoneBackgroundSamples.Clear();
                _manifestStatus.Text = "有效画布已修改：重新识别格位，背景取样已清空";
            }
            Rerun();
        };
        _sourceEditor.SelectedFrameChanged += (_, _) =>
        {
            if (_synchronizingSelection || _sourceEditor.SelectedFrameKey == null) return;
            _synchronizingSelection = true;
            try
            {
                foreach (DataGridViewRow row in _diagnostics.Rows)
                    if (row.Tag is AiAtlasFrameDiagnostic item && item.FrameKey == _sourceEditor.SelectedFrameKey)
                    {
                        _diagnostics.CurrentCell = row.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                        row.Selected = true;
                        break;
                    }
            }
            finally { _synchronizingSelection = false; }
        };
        _maskPreview.Paint += (_, args) => DrawBackgroundSamples(args.Graphics);
        _showGuides.CheckedChanged += (_, _) =>
        {
            foreach (var box in new[] { _referencePreview, _rawPreview, _alignedPreview, _finalPreview, _animationPreview }) box.Invalidate();
        };
        foreach (var box in new[] { _referencePreview, _rawPreview, _alignedPreview, _finalPreview, _animationPreview })
            box.Paint += (_, args) => DrawFrameGuides(box, args.Graphics);
    }

    private void UpdateSourceEditor(AiAtlasNormalizationResult result)
    {
        var frames = _legacyThreeTurns.Checked ? _legacyReferenceFrames ?? _referenceFrames : _referenceFrames;
        var mapping = result.SourceMapping ?? _options.SourceMapping?.Clone() ?? new AtlasSourceMapping
        {
            EffectiveBounds = new Rectangle(Point.Empty, result.SourceSize)
        };
        if (mapping.FrameRectangles.Count == 0)
            foreach (var diagnostic in result.FrameDiagnostics.Where(item => !item.FrameKey.StartsWith("reserved:", StringComparison.Ordinal)))
                mapping.FrameRectangles[diagnostic.FrameKey] = diagnostic.SourceBounds;
        _sourceEditor.SetSource(result.SourcePreview, mapping, frames.Select(frame => frame.Descriptor).ToArray());
    }

    private void SelectSourceFrame()
    {
        if (!_synchronizingSelection && _diagnostics.CurrentRow?.Tag is AiAtlasFrameDiagnostic item)
        {
            _synchronizingSelection = true;
            try { _sourceEditor.SelectFrame(item.FrameKey); }
            finally { _synchronizingSelection = false; }
        }
        _animationIndex = 0;
        ShowAnimationFrame();
    }

    private void AssociateManifest()
    {
        using var picker = new OpenFileDialog { Title = "选择管理器导出的布局清单", Filter = "布局清单 (*.atlas.json)|*.atlas.json|JSON (*.json)|*.json" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            AssociateManifestFile(picker.FileName);
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "布局清单无法使用", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void AssociateManifestFile(string path)
    {
        if (_result == null) throw new InvalidOperationException("请等待源图载入后再关联布局清单。");
        var manifest = AtlasManifest.Load(path);
        var mapping = manifest.CreateSourceMapping(_context, _result.SourceSize, _sourceEditor.Mapping?.EffectiveBounds);
        mapping.RepairAspectRatio = _sourceEditor.Mapping?.RepairAspectRatio ?? false;
        _sourceEditor.ReplaceMapping(mapping);
        _manifestStatus.Text = $"布局：{Path.GetFileName(path)}；目标仍为当前选中形象";
    }

    private void ResetSourceMapping()
    {
        var current = _sourceEditor.Mapping;
        if (current == null) { Rerun(); return; }
        current.FrameRectangles.Clear();
        current.ConfirmedFrames.Clear();
        _manifestStatus.Text = "保留有效画布，重新自动识别动作格位";
        _sourceEditor.ReplaceMapping(current);
    }

    private void UndoBackgroundSample()
    {
        if (_options.BackgroundSamples.Count == 0) return;
        _undoneBackgroundSamples.Push(_options.BackgroundSamples[^1]);
        _options.BackgroundSamples.RemoveAt(_options.BackgroundSamples.Count - 1);
        Rerun();
    }

    private void RedoBackgroundSample()
    {
        if (_undoneBackgroundSamples.Count == 0) return;
        _options.BackgroundSamples.Add(_undoneBackgroundSamples.Pop());
        Rerun();
    }

    private void ClearBackgroundSamples()
    {
        if (_options.BackgroundSamples.Count == 0 && _options.BackgroundSample == null) return;
        _options.BackgroundSamples.Clear();
        _options.BackgroundSample = null;
        _undoneBackgroundSamples.Clear();
        Rerun();
    }

    private void UpdateMaskBackdrop()
    {
        var old = _maskPreview.BackgroundImage;
        _maskPreview.BackgroundImage = null;
        old?.Dispose();
        _maskPreview.BackColor = _maskBackdrop.SelectedIndex == 1 ? Color.White : Color.FromArgb(38, 40, 43);
        if (_maskBackdrop.SelectedIndex == 2)
        {
            var checker = new Bitmap(20, 20);
            using var graphics = Graphics.FromImage(checker);
            graphics.Clear(Color.WhiteSmoke);
            graphics.FillRectangle(Brushes.LightGray, 0, 0, 10, 10);
            graphics.FillRectangle(Brushes.LightGray, 10, 10, 10, 10);
            _maskPreview.BackgroundImage = checker;
            _maskPreview.BackgroundImageLayout = ImageLayout.Tile;
        }
        _maskPreview.Invalidate();
    }

    private void DrawBackgroundSamples(Graphics graphics)
    {
        if (_result == null) return;
        var size = _result.SourceSize;
        var scale = Math.Min(_maskPreview.ClientSize.Width / (double)size.Width, _maskPreview.ClientSize.Height / (double)size.Height);
        var left = (_maskPreview.ClientSize.Width - size.Width * scale) / 2;
        var top = (_maskPreview.ClientSize.Height - size.Height * scale) / 2;
        using var pen = new Pen(Color.LimeGreen, 2);
        foreach (var sample in _options.BackgroundSamples)
        {
            var x = (float)(left + sample.X * scale); var y = (float)(top + sample.Y * scale);
            graphics.DrawEllipse(pen, x - 4, y - 4, 8, 8);
            graphics.DrawLine(pen, x - 7, y, x + 7, y);
            graphics.DrawLine(pen, x, y - 7, x, y + 7);
        }
    }

    private void DrawFrameGuides(PictureBox box, Graphics graphics)
    {
        if (!_showGuides.Checked || box.Image == null) return;
        var scale = Math.Min(box.ClientSize.Width / (double)box.Image.Width, box.ClientSize.Height / (double)box.Image.Height);
        var width = (float)(box.Image.Width * scale); var height = (float)(box.Image.Height * scale);
        var left = (box.ClientSize.Width - width) / 2; var top = (box.ClientSize.Height - height) / 2;
        using var pen = new Pen(Color.FromArgb(180, Color.LimeGreen), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        graphics.DrawLine(pen, left + width / 2, top, left + width / 2, top + height);
        graphics.DrawLine(pen, left, top + height - 1, left + width, top + height - 1);
    }

    private IReadOnlyList<DecodedFrame> AnimationFrames()
    {
        if (_result == null) return [];
        if (_animationScope.SelectedIndex == 1 || _diagnostics.CurrentRow?.Tag is not AiAtlasFrameDiagnostic selected)
            return _result.Frames;
        var descriptor = selected.Descriptor;
        return _result.Frames.Where(frame => frame.Descriptor.Turn == descriptor.Turn &&
            frame.Descriptor.Action == descriptor.Action && (_asset.Kind != ResourceKind.SComposite
                ? frame.Descriptor.Direction == descriptor.Direction
                : descriptor.Action == "atk" ? frame.Descriptor.FrameIndex / 4 == descriptor.FrameIndex / 4
                : descriptor.Action == "mov" ? descriptor.FrameIndex < 6
                    ? frame.Descriptor.FrameIndex < 6 && frame.Descriptor.FrameIndex / 2 == descriptor.FrameIndex / 2
                    : frame.Descriptor.FrameIndex == descriptor.FrameIndex
                : true)).OrderBy(frame => frame.Descriptor.FrameIndex).ToArray();
    }
}
