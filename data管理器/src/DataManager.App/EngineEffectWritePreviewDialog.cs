using System.Globalization;
using DataManager.Core.Models;
using ImageManager.App;

namespace DataManager.App;

/// <summary>Shows every byte patch before the shared binary transaction writes it.</summary>
public sealed class EngineEffectWritePreviewDialog : Form
{
    private readonly EngineEffectWritePreview _preview;
    private readonly Button _writeButton = new() { Text = "写入", AutoSize = true, DialogResult = DialogResult.OK };
    private readonly Button _cancelButton = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly DataGridView _grid = new BufferedPreviewGrid();
    private readonly IReadOnlyList<string> _impact;

    public EngineEffectWritePreviewDialog(EngineEffectWritePreview preview, IReadOnlyList<string>? impact = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
        _impact = impact ?? [];
        Text = preview.EffectId < 0 ? "写入调用点预览" : "写入引擎预览";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 980;
        Height = 560;
        MinimumSize = new Size(760, 420);
        BuildLayout();
        Populate();
    }

    private void BuildLayout()
    {
        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10, 6, 10, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = (_preview.EffectId < 0 ? "调用点写入" : $"引擎特效 {_preview.EffectId}") +
                   $"：将写入 {_preview.Changes.Count} 项；{DescribeFiles()}\r\n" +
                   "写前会备份到 项目_历史记录/引擎写入/备份/…；写入后立即复读全部字节。"
        };

        _grid.Dock = DockStyle.Fill;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        SelectionAppearance.Configure(_grid);
        var columns = new (string Name, float Weight)[]
        {
            ("文件", 12), ("偏移", 10), ("长度", 6), ("原字节", 26), ("新字节", 26), ("项目", 20)
        };
        foreach (var (name, weight) in columns)
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = name,
                FillWeight = weight,
                MinimumWidth = 52,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });

        var errors = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Height = string.IsNullOrEmpty(ErrorMessage()) ? 0 : 98,
            Visible = !string.IsNullOrEmpty(ErrorMessage()),
            BackColor = Color.FromArgb(255, 245, 245),
            ForeColor = Color.Firebrick,
            BorderStyle = BorderStyle.FixedSingle,
            Text = ErrorMessage()
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        _cancelButton.Margin = new Padding(8, 0, 0, 0);
        _writeButton.Margin = new Padding(8, 0, 0, 0);
        _writeButton.Enabled = _preview.CanWrite;
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_writeButton);

        Controls.Add(_grid);
        Controls.Add(errors);
        if (_impact.Count > 0)
            Controls.Add(new TextBox { Dock = DockStyle.Bottom, Height = 108, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(248, 250, 252), Text = "分配影响：" + Environment.NewLine + string.Join(Environment.NewLine, _impact) });
        Controls.Add(buttons);
        Controls.Add(summary);
        AcceptButton = _preview.CanWrite ? _writeButton : _cancelButton;
        CancelButton = _cancelButton;
    }

    private void Populate()
    {
        foreach (var change in _preview.Changes)
        {
            var index = _grid.Rows.Add(
                Path.GetFileName(change.FilePath),
                $"0x{change.FileOffset:X}",
                change.AfterBytes.Length.ToString(CultureInfo.InvariantCulture),
                HexSummary(change.BeforeBytes),
                HexSummary(change.AfterBytes),
                change.DisplayName);
            var row = _grid.Rows[index];
            row.Cells[0].ToolTipText = change.FilePath;
            row.Cells[5].ToolTipText = $"{change.Kind}；特效 {change.EffectId}";
        }
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
    }

    private string DescribeFiles()
    {
        var files = _preview.Changes.Select(change => Path.GetFileName(change.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return files.Length == 0 ? "没有文件被修改。" : "涉及 " + string.Join("、", files) + "。";
    }

    private string ErrorMessage() => string.Join(Environment.NewLine, _preview.Errors);

    private static string HexSummary(byte[] bytes)
    {
        if (bytes.Length == 0) return "—";
        var shown = bytes.Length <= 16 ? bytes : bytes.Take(12).ToArray();
        var text = string.Join(" ", shown.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        return bytes.Length <= 16 ? text : $"{text} …（{bytes.Length.ToString(CultureInfo.InvariantCulture)} 字节）";
    }

    private sealed class BufferedPreviewGrid : DataGridView
    {
        public BufferedPreviewGrid() => DoubleBuffered = true;
    }
}
