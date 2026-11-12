using System.Globalization;
using DataManager.Core.Editing;
using DataManager.Core.Scanner;
using ImageManager.App;

namespace DataManager.App;

/// <summary>右侧战斗公式详情：计算过程、可改常量、证据。</summary>
public sealed class EngineBattleFormulaPanel : UserControl
{
    private readonly Label _title = new() { AutoEllipsis = true };
    private readonly Label _subtitle = new() { ForeColor = Color.DimGray, AutoEllipsis = true };
    private readonly DataGridView _stepGrid = new BufferedGrid();
    private readonly DataGridView _constantGrid = new BufferedGrid();
    private readonly TextBox _evidence = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(250, 250, 250), WordWrap = true
    };
    private readonly Button _write = new() { Text = "写入修改", Enabled = false, AutoSize = false, Width = 110, Height = 30 };
    private readonly Button _discard = new() { Text = "放弃修改", Enabled = false, AutoSize = false, Width = 96, Height = 30 };
    private readonly Button _refresh = new() { Text = "刷新", Enabled = false, AutoSize = false, Width = 72, Height = 30 };
    private readonly Label _pendingLabel = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 8, 0, 0) };
    private readonly EngineBattleFormulaService _service = new();
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);
    private ProjectWorkspace? _workspace;
    private string? _currentFormulaId;
    private BattleFormulaValue? _value;
    private bool _binding;
    private bool _committing;
    private const int ValueColumn = 5;

    public EngineBattleFormulaPanel()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        ConfigureStepGrid();
        ConfigureConstantGrid();
        Controls.Add(BuildLayout());
        _constantGrid.SelectionChanged += (_, _) => UpdateEvidence();
        _constantGrid.CellBeginEdit += OnCellBeginEdit;
        _constantGrid.CellValidating += OnCellValidating;
        _constantGrid.CellEndEdit += OnCellEndEdit;
        _write.Click += (_, _) => CommitFromButton();
        _discard.Click += (_, _) => DiscardPendingChanges();
        _refresh.Click += (_, _) => RefreshFromButton();
        UpdateButtons();
    }

    public event EventHandler? PendingChangesChanged;
    public event EventHandler<EngineConstantStatusEventArgs>? StatusChanged;

    internal Func<bool>? CommitAllRequested { get; set; }
    internal IReadOnlyList<EngineConstantEdit> PendingConstantEdits => PendingEdits().Select(edit => new EngineConstantEdit(edit.ConstantId, edit.Value, null)).ToArray();
    public bool HasPendingChanges => _pending.Count > 0;
    internal Action CaptureDraftRecovery()
    {
        var pending = _pending.ToArray();
        return () => { _pending.Clear(); foreach (var pair in pending) _pending.Add(pair.Key, pair.Value); ApplyPendingToGrid(); UpdateButtons(); };
    }
    public string? CurrentFormulaId => _currentFormulaId;

    public void Bind(ProjectWorkspace? workspace, string formulaId)
    {
        if (_currentFormulaId != formulaId) _pending.Clear();
        _workspace = workspace;
        _currentFormulaId = formulaId;
        _value = workspace == null
            ? _service.ReadDefinition(formulaId, null)
            : _service.ReadFormula(workspace, formulaId);
        if (_value == null)
        {
            Reset();
            RaiseStatus($"公式不存在：{formulaId}", true);
            return;
        }
        Render();
        UpdateButtons();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _workspace = null;
        _currentFormulaId = null;
        _value = null;
        _pending.Clear();
        _binding = true;
        try
        {
            _stepGrid.Rows.Clear();
            _constantGrid.Rows.Clear();
        }
        finally { _binding = false; }
        _title.Text = "尚未打开项目。";
        _subtitle.Text = string.Empty;
        _evidence.Text = string.Empty;
        ResetConstantHeaders();
        UpdateButtons();
    }

    public void RefreshValues()
    {
        if (_workspace == null || _currentFormulaId == null) { Reset(); return; }
        var selected = SelectedConstantId();
        var value = _service.ReadFormula(_workspace, _currentFormulaId);
        if (value == null) { Reset(); return; }
        _value = value;
        Render();
        SelectConstant(selected);
        UpdateButtons();
    }

    public bool PreparePendingEdits()
    {
        if (!_constantGrid.EndEdit()) return false;
        if (_workspace == null || _pending.Count == 0) return true;
        var error = _service.ValidateEdits(_workspace, PendingEdits())
            .FirstOrDefault(item => item.Severity == BattleFormulaIssueSeverity.Error);
        if (error != null)
        {
            RaiseStatus(error.Message, true);
            return false;
        }
        return true;
    }

    public bool TryCommit(out string message)
    {
        message = string.Empty;
        if (_workspace == null) { message = "尚未打开项目。"; return false; }
        if (!PreparePendingEdits()) { message = "当前输入尚未通过校验。"; return false; }
        if (_pending.Count == 0) { message = "没有需要写入的修改。"; return true; }

        if (CommitAllRequested != null) { var saved = CommitAllRequested(); message = saved ? "公式与工作区草稿已统一保存。" : "保存未完成，草稿已保留。"; return saved; }
        var edits = PendingEdits();
        var issues = _service.ValidateEdits(_workspace, edits);
        if (issues.Any(item => item.Severity == BattleFormulaIssueSeverity.Error))
        {
            message = string.Join(Environment.NewLine, issues
                .Where(item => item.Severity == BattleFormulaIssueSeverity.Error)
                .Select(item => item.Message));
            return false;
        }

        _committing = true;
        UpdateButtons();
        try
        {
            var result = _service.Commit(_workspace, edits);
            if (!result.Success) { message = result.Message; return false; }
            _pending.Clear();
            message = result.Message;
            RaiseStatus(message, false);
            RefreshValues();
            UpdateButtons();
            PendingChangesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            RaiseStatus("公式常量写入失败：" + ex.Message, true);
            return false;
        }
        finally
        {
            _committing = false;
            UpdateButtons();
        }
    }

    public void DiscardPendingChanges()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        ApplyPendingToGrid();
        UpdateButtons();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TrySwitchFormula(string formulaId, out bool changed)
    {
        changed = false;
        if (string.Equals(_currentFormulaId, formulaId, StringComparison.Ordinal)) return true;
        if (_pending.Count == 0) { changed = true; return true; }
        var result = MessageBox.Show(this, "公式常量尚未写入。是否先写入？", "公式设定",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes && !TryCommit(out var message))
        {
            RaiseStatus(message, true);
            return false;
        }
        if (result == DialogResult.No) DiscardPendingChanges();
        changed = true;
        return true;
    }

    private Control BuildLayout()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _title.Dock = DockStyle.Fill;
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.AutoSize = false;
        _title.Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);
        _subtitle.Dock = DockStyle.Fill;
        _subtitle.AutoSize = false;

        var titleHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        titleHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleHost.RowStyles.Add(new RowStyle(SizeType.Absolute, _title.Font.Height + 10));
        titleHost.RowStyles.Add(new RowStyle(SizeType.Absolute, _subtitle.Font.Height + 4));
        titleHost.Controls.Add(_title, 0, 0);
        titleHost.Controls.Add(_subtitle, 0, 1);

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 0)
        };
        tools.Controls.Add(_pendingLabel);
        tools.Controls.Add(_refresh);
        tools.Controls.Add(_discard);
        tools.Controls.Add(_write);
        header.Controls.Add(titleHost, 0, 0);
        header.Controls.Add(tools, 1, 0);

        var middle = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 4, 0, 4)
        };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        middle.Controls.Add(_stepGrid, 0, 0);
        middle.Controls.Add(_constantGrid, 0, 1);

        body.Controls.Add(header, 0, 0);
        body.Controls.Add(middle, 0, 1);
        body.Controls.Add(_evidence, 0, 2);
        return body;
    }

    private void ConfigureStepGrid()
    {
        _stepGrid.Dock = DockStyle.Fill;
        _stepGrid.ReadOnly = true;
        _stepGrid.AllowUserToAddRows = false;
        _stepGrid.AllowUserToDeleteRows = false;
        _stepGrid.AllowUserToResizeRows = false;
        _stepGrid.RowHeadersVisible = false;
        _stepGrid.MultiSelect = false;
        _stepGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _stepGrid.BackgroundColor = Color.White;
        _stepGrid.BorderStyle = BorderStyle.FixedSingle;
        _stepGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var column in new[] { ("步骤", 6f, 42), ("计算式", 54f, 180), ("说明", 40f, 120) })
            _stepGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = column.Item1, FillWeight = column.Item2, MinimumWidth = column.Item3,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        _stepGrid.Columns[1].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _stepGrid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _stepGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        SelectionAppearance.Configure(_stepGrid);
    }

    private void ConfigureConstantGrid()
    {
        _constantGrid.Dock = DockStyle.Fill;
        _constantGrid.ReadOnly = false;
        _constantGrid.AllowUserToAddRows = false;
        _constantGrid.AllowUserToDeleteRows = false;
        _constantGrid.AllowUserToResizeRows = false;
        _constantGrid.RowHeadersVisible = false;
        _constantGrid.MultiSelect = false;
        _constantGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _constantGrid.BackgroundColor = Color.White;
        _constantGrid.BorderStyle = BorderStyle.FixedSingle;
        _constantGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var column in new[]
                 {
                     ("常量", 20f, 110), ("作用", 26f, 140), ("6.4", 7f, 42), ("6.5", 7f, 42),
                     ("6.6", 7f, 42), ("新值", 9f, 52), ("范围", 8f, 56), ("状态", 16f, 92)
                 })
        {
            var gridColumn = new DataGridViewTextBoxColumn
            {
                HeaderText = column.Item1, FillWeight = column.Item2, MinimumWidth = column.Item3,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ReadOnly = column.Item1 != "新值"
            };
            _constantGrid.Columns.Add(gridColumn);
        }
        _constantGrid.Columns[0].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _constantGrid.Columns[1].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _constantGrid.Columns[7].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _constantGrid.RowTemplate.Height = 24;
        _constantGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        SelectionAppearance.Configure(_constantGrid);
    }

    private void Render()
    {
        if (_value == null) return;
        var version = _workspace?.ActiveProfile.Profile.Version;
        _title.Text = $"{_value.Definition.Category} · {_value.Definition.DisplayName}";
        _subtitle.Text = version == null
            ? "未打开项目"
            : $"当前基底 {version} ｜ 6.4：{_value.Definition.Value64}  6.5：{_value.Definition.Value65}  6.6：{_value.Definition.Value66}";
        ConfigureConstantHeaders(version);

        _binding = true;
        try
        {
            _stepGrid.Rows.Clear();
            foreach (var step in _value.Steps)
            {
                var index = _stepGrid.Rows.Add(step.Index.ToString(CultureInfo.InvariantCulture),
                    step.Expression, step.Note);
                _stepGrid.Rows[index].Tag = step;
            }

            _constantGrid.Rows.Clear();
            foreach (var constant in _value.Constants)
            {
                var current = constant.CurrentValue;
                var value64 = version == "6.4" ? current : constant.Constant.Value64;
                var value65 = version == "6.5" ? current : constant.Constant.Value65;
                var value66 = version == "6.6" ? current : constant.Constant.Value66;
                var index = _constantGrid.Rows.Add(constant.Constant.Name, constant.Constant.Role,
                    value64, value65, value66, string.Empty, constant.Range, constant.Status);
                var row = _constantGrid.Rows[index];
                row.Tag = constant;
                row.Cells[ValueColumn].ReadOnly = !constant.Editable;
                row.Cells[ValueColumn].Style.BackColor = constant.Editable
                    ? Color.FromArgb(255, 255, 230)
                    : Color.FromArgb(245, 245, 245);
                if (constant.CurrentValue == "—") row.DefaultCellStyle.ForeColor = Color.Gray;
                if (constant.Status == "不适用此基底") row.DefaultCellStyle.ForeColor = Color.Gray;
            }
        }
        finally { _binding = false; }

        ApplyPendingToGrid();
        if (_constantGrid.Rows.Count > 0)
            SelectConstant(null);
        UpdateEvidence();
    }

    private void ConfigureConstantHeaders(string? version)
    {
        var names = new[] { "6.4", "6.5", "6.6" };
        for (var i = 0; i < names.Length; i++)
            _constantGrid.Columns[i + 2].HeaderText = version == names[i] ? names[i] + "（当前）" : names[i];
    }

    private void ResetConstantHeaders()
    {
        _constantGrid.Columns[2].HeaderText = "6.4";
        _constantGrid.Columns[3].HeaderText = "6.5";
        _constantGrid.Columns[4].HeaderText = "6.6";
    }

    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (e.ColumnIndex != ValueColumn || _binding)
        {
            e.Cancel = true;
            return;
        }
        if (_constantGrid.Rows[e.RowIndex].Tag is not BattleFormulaConstantValue value || !value.Editable)
            e.Cancel = true;
    }

    private void OnCellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (e.ColumnIndex != ValueColumn) return;
        var text = Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            Reject(e, "请输入整数。");
            return;
        }
        var row = _constantGrid.Rows[e.RowIndex].Tag as BattleFormulaConstantValue;
        if (row == null || !row.Editable)
        {
            Reject(e, "此常量只读。");
            return;
        }
        if (!TryRange(row, number))
        {
            Reject(e, $"请输入 {row.Range}。");
            return;
        }
        if (_workspace == null || row.Constant.ConstantId is not { } id)
        {
            Reject(e, "尚未打开项目。");
            return;
        }
        var candidate = _pending
            .Where(item => item.Key != id)
            .Select(item => new BattleFormulaEdit(item.Key, item.Value))
            .Append(new BattleFormulaEdit(id, number))
            .ToArray();
        var issues = _service.ValidateEdits(_workspace, candidate);
        var error = issues.FirstOrDefault(item => item.Severity == BattleFormulaIssueSeverity.Error);
        if (error != null)
        {
            Reject(e, error.Message);
            return;
        }
        RaiseStatus(BuildWarningText(issues), false);
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex != ValueColumn) return;
        var row = _constantGrid.Rows[e.RowIndex].Tag as BattleFormulaConstantValue;
        if (row?.Constant.ConstantId is not { } id) return;
        var text = Convert.ToString(_constantGrid.Rows[e.RowIndex].Cells[ValueColumn].Value,
            CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _pending.Remove(id);
        }
        else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                 CurrentNumber(row) != number)
        {
            _pending[id] = number;
        }
        else
        {
            _pending.Remove(id);
        }
        ApplyPendingToGrid();
        UpdateButtons();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPendingToGrid()
    {
        if (_binding) return;
        _binding = true;
        try
        {
            foreach (DataGridViewRow row in _constantGrid.Rows)
            {
                if (row.Tag is not BattleFormulaConstantValue value ||
                    value.Constant.ConstantId is not { } id) continue;
                row.Cells[ValueColumn].Value = _pending.TryGetValue(id, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
            }
        }
        finally { _binding = false; }
    }

    private void UpdateButtons()
    {
        var hasPending = _pending.Count > 0 && !_committing;
        _write.Enabled = _workspace != null && hasPending;
        _discard.Enabled = hasPending;
        _refresh.Enabled = _workspace != null && !_committing;
        _pendingLabel.Text = _pending.Count == 0 ? "无待写入修改" : $"● {_pending.Count} 处待写入";
        _pendingLabel.ForeColor = _pending.Count == 0 ? Color.DimGray : Color.DarkOrange;
    }

    private void SelectConstant(string? constantId)
    {
        _binding = true;
        try
        {
            _constantGrid.ClearSelection();
            var target = constantId == null
                ? (_constantGrid.Rows.Count > 0 ? _constantGrid.Rows[0] : null)
                : _constantGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row =>
                    row.Tag is BattleFormulaConstantValue value &&
                    string.Equals(value.Constant.ConstantId, constantId, StringComparison.Ordinal));
            target ??= _constantGrid.Rows.Count > 0 ? _constantGrid.Rows[0] : null;
            if (target != null)
            {
                _constantGrid.CurrentCell = target.Cells[0];
                target.Selected = true;
            }
        }
        finally { _binding = false; }
        UpdateEvidence();
    }

    private string? SelectedConstantId()
    {
        if (_constantGrid.SelectedRows.Count == 0) return null;
        return (_constantGrid.SelectedRows[0].Tag as BattleFormulaConstantValue)?.Constant.ConstantId;
    }

    private void UpdateEvidence()
    {
        if (_value == null) { _evidence.Text = string.Empty; return; }
        var text = _value.Definition.Description + Environment.NewLine + Environment.NewLine +
            _value.Definition.Detail + Environment.NewLine + Environment.NewLine +
            "证据：" + _value.Definition.Evidence;
        if (_constantGrid.SelectedRows.Count > 0 &&
            _constantGrid.SelectedRows[0].Tag is BattleFormulaConstantValue constant)
        {
            text += Environment.NewLine + Environment.NewLine +
                $"选中常量：{constant.Constant.Name} ｜ {constant.Status}" + Environment.NewLine +
                $"位置：{constant.Constant.Location}" + Environment.NewLine +
                (constant.Range.Length == 0 ? string.Empty : $"范围：{constant.Range}" + Environment.NewLine) +
                (string.IsNullOrWhiteSpace(constant.Diagnostic) ? string.Empty : "诊断：" + constant.Diagnostic);
            if (constant.Constant.ConstantId is { } id)
            {
                var definition = EngineConstantService.Definitions
                    .FirstOrDefault(item => item.Id == id);
                if (definition != null)
                {
                    text += Environment.NewLine + string.Join(Environment.NewLine,
                        definition.Layouts.Select(layout =>
                            $"签名 {layout.Label}：{Hex(layout.Prefix)} [值] {Hex(layout.Suffix)}"));
                }
            }
        }
        _evidence.Text = text;
    }

    private static string Hex(IReadOnlyList<byte> bytes) =>
        string.Join(" ", bytes.Select(item => item.ToString("X2")));

    private static bool TryRange(BattleFormulaConstantValue item, int value)
    {
        var parts = item.Range.Split("..", StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return true;
        return int.TryParse(parts[0], out var minimum) && int.TryParse(parts[1], out var maximum) &&
               value >= minimum && value <= maximum;
    }

    private static int? CurrentNumber(BattleFormulaConstantValue item) =>
        int.TryParse(item.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : null;

    private BattleFormulaEdit[] PendingEdits() =>
        _pending.Select(item => new BattleFormulaEdit(item.Key, item.Value)).ToArray();

    private string DescribeChange(BattleFormulaEdit edit)
    {
        var row = _value?.Constants.FirstOrDefault(item => item.Constant.ConstantId == edit.ConstantId);
        return row == null
            ? $"{edit.ConstantId} → {edit.Value}"
            : $"{row.Constant.Name} {row.CurrentValue} → {edit.Value}";
    }

    private static string BuildWarningText(IReadOnlyList<BattleFormulaValidationIssue> issues)
    {
        var warnings = issues.Where(item => item.Severity == BattleFormulaIssueSeverity.Warning).ToArray();
        return warnings.Length == 0 ? string.Empty : string.Join(Environment.NewLine, warnings.Select(item => item.Message));
    }

    private void CommitFromButton()
    {
        if (_workspace == null || _pending.Count == 0) return;
        if (!PreparePendingEdits()) return;
        var edits = PendingEdits();
        var issues = _service.ValidateEdits(_workspace, edits);
        if (issues.Any(item => item.Severity == BattleFormulaIssueSeverity.Error))
        {
            RaiseStatus(issues.First(item => item.Severity == BattleFormulaIssueSeverity.Error).Message, true);
            return;
        }
        var summary = string.Join(Environment.NewLine, edits.Select(DescribeChange));
        var warnings = BuildWarningText(issues);
        if (warnings.Length > 0) summary += Environment.NewLine + Environment.NewLine + warnings;
        if (MessageBox.Show(this, summary, "写入公式常量", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!TryCommit(out var message))
            MessageBox.Show(this, message, "公式常量写入", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RefreshFromButton()
    {
        if (_pending.Count > 0)
        {
            var result = MessageBox.Show(this, "公式常量尚未写入。刷新会放弃这些修改，是否继续？",
                "公式设定", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
            DiscardPendingChanges();
        }
        RefreshValues();
    }

    private void Reject(DataGridViewCellValidatingEventArgs e, string message)
    {
        e.Cancel = true;
        RaiseStatus(message, true);
    }

    private void RaiseStatus(string message, bool error)
        => StatusChanged?.Invoke(this, new EngineConstantStatusEventArgs(message, error));

    internal DataGridView StepGridForSmokeTest => _stepGrid;
    internal DataGridView ConstantGridForSmokeTest => _constantGrid;
    internal Button WriteButtonForSmokeTest => _write;
    internal Button DiscardButtonForSmokeTest => _discard;
    internal IReadOnlyDictionary<string, int> PendingForSmokeTest => _pending;
    internal BattleFormulaValue? ValueForSmokeTest => _value;

    internal bool SetPendingForSmokeTest(string constantId, int value)
    {
        if (_workspace == null) return false;
        var row = _value?.Constants.FirstOrDefault(item => item.Constant.ConstantId == constantId);
        if (row == null || !row.Editable || !TryRange(row, value)) return false;
        var candidate = _pending
            .Where(item => item.Key != constantId)
            .Select(item => new BattleFormulaEdit(item.Key, item.Value))
            .Append(new BattleFormulaEdit(constantId, value))
            .ToArray();
        if (_service.ValidateEdits(_workspace, candidate)
            .Any(item => item.Severity == BattleFormulaIssueSeverity.Error)) return false;
        _pending[constantId] = value;
        ApplyPendingToGrid();
        UpdateButtons();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool TryCommitForSmokeTest(out string message) => TryCommit(out message);

    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() => DoubleBuffered = true;
    }
}
