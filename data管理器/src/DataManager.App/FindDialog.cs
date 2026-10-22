using System.Data;
using DataManager.Core.Models;

namespace DataManager.App;

internal sealed class FindDialog : Form
{
    private readonly ComboBox _column = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _find = new() { Width = 260 };
    private readonly TextBox _replace = new() { Width = 260 };
    private readonly Label _replaceLabel = new() { Text = "替换为", AutoSize = true, Anchor = AnchorStyles.Left };

    public string ColumnName => _column.SelectedItem?.ToString() ?? string.Empty;
    public string FindText => _find.Text;
    public string ReplaceText => _replace.Text;

    public FindDialog(TableDocument document, bool replaceMode)
    {
        Text = replaceMode ? $"查找替换：{document.Definition.DisplayName}" : $"查找：{document.Definition.DisplayName}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 410;
        Height = replaceMode ? 220 : 175;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 2,
            RowCount = replaceMode ? 4 : 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "字段", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_column, 1, 0);
        layout.Controls.Add(new Label { Text = "查找", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_find, 1, 1);
        if (replaceMode)
        {
            layout.Controls.Add(_replaceLabel, 0, 2);
            layout.Controls.Add(_replace, 1, 2);
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, replaceMode ? 3 : 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        if (!replaceMode) _column.Items.Add("全部字段");
        foreach (DataColumn column in document.Data.Columns)
        {
            if (column.ColumnName != "ID" && !column.ReadOnly) _column.Items.Add(column.ColumnName);
        }
        if (_column.Items.Count > 0) _column.SelectedIndex = 0;
        Shown += (_, _) => _find.Focus();
    }
}
