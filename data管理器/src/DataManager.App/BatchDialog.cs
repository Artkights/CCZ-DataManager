using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.App;

internal sealed class BatchDialog : Form
{
    private readonly CheckedListBox _columns = new() { Width = 300, Height = 105, CheckOnClick = true };
    private readonly ComboBox _operation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly TextBox _value = new() { Width = 220 };
    private readonly Label _preview = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray };
    private readonly TableDocument _document;
    private readonly IReadOnlyList<int> _rows;
    private readonly ResolvedProfile _profile;
    private readonly ProjectWorkspace _workspace;
    public IReadOnlyList<string> ColumnNames => _columns.CheckedItems.Cast<object>().Select(item => item.ToString() ?? string.Empty).Where(name => name.Length > 0).ToArray();
    public string Operation => _operation.SelectedItem?.ToString() ?? string.Empty;
    public string InputValue => _value.Text;

    public BatchDialog(TableDocument document, IReadOnlyList<int> rows, ResolvedProfile profile, ProjectWorkspace workspace)
    {
        Text = $"批量修改：{document.Definition.DisplayName}（{rows.Count} 行）";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 520;
        Height = 440;
        _document = document;
        _rows = rows;
        _profile = profile;
        _workspace = workspace;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "字段", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_columns, 1, 0);
        layout.Controls.Add(new Label { Text = "操作", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_operation, 1, 1);
        layout.Controls.Add(new Label { Text = "值", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_value, 1, 2);
        layout.Controls.Add(new Label { Text = "预览", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_preview, 1, 3);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 4);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        foreach (DataColumn column in document.Data.Columns)
        {
            if (column.ColumnName != "ID" && !column.ReadOnly) _columns.Items.Add(column.ColumnName);
        }
        _operation.Items.AddRange(["统一赋值", "数值增加", "数值减少", "查找替换", "将首行复制到选中行"]);
        if (_columns.Items.Count > 0) _columns.SetItemChecked(0, true);
        _operation.SelectedIndex = 0;
        _columns.ItemCheck += (_, _) => BeginInvoke(UpdatePreview);
        _operation.SelectedIndexChanged += (_, _) => { _value.Enabled = Operation != "将首行复制到选中行"; UpdatePreview(); };
        _value.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var selected = ColumnNames;
        if (selected.Count == 0 || _rows.Count == 0)
        {
            _preview.Text = "请勾选至少一个可编辑字段。";
            return;
        }
        var columnName = selected[0];
        var first = _document.Data.Rows[_rows[0]][columnName];
        var old = Convert.ToString(first, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var operation = Operation;
        var input = _value.Text;
        var result = operation switch
        {
            "统一赋值" => input,
            "数值增加" when int.TryParse(old, out var current) && int.TryParse(input, out var add) => (current + add).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "数值减少" when int.TryParse(old, out var current) && int.TryParse(input, out var subtract) => (current - subtract).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "查找替换" => input.Contains("=>", StringComparison.Ordinal) ? old.Replace(input.Split("=>", 2)[0], input.Split("=>", 2)[1], StringComparison.Ordinal) : "格式：查找=>替换",
            "将首行复制到选中行" => old,
            _ => "请输入数值"
        };
        var (outOfRange, invalidReferences) = CountPreviewErrors(selected, operation, input);
        _preview.Text = $"Profile：{_profile.Profile.DisplayName}\n布局：{_profile.Variant.DisplayName}\n目标：{_document.Definition.FileName}\n" +
                        $"将检查 {_rows.Count} 行、{selected.Count} 字段；越界值 {outOfRange} 项，无效引用 {invalidReferences} 项。\n" +
                        $"示例 ID={_document.Data.Rows[_rows[0]]["ID"]} / {columnName}: {old} -> {result}";
    }

    private (int OutOfRange, int InvalidReferences) CountPreviewErrors(IReadOnlyList<string> columns, string operation, string input)
    {
        var outOfRange = 0;
        var invalidReferences = 0;
        foreach (var columnName in columns)
        {
            var field = _document.Data.Columns[columnName]?.ExtendedProperties["Field"] as TableFieldDefinition;
            if (field == null) continue;
            foreach (var rowIndex in _rows)
            {
                var currentText = Convert.ToString(_document.Data.Rows[rowIndex][columnName], CultureInfo.InvariantCulture) ?? string.Empty;
                var candidateText = operation switch
                {
                    "统一赋值" => input,
                    "将首行复制到选中行" => Convert.ToString(_document.Data.Rows[_rows[0]][columnName], CultureInfo.InvariantCulture) ?? string.Empty,
                    _ => currentText
                };
                if (field.Kind == FieldKind.FixedString)
                {
                    if (operation == "查找替换" && input.Contains("=>", StringComparison.Ordinal))
                    {
                        var parts = input.Split("=>", 2);
                        candidateText = currentText.Replace(parts[0], parts[1], StringComparison.Ordinal);
                    }
                    if (EncodingService.GameEncoding.GetByteCount(candidateText) > field.Size) outOfRange++;
                    continue;
                }
                if (field.Kind is not (FieldKind.UInt8 or FieldKind.UInt16)) continue;
                var current = Convert.ToInt64(_document.Data.Rows[rowIndex][columnName], CultureInfo.InvariantCulture);
                var validInput = TryParseInteger(input, out var parsed);
                var candidate = operation switch
                {
                    "统一赋值" when validInput => parsed,
                    "数值增加" when validInput => current + parsed,
                    "数值减少" when validInput => current - parsed,
                    "将首行复制到选中行" => Convert.ToInt64(_document.Data.Rows[_rows[0]][columnName], System.Globalization.CultureInfo.InvariantCulture),
                    _ => current
                };
                if (operation is "统一赋值" or "数值增加" or "数值减少" && !validInput)
                {
                    outOfRange++;
                    continue;
                }
                if (candidate < field.Minimum || candidate > field.Maximum)
                {
                    outOfRange++;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(field.ReferenceTable) &&
                    _workspace.Documents.TryGetValue(field.ReferenceTable, out var target) &&
                    !target.Data.Rows.Cast<DataRow>().Any(row => Convert.ToInt64(row["ID"], CultureInfo.InvariantCulture) == candidate) &&
                    candidate is not (255 or 1024 or 1025 or 65535))
                {
                    invalidReferences++;
                }
            }
        }
        return (outOfRange, invalidReferences);
    }

    private static bool TryParseInteger(string text, out long value)
        => text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
