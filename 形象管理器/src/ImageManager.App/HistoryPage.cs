using System.Diagnostics;
using ImageManager.Core;

namespace ImageManager.App;

public sealed class HistoryPage : UserControl
{
    private readonly IHistoryService _history;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly TextBox _details = new() { Dock = DockStyle.Bottom, Height = 150, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9F) };
    private ProjectProfile? _project;
    private List<HistoryRecord> _records = [];

    public HistoryPage(IHistoryService history)
    {
        _history = history;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Dpi;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", DataPropertyName = nameof(HistoryRecord.CreatedAt), Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "操作", DataPropertyName = nameof(HistoryRecord.Kind), Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "摘要", DataPropertyName = nameof(HistoryRecord.Summary), Width = 390 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "目标数", DataPropertyName = "TargetCount", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = nameof(HistoryRecord.Status), Width = 90 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "永久保留", DataPropertyName = nameof(HistoryRecord.KeepForever), Width = 80 });
        _grid.SelectionChanged += (_, _) => ShowDetails();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, 44),
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true };
        foreach (var item in new[] { ("刷新", (Action)RefreshRecords), ("详情", ShowDetails), ("打开备份目录", OpenDirectory),
                     ("撤回", UndoSelected), ("前进", RedoSelected), ("切换永久保留", ToggleKeep) })
        {
            var button = new Button { Text = item.Item1, AutoSize = true, MinimumSize = new Size(64, 30) };
            button.Click += (_, _) => item.Item2();
            toolbar.Controls.Add(button);
        }
        Controls.Add(_grid);
        Controls.Add(_details);
        Controls.Add(toolbar);
    }

    public void LoadProject(ProjectProfile project)
    {
        _project = project;
        RefreshRecords();
    }

    public void UndoLatest()
    {
        if (_project == null) return;
        var record = _records.LastOrDefault(item => item.Status == HistoryStatus.Active && item.Kind is not (MutationKind.Undo or MutationKind.Redo));
        if (record == null) { MessageBox.Show(this, "没有可撤回的活动记录。", "撤回"); return; }
        RunTransition(() => _history.Undo(_project, record.Id));
    }

    public void RedoLatest()
    {
        if (_project == null) return;
        var record = _records.LastOrDefault(item => item.Status == HistoryStatus.Undone);
        if (record == null) { MessageBox.Show(this, "没有可前进的记录。", "前进"); return; }
        RunTransition(() => _history.Redo(_project, record.Id));
    }

    private void RefreshRecords()
    {
        if (_project == null) return;
        _records = _history.List(_project).ToList();
        _grid.DataSource = _records.Select(record => new HistoryRow(record)).ToList();
        ShowDetails();
    }

    private HistoryRecord? Selected()
        => (_grid.CurrentRow?.DataBoundItem as HistoryRow)?.Record;

    private void ShowDetails()
    {
        var record = Selected();
        if (record == null) { _details.Clear(); return; }
        var lines = new List<string>
        {
            $"ID: {record.Id}", $"时间: {record.CreatedAt:yyyy-MM-dd HH:mm:ss}", $"操作: {record.Kind}",
            $"状态: {record.Status}", $"摘要: {record.Summary}", $"父记录: {record.ParentId ?? "无"}"
        };
        foreach (var target in record.Targets)
        {
            lines.Add("");
            lines.Add("目标: " + target.TargetPath);
            lines.Add("写前 SHA: " + target.BeforeSha256);
            lines.Add("写后 SHA: " + target.AfterSha256);
            lines.Add("写前快照: " + target.SnapshotPath);
            lines.Add("写后快照: " + target.AfterSnapshotPath);
        }
        foreach (var pair in record.Metadata) lines.Add($"{pair.Key}: {pair.Value}");
        _details.Lines = lines.ToArray();
    }

    private void OpenDirectory()
    {
        if (_project == null) return;
        var path = _history.BackupRoot(_project);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void UndoSelected()
    {
        if (_project == null || Selected() is not { } record) return;
        if (MessageBox.Show(this, "撤回前会校验当前文件 SHA-256。继续？", "确认撤回", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
        RunTransition(() => _history.Undo(_project, record.Id));
    }

    private void RedoSelected()
    {
        if (_project == null || Selected() is not { } record) return;
        RunTransition(() => _history.Redo(_project, record.Id));
    }

    private void ToggleKeep()
    {
        if (_project == null || Selected() is not { } record) return;
        _history.SetKeepForever(_project, record.Id, !record.KeepForever);
        RefreshRecords();
    }

    private void RunTransition(Func<MutationResult> action)
    {
        try
        {
            var result = action();
            MessageBox.Show(this, result.Success ? result.Summary : string.Join(Environment.NewLine, result.Diagnostics),
                result.Success ? "完成" : "冲突", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshRecords();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "历史操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private sealed class HistoryRow
    {
        public HistoryRow(HistoryRecord record) => Record = record;
        public HistoryRecord Record { get; }
        public DateTime CreatedAt => Record.CreatedAt;
        public MutationKind Kind => Record.Kind;
        public string Summary => Record.Summary;
        public int TargetCount => Record.Targets.Count;
        public HistoryStatus Status => Record.Status;
        public bool KeepForever => Record.KeepForever;
    }
}
