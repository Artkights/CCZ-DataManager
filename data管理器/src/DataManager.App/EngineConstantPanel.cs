using DataManager.Core.Editing;
using DataManager.Core.Scanner;
using ImageManager.App;

namespace DataManager.App;

/// <summary>Edits the confirmed special-skill constants with a backup and read-back transaction.</summary>
public sealed class EngineConstantPanel : UserControl
{
    private readonly Label _hint = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView _grid = new BufferedGrid();
    private readonly Label _editorTitle = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly NumericUpDown _number = new() { Minimum = 0, Maximum = 255, Width = 90 };
    private readonly TextBox _text = new() { Width = 420, MaxLength = 16 };
    private readonly Button _write = new() { Text = "写入此常量", AutoSize = true };
    private readonly Button _refresh = new() { Text = "刷新", AutoSize = true };
    private readonly Label _editorHint = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly EngineConstantService _service = new();
    private ProjectWorkspace? _workspace;
    private IReadOnlyList<EngineConstantValue> _values = [];
    private bool _binding;

    public EngineConstantPanel()
    {
        Dock = DockStyle.Fill;
        _hint.Text = "本页改代码固定值、文本槽；6.6 疾风攻击的每点增伤在兵种76槽。写入前备份、写后复读，失败回滚。";
        _hint.ForeColor = Color.FromArgb(70, 80, 90);
        _hint.Padding = new Padding(8, 6, 8, 4);

        ConfigureGrid();
        var editor = BuildEditor();

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        body.Controls.Add(_hint, 0, 0);
        body.Controls.Add(_grid, 0, 1);
        body.Controls.Add(editor, 0, 2);
        Controls.Add(body);

        _grid.SelectionChanged += (_, _) => BindSelection();
        _write.Click += (_, _) => WriteSelected();
        _refresh.Click += (_, _) => RefreshValues();
    }

    public event EventHandler<EngineConstantStatusEventArgs>? StatusChanged;

    public void Bind(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        RefreshValues();
    }

    public void Reset()
    {
        _workspace = null;
        _values = [];
        _grid.Rows.Clear();
        _editorTitle.Text = "尚未打开项目。";
        _number.Enabled = false;
        _text.Enabled = false;
        _write.Enabled = false;
    }

