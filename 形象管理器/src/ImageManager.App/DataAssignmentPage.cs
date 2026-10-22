using ImageManager.Core;

namespace ImageManager.App;

public sealed class DataAssignmentPage : UserControl
{
    private readonly ICharacterAssignmentService _service;
    private readonly IImageMutationService _mutations;
    private readonly IImageCatalogService _catalog;
    private readonly ISPhysicalMappingService _sMapping = new SPhysicalMappingService();
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeColumns = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false
    };
    private readonly PictureBox[] _rPreviews = Enumerable.Range(0, 8).Select(_ => PreviewBox()).ToArray();
    private readonly PictureBox[] _sPreviews = Enumerable.Range(0, 12).Select(_ => PreviewBox()).ToArray();
    private readonly RadioButton[] _turnButtons = Enumerable.Range(1, 3).Select(turn => new RadioButton
    {
        Text = $"{ChineseTurn(turn)}转", Appearance = Appearance.Button, AutoSize = true,
        MinimumSize = new Size(68, 30), TextAlign = ContentAlignment.MiddleCenter, Tag = turn, Margin = new Padding(0)
    }).ToArray();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    private readonly BindingSource _source = new();
    private CancellationTokenSource? _previewCancellation;
    private ProjectProfile? _project;
    private ImageResourceDescriptor? _rResource;
    private ImageResourceDescriptor? _sResource;
    private List<CharacterAssignmentRow> _rows = [];

    public DataAssignmentPage(ICharacterAssignmentService service, IImageMutationService mutations, IImageCatalogService catalog)
    {
        _service = service;
        _mutations = mutations;
        _catalog = catalog;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "角色名", DataPropertyName = nameof(CharacterAssignmentRow.Name), ReadOnly = true, Width = 205 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "R 形象编号", DataPropertyName = nameof(CharacterAssignmentRow.RId), Width = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "S 形象编号", DataPropertyName = nameof(CharacterAssignmentRow.SId), Width = 105 });
        _grid.SelectionChanged += (_, _) => BeginUpdatePreview();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 442));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var turnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 2, 4, 6)
        };
        turnBar.Controls.Add(new Label { Text = "S 预览转数", AutoSize = true, Margin = new Padding(0, 7, 10, 0) });
        foreach (var button in _turnButtons)
        {
            turnBar.Controls.Add(button);
            button.CheckedChanged += (_, _) => { if (button.Checked) BeginUpdatePreview(); };
        }
        _turnButtons[0].Checked = true;

        var previewArea = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8) };
        previewArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewArea.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        previewArea.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        previewArea.Controls.Add(BuildPreviewMatrix(_rPreviews, 2, 4), 0, 0);
        previewArea.Controls.Add(BuildPreviewMatrix(_sPreviews, 3, 4), 0, 1);

        root.Controls.Add(turnBar, 0, 0);
        root.SetColumnSpan(turnBar, 2);
        root.Controls.Add(_grid, 0, 1);
        root.Controls.Add(previewArea, 1, 1);
        Controls.Add(root);
        Controls.Add(_status);
    }

    public IReadOnlySet<int> UsedRIds => _rows.Select(row => row.RId).ToHashSet();
    public IReadOnlySet<int> UsedSIds => _rows.Where(row => row.SId > 0).Select(row => row.SId).ToHashSet();
    public IReadOnlySet<int> UsedSPhysicalIds => _sMapping.ResolveUsedPhysicalIds(
        _rows.Select(row => new CharacterAssignment(row.RowIndex, row.Name, row.JobId, row.RId, row.SId)));

    public void LoadProject(ProjectProfile project, IReadOnlyList<ImageResourceDescriptor>? catalog = null)
    {
        _project = project;
        var resources = catalog ?? _catalog.BuildCatalog(project);
        _rResource = resources.FirstOrDefault(item => item.Category == ImageCategory.CharacterR);
        _sResource = resources.FirstOrDefault(item => item.Category == ImageCategory.CharacterS);
        _rows = _service.Read(project)
            .Select(row => new CharacterAssignmentRow(row.RowIndex, row.Name, row.JobId, row.RId, row.SId)).ToList();
        _source.DataSource = _rows;
        _grid.DataSource = _source;
        var layout = _service.GetLayout(project);
        var readOnly = project.IsReadOnly || !layout.CanWrite;
        _grid.Columns[1].ReadOnly = readOnly;
        _grid.Columns[2].ReadOnly = readOnly;
        _status.Text = readOnly
            ? "当前项目的人物指定表未通过可写验证；角色名与职业始终只读。"
            : $"修改 R/S 编号后按 Ctrl+S 事务保存。布局：R@0x{layout.ROffset:X} / S@0x{layout.SOffset:X}";
        BeginUpdatePreview();
    }

    public void SaveAssignments()
    {
        if (_project == null) return;
        try
        {
            _grid.EndEdit();
            var assignments = _rows.Select(row =>
                new CharacterAssignment(row.RowIndex, row.Name, row.JobId, row.RId, row.SId)).ToArray();
            var result = _mutations.Commit(_project, _service.PlanSave(_project, assignments));
            MessageBox.Show(this,
                result.Success ? "形象指定已保存并建立历史快照。" : string.Join(Environment.NewLine, result.Diagnostics),
                result.Success ? "保存完成" : "保存失败", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void BeginUpdatePreview()
    {
        if (_project == null || _grid.CurrentRow?.DataBoundItem is not CharacterAssignmentRow row) return;
        var effectiveTurn = row.SId == 0 && row.JobId is >= 0 and <= 59 ? row.JobId % 3 + 1 : 1;
        foreach (var button in _turnButtons) button.Enabled = false;
        if (!_turnButtons[effectiveTurn - 1].Checked)
        {
            _turnButtons[effectiveTurn - 1].Checked = true;
            return;
        }

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        _ = UpdatePreviewAsync(row, SelectedTurn(), _previewCancellation.Token);
    }

    private async Task UpdatePreviewAsync(CharacterAssignmentRow row, int turn, CancellationToken cancellationToken)
    {
        ClearPreviews();
        try
        {
            var images = await Task.Run(() => LoadPreviewImages(row, turn, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var image in images.R.Concat(images.S)) image?.Dispose();
                return;
            }
            for (var index = 0; index < _rPreviews.Length; index++) ReplaceImage(_rPreviews[index], images.R[index]);
            for (var index = 0; index < _sPreviews.Length; index++) ReplaceImage(_sPreviews[index], images.S[index]);
            _status.Text = $"{row.Name}：R{row.RId:000}；S{row.SId:000} / 职业 {row.JobId} / {ChineseTurn(turn)}转";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested) _status.Text = "预览失败：" + ex.Message;
        }
    }

    private (Bitmap?[] R, Bitmap?[] S) LoadPreviewImages(CharacterAssignmentRow row, int turn, CancellationToken token)
    {
        if (_project == null) return (new Bitmap?[8], new Bitmap?[12]);
        if (_rResource == null || _sResource == null) throw new InvalidDataException("项目缺少 R 或 S 形象资源。");
        var rAsset = _catalog.ReadAssets(_project, _rResource).FirstOrDefault(item => item.LogicalId == row.RId);
        var physical = _sMapping.Resolve(row.SId, row.JobId, turn).ImageNumber;
        token.ThrowIfCancellationRequested();

        var rImages = new Bitmap?[8];
        if (rAsset != null)
        {
            var decoded = _catalog.DecodeFrames(_project, rAsset);
            try
            {
                var order = rAsset.Frames.Where(frame => frame.FrameIndex < 4)
                    .OrderBy(frame => frame.Direction == "front" ? 0 : 1).ThenBy(frame => frame.FrameIndex).ToArray();
                for (var index = 0; index < Math.Min(order.Length, rImages.Length); index++)
                {
                    var frame = decoded.First(item => SameFrame(item.Descriptor, order[index]));
                    rImages[index] = new Bitmap(frame.Bitmap);
                }
            }
            finally { foreach (var frame in decoded) frame.Dispose(); }
        }

        token.ThrowIfCancellationRequested();
        var sImages = new Bitmap?[12];
        var attackPath = _sResource.Paths.FirstOrDefault(path =>
            Path.GetFileName(path).Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase));
        if (attackPath != null && physical <= E5Archive.Open(attackPath).Entries.Count)
        {
            var bytes = E5Archive.Open(attackPath).ReadDecoded(physical);
            using var strip = ImageRendering.IsStandardImage(bytes)
                ? ImageRendering.DecodeStandardImage(bytes)
                : ImageRendering.DecodeIndexed(bytes, 64, 64 * 12, new PaletteService().Load(_project));
            var order = new[] { 0, 1, 2, 3, 8, 9, 10, 11, 4, 5, 6, 7 };
            for (var index = 0; index < order.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                sImages[index] = ImageRendering.Crop(strip, new Rectangle(0, order[index] * 64, 64, 64));
            }
        }
        return (rImages, sImages);
    }

    private static bool SameFrame(FrameDescriptor left, FrameDescriptor right)
        => left.SourcePath.Equals(right.SourcePath, StringComparison.OrdinalIgnoreCase) &&
           left.ImageNumber == right.ImageNumber && left.FrameIndex == right.FrameIndex;

    private int SelectedTurn() => _turnButtons.FirstOrDefault(button => button.Checked)?.Tag is int turn ? turn : 1;

    private void ClearPreviews()
    {
        foreach (var box in _rPreviews.Concat(_sPreviews)) ReplaceImage(box, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            ClearPreviews();
        }
        base.Dispose(disposing);
    }

    private static TableLayoutPanel BuildPreviewMatrix(IReadOnlyList<PictureBox> boxes, int rows, int columns)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = rows, ColumnCount = columns, Margin = new Padding(0) };
        for (var column = 0; column < columns; column++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
        for (var row = 0; row < rows; row++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
        for (var index = 0; index < boxes.Count; index++) table.Controls.Add(boxes[index], index % columns, index / columns);
        return table;
    }

    private static PictureBox PreviewBox() => new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(232, 234, 236),
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4), MinimumSize = new Size(48, 48)
    };

    private static void ReplaceImage(PictureBox box, Image? image)
    {
        var old = box.Image;
        box.Image = image;
        old?.Dispose();
    }

    private static string ChineseTurn(int turn) => turn switch { 1 => "一", 2 => "二", 3 => "三", _ => turn.ToString() };

    private sealed class CharacterAssignmentRow(int rowIndex, string name, int jobId, int rId, int sId)
    {
        public int RowIndex { get; } = rowIndex;
        public string Name { get; } = name;
        public int JobId { get; } = jobId;
        public int RId { get; set; } = rId;
        public int SId { get; set; } = sId;
    }
}
