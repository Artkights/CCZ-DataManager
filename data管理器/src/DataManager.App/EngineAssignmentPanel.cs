using DataManager.Core.Models;
using ImageManager.App;

namespace DataManager.App;

/// <summary>A draft-only assignment view. File writes are owned by MainForm's shared preview.</summary>
public sealed class EngineAssignmentPanel : UserControl
{
    private readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Top, Height = 30, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _shared = new() { Dock = DockStyle.Top, Height = 36, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox _filter = new() { Width = 220, PlaceholderText = "筛选特效号、名称或说明" };
    private readonly CheckBox _onlyAvailable = new() { Text = "仅可分配", AutoSize = true };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(10, 5, 0, 0) };
    private readonly DataGridView _assigned = CreateGrid();
    private readonly DataGridView _pool = CreateGrid();
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 250, 252) };
    private readonly Button _remove = new() { Text = "移回特效池", AutoSize = true };
    private readonly Button _value = new() { Text = "修改数值", AutoSize = true };
    private readonly Button _write = new() { Text = "预览并写入全部草稿", AutoSize = true };
    private readonly Button _discard = new() { Text = "撤销分配草稿", AutoSize = true };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30000 };
    private IReadOnlyList<EngineAssignmentDisplayRow> _poolRows = [];
    private bool _binding;
    private string? _preparedPoolKey;
    private string? _contextTitle;
    private int _selectionRevision;
    public Func<EngineAssignmentDisplayRow, bool>? PreparePoolSelection { get; set; }
    public bool IsJob { get; }
    public event EventHandler<EngineAssignmentRowEventArgs>? BindRequested;
    public event EventHandler<EngineAssignmentRowEventArgs>? UnbindRequested;
    public event EventHandler<EngineAssignmentRowEventArgs>? ValueRequested;
    public event EventHandler? CommitRequested;
    public event EventHandler? DiscardRequested;

    public EngineAssignmentPanel(bool isJob)
    {
        IsJob = isJob;
        Dock = DockStyle.Fill;
        Padding = new Padding(2, 4, 2, 0);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, AutoScroll = true };
        commands.Controls.AddRange([_remove, _value, _write, _discard]);
        var poolHeader = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, AutoScroll = true };
        poolHeader.Controls.AddRange([new Label { Text = "特效池", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _filter, _onlyAvailable, _summary]);
        var allocatedBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2) };
        allocatedBox.Controls.Add(_assigned);
        allocatedBox.Controls.Add(new Label { Text = "已分配特效（选中查看来源、继承和影响）", Dock = DockStyle.Top, Height = 24 });
        var poolBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 2) };
        poolBox.Controls.Add(_pool);
        poolBox.Controls.Add(poolHeader);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        body.Controls.Add(allocatedBox, 0, 0);
        body.Controls.Add(poolBox, 0, 1);
        body.Controls.Add(_details, 0, 2);
        Controls.Add(body);
        Controls.Add(commands);
        Controls.Add(_shared);
        Controls.Add(_title);
        _filter.TextChanged += (_, _) => PopulatePool();
        _onlyAvailable.CheckedChanged += (_, _) => PopulatePool();
        _assigned.SelectionChanged += (_, _) => SelectionChanged(_assigned);
        _pool.SelectionChanged += (_, _) => SelectionChanged(_pool);
        _pool.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _pool.Rows[e.RowIndex].Tag is EngineAssignmentDisplayRow { Selectable: true } row) RequestPoolBind(row); };
        _pool.CellClick += (_, e) => { if (e.RowIndex >= 0 && _pool.Rows[e.RowIndex].Tag is EngineAssignmentDisplayRow row) _details.Text = row.Details; };
        // Upper rows represent actual bindings. Double-click stages an unload;
        // editing a value remains an explicit button.
        _assigned.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && Selected(_assigned) is { Selectable: true } row) UnbindRequested?.Invoke(this, new(row)); };
        _remove.Click += (_, _) => { if (Selected(_assigned) is { Selectable: true } row) UnbindRequested?.Invoke(this, new(row)); };
        _value.Click += (_, _) => { if (Selected(_assigned) is { Selectable: true, CanEditValue: true } row) ValueRequested?.Invoke(this, new(row)); };
        _write.Click += (_, _) => CommitRequested?.Invoke(this, EventArgs.Empty);
        _discard.Click += (_, _) => DiscardRequested?.Invoke(this, EventArgs.Empty);
        _tips.SetToolTip(_pool, "选中时查看占用并准备分配，双击才加入草稿；灰色条目仅供查看来源，不能分配。" + (isJob ? "名称末尾 *2/*1/*0 表示空余兵种槽；只有 *0 显示红色。满槽选中时先选替换槽。" : string.Empty));
        _tips.SetToolTip(_value, isJob ? "修改选中兵种槽的特效值。" : "宝物数值由物品共用参数决定，详见下方来源说明。");
        _value.Visible = true;
    }

    public void BindEntity(string title, string shared, IReadOnlyList<EngineAssignmentDisplayRow> assigned,
        IReadOnlyList<EngineAssignmentDisplayRow> pool, bool hasPending)
    {
        var assignedKey = Selected(_assigned)?.SelectionKey;
        _binding = true;
        try
        {
            if (_contextTitle != title) { _preparedPoolKey = null; _contextTitle = title; }
            _title.Text = title + (hasPending ? "  * 草稿" : string.Empty);
            _shared.Text = shared;
            _tips.SetToolTip(_shared, shared);
            FillRows(_assigned, assigned, assignedKey);
            _poolRows = pool;
            PopulatePool();
            _write.Enabled = hasPending;
            _discard.Enabled = hasPending;
        }
        finally { _binding = false; }
        SelectionChanged(_assigned);
        if (Selected(_assigned) == null && Selected(_pool) == null)
            _details.Text = "选中特效查看来源、条件和影响范围。特效池中选中条目只准备分配；双击才加入草稿。";
    }

    public void ShowEmpty(string message)
    {
        BindEntity(IsJob ? "兵种特效" : "宝物特效", message, [], [], false);
        _details.Text = message;
    }

    private void PopulatePool()
    {
        var selected = Selected(_pool)?.SelectionKey;
        var text = _filter.Text.Trim();
        var rows = _poolRows.Where(row => (!_onlyAvailable.Checked || row.Selectable) &&
            (text.Length == 0 || $"{row.Number} {row.Name} {row.Description} {row.Details}".Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        var binding = _binding;
        _binding = true;
        try { FillRows(_pool, rows, selected, selectFirst: false); }
        finally { _binding = binding; }
        _summary.Text = $"{rows.Length} 项；双击分配";
    }

    private void SelectionChanged(DataGridView grid)
    {
        if (_binding) return;
        if (Selected(grid) is { } row)
        {
            _details.Text = row.Details;
            if (!row.Selectable)
            {
                _details.Text = string.IsNullOrWhiteSpace(row.ReadOnlyReason) ? row.Details : row.ReadOnlyReason + Environment.NewLine + Environment.NewLine + row.Details;
                ClearSelection(grid);
                _preparedPoolKey = null;
            }
            else if (ReferenceEquals(grid, _pool) && _preparedPoolKey != row.SelectionKey)
            {
                _preparedPoolKey = null;
                var revision = ++_selectionRevision;
                // The mouse and keyboard both pass here. Defer modal prompts until
                // DataGridView finishes its current-cell transition.
                BeginInvoke(() =>
                {
                    if (revision != _selectionRevision || Selected(_pool)?.SelectionKey != row.SelectionKey) return;
                    PrepareSelectedPool(row);
                });
            }
        }
        var assigned = Selected(_assigned);
        _remove.Enabled = assigned is { Selectable: true };
        _value.Enabled = assigned is { Selectable: true, CanEditValue: true };
    }

    private bool PrepareSelectedPool(EngineAssignmentDisplayRow row)
    {
        if (_preparedPoolKey == row.SelectionKey) return true;
        if (PreparePoolSelection?.Invoke(row) != false) { _preparedPoolKey = row.SelectionKey; return true; }
        ClearSelection(_pool);
        _preparedPoolKey = null;
        _details.Text = "已取消分配选择；没有新增草稿。" + Environment.NewLine + Environment.NewLine + row.Details;
        return false;
    }

    private void RequestPoolBind(EngineAssignmentDisplayRow row)
    {
        if (Selected(_pool)?.SelectionKey != row.SelectionKey || !PrepareSelectedPool(row)) return;
        BindRequested?.Invoke(this, new(row));
    }

    private void ClearSelection(DataGridView grid)
    {
        var binding = _binding;
        _binding = true;
        try { grid.ClearSelection(); grid.CurrentCell = null; }
        finally { _binding = binding; }
        _selectionRevision++;
    }

    private static EngineAssignmentDisplayRow? Selected(DataGridView grid)
        => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as EngineAssignmentDisplayRow : null;

    private static void FillRows(DataGridView grid, IEnumerable<EngineAssignmentDisplayRow> rows, string? selected, bool selectFirst = true)
    {
        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var item in rows)
            {
                var index = grid.Rows.Add(item.Number, item.Name, item.Value, item.Description);
                var row = grid.Rows[index];
                row.Tag = item;
                row.Cells[1].Style.ForeColor = !item.Selectable ? Color.Gray : item.Full ? Color.Firebrick : grid.DefaultCellStyle.ForeColor;
                if (!item.Selectable) row.DefaultCellStyle.ForeColor = Color.Gray;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = item.Details;
            }
            grid.ClearSelection();
            var target = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (row.Tag as EngineAssignmentDisplayRow)?.SelectionKey == selected)
                ?? (selectFirst ? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (row.Tag as EngineAssignmentDisplayRow)?.Selectable == true) : null);
            if (target != null) { target.Selected = true; grid.CurrentCell = target.Cells[0]; }
            else grid.CurrentCell = null;
        }
        finally { grid.ResumeLayout(); }
    }

    private static DataGridView CreateGrid()
    {
        var grid = new AssignmentGrid { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        foreach (var column in new[] { ("特效号", 12f, 58), ("特效名", 22f, 130), ("特效值", 20f, 165), ("特效说明", 46f, 160) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Name = column.Item1, FillWeight = column.Item2,
                MinimumWidth = column.Item3, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        SelectionAppearance.Configure(grid);
        return grid;
    }

    internal DataGridView AssignedGridForSmokeTest => _assigned;
    internal DataGridView PoolGridForSmokeTest => _pool;
    internal TextBox FilterForSmokeTest => _filter;
    internal TextBox DetailsForSmokeTest => _details;
    internal string SharedForSmokeTest => _shared.Text;
    internal Button ValueButtonForSmokeTest => _value;
    internal string? PreparedPoolKeyForSmokeTest => _preparedPoolKey;
    internal void BindSelectedForSmokeTest() { if (Selected(_pool) is { } row) RequestPoolBind(row); }
    internal void UnbindSelectedForSmokeTest() { if (Selected(_assigned) is { } row) UnbindRequested?.Invoke(this, new(row)); }
    private sealed class AssignmentGrid : DataGridView { public AssignmentGrid() => DoubleBuffered = true; }
}

public sealed record EngineAssignmentDisplayRow(string SelectionKey, string Key, string Number, string Name, string Value,
    string Description, string Details, bool Selectable, bool Full = false, int? Slot = null, int? NumericValue = null,
    bool CanEditValue = true, string ReadOnlyReason = "");
public sealed class EngineAssignmentRowEventArgs(EngineAssignmentDisplayRow row) : EventArgs { public EngineAssignmentDisplayRow Row { get; } = row; }

internal sealed class EngineAssignmentValueDialog : Form
{
    private readonly TextBox _value = new() { Width = 140, PlaceholderText = "请输入 0..255" };
    private readonly ComboBox _slot = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    public int Value => int.Parse(_value.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    public int? ReplacementSlot => _slot.SelectedItem is SlotChoice choice ? choice.Slot : null;
    public EngineAssignmentValueDialog(string name, int? value, IReadOnlyList<EngineEffectJobSlot>? slots = null)
    {
        Text = "分配特效：" + name; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(520, slots?.Count > 0 ? 185 : 135);
        _value.Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, slots?.Count > 0 ? 48 : 0));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(new Label { Text = "特效值 N", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); body.Controls.Add(_value, 1, 0);
        if (slots?.Count > 0)
        {
            body.Controls.Add(new Label { Text = "替换兵种槽", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); body.Controls.Add(_slot, 1, 1);
            foreach (var slot in slots) _slot.Items.Add(new SlotChoice(slot.Slot, $"槽 {slot.Slot + 1}：兵种 {slot.JobId}；N={slot.Value}；{slot.MatchScope}"));
            _slot.SelectedIndex = -1;
        }
        var ok = new Button { Text = "加入草稿", AutoSize = true, Enabled = false };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); body.Controls.Add(buttons, 0, 2); body.SetColumnSpan(buttons, 2);
        void ValidateValue() => ok.Enabled = int.TryParse(_value.Text.Trim(), out var parsed) && parsed is >= 0 and <= 255 &&
            (slots?.Count is not > 0 || _slot.SelectedIndex >= 0);
        _slot.SelectedIndexChanged += (_, _) => ValidateValue();
        _value.TextChanged += (_, _) => ValidateValue();
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        AcceptButton = ok; CancelButton = cancel; Controls.Add(body);
        ValidateValue();
    }
    internal TextBox ValueForSmokeTest => _value;
    internal bool CanAcceptForSmokeTest => AcceptButton is Button button && button.Enabled;
    private sealed record SlotChoice(int Slot, string Text) { public override string ToString() => Text; }
}

/// <summary>Prepares a replacement choice without touching the assignment draft.</summary>
internal sealed class EngineAssignmentSlotDialog : Form
{
    private readonly ComboBox _slots = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _scope = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    public int ReplacementSlot => ((SlotChoice)_slots.SelectedItem!).Slot.Slot;
    public EngineAssignmentSlotDialog(string name, IReadOnlyList<EngineEffectJobSlot> slots,
        Func<int, string> jobName)
    {
        Text = "选择替换槽：" + name;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        MinimizeBox = false; MaximizeBox = false; ClientSize = new Size(650, 300);
        var header = new Label { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 6, 10, 4),
            Text = $"{slots.Count}个兵种槽均已占用。请选择要替换的槽，或取消。\r\n此步骤只记录选择；回到特效池双击后才加入草稿。" };
        foreach (var slot in slots) _slots.Items.Add(new SlotChoice(slot, $"槽 {slot.Slot + 1}：{jobName(slot.JobId)}；N={slot.Value}"));
        var ok = new Button { Text = "继续", AutoSize = true, Enabled = false };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        _slots.SelectedIndexChanged += (_, _) =>
        {
            ok.Enabled = _slots.SelectedIndex >= 0;
            if (_slots.SelectedItem is not SlotChoice choice) return;
            _scope.Text = $"替换来源：{jobName(choice.Slot.JobId)}，槽 {choice.Slot.Slot + 1}，N={choice.Slot.Value}\r\n" +
                $"匹配范围：{choice.Slot.MatchScope}\r\n当前受影响兵种：{string.Join("、", choice.Slot.AffectedJobIds.Select(jobName))}\r\n" +
                $"同族模式下可能影响：{string.Join("、", choice.Slot.PotentialAffectedJobIds.Select(jobName))}";
        };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, ok]);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        body.Controls.Add(_scope); body.Controls.Add(_slots);
        Controls.Add(body); Controls.Add(buttons); Controls.Add(header);
        AcceptButton = ok; CancelButton = cancel;
    }
    private sealed record SlotChoice(EngineEffectJobSlot Slot, string Text) { public override string ToString() => Text; }
}
