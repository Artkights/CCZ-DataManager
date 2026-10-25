using ImageManager.Core;

namespace ImageManager.App;

/// <summary>A focused scene terrain editor. The owning page commits edits through its normal history transaction.</summary>
public sealed class SceneTerrainEditorDialog : Form
{
    private readonly SceneTerrainDocument _document;
    private readonly Func<Task<bool>> _save;
    private readonly DiamondTerrainCanvas _canvas;
    private readonly Panel _scroll = new() { Name = "TerrainScroll", Dock = DockStyle.Fill, AutoScroll = true,
        BackColor = Color.FromArgb(55, 57, 60) };
    private readonly CheckBox _terrain = new() { Name = "ShowTerrain", Text = "显示地形", Checked = true, AutoSize = true };
    private readonly CheckBox _grid = new() { Name = "ShowGrid", Text = "显示网格", Checked = true, AutoSize = true };
    private readonly Label _coordinates = new() { Name = "TerrainCoordinates", Text = "X：—  Y：—", AutoSize = true,
        TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Right, MinimumSize = new Size(135, 24) };
    private readonly Label _hint = new() { Name = "TerrainHint", Dock = DockStyle.Fill, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, MinimumSize = new Size(160, 26) };
    private readonly Label _diagnostic = new() { Name = "TerrainDiagnostic", Dock = DockStyle.Top,
        AutoSize = true, Padding = new Padding(9, 6, 9, 6), MaximumSize = new Size(1200, 0) };
    private readonly string _baseTitle;
    private bool _saving;
    private bool _allowClose;
    private bool _pointerRefreshQueued;

