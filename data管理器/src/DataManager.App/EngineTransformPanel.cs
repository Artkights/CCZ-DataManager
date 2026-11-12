using DataManager.Core.Domain;
using TransformManager.App;

namespace DataManager.App;

public sealed class EngineTransformPanel : UserControl
{
    private readonly DataGridView _rules = GrowthEditorLayout.Grid("TransformRules");
    private readonly ListBox _references = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button _add = new() { Text = "新增", AutoSize = true };
    private readonly Button _delete = new() { Text = "删除", AutoSize = true };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
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
        var body = GrowthEditorLayout.TwoEqualRows();
        body.Controls.Add(GrowthEditorLayout.Section("等级规则", _rules, _add, _delete), 0, 0);
        body.Controls.Add(GrowthEditorLayout.Section("引用装备", _references), 0, 1);
        Controls.Add(body);
        _rules.CurrentCellDirtyStateChanged += (_, _) => { if (!_binding) { _dirty = true; PendingChangesChanged?.Invoke(this, EventArgs.Empty); } };
        _rules.CellEndEdit += (_, _) => { if (!_binding) Flush(); };
        _rules.DataError += (_, e) => { e.ThrowException = false; _errors.SetError(_rules, "请输入有效整数。"); };
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
            _rules.Rows.Clear(); _references.Items.Clear();
            if (slot != null)
            {
                foreach (var rule in slot.Stages.OrderByDescending(stage => stage.Level)) _rules.Rows.Add(rule.Level, rule.Value);
                foreach (var item in slot.Refs) _references.Items.Add($"#{item.Id}  {item.Name} · {item.CategoryName}");
                if (slot.Refs.Count == 0) _references.Items.Add("（没有装备引用）");
            }
            var editable = slot != null && _draft?.CanEditRules == true;
            _rules.ReadOnly = !editable; _add.Enabled = editable && _rules.Rows.Count < 8;
            _delete.Enabled = editable && _rules.Rows.Count > 0;
            _errors.SetError(_rules, editable ? string.Empty : _draft?.RulesReadOnlyReason ?? string.Empty);
            if (selected >= 0 && _rules.Rows.Count > 0) _rules.CurrentCell = _rules.Rows[Math.Min(selected, _rules.Rows.Count - 1)].Cells[0];
            _dirty = false;
        }
        finally { _binding = false; }
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
            _add.Enabled = _rules.Rows.Count < 8; _delete.Enabled = _rules.Rows.Count > 0;
            PendingChangesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) { _errors.SetError(_rules, ex.Message); return false; }
        finally { _submitting = false; }
    }

    private void AddRule()
    {
        if (!Flush() || _rules.Rows.Count >= 8) return;
        var used = _rules.Rows.Cast<DataGridViewRow>().Select(row => Convert.ToInt32(row.Cells[0].Value)).ToHashSet();
        var level = Enumerable.Range(1, 99).First(value => !used.Contains(value));
        var rowIndex = _rules.Rows.Add(level, 0); _dirty = true;
        Flush(); _rules.CurrentCell = _rules.Rows[rowIndex].Cells[0];
    }

    private void DeleteRule()
    {
        if (!Flush() || _rules.CurrentRow is not { } row) return;
        _rules.Rows.Remove(row); _dirty = true; Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (_draft != null) _draft.Changed -= DraftChanged; _errors.Dispose(); }
        base.Dispose(disposing);
    }
    internal DataGridView RulesForSmokeTest => _rules;
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
