using ImageManager.Core;

namespace ImageManager.App;

public sealed class BatchRenameDialog : Form
{
    private readonly IBatchRenameService _service;
    private readonly ProjectProfile _project;
    private readonly TextBox _root = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill };
    private readonly TextBox _replacement = new() { Dock = DockStyle.Fill };
    private readonly TextBox _prefix = new() { Dock = DockStyle.Fill };
    private readonly TextBox _suffix = new() { Dock = DockStyle.Fill };
    private readonly TextBox _template = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _regex = new() { Text = "正则表达式", AutoSize = true };
    private readonly CheckBox _directories = new() { Text = "同时修改目录名", AutoSize = true };
    private readonly DataGridView _preview = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false };
    private RenamePlan? _plan;

    public BatchRenameDialog(IBatchRenameService service, ProjectProfile project)
    {
        _service = service;
        _project = project;
        Text = "批量修改文件名";
        ClientSize = WorkingSize(900, 670);
        MinimumSize = new Size(680, 520);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        _root.Text = project.RootPath;
        _preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原路径", DataPropertyName = nameof(RenameItem.SourcePath), Width = 390 });
        _preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "目标路径", DataPropertyName = nameof(RenameItem.PreviewTargetPath), Width = 390 });
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 190),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 5, Padding = new Padding(8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddField(fields, "根目录", _root, 0, 0, 3);
        AddField(fields, "查找", _search, 0, 1);
        AddField(fields, "替换", _replacement, 2, 1);
        AddField(fields, "前缀", _prefix, 0, 2);
        AddField(fields, "后缀", _suffix, 2, 2);
        AddField(fields, "模板", _template, 0, 3, 3);
        fields.Controls.Add(_regex, 1, 4);
        fields.Controls.Add(_directories, 3, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, MinimumSize = new Size(0, 48),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8), WrapContents = true };
        var apply = CommandButton("立即改名并保存");
        var preview = CommandButton("预检");
        var browse = CommandButton("选择目录");
        preview.Click += (_, _) => PreviewPlan();
        apply.Click += (_, _) => ApplyPlan();
        browse.Click += (_, _) => Browse();
        buttons.Controls.Add(apply);
        buttons.Controls.Add(preview);
        buttons.Controls.Add(browse);
        Controls.Add(_preview);
        Controls.Add(fields);
        Controls.Add(buttons);
    }

    private void PreviewPlan()
    {
        try
        {
            _plan = _service.Preview(_root.Text, new RenameRule(_search.Text, _replacement.Text, _prefix.Text, _suffix.Text,
                string.IsNullOrWhiteSpace(_template.Text) ? null : _template.Text, _regex.Checked, _directories.Checked));
            _preview.DataSource = _plan.Items.ToList();
            var messages = _plan.Errors.Concat(_plan.Warnings).ToArray();
            if (messages.Length > 0) MessageBox.Show(this, string.Join(Environment.NewLine, messages), "预检结果",
                MessageBoxButtons.OK, _plan.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "预检失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ApplyPlan()
    {
        PreviewPlan();
        if (_plan is not { CanApply: true }) return;
        if (MessageBox.Show(this, $"将通过两阶段改名处理 {_plan.Items.Count} 项，继续？", "确认批量改名",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var result = _service.Apply(_project, _plan);
        MessageBox.Show(this, result.Success ? result.Summary : string.Join(Environment.NewLine, result.Diagnostics),
            result.Success ? "改名完成" : "改名失败", MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        if (result.Success) Close();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(_root.Text) ? _root.Text : _project.RootPath };
        if (dialog.ShowDialog(this) == DialogResult.OK) _root.Text = dialog.SelectedPath;
    }

    private static void AddField(TableLayoutPanel panel, string label, Control control, int column, int row, int span = 1)
    {
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, column, row);
        panel.Controls.Add(control, column + 1, row);
        if (span > 1) panel.SetColumnSpan(control, span);
    }

    private static Button CommandButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 32) };

    private static Size WorkingSize(int width, int height)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, height);
        return new Size(Math.Min(width, (int)(area.Width * .9)), Math.Min(height, (int)(area.Height * .9)));
    }
}