    public void RefreshValues()
    {
        if (_workspace == null) { Reset(); return; }
        try
        {
            _values = _service.Read(_workspace);
            var version = _workspace.ActiveProfile.Profile.Version;
            _hint.Text = version == "6.6"
                ? $"当前基底 {version}。疾风攻击每点增伤改兵种76槽特效值；其他特效值到特效分配页改。写入自动备份并复读。"
                : $"当前基底 {version}。本页改代码固定值和文本槽；特效值到特效分配页改。写入自动备份并复读。";
            _binding = true;
            try
            {
                _grid.Rows.Clear();
                foreach (var value in _values)
                {
                    var index = _grid.Rows.Add(value.Definition.Module, value.Definition.DisplayName,
                        Display(value), value.Definition.LocationText, value.Definition.Description,
                        value.Nature);
                    _grid.Rows[index].Tag = value;
                    if (!value.SignatureMatches) _grid.Rows[index].DefaultCellStyle.ForeColor = Color.Gray;
                }
                _grid.ClearSelection();
                if (_grid.Rows.Count > 0)
                {
                    _grid.CurrentCell = _grid.Rows[0].Cells[0];
                    _grid.Rows[0].Selected = true;
                }
            }
            finally { _binding = false; }
            BindSelection();
            RaiseStatus($"常量 {_values.Count} 项，当前基底可改 {_values.Count(value => value.SignatureMatches)} 项；选择一行修改。", false);
        }
        catch (Exception ex)
        {
            Reset();
            RaiseStatus("常量读取失败：" + ex.Message, true);
        }
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var column in new[] { ("模块", 8f, 64), ("常量", 16f, 140), ("当前值", 9f, 80),
                     ("位置", 25f, 170), ("说明", 30f, 200), ("性质", 12f, 140) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, FillWeight = column.Item2,
                MinimumWidth = column.Item3, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns[4].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns[5].DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        SelectionAppearance.Configure(_grid);
    }

    private Control BuildEditor()
    {
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6) };
        var commands = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false };
        commands.Controls.AddRange([_editorTitle, _number, _text, _write, _refresh]);
        _editorTitle.Margin = new Padding(0, 8, 10, 0);
        _number.Margin = new Padding(0, 5, 8, 0);
        _text.Margin = new Padding(0, 5, 8, 0);
        _write.Margin = new Padding(0, 4, 8, 0);
        _refresh.Margin = new Padding(0, 4, 0, 0);
        _editorHint.Dock = DockStyle.Fill;
        _editorHint.ForeColor = Color.FromArgb(70, 80, 90);
        host.Controls.Add(_editorHint);
        host.Controls.Add(commands);
        return host;
    }

    private void BindSelection()
    {
        if (_binding) return;
        var value = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as EngineConstantValue : null;
        if (value == null)
        {
            _editorTitle.Text = "尚未选择常量。";
            _number.Enabled = false; _text.Enabled = false; _write.Enabled = false;
            _editorHint.Text = string.Empty;
            return;
        }
        var definition = value.Definition;
        _editorTitle.Text = definition.DisplayName;
        var isText = definition.Kind == EngineConstantKind.Text;
        _number.Visible = !isText;
        _text.Visible = isText;
        _number.Enabled = !isText && value.SignatureMatches;
        _text.Enabled = isText && value.SignatureMatches;
        _write.Enabled = value.SignatureMatches;
        _binding = true;
        try
        {
            if (isText) _text.Text = value.TextValue;
            else
            {
                _number.Minimum = definition.Minimum;
                _number.Maximum = definition.Maximum;
                _number.Value = Math.Clamp(value.NumericValue ?? 0, definition.Minimum, definition.Maximum);
            }
        }
        finally { _binding = false; }
        _editorHint.Text = value.SignatureMatches
            ? $"可改；范围 {definition.Minimum}..{definition.Maximum}；写入后会复读校验。"
            : "只读：" + FriendlyHint(value.Diagnostic);
    }

    private void WriteSelected()
    {
        if (_workspace == null || _grid.SelectedRows.Count == 0 ||
            _grid.SelectedRows[0].Tag is not EngineConstantValue value) return;
        var definition = value.Definition;
        if (!value.SignatureMatches)
        {
            RaiseStatus($"{definition.DisplayName}：{FriendlyHint(value.Diagnostic)}", true);
            return;
        }
        EngineConstantEdit edit;
        string description;
        if (definition.Kind == EngineConstantKind.Text)
        {
            var text = _text.Text.Trim();
            edit = new(definition.Id, null, text);
            description = $"“{value.TextValue}” → “{text}”";
        }
        else
        {
            var number = (int)_number.Value;
            edit = new(definition.Id, number, null);
            description = $"{value.NumericValue} → {number}";
        }
        if (MessageBox.Show(this, $"{definition.DisplayName}：{description}。{Environment.NewLine}{Environment.NewLine}" +
                "写入前会备份 Ekd5.exe，写入后自动复读；确认写入？",
                "写入引擎常量", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var result = _service.Commit(_workspace, [edit]);
        if (!result.Success)
        {
            RaiseStatus(result.Message, true);
            MessageBox.Show(this, result.Message, "引擎常量写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        RaiseStatus(result.Message, false);
    }

    private static string Display(EngineConstantValue value) => value.Definition.Kind == EngineConstantKind.Text
        ? (string.IsNullOrEmpty(value.TextValue) ? "（空）" : value.TextValue)
        : value.NumericValue?.ToString() ?? "—";

    private void RaiseStatus(string message, bool error)
        => StatusChanged?.Invoke(this, new EngineConstantStatusEventArgs(message, error));

    internal DataGridView GridForSmokeTest => _grid;
    internal string EditorHintForSmokeTest => _editorHint.Text;
    internal NumericUpDown NumberForSmokeTest => _number;
    internal TextBox TextForSmokeTest => _text;
    internal Button WriteForSmokeTest => _write;
    internal IReadOnlyList<EngineConstantValue> ValuesForSmokeTest => _values;
    internal void SelectRowForSmokeTest(int index)
    {
        if (index < 0 || index >= _grid.Rows.Count) return;
        _grid.ClearSelection();
        _grid.CurrentCell = _grid.Rows[index].Cells[0];
        _grid.Rows[index].Selected = true;
        BindSelection();
    }

    /// <summary>从战斗公式页跳转过来时按常量 ID 选中对应行。</summary>
    internal void SelectDefinition(string id)
    {
        for (var index = 0; index < _grid.Rows.Count; index++)
        {
            if (_grid.Rows[index].Tag is EngineConstantValue value && value.Definition.Id == id)
            {
                SelectRowForSmokeTest(index);
                return;
            }
        }
    }

    private static string FriendlyHint(string diagnostic)
    {
        if (diagnostic.Contains("只适用于", StringComparison.Ordinal)) return diagnostic;
        if (diagnostic.Contains("签名不匹配", StringComparison.Ordinal)) return "当前基底没有这段代码，只读。";
        if (diagnostic.Contains("签名不唯一", StringComparison.Ordinal)) return "这段代码在基底里有多处相同，无法唯一确定，只读。";
        if (diagnostic.Contains("值不一致", StringComparison.Ordinal)) return "成对字段的值不一致，只读。";
        if (diagnostic.Contains("超出范围", StringComparison.Ordinal)) return "当前值超出可写范围，只读。";
        if (diagnostic.Contains("特效表指针", StringComparison.Ordinal) ||
            diagnostic.Contains("指针值", StringComparison.Ordinal)) return "特效表指针无法定位，只读。";
        if (diagnostic.Contains("GBK", StringComparison.Ordinal)) return "文本内容无法识别，只读。";
        return diagnostic;
    }

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() => DoubleBuffered = true;
    }
}

public sealed class EngineConstantStatusEventArgs(string message, bool error) : EventArgs
{
    public string Message { get; } = message;
    public bool Error { get; } = error;
}
