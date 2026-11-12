namespace RangeManager.App;

/// <summary>
/// Modal editor for one entry of the 0x4D1400 shape library. The ID combo only
/// offers ids the dispatcher actually reads through the pointer table; ids 0xA
/// and 0xB are code-special-cased and therefore excluded. Editing accepts both
/// raw generator text and clicks on the expanded preview grid.
/// </summary>
public sealed class ShapeEditDialog : Form
{
    private readonly RangeShapeTable _table;
    private readonly ComboBox _id = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Height = 30 };
    private readonly TextBox _cells = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };
    private readonly ShapeGridControl _preview = new() { Dock = DockStyle.Fill, Editable = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 46, ForeColor = Color.FromArgb(107, 114, 128), Padding = new Padding(8, 2, 8, 2) };

    public int SelectedId { get; private set; }
    public List<CellOffset> Cells { get; private set; } = [];

    public ShapeEditDialog(RangeShapeTable table, int initialId)
    {
        _table = table;
        Text = "新增 / 编辑范围形状";
        StartPosition = FormStartPosition.CenterParent;
        Width = 760;
        Height = 500;
        MinimumSize = new Size(660, 440);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(246, 247, 249);

        foreach (var row in table.Rows)
        {
            if (row.Id is 0x0A or 0x0B) continue;
            _id.Items.Add(new IdItem(row.Id, $"ID {row.Id:D2} · 生成元 {row.Cells.Count} · 实际 {row.ExpandedCount} 格"));
        }
        _id.SelectedIndexChanged += (_, _) => LoadSelected();
        _cells.TextChanged += (_, _) => RefreshPreview();
        _preview.CellClicked += (_, cell) => ToggleCell(cell);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 8, 10, 2), BackColor = Color.White };
        top.Controls.Add(new Label { Text = "形状 ID", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
        top.Controls.Add(_id);

        var body = new SplitContainer { Dock = DockStyle.Fill, BackColor = Color.White };
        body.SizeChanged += (_, _) =>
        {
            try
            {
                if (body.Width <= 600) return;
                body.Panel1MinSize = 260;
                body.SplitterDistance = 320;
            }
            catch (InvalidOperationException) { }
        };
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };
        left.Controls.Add(_cells);
        var leftHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = "生成元（dx,dy; …）"
        };
        left.Controls.Add(leftHeader);
        body.Panel1.Controls.Add(left);
        body.Panel2.Controls.Add(_preview);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8), BackColor = Color.White };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true, Height = 30 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 30 };
        ok.Click += (_, _) => Commit();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(body);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(_status);
        AcceptButton = ok;
        CancelButton = cancel;

        int index = 0;
        for (int i = 0; i < _id.Items.Count; i++)
        {
            if (_id.Items[i] is IdItem item && item.Id == initialId) { index = i; break; }
        }
        _id.SelectedIndex = index;
    }

    private int FixedRadius => Math.Max(4, _table.Rows
        .SelectMany(r => ShapeGeometry.ExpandMirror(r.Cells))
        .Select(c => Math.Max(Math.Abs(c.Dx), Math.Abs(c.Dy)))
        .DefaultIfEmpty(0)
        .Max() + 1);

    private void LoadSelected()
    {
        if (_id.SelectedItem is not IdItem item) return;
        var row = _table.Find(item.Id);
        _cells.Text = row == null ? "" : row.CellsText;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            var generators = RangeShapeTable.ParseText(_cells.Text);
            var expanded = ShapeGeometry.ExpandMirror(generators);
            SelectedId = (_id.SelectedItem as IdItem)?.Id ?? 1;
            _preview.SetShape(expanded, ShapeLayer.Attack, FixedRadius, $"生成元 {generators.Count} → 实际 {expanded.Count} 格");
            int needed = RangeShapeTable.RequiredBytes(generators.Count);
            _status.Text = $"生成元 {generators.Count} 个 → 实际 {expanded.Count} 格" +
                $"　·　本形状需要 {needed} 字节" +
                $"　·　安全区剩余 {_table.ArenaRemainingBytes} 字节";
        }
        catch (FormatException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void ToggleCell(CellOffset cell)
    {
        try
        {
            var generators = RangeShapeTable.ParseText(_cells.Text);
            var toggled = ShapeGeometry.Toggle(generators, cell);
            _cells.Text = string.Join("; ", toggled.Select(c => $"{c.Dx},{c.Dy}"));
        }
        catch (FormatException)
        {
            // Keep the current text; the status line already shows the parse error.
        }
    }

    private void Commit()
    {
        try
        {
            SelectedId = (_id.SelectedItem as IdItem)?.Id ?? 1;
            Cells = RangeShapeTable.ParseText(_cells.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
        }
    }

    private sealed record IdItem(int Id, string Text)
    {
        public override string ToString() => Text;
    }
}
