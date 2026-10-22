using System.Globalization;
using DataManager.Core.Migration;
using ImageManager.App;

namespace DataManager.App;

/// <summary>Indexes the plan without allocating a grid row and nine cells for every field.</summary>
internal sealed class MigrationReviewDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(14, 6, 0, 0) };
    private readonly MigrationPlan _plan;
    private readonly Dictionary<int, MigrationChoice> _choices = new();
    private readonly Dictionary<string, int[]> _indicesByTable = new(StringComparer.OrdinalIgnoreCase);
    private readonly int[] _allIndices;
    private int[] _visibleIndices = [];

    // Skip is implicit, so an untouched plan does not need a second entry per field.
    public IReadOnlyDictionary<int, MigrationChoice> Choices => _choices;
    internal DataGridView GridForSmokeTest => _grid;
    internal ComboBox FilterForSmokeTest => _filter;
    internal void SetAllForSmokeTest(MigrationChoice choice) => SetAll(choice);

    public MigrationReviewDialog(MigrationPlan plan)
    {
        _plan = plan;
        _allIndices = Enumerable.Range(0, plan.Changes.Count).ToArray();
        foreach (var group in _allIndices.GroupBy(index => plan.Changes[index].TableKey, StringComparer.OrdinalIgnoreCase))
            _indicesByTable[group.Key] = group.ToArray();
        Text = "迁移差异逐项选择";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1180;
        Height = 680;
        MinimizeBox = false;
        MaximizeBox = true;
        BuildLayout();
        LoadRows();
    }

    private static bool CanWrite(MigrationChange change)
        => change.Kind is MigrationChangeKind.Added or MigrationChangeKind.Changed
            or MigrationChangeKind.ResourceAdded or MigrationChangeKind.ResourceChanged
            or MigrationChangeKind.PhysicalFileAdded or MigrationChangeKind.NativeStructureChanged;

    private void BuildLayout()
    {
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(8),
            Text = "默认全部跳过；不可映射和整段忽略项显示原因且不可选择。合并保留已有目标标量；新建拓展表须同时选择“新建物理文件”和相应逻辑字段。"
        };
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 2, 8, 2), WrapContents = false };
        filters.Controls.Add(new Label { Text = "查看表/资源", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        filters.Controls.Add(_filter);
        filters.Controls.Add(_summary);
        _filter.Items.Add(new TableFilter(null, "全部差异"));
        foreach (var entry in _indicesByTable.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = entry.Key switch
            {
                "__resource__" => "资源文件",
                "__physical_file__" => "新建拓展文件",
                _ => _plan.Changes[entry.Value[0]].TableDisplayName
            };
            _filter.Items.Add(new TableFilter(entry.Key, $"{label}（{entry.Value.Length:N0} 项）"));
        }
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, _) => LoadRows();
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.VirtualMode = true;
        SelectionAppearance.Configure(_grid);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", HeaderText = "序号", Width = 65, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Choice", HeaderText = "动作", Width = 90,
            DataSource = new[] { new ActionOption(MigrationChoice.Skip, "跳过"), new ActionOption(MigrationChoice.Overwrite, "覆盖"), new ActionOption(MigrationChoice.Merge, "合并") },
            DisplayMember = nameof(ActionOption.Text), ValueMember = nameof(ActionOption.Value),
            ValueType = typeof(MigrationChoice), DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
        });
        foreach (var (name, text, width) in new[]
        {
            ("Type", "类型", 100), ("Path", "表/资源", 260), ("Id", "ID", 65), ("Field", "字段", 150),
            ("Target", "目标值/哈希", 180), ("Source", "源值/哈希", 180), ("Reason", "映射/限制说明", 440)
        })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = text, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_visibleIndices.Length) return;
            var index = _visibleIndices[e.RowIndex];
            var change = _plan.Changes[index];
            e.Value = _grid.Columns[e.ColumnIndex].Name switch
            {
                "No" => (index + 1).ToString(CultureInfo.InvariantCulture),
                "Choice" => _choices.GetValueOrDefault(index, MigrationChoice.Skip),
                "Type" => ChangeType(change.Kind),
                "Path" => change.TableDisplayName,
                "Id" => change.TableKey is "__resource__" or "__physical_file__" || change.Id < 0 ? string.Empty : change.Id.ToString(CultureInfo.InvariantCulture),
                "Field" => change.FieldName,
                "Target" => change.DisplayValue(change.TargetValue),
                "Source" => change.DisplayValue(change.SourceValue),
                "Reason" => change.Reason,
                _ => null
            };
        };
        _grid.CellValuePushed += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_visibleIndices.Length || _grid.Columns[e.ColumnIndex].Name != "Choice" || e.Value is not MigrationChoice choice) return;
            var index = _visibleIndices[e.RowIndex];
            if (!CanWrite(_plan.Changes[index])) return;
            SetChoice(index, choice);
            UpdateSummary();
        };
        _grid.CellBeginEdit += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_visibleIndices.Length || !CanWrite(_plan.Changes[_visibleIndices[e.RowIndex]])) e.Cancel = true;
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.CellStyle != null && (uint)e.RowIndex < (uint)_visibleIndices.Length && !CanWrite(_plan.Changes[_visibleIndices[e.RowIndex]]))
                e.CellStyle.BackColor = SystemColors.Control;
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var selectAll = new Button { Text = "当前筛选覆盖", AutoSize = true };
        selectAll.Click += (_, _) => SetAll(MigrationChoice.Overwrite);
        var skipAll = new Button { Text = "当前筛选跳过", AutoSize = true };
        skipAll.Click += (_, _) => SetAll(MigrationChoice.Skip);
        var mergeAll = new Button { Text = "当前筛选合并", AutoSize = true };
        mergeAll.Click += (_, _) => SetAll(MigrationChoice.Merge);
        var ok = new Button { Text = "应用已选", AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => { if (!_grid.EndEdit()) DialogResult = DialogResult.None; };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); buttons.Controls.Add(mergeAll); buttons.Controls.Add(selectAll); buttons.Controls.Add(skipAll);
        Controls.Add(_grid); Controls.Add(buttons); Controls.Add(filters); Controls.Add(hint);
        AcceptButton = ok; CancelButton = cancel;
    }

    private void LoadRows()
    {
        if (!_grid.EndEdit()) return;
        _grid.CurrentCell = null;
        var filterKey = (_filter.SelectedItem as TableFilter)?.Key;
        // RowCount shrinking removes rows one by one; clearing the shared collection is linear.
        _grid.Rows.Clear();
        _visibleIndices = filterKey == null ? _allIndices : _indicesByTable[filterKey];
        _grid.RowCount = _visibleIndices.Length;
        _grid.Invalidate();
        UpdateSummary();
    }

    private void SetAll(MigrationChoice choice)
    {
        if (!_grid.EndEdit()) return;
        // Iterating Grid.Rows would unshare millions of virtual rows.
        foreach (var index in _visibleIndices)
            if (CanWrite(_plan.Changes[index])) SetChoice(index, choice);
        _grid.Invalidate();
        UpdateSummary();
    }

    private void SetChoice(int index, MigrationChoice choice)
    {
        if (choice == MigrationChoice.Skip) _choices.Remove(index);
        else _choices[index] = choice;
    }

    private void UpdateSummary() => _summary.Text = $"当前 {_visibleIndices.Length:N0} 项；已选 {_choices.Count:N0} 项";

    private static string ChangeType(MigrationChangeKind kind) => kind switch
    {
        MigrationChangeKind.PhysicalFileAdded => "新建物理文件",
        MigrationChangeKind.NativeStructureChanged => "专用结构",
        MigrationChangeKind.Added or MigrationChangeKind.Changed => "数据字段",
        MigrationChangeKind.ResourceAdded => "新增资源",
        MigrationChangeKind.ResourceChanged => "资源变化",
        MigrationChangeKind.MissingSourceTable or MigrationChangeKind.ResourceMissingSource => "源缺失",
        MigrationChangeKind.MissingTargetTable => "目标缺失",
        MigrationChangeKind.Incompatible => "不可映射",
        MigrationChangeKind.Ignored => "整段忽略",
        _ => kind.ToString()
    };

    private sealed record TableFilter(string? Key, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record ActionOption(MigrationChoice Value, string Text);
}
