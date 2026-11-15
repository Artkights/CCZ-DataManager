using DataManager.Core.Domain;
using TransformManager.App;

namespace DataManager.App;

public sealed class EngineTransformPanel : UserControl
{
    private readonly DataGridView _rules = GrowthEditorLayout.Grid("TransformRules");
    private readonly DataGridView _preview = GrowthEditorLayout.Grid("TransformPreview");
    private readonly ListBox _references = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _add = new() { Text = "新增", AutoSize = true };
    private readonly Button _delete = new() { Text = "删除", AutoSize = true };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private static readonly object NativeRuleTag = new();
    private EngineGrowthDraftService? _draft;
    private int? _effect;
    private bool _binding, _submitting, _dirty;
    public bool HasPendingInput => _dirty || _rules.IsCurrentCellInEditMode;
    public event EventHandler? PendingChangesChanged;

    public EngineTransformPanel()
    {
        Dock = DockStyle.Fill;
        _rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "等级", FillWeight = 40 });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "特效值", FillWeight = 60 });
        _preview.ReadOnly = true;
        _preview.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "等级", FillWeight = 40 });
        _preview.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "特效值", FillWeight = 60 });
        var body = GrowthEditorLayout.WeightedRows(4, 4, 2);
        body.Controls.Add(GrowthEditorLayout.Section("等级规则（灰色=原版默认）", _rules, _add, _delete), 0, 0);
        body.Controls.Add(GrowthEditorLayout.Section("效果预览", _preview), 0, 1);
        body.Controls.Add(GrowthEditorLayout.Section("引用装备", _references), 0, 2);
        Controls.Add(body);
        _rules.CurrentCellDirtyStateChanged += (_, _) => { if (!_binding) { _dirty = true; PendingChangesChanged?.Invoke(this, EventArgs.Empty); } };
        _rules.CellEndEdit += (_, _) => { if (!_binding) Flush(); };
        _rules.DataError += (_, e) => { e.ThrowException = false; _errors.SetError(_rules, "请输入有效整数。"); };
        _rules.SelectionChanged += (_, _) => UpdateButtons();
        _add.Click += (_, _) => AddRule();
        _delete.Click += (_, _) => DeleteRule();
    }

    public void Bind(EngineGrowthDraftService draft, int? effect)
    {
        if (!ReferenceEquals(_draft, draft))
        {
            if (_draft != null) _draft.Changed -= DraftChanged;
            _draft = draft; _draft.Changed += DraftChanged;
        }
        _effect = effect;
        Render();
    }

    private void DraftChanged(object? sender, EventArgs e) { if (!_submitting && !_dirty && !IsDisposed) Render(); }
    private void Render()
    {
        var slot = _draft?.Slots.FirstOrDefault(slot => slot.Id == _effect);
        var selected = _rules.CurrentCell?.RowIndex ?? -1;
        _binding = true;
        try
        {
            _rules.Rows.Clear(); _references.Items.Clear(); _preview.Rows.Clear();
            if (slot != null)
            {
                foreach (var rule in slot.Stages.OrderByDescending(stage => stage.Level)) _rules.Rows.Add(rule.Level, rule.Value);
                if (_draft != null)
                    foreach (var rule in _draft.NativeDefaultStages(slot.Id).OrderByDescending(stage => stage.Level))
                        AddNativeRow(rule);
                foreach (var item in slot.Refs) _references.Items.Add($"#{item.Id}  {item.Name} · {item.CategoryName}");
                if (slot.Refs.Count == 0) _references.Items.Add("（没有装备引用）");
                if (_draft != null)
                    foreach (var row in _draft.Preview(slot.Id)) _preview.Rows.Add(row.Level, row.Value);
            }
            if (selected >= 0 && _rules.Rows.Count > 0) _rules.CurrentCell = _rules.Rows[Math.Min(selected, _rules.Rows.Count - 1)].Cells[0];
            _dirty = false;
        }
        finally { _binding = false; }
        UpdateButtons();
    }

    private void AddNativeRow(RuleStage rule)
    {
        var row = _rules.Rows[_rules.Rows.Add(rule.Level, rule.Value)];
        row.Tag = NativeRuleTag; row.ReadOnly = true;
        row.DefaultCellStyle.ForeColor = Color.Gray;
        row.DefaultCellStyle.SelectionForeColor = Color.Gray;
    }

    private static bool IsNativeRow(DataGridViewRow row) => ReferenceEquals(row.Tag, NativeRuleTag);
    private int CustomRuleCount() => _rules.Rows.Cast<DataGridViewRow>().Count(row => !IsNativeRow(row));
    private bool CurrentRowIsCustom() => _rules.CurrentRow is { } row && !IsNativeRow(row);

    private void UpdateButtons()
    {
        if (_binding) return;
        var editable = _draft is { CanEditRules: true } draft && draft.Slots.Any(slot => slot.Id == _effect);
        _rules.ReadOnly = !editable;
        _add.Enabled = editable && CustomRuleCount() < 8;
        _delete.Enabled = editable && CustomRuleCount() > 0 && CurrentRowIsCustom();
        _errors.SetError(_rules, editable ? string.Empty : _draft?.RulesReadOnlyReason ?? string.Empty);
    }

    private void UpdatePreview()
    {
        _preview.Rows.Clear();
        if (_draft == null || _effect == null) return;
        foreach (var row in _draft.Preview(_effect.Value)) _preview.Rows.Add(row.Level, row.Value);
    }

    private void ScheduleNativeRowRefresh()
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && !Disposing && !_dirty) RefreshNativeRows();
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void RefreshNativeRows()
    {
        if (_draft == null || _effect == null) return;
        var selected = _rules.CurrentCell?.RowIndex ?? -1;
        _binding = true;
        try
        {
            for (var index = _rules.Rows.Count - 1; index >= 0; index--)
                if (IsNativeRow(_rules.Rows[index])) _rules.Rows.RemoveAt(index);
            foreach (var rule in _draft.NativeDefaultStages(_effect.Value).OrderByDescending(stage => stage.Level))
                AddNativeRow(rule);
            if (selected >= 0 && selected < _rules.Rows.Count) _rules.CurrentCell = _rules.Rows[selected].Cells[0];
        }
        finally { _binding = false; }
        UpdateButtons();
    }

    public bool Flush()
    {
        if (_binding || _submitting || _draft == null || _effect == null) return true;
        if (!_rules.EndEdit()) return false;
        if (!_dirty) return true;
        try
        {
            var stages = new List<RuleStage>();
            foreach (DataGridViewRow row in _rules.Rows)
            {
                if (IsNativeRow(row)) continue;
                if (!int.TryParse(Convert.ToString(row.Cells[0].Value), out var level) || level is < 1 or > 99)
                    throw new InvalidDataException("等级必须为 1～99。");
                if (!int.TryParse(Convert.ToString(row.Cells[1].Value), out var value) || value is < 0 or > 255)
                    throw new InvalidDataException("特效值必须为 0～255。");
                if (stages.Any(stage => stage.Level == level)) throw new InvalidDataException("规则等级不能重复。");
                stages.Add(new(level, value));
            }
            _submitting = true;
            _draft.SetRules(_effect.Value, stages);
            _dirty = false; _errors.SetError(_rules, string.Empty);
            UpdateButtons();
            UpdatePreview();
            PendingChangesChanged?.Invoke(this, EventArgs.Empty);
            ScheduleNativeRowRefresh();
            return true;
        }
        catch (Exception ex) { _errors.SetError(_rules, ex.Message); return false; }
        finally { _submitting = false; }
    }

    private void AddRule()
    {
        if (!Flush() || CustomRuleCount() >= 8) return;
        var used = _rules.Rows.Cast<DataGridViewRow>().Where(row => !IsNativeRow(row))
            .Select(row => Convert.ToInt32(row.Cells[0].Value)).ToHashSet();
        var level = Enumerable.Range(1, 99).First(value => !used.Contains(value));
        var rowIndex = _rules.Rows.Add(level, 0); _dirty = true;
        Flush(); _rules.CurrentCell = _rules.Rows[Math.Min(rowIndex, _rules.Rows.Count - 1)].Cells[0];
    }

    private void DeleteRule()
    {
        if (!Flush() || _rules.CurrentRow is not { } row || IsNativeRow(row)) return;
        _rules.Rows.Remove(row); _dirty = true; Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (_draft != null) _draft.Changed -= DraftChanged; _errors.Dispose(); }
        base.Dispose(disposing);
    }
    internal DataGridView RulesForSmokeTest => _rules;
    internal DataGridView PreviewForSmokeTest => _preview;
    internal int NativeRuleCountForSmokeTest => _rules.Rows.Cast<DataGridViewRow>().Count(IsNativeRow);
    internal ListBox ReferencesForSmokeTest => _references;
    internal Button AddForSmokeTest => _add;
    internal Button DeleteForSmokeTest => _delete;
}

internal static class GrowthEditorLayout
{
    internal static DataGridView Grid(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
    };
    internal static TableLayoutPanel TwoEqualRows()
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        return body;
    }
    internal static TableLayoutPanel WeightedRows(params int[] weights)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = weights.Length, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var weight in weights) body.RowStyles.Add(new RowStyle(SizeType.Percent, weight));
        return body;
    }
    internal static Control Section(string title, Control content, params Control[] commands)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Margin = new Padding(3), Padding = new Padding(6) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = commands.Length > 0 ? 2 : 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(content, 0, 0);
        if (commands.Length > 0)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            buttons.Controls.AddRange(commands); table.Controls.Add(buttons, 0, 1);
        }
        group.Controls.Add(table); return group;
    }
}