    public SceneTerrainEditorDialog(SceneTerrainDocument document, Func<Task<bool>> save)
    {
        _document = document;
        _save = save;
        _baseTitle = $"地形编辑 - 内场景 {document.MmapImageNumber:000} / 地形 {document.PmapImageNumber:000}";
        Name = "SceneTerrainEditorDialog";
        Text = _baseTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(1180, Math.Max(760, area.Width - 100)),
            Math.Min(820, Math.Max(540, area.Height - 110)));
        MinimumSize = new Size(760, 540);
        KeyPreview = true;
        _canvas = new DiamondTerrainCanvas(document) { Location = Point.Empty };
        _scroll.Controls.Add(_canvas);
        Controls.Add(_scroll);
        Controls.Add(BuildFooter());
        Controls.Add(_diagnostic);
        _diagnostic.Text = document.Diagnostic;
        _diagnostic.Visible = !string.IsNullOrWhiteSpace(document.Diagnostic);
        _diagnostic.ForeColor = document.CanEdit ? Color.DarkSlateGray : Color.Firebrick;
        _canvas.EditingEnabled = document.CanEdit;
        _canvas.DocumentChanged += UpdateState;
        _canvas.HoverCellChanged += cell =>
        {
            var game = cell is { } value ? document.Geometry.FileToGame(value) : (Point?)null;
            _coordinates.Text = game is { } position ? $"X：{position.X}  Y：{position.Y}" : "X：—  Y：—";
        };
        _canvas.ZoomRequested += ZoomAtMouse;
        _scroll.Scroll += (_, _) => { _canvas.StopStroke(); QueuePointerRefresh(); };
        _scroll.SizeChanged += (_, _) => { _canvas.StopStroke(); QueuePointerRefresh(); };
        DpiChanged += (_, _) => { _canvas.StopStroke(); QueuePointerRefresh(); };
        _terrain.CheckedChanged += (_, _) => { _canvas.ShowTerrain = _terrain.Checked; UpdateState(); };
        _grid.CheckedChanged += (_, _) => _canvas.ShowGrid = _grid.Checked;
        Shown += (_, _) => { FitInitial(); _canvas.Focus(); };
        Deactivate += (_, _) => _canvas.StopStroke();
        FormClosing += ClosingEditor;
        UpdateState();
    }

    private Control BuildFooter()
    {
        var panel = new TableLayoutPanel
        {
            Name = "TerrainFooter", Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 5, 8, 5),
            ColumnCount = 3, RowCount = 1, BackColor = SystemColors.Control
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var toggles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 0, 5, 0) };
        toggles.Controls.Add(_terrain);
        toggles.Controls.Add(_grid);
        panel.Controls.Add(_hint, 0, 0);
        panel.Controls.Add(toggles, 1, 0);
        panel.Controls.Add(_coordinates, 2, 0);
        return panel;
    }

    private void FitInitial()
    {
        var width = Math.Max(1, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        var height = Math.Max(1, _scroll.ClientSize.Height - SystemInformation.HorizontalScrollBarHeight);
        _canvas.Zoom = Math.Clamp(Math.Min((double)width / _canvas.SourceSize.Width,
            (double)height / _canvas.SourceSize.Height), .25, 8);
        _scroll.AutoScrollPosition = Point.Empty;
        _canvas.RefreshHoverFromMouse();
        UpdateState();
    }

    private void ZoomAtMouse(MouseEventArgs e)
    {
        if (_saving) return;
        var oldZoom = _canvas.Zoom;
        var source = new PointF((float)(e.X / oldZoom), (float)(e.Y / oldZoom));
        var viewport = _scroll.PointToClient(_canvas.PointToScreen(e.Location));
        var zoom = Math.Clamp(oldZoom * (e.Delta > 0 ? 1.25 : .8), .25, 8);
        if (Math.Abs(zoom - oldZoom) < .00001) return;
        _canvas.Zoom = zoom;
        _scroll.AutoScrollPosition = new Point(Math.Max(0, (int)Math.Round(source.X * zoom - viewport.X)),
            Math.Max(0, (int)Math.Round(source.Y * zoom - viewport.Y)));
        _canvas.RefreshHoverFromMouse();
        UpdateState();
    }

    private void QueuePointerRefresh()
    {
        if (_pointerRefreshQueued || !IsHandleCreated || IsDisposed) return;
        _pointerRefreshQueued = true;
        BeginInvoke((Action)(() =>
        {
            _pointerRefreshQueued = false;
            if (!IsDisposed) _canvas.RefreshHoverFromMouse();
        }));
    }

    private void UpdateState()
    {
        Text = _baseTitle + (_document.IsDirty ? " *" : "") + (_saving ? "（正在保存）" : "");
        _hint.Text = _saving ? "正在保存并复读校验…" : !_document.CanEdit ? "当前地形只读；可查看网格和坐标。" :
            !_terrain.Checked ? "已退出填涂模式；勾选“显示地形”后可继续编辑。" :
            $"左键填涂 · 右键擦除 · Ctrl+S 保存 · Ctrl+Z/Y 撤回/重做 · Ctrl+滚轮缩放（{_canvas.Zoom:P0}）";
    }

    private async Task<bool> SaveDocument()
    {
        _canvas.StopStroke();
        if (_saving || !_document.CanEdit) return false;
        if (!_document.IsDirty) return true;
        _saving = true;
        _canvas.EditingEnabled = false;
        _terrain.Enabled = _grid.Enabled = false;
        UseWaitCursor = true;
        UpdateState();
        try
        {
            var saved = await _save();
            if (saved) _canvas.Invalidate();
            return saved;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "地形保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _saving = false;
            UseWaitCursor = false;
            _canvas.EditingEnabled = _document.CanEdit;
            _terrain.Enabled = _grid.Enabled = true;
            UpdateState();
        }
    }

    private async void ClosingEditor(object? sender, FormClosingEventArgs e)
    {
        _canvas.StopStroke();
        if (_allowClose) return;
        if (_saving) { e.Cancel = true; return; }
        if (!_document.IsDirty) return;
        e.Cancel = true;
        var choice = MessageBox.Show(this, "地形有尚未保存的修改。是否保存后关闭？\n\n选择“否”将放弃本次未保存修改。",
            "保存地形修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        if (choice == DialogResult.Yes && !await SaveDocument()) return;
        _allowClose = true;
        if (!IsDisposed) BeginInvoke((Action)Close);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { _ = SaveDocument(); return true; }
        if (keyData == (Keys.Control | Keys.Z)) { if (!_saving) _canvas.Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))
        { if (!_saving) _canvas.Redo(); return true; }
        if (keyData == Keys.Escape) { if (!_saving) Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
