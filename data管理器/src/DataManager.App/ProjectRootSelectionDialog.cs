namespace DataManager.App;

internal sealed class ProjectRootSelectionDialog : Form
{
    private readonly ListBox _roots = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    public string SelectedRoot => (string)_roots.SelectedItem!;

    public ProjectRootSelectionDialog(IReadOnlyList<string> candidates)
    {
        Text = "选择实际游戏目录";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(600, 300);
        Size = new Size(850, 380);
        Padding = new Padding(14);
        var instructions = new Label { Text = "这个文件夹包含多个基底，请选择要编辑的游戏目录。", Dock = DockStyle.Top, Height = 36 };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var open = new Button { Text = "打开选中目录", DialogResult = DialogResult.OK, AutoSize = true };
        actions.Controls.Add(cancel); actions.Controls.Add(open);
        Controls.Add(_roots); Controls.Add(actions); Controls.Add(instructions);
        foreach (var root in candidates) _roots.Items.Add(root);
        if (_roots.Items.Count > 0) _roots.SelectedIndex = 0;
        AcceptButton = open; CancelButton = cancel;
        _roots.DoubleClick += (_, _) => { if (_roots.SelectedItem != null) { DialogResult = DialogResult.OK; Close(); } };
    }
}
