using ImageManager.Core;

namespace ImageManager.App;

internal sealed class ImageExchangeOptionsDialog : Form
{
    private readonly CheckBox _allTurns = new() { Text = "全部转数（turn1 / turn2 / turn3 子目录）", AutoSize = true };
    private readonly ComboBox _scale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox _transparent = new() { Text = "同时导出透明观察版", AutoSize = true };
    public bool AllTurns => _allTurns.Checked;
    public ReferenceAtlasExportOptions AtlasOptions => new()
    {
        Scale = _scale.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 },
        IncludeTransparent = _transparent.Checked
    };

    public ImageExchangeOptionsDialog(bool referenceAtlas, int turn)
    {
        Text = referenceAtlas ? "参考图导出设置" : "S 动作条范围";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, referenceAtlas ? 190 : 150);
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(14)
        };
        content.Controls.Add(new Label
        {
            Text = referenceAtlas ? "只导出当前预览的一套动作，格位和物理绑定保持一致。" : $"默认仅处理当前预览的第 {turn} 转。",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12)
        });
        if (referenceAtlas)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            row.Controls.Add(new Label { Text = "像素倍率", AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
            _scale.Items.AddRange(["1 倍（原始）", "2 倍", "4 倍"]);
            _scale.SelectedIndex = 0;
            row.Controls.Add(_scale);
            content.Controls.Add(row);
            content.Controls.Add(_transparent);
        }
        else content.Controls.Add(_allTurns);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(8) };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(content);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
