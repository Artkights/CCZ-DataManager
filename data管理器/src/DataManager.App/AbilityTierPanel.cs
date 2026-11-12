using DataManager.Core.Domain;
using TierManager.App;

namespace DataManager.App;

public sealed class AbilityTierPanel : UserControl
{
    private readonly DataGridView _grid = GrowthEditorLayout.Grid("AbilityTierRules");
    private readonly Button _add = new() { Text = "新增", AutoSize = true };
    private readonly Button _delete = new() { Text = "删除", AutoSize = true };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private Font? _hintFont;
    private Font? _tableFont;
    private EngineGrowthDraftService? _draft;
    private bool _binding, _submitting, _dirty;
    public event EventHandler? PendingChangesChanged;
    public bool HasPendingInput => _dirty || _grid.IsCurrentCellInEditMode;

    public AbilityTierPanel()
    {
        Dock = DockStyle.Fill;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ordinal", HeaderText = "档位", ReadOnly = true, FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Threshold", HeaderText = "阈值", FillWeight = 36 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "显示", FillWeight = 36 });
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        var title = new Label { Text = "档位设定（全局）", AutoSize = true, Margin = Padding.Empty };
        var hint = new Label { Text = "显示最多2字节", AccessibleDescription = "按 GBK 编码计算，显示最多 2 字节", AutoSize = true, Margin = new Padding(4, 3, 0, 0) };
        void FitHint()
        {
            var tablePoints = Math.Min(9f, Font.SizeInPoints);
            if (_tableFont == null || Math.Abs(_tableFont.SizeInPoints - tablePoints) > .01f)
            {
                var previousTable = _tableFont; _tableFont = new Font(Font.FontFamily, tablePoints);
                _grid.Font = title.Font = _add.Font = _delete.Font = _tableFont;
                _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                _grid.ColumnHeadersHeight = _tableFont.Height + 8;
                _grid.RowTemplate.Height = _tableFont.Height + 8;
                foreach (DataGridViewRow row in _grid.Rows) row.Height = _grid.RowTemplate.Height;
                previousTable?.Dispose();
            }
            var points = Math.Clamp(Font.SizeInPoints * .75f, 7.5f, 8.25f);
            if (_hintFont == null || Math.Abs(_hintFont.SizeInPoints - points) > .01f)
            {
                var previous = _hintFont; _hintFont = new Font(Font.FontFamily, points);
                hint.Font = _hintFont; previous?.Dispose();
            }
            hint.MaximumSize = new Size(Math.Max(110, ClientSize.Width - _add.PreferredSize.Width - _delete.PreferredSize.Width - 44), 0);
        }
        FontChanged += (_, _) => FitHint(); SizeChanged += (_, _) => FitHint();
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(3), Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(title, 0, 0);
        body.Controls.Add(_grid, 0, 1);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        commands.Controls.AddRange([_add, _delete, hint]); body.Controls.Add(commands, 0, 2);
        Controls.Add(body);
        FitHint();
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (!_binding) { _dirty = true; PendingChangesChanged?.Invoke(this, EventArgs.Empty); } };
        _grid.CellEndEdit += (_, _) => { if (!_binding) Flush(); };
        _grid.DataError += (_, e) => { e.ThrowException = false; _errors.SetError(_grid, "请输入有效的档位设定。"); };
        _add.Click += (_, _) => AddTier(); _delete.Click += (_, _) => DeleteTier();
    }
    public void Bind(EngineGrowthDraftService draft)
    {
        if (!ReferenceEquals(_draft, draft))
        {
            if (_draft != null) _draft.Changed -= DraftChanged;
            _draft = draft; _draft.Changed += DraftChanged;
        }
        Render();
    }
    private void DraftChanged(object? sender, EventArgs e) { if (!_submitting && !_dirty && !IsDisposed) Render(); }
    private void Render()
    {
        var selected = _grid.CurrentCell?.RowIndex ?? -1;
        _binding = true;
        try
        {
            _grid.Rows.Clear();
            if (_draft != null)
                foreach (var entry in _draft.TierEntries)
                {
                    var row = _grid.Rows[_grid.Rows.Add(entry.Ordinal, entry.Threshold?.ToString() ?? "—", entry.Label)];
                    row.Tag = entry.OriginalReturn; row.Cells[1].ReadOnly = entry.Threshold == null;
                }
            _grid.ReadOnly = _draft?.CanEditTiers != true;
            _add.Enabled = !_grid.ReadOnly && _grid.Rows.Count < 12;
            _delete.Enabled = !_grid.ReadOnly && _grid.Rows.Count > 2;
            _errors.SetError(_grid, _grid.ReadOnly ? _draft?.TiersReadOnlyReason ?? string.Empty : string.Empty);
            if (selected >= 0 && _grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[Math.Min(selected, _grid.Rows.Count - 1)].Cells[2];
            _dirty = false;
        }
        finally { _binding = false; }
    }
    public bool Flush()
    {
        if (_binding || _submitting || _draft == null) return true;
        if (!_grid.EndEdit()) return false;
        if (!_dirty) return true;
        try
        {
            var entries = new List<TierEntry>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                byte? threshold = null;
                if (row.Index > 0)
                {
                    if (!byte.TryParse(Convert.ToString(row.Cells[1].Value), out var number)) throw new InvalidDataException("阈值必须为 0～255 的整数。");
                    threshold = number;
                }
                entries.Add(new TierEntry { Ordinal = Convert.ToInt32(row.Cells[0].Value), Threshold = threshold,
                    Label = Convert.ToString(row.Cells[2].Value) ?? string.Empty, OriginalReturn = row.Tag is byte original ? original : (byte)0 });
            }
            _submitting = true; _draft.SetTiers(entries); _dirty = false;
            _errors.SetError(_grid, string.Empty); PendingChangesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) { _errors.SetError(_grid, ex.Message); return false; }
        finally { _submitting = false; }
    }
    private static TierEntry Clone(TierEntry entry) => new() { Ordinal = entry.Ordinal, Threshold = entry.Threshold, Label = entry.Label, OriginalReturn = entry.OriginalReturn };
    private void AddTier()
    {
        if (!Flush() || _draft == null || _draft.TierEntries.Count >= 12) return;
        var entries = _draft.TierEntries.Select(Clone).ToList();
        var index = Math.Clamp(_grid.CurrentRow?.Index ?? entries.Count, 1, entries.Count);
        var lower = entries[index - 1].Threshold ?? 0;
        var upper = index < entries.Count ? entries[index].Threshold!.Value : 255;
        int threshold;
        if (index < entries.Count && upper - lower >= 2) threshold = lower + (upper - lower) / 2;
        else
        {
            index = entries.Count; var last = entries[^1].Threshold ?? 0;
            if (last >= 250) { _errors.SetError(_grid, "阈值之间和末尾没有可用空间，请先调整阈值。"); return; }
            threshold = last + 5;
        }
        entries.Insert(index, new TierEntry { Threshold = (byte)threshold, Label = "?" });
        Apply(entries, index);
    }
    private void DeleteTier()
    {
        if (!Flush() || _draft == null || _draft.TierEntries.Count <= 2 || _grid.CurrentRow is not { Index: > 0 } selected) return;
        var entries = _draft.TierEntries.Select(Clone).ToList(); entries.RemoveAt(selected.Index); Apply(entries, Math.Min(selected.Index, entries.Count - 1));
    }
    private void Apply(List<TierEntry> entries, int selected)
    {
        var first = entries[0].Ordinal;
        for (var index = 0; index < entries.Count; index++) entries[index].Ordinal = first + index;
        try { _draft!.SetTiers(entries); Render(); _grid.CurrentCell = _grid.Rows[selected].Cells[2]; PendingChangesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _errors.SetError(_grid, ex.Message); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (_draft != null) _draft.Changed -= DraftChanged; _errors.Dispose(); _hintFont?.Dispose(); _tableFont?.Dispose(); }
        base.Dispose(disposing);
    }
    internal DataGridView GridForSmokeTest => _grid;
    internal Button AddForSmokeTest => _add;
    internal Button DeleteForSmokeTest => _delete;
}
