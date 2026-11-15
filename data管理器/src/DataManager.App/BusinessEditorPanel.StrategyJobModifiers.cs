using System.Data;
using System.Globalization;
using System.Text;
using DataManager.Core.Domain;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed record StrategyJobModifierApplyRequest(
    IReadOnlyList<StrategyJobModifierRule> Rules,
    bool InstallHook,
    StrategyBuiltInRuleSettings? BuiltIn);

public sealed partial class BusinessEditorPanel
{
    private readonly StrategyJobModifierService _strategyJobModifierService = new();
    private List<StrategyJobModifierRule>? _strategyJobModifierDraft;
    private bool _strategyJobModifierHookInstalled;
    private string? _strategyJobModifierBaselineSignature;
    private Action? _strategyJobModifierRefresh;
    private StrategyBuiltInRuleSettings? _strategyBuiltInDraft;
    private StrategyBuiltInRuleSettings? _strategyBuiltInDefaults;
    private StrategyBuiltInRuleInfo? _strategyBuiltInInfo;
    private string? _strategyBuiltInBaselineSignature;
    private Action? _strategyBuiltInRefresh;

    /// <summary>Set by MainForm so the tab stages the rule table in the common save transaction.</summary>
    public Func<StrategyJobModifierApplyRequest, bool>? StrategyJobModifierApply { get; set; }

    internal Func<StrategyJobModifierApplyRequest, bool>? StrategyJobModifierApplyForSmokeTest { get; set; }
    internal DataGridView? StrategyJobModifierGridForSmokeTest { get; private set; }
    internal IReadOnlyList<StrategyJobModifierRule>? StrategyJobModifierDraftForSmokeTest => _strategyJobModifierDraft;
    internal StrategyBuiltInRuleSettings? StrategyBuiltInDraftForSmokeTest => _strategyBuiltInDraft;

    private void AddStrategyJobModifierTab()
    {
        var page = DetailPage("策略专项");
        page.AccessibleName = "策略专项";
        var version = _workspace.ActiveProfile.Profile.Version ?? _workspace.ActiveProfile.ProfileId;
        if (_strategyJobModifierDraft == null || _strategyBuiltInInfo == null)
        {
            var snapshot = _strategyJobModifierService.Read(_workspace);
            if (_strategyJobModifierDraft == null)
            {
                _strategyJobModifierDraft = snapshot.Rules.ToList();
                _strategyJobModifierHookInstalled = snapshot.HookInstalled;
                _strategyJobModifierBaselineSignature = null;
            }
            _strategyBuiltInInfo = snapshot.BuiltIn;
            if (_strategyBuiltInDraft == null && snapshot.BuiltIn.Supported)
            {
                _strategyBuiltInDraft = snapshot.BuiltIn.Current;
                _strategyBuiltInDefaults = snapshot.BuiltIn.Current;
                _strategyBuiltInBaselineSignature = null;
            }
        }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 215));
        page.Controls.Add(root);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4), Margin = Padding.Empty };
        Button ActionButton(string text, string name) => new()
        {
            Text = text, Name = name, AutoSize = true, MinimumSize = new Size(92, 30), Margin = new Padding(0, 0, 8, 0)
        };
        var add = ActionButton("新增规则", "StrategyJobModifierAdd");
        var remove = ActionButton("删除选中", "StrategyJobModifierDelete");
        var up = ActionButton("上移", "StrategyJobModifierUp");
        var down = ActionButton("下移", "StrategyJobModifierDown");
        var reload = ActionButton("重新载入", "StrategyJobModifierReload");
        var import = ActionButton("导入 CSV", "StrategyJobModifierImport");
        var export = ActionButton("导出 CSV", "StrategyJobModifierExport");
        var apply = ActionButton("应用并保存", "StrategyJobModifierApply");
        var uninstall = ActionButton("卸载 Hook", "StrategyJobModifierUninstall");
        bar.Controls.AddRange([add, remove, up, down, reload, import, export, apply, uninstall]);
        root.Controls.Add(bar, 0, 0);

        var status = new Label
        {
            Name = "StrategyJobModifierStatus", AutoSize = true, Dock = DockStyle.Fill,
            ForeColor = Color.DimGray, Padding = new Padding(3, 2, 3, 4)
        };
        root.Controls.Add(status, 0, 1);

        var table = new DataTable();
        table.Columns.Add("序号", typeof(int));
        table.Columns.Add("作用侧", typeof(string));
        table.Columns.Add("条件", typeof(string));
        table.Columns.Add("条件值", typeof(string));
        table.Columns.Add("兵种系", typeof(string));
        table.Columns.Add("伤害%", typeof(int));
        table.Columns.Add("命中%", typeof(int));
        table.Columns.Add("启用", typeof(bool));
        table.Columns.Add("备注", typeof(string));
        var grid = GridFor(table);
        grid.Name = "StrategyJobModifierGrid";
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowHeadersVisible = false;
        StrategyJobModifierGridForSmokeTest = grid;
        root.Controls.Add(grid, 0, 2);

        var sideOptions = new[] { "受击方", "施法方" };
        var kindOptions = new[] { "策略类型", "策略ID" };
        var conditionOptions = BuildConditionOptions();
        var seriesOptions = Enumerable.Range(0, 40).Select(id => $"{id}：{SeriesName(id)}").ToArray();

        ReplaceWithCombo(grid, "作用侧", sideOptions);
        ReplaceWithCombo(grid, "条件", kindOptions);
        ReplaceWithCombo(grid, "条件值", conditionOptions);
        ReplaceWithCombo(grid, "兵种系", seriesOptions);
        grid.Columns["序号"]!.ReadOnly = true;
        grid.Columns["序号"]!.Width = 50;
        grid.Columns["伤害%"]!.Width = 70;
        grid.Columns["命中%"]!.Width = 70;
        grid.Columns["启用"]!.Width = 55;
        grid.Columns["备注"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var builtInInfo = _strategyBuiltInInfo ?? StrategyBuiltInRuleInfo.Unsupported("尚未读取内置条目。");
        var builtInValues = _strategyBuiltInDraft ?? StrategyBuiltInRuleSettings.Default;
        var typeOptions = Enumerable.Range(0, 256)
            .Select(id => $"类型 {id}：{StrategyTypeName(id) ?? "基底原值"}").ToArray();
        var idOptions = conditionOptions.Where(option => option.StartsWith("ID ", StringComparison.Ordinal)).ToArray();
        string OptionFor(int value, bool typeBased)
        {
            var prefix = typeBased ? $"类型 {value}：" : $"ID {value}：";
            return (typeBased ? typeOptions : idOptions)
                .FirstOrDefault(option => option.StartsWith(prefix, StringComparison.Ordinal))
                ?? value.ToString(CultureInfo.InvariantCulture);
        }

        Label Caption(string text) => new()
        {
            Text = text, AutoSize = true, Margin = new Padding(10, 6, 2, 0)
        };
        ComboBox EditableCombo(string name, IReadOnlyList<string> options, int value, bool typeBased)
        {
            var combo = new ComboBox
            {
                Name = name, DropDownStyle = ComboBoxStyle.DropDown, Width = 145,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
                Margin = new Padding(2, 3, 8, 0)
            };
            combo.Items.AddRange(options.Cast<object>().ToArray());
            combo.Text = OptionFor(value, typeBased);
            return combo;
        }
        ComboBox SeriesCombo(string name, byte value)
        {
            var combo = new ComboBox
            {
                Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Width = 150,
                Margin = new Padding(2, 3, 8, 0)
            };
            combo.Items.AddRange(seriesOptions.Cast<object>().ToArray());
            combo.SelectedIndex = Math.Clamp((int)value, 0, seriesOptions.Length - 1);
            return combo;
        }

        var builtInGroup = new GroupBox
        {
            Text = "引擎内置规则（可编辑；自定义规则在基础伤害/系数/行6/内置专项之后、防御与特效之前叠加）",
            Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 4, 0, 0),
            AccessibleName = "StrategyJobModifierBuiltIn"
        };
        var builtInForm = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, AutoSize = true, Margin = Padding.Empty
        };
        builtInForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) builtInForm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        builtInGroup.Controls.Add(builtInForm);
        root.Controls.Add(builtInGroup, 0, 3);

        var diagnostic = new Label
        {
            Name = "StrategyBuiltInDiagnostic", AutoSize = true, Dock = DockStyle.Fill,
            Text = builtInInfo.Supported
                ? "可直接修改；保存前会校验原字节。"
                : "当前基底内置条目只读：" + builtInInfo.Diagnostic,
            ForeColor = builtInInfo.Supported ? Color.DimGray : Color.Firebrick,
            Padding = new Padding(2, 2, 2, 2)
        };
        builtInForm.Controls.Add(diagnostic, 0, 0);

        var fireRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        fireRow.Controls.Add(new Label
        {
            Text = "受击方火系", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 6, 8, 0)
        });
        var fireEnabled = new CheckBox
        {
            Text = "启用", Name = "StrategyBuiltInFireEnabled", Checked = builtInValues.FireEnabled,
            AutoSize = true, Margin = new Padding(0, 7, 8, 0)
        };
        var fireTrigger = EditableCombo("StrategyBuiltInFireTrigger", typeOptions, builtInValues.FireTriggerType, true);
        var fireSeries = SeriesCombo("StrategyBuiltInFireSeries", builtInValues.FireSeries);
        var fireBonus = new NumericUpDown
        {
            Name = "StrategyBuiltInFireBonus", Minimum = -100, Maximum = 127,
            Value = Math.Clamp((int)builtInValues.FireBonus, -100, 127), Width = 70,
            Margin = new Padding(2, 3, 8, 0)
        };
        fireRow.Controls.AddRange([fireEnabled, Caption("触发类型"), fireTrigger, Caption("兵种系"), fireSeries,
            Caption("加成%"), fireBonus]);
        builtInForm.Controls.Add(fireRow, 0, 1);

        var charmRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        charmRow.Controls.Add(new Label
        {
            Text = "施法方诱惑", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 6, 8, 0)
        });
        var charmEnabled = new CheckBox
        {
            Text = "启用", Name = "StrategyBuiltInCharmEnabled", Checked = builtInValues.CharmEnabled,
            AutoSize = true, Margin = new Padding(0, 7, 8, 0)
        };
        var charmDamage = EditableCombo("StrategyBuiltInCharmDamage",
            builtInInfo.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType ? typeOptions : idOptions,
            builtInValues.CharmDamageValue, builtInInfo.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType);
        var charmHit = EditableCombo("StrategyBuiltInCharmHit",
            builtInInfo.CharmHitKind == StrategyBuiltInMatchKind.StrategyType ? typeOptions : idOptions,
            builtInValues.CharmHitValue, builtInInfo.CharmHitKind == StrategyBuiltInMatchKind.StrategyType);
        var charmSeries = SeriesCombo("StrategyBuiltInCharmSeries", builtInValues.CharmSeries);
        charmRow.Controls.AddRange([charmEnabled,
            Caption(builtInInfo.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType ? "伤害判定（类型）" : "伤害判定"),
            charmDamage,
            Caption(builtInInfo.CharmHitKind == StrategyBuiltInMatchKind.StrategyType ? "命中判定（类型）" : "命中判定"),
            charmHit, Caption("兵种系"), charmSeries]);
        builtInForm.Controls.Add(charmRow, 0, 2);

        var bottomRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var resetBuiltIn = new Button
        {
            Text = "恢复当前值", Name = "StrategyBuiltInReset", AutoSize = true,
            MinimumSize = new Size(96, 28), Margin = new Padding(0, 4, 8, 0)
        };
        var builtInNote = new Label
        {
            AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0),
            Text = "倍率固定为伤害×2、命中×1.5；其他倍率请关闭内置并用自定义规则 +100%/+50% 替代。"
                + " 内置与自定义规则当前都按兵种系；具体兵种（job）暂不支持。"
        };
        bottomRow.Controls.AddRange([resetBuiltIn, builtInNote]);
        builtInForm.Controls.Add(bottomRow, 0, 3);

        void UpdateBuiltInEnabledStates()
        {
            var supported = builtInInfo.Supported;
            fireEnabled.Enabled = supported;
            charmEnabled.Enabled = supported;
            fireTrigger.Enabled = supported;
            fireSeries.Enabled = supported && fireEnabled.Checked;
            fireBonus.Enabled = supported && fireEnabled.Checked;
            charmDamage.Enabled = supported && charmEnabled.Checked;
            charmHit.Enabled = supported && charmEnabled.Checked;
            charmSeries.Enabled = supported && charmEnabled.Checked;
            resetBuiltIn.Enabled = supported;
        }
        UpdateBuiltInEnabledStates();

        var updating = false;
        string BuiltInSignature() => $"{fireEnabled.Checked}|{fireTrigger.Text}|{fireSeries.SelectedIndex}|{fireBonus.Value}"
            + $"|{charmEnabled.Checked}|{charmDamage.Text}|{charmHit.Text}|{charmSeries.SelectedIndex}";

        void UpdateBuiltInUi(StrategyBuiltInRuleSettings settings)
        {
            updating = true;
            try
            {
                fireEnabled.Checked = settings.FireEnabled;
                fireTrigger.Text = OptionFor(settings.FireTriggerType, true);
                fireSeries.SelectedIndex = Math.Clamp((int)settings.FireSeries, 0, seriesOptions.Length - 1);
                fireBonus.Value = Math.Clamp((int)settings.FireBonus, -100, 127);
                charmEnabled.Checked = settings.CharmEnabled;
                charmDamage.Text = OptionFor(settings.CharmDamageValue,
                    builtInInfo.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType);
                charmHit.Text = OptionFor(settings.CharmHitValue,
                    builtInInfo.CharmHitKind == StrategyBuiltInMatchKind.StrategyType);
                charmSeries.SelectedIndex = Math.Clamp((int)settings.CharmSeries, 0, seriesOptions.Length - 1);
                UpdateBuiltInEnabledStates();
            }
            finally { updating = false; }
            _strategyBuiltInBaselineSignature ??= BuiltInSignature();
        }

        bool TryBuildBuiltIn(out StrategyBuiltInRuleSettings settings, out string error)
        {
            settings = StrategyBuiltInRuleSettings.Default;
            error = string.Empty;
            if (!builtInInfo.Supported) return true;
            var trigger = NumericPrefix(fireTrigger.Text);
            if (trigger is null or < 0 or > 255)
            {
                error = "火系触发类型无法解析。";
                return false;
            }
            var damage = NumericPrefix(charmDamage.Text);
            if (damage is null or < 0 or > 255)
            {
                error = "诱惑伤害判定值无法解析。";
                return false;
            }
            var hit = NumericPrefix(charmHit.Text);
            if (hit is null or < 0 or > 255)
            {
                error = "诱惑命中判定值无法解析。";
                return false;
            }
            settings = new StrategyBuiltInRuleSettings(
                fireEnabled.Checked,
                (byte)trigger.Value,
                (byte)Math.Clamp(fireSeries.SelectedIndex, 0, 39),
                (sbyte)fireBonus.Value,
                charmEnabled.Checked,
                (byte)damage.Value,
                (byte)hit.Value,
                (byte)Math.Clamp(charmSeries.SelectedIndex, 0, 39));
            return true;
        }

        bool CommitBuiltInDraft(bool report = true)
        {
            if (!builtInInfo.Supported)
            {
                _strategyBuiltInDraft = null;
                return true;
            }
            if (!TryBuildBuiltIn(out var settings, out var error))
            {
                if (report) ShowEditorError(builtInGroup, error);
                return false;
            }
            var validation = _strategyJobModifierService.ValidateBuiltIn(settings);
            var issue = validation.Issues.FirstOrDefault(item => item.Severity == StrategyJobModifierIssueSeverity.Error);
            if (issue != null)
            {
                if (report) ShowEditorError(builtInGroup, issue.Message);
                return false;
            }
            _strategyBuiltInDraft = settings;
            UpdateStatus();
            NotifyPendingChanges();
            return true;
        }

        void ResetBuiltIn()
        {
            if (!builtInInfo.Supported || _strategyBuiltInDefaults == null) return;
            _strategyBuiltInDraft = _strategyBuiltInDefaults;
            UpdateBuiltInUi(_strategyBuiltInDraft);
            _strategyBuiltInBaselineSignature = BuiltInSignature();
            NotifyPendingChanges();
        }

        string Signature() => string.Join('\u001f', table.Rows.Cast<DataRow>().Select(row =>
            $"{row["作用侧"]}|{row["条件"]}|{row["条件值"]}|{row["兵种系"]}|{row["伤害%"]}|{row["命中%"]}|{row["启用"]}|{row["备注"]}"));

        string ConditionDisplay(StrategyJobModifierRule rule)
        {
            var prefix = rule.MatchKind == StrategyModifierMatchKind.StrategyId
                ? $"ID {rule.MatchValue}：" : $"类型 {rule.MatchValue}：";
            return conditionOptions.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.Ordinal))
                ?? (rule.MatchKind == StrategyModifierMatchKind.StrategyId
                    ? $"ID {rule.MatchValue}：策略 {rule.MatchValue}"
                    : $"类型 {rule.MatchValue}：基底原值");
        }

        void RefreshRows(int selected = 0)
        {
            updating = true;
            try
            {
                table.Rows.Clear();
                for (var index = 0; index < _strategyJobModifierDraft!.Count; index++)
                {
                    var rule = _strategyJobModifierDraft[index];
                    table.Rows.Add(index + 1, SideText(rule.Side), KindText(rule.MatchKind),
                        ConditionDisplay(rule), $"{rule.Series}：{SeriesName(rule.Series)}",
                        rule.DamagePercent, rule.HitPercent, rule.Enabled, rule.Note);
                }
                if (grid.Rows.Count > 0)
                {
                    selected = Math.Clamp(selected, 0, grid.Rows.Count - 1);
                    grid.CurrentCell = grid.Rows[selected].Cells[0];
                    grid.Rows[selected].Selected = true;
                }
            }
            finally { updating = false; }
            _strategyJobModifierBaselineSignature ??= Signature();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            var count = table.Rows.Count;
            status.Text = $"规则 {count}/{StrategyJobModifierRule.Capacity} · Hook {(_strategyJobModifierHookInstalled ? "已加入草稿/已安装" : "未安装")} · {version}"
                + $" · 伤害 0x{StrategyHookSectionService.DamageHookVa:X8} / 命中 0x{StrategyHookSectionService.HitHookVa:X8}";
            status.ForeColor = count >= StrategyJobModifierRule.Capacity ? Color.Firebrick : Color.DimGray;
            add.Enabled = count < StrategyJobModifierRule.Capacity;
            remove.Enabled = count > 0;
            up.Enabled = count > 1;
            down.Enabled = count > 1;
        }

        bool CommitDraft(bool report = true)
        {
            if (updating) return true;
            if (!grid.EndEdit()) return false;
            if (!TryBuildRules(table, version, out var rules, out var error))
            {
                if (report) ShowEditorError(grid, error);
                return false;
            }
            var validation = _strategyJobModifierService.Validate(rules);
            var issue = validation.Issues.FirstOrDefault(item => item.Severity == StrategyJobModifierIssueSeverity.Error);
            if (issue != null)
            {
                if (report) ShowEditorError(grid, issue.Message);
                return false;
            }
            _strategyJobModifierDraft = rules.ToList();
            UpdateStatus();
            NotifyPendingChanges();
            return true;
        }

        void AddRow()
        {
            if (table.Rows.Count >= StrategyJobModifierRule.Capacity)
            {
                ShowEditorError(grid, $"规则最多 {StrategyJobModifierRule.Capacity} 条。");
                return;
            }
            if (!CommitDraft()) return;
            var fireSeries = version == "6.5" ? 30 : 12;
            var rule = StrategyJobModifierRule.CreateDefault((byte)fireSeries);
            _strategyJobModifierDraft!.Add(rule);
            RefreshRows(_strategyJobModifierDraft.Count - 1);
            NotifyPendingChanges();
        }

        void DeleteRows()
        {
            if (grid.SelectedRows.Count == 0) return;
            if (!CommitDraft()) return;
            var indices = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Index).OrderByDescending(index => index).ToArray();
            foreach (var index in indices)
                if (index >= 0 && index < _strategyJobModifierDraft!.Count) _strategyJobModifierDraft.RemoveAt(index);
            RefreshRows(Math.Min(indices.LastOrDefault(), Math.Max(0, _strategyJobModifierDraft!.Count - 1)));
            NotifyPendingChanges();
        }

        void MoveRow(int delta)
        {
            if (grid.CurrentCell is not { RowIndex: >= 0 } cell) return;
            if (!CommitDraft()) return;
            var index = cell.RowIndex;
            var target = index + delta;
            if (target < 0 || target >= _strategyJobModifierDraft!.Count) return;
            (_strategyJobModifierDraft[index], _strategyJobModifierDraft[target]) =
                (_strategyJobModifierDraft[target], _strategyJobModifierDraft[index]);
            RefreshRows(target);
            NotifyPendingChanges();
        }

        bool ApplyRules(bool install)
        {
            if (!CommitDraft() || !CommitBuiltInDraft()) return false;
            var request = new StrategyJobModifierApplyRequest(
                _strategyJobModifierDraft!.ToArray(), install,
                builtInInfo.Supported ? _strategyBuiltInDraft : null);
            var callback = StrategyJobModifierApplyForSmokeTest ?? StrategyJobModifierApply;
            if (callback == null)
            {
                ShowEditorError(grid, "当前窗口没有接入策略 Hook 保存事务。");
                return false;
            }
            if (!callback(request)) return false;
            _strategyJobModifierHookInstalled = install && request.Rules.Count > 0;
            _strategyJobModifierBaselineSignature = Signature();
            _strategyBuiltInBaselineSignature = BuiltInSignature();
            UpdateStatus();
            _status.Text = "策略专项规则/内置条目已加入草稿；请使用顶部保存写回。";
            _status.ForeColor = Color.DimGray;
            _status.Visible = true;
            if (StrategyJobModifierApplyForSmokeTest == null) SaveRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        void Reload()
        {
            grid.EndEdit();
            var jobDirty = _strategyJobModifierBaselineSignature != null && Signature() != _strategyJobModifierBaselineSignature;
            var builtInDirty = builtInInfo.Supported && _strategyBuiltInBaselineSignature != null
                && BuiltInSignature() != _strategyBuiltInBaselineSignature;
            if ((jobDirty || builtInDirty)
                && MessageBox.Show(this, "放弃尚未加入草稿的策略专项修改并重新读取？", "重新载入",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var fresh = _strategyJobModifierService.Read(_workspace);
            _strategyJobModifierDraft = fresh.Rules.ToList();
            _strategyJobModifierHookInstalled = fresh.HookInstalled;
            _strategyJobModifierBaselineSignature = null;
            builtInInfo = fresh.BuiltIn;
            _strategyBuiltInInfo = fresh.BuiltIn;
            _strategyBuiltInDraft = fresh.BuiltIn.Supported ? fresh.BuiltIn.Current : null;
            _strategyBuiltInDefaults = _strategyBuiltInDraft;
            _strategyBuiltInBaselineSignature = null;
            RefreshRows();
            UpdateBuiltInUi(_strategyBuiltInDraft ?? StrategyBuiltInRuleSettings.Default);
            _errors.SetError(grid, string.Empty);
        }

        add.Click += (_, _) => AddRow();
        remove.Click += (_, _) => DeleteRows();
        up.Click += (_, _) => MoveRow(-1);
        down.Click += (_, _) => MoveRow(1);
        reload.Click += (_, _) => Reload();
        apply.Click += (_, _) => ApplyRules(true);
        uninstall.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "卸载策略 Hook 会恢复两个 Hook 站点的原始字节，规则表保留。继续？",
                    "卸载策略 Hook", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            ApplyRules(false);
        };
        import.Click += (_, _) =>
        {
            grid.EndEdit();
            if (_strategyJobModifierBaselineSignature != null && Signature() != _strategyJobModifierBaselineSignature
                && MessageBox.Show(this, "导入会替换当前未加入草稿的规则，继续？", "导入 CSV",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            using var dialog = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var imported = ImportRules(dialog.FileName);
                var validation = _strategyJobModifierService.Validate(imported);
                var issue = validation.Issues.FirstOrDefault(item => item.Severity == StrategyJobModifierIssueSeverity.Error);
                if (issue != null) throw new InvalidDataException(issue.Message);
                _strategyJobModifierDraft = imported.ToList();
                RefreshRows();
                _status.Text = "CSV 已载入草稿；请使用“应用并保存”写回。";
                _status.ForeColor = Color.DimGray;
                _status.Visible = true;
            }
            catch (Exception ex) { ShowEditorError(grid, "导入失败：" + ex.Message); }
        };
        export.Click += (_, _) =>
        {
            if (!CommitDraft()) return;
            using var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "策略专项规则.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                ExportRules(dialog.FileName, _strategyJobModifierDraft!);
                _status.Text = "策略专项规则 CSV 已导出。";
                _status.ForeColor = Color.DimGray;
                _status.Visible = true;
            }
            catch (Exception ex) { ShowEditorError(grid, "导出失败：" + ex.Message); }
        };
        grid.CellEndEdit += (_, _) => CommitDraft();
        grid.CellValueChanged += (_, args) =>
        {
            if (updating || args.RowIndex < 0 || args.ColumnIndex < 0) return;
            if (grid.Columns[args.ColumnIndex].Name != "条件") { NotifyPendingChanges(); return; }
            var kind = Convert.ToString(table.Rows[args.RowIndex]["条件"], CultureInfo.InvariantCulture) ?? "策略类型";
            var current = Convert.ToString(table.Rows[args.RowIndex]["条件值"], CultureInfo.InvariantCulture) ?? string.Empty;
            var prefix = kind == "策略ID" ? "ID " : "类型 ";
            if (!current.StartsWith(prefix, StringComparison.Ordinal))
            {
                var replacement = conditionOptions.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.Ordinal));
                if (replacement != null) table.Rows[args.RowIndex]["条件值"] = replacement;
            }
            NotifyPendingChanges();
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => NotifyPendingChanges();
        grid.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Delete) return;
            DeleteRows();
            args.Handled = true;
            args.SuppressKeyPress = true;
        };
        grid.DataError += (_, args) => { args.ThrowException = false; args.Cancel = true; };

        fireEnabled.CheckedChanged += (_, _) =>
        {
            UpdateBuiltInEnabledStates();
            if (!updating) NotifyPendingChanges();
        };
        charmEnabled.CheckedChanged += (_, _) =>
        {
            if (charmEnabled.Checked)
            {
                var damage = NumericPrefix(charmDamage.Text);
                if (damage is null || damage >= (int)StrategyBuiltInRuleSettings.DisabledValue)
                    charmDamage.Text = OptionFor(0x14,
                        builtInInfo.CharmDamageKind == StrategyBuiltInMatchKind.StrategyType);
                var hit = NumericPrefix(charmHit.Text);
                if (hit is null || hit >= (int)StrategyBuiltInRuleSettings.DisabledValue)
                    charmHit.Text = OptionFor(0x14,
                        builtInInfo.CharmHitKind == StrategyBuiltInMatchKind.StrategyType);
            }
            UpdateBuiltInEnabledStates();
            if (!updating) NotifyPendingChanges();
        };
        fireTrigger.TextChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        fireSeries.SelectedIndexChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        fireBonus.ValueChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        charmDamage.TextChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        charmHit.TextChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        charmSeries.SelectedIndexChanged += (_, _) => { if (!updating) NotifyPendingChanges(); };
        resetBuiltIn.Click += (_, _) => ResetBuiltIn();

        _pendingChecks.Add(() => grid.IsCurrentCellDirty
            || (_strategyJobModifierBaselineSignature != null && Signature() != _strategyJobModifierBaselineSignature)
            || (builtInInfo.Supported && _strategyBuiltInBaselineSignature != null
                && BuiltInSignature() != _strategyBuiltInBaselineSignature));
        _flushers.Add(() => CommitDraft());
        _flushers.Add(() => CommitBuiltInDraft());
        _strategyJobModifierRefresh = () => { if (!grid.IsDisposed) RefreshRows(); };
        _strategyBuiltInRefresh = () =>
        {
            if (builtInGroup.IsDisposed) return;
            if (_strategyBuiltInInfo != null) builtInInfo = _strategyBuiltInInfo;
            UpdateBuiltInUi(_strategyBuiltInDraft ?? StrategyBuiltInRuleSettings.Default);
        };
        RefreshRows();
        UpdateBuiltInUi(_strategyBuiltInDraft ?? StrategyBuiltInRuleSettings.Default);
        _entityRebinders.Add(() =>
        {
            for (var index = 0; index < table.Rows.Count; index++)
            {
                var series = NumericPrefix(Convert.ToString(table.Rows[index]["兵种系"], CultureInfo.InvariantCulture));
                if (series is >= 0 and <= 39)
                    table.Rows[index]["兵种系"] = $"{series}：{SeriesName(series.Value)}";
            }
        });
    }

    private void AcceptStrategyJobModifierSaved()
    {
        if (_strategyJobModifierDraft == null) return;
        var fresh = _strategyJobModifierService.Read(_workspace);
        _strategyJobModifierDraft = fresh.Rules.ToList();
        _strategyJobModifierHookInstalled = fresh.HookInstalled;
        _strategyJobModifierBaselineSignature = null;
        _strategyBuiltInInfo = fresh.BuiltIn;
        _strategyBuiltInDraft = fresh.BuiltIn.Supported ? fresh.BuiltIn.Current : null;
        _strategyBuiltInDefaults = _strategyBuiltInDraft;
        _strategyBuiltInBaselineSignature = null;
        _strategyJobModifierRefresh?.Invoke();
        _strategyBuiltInRefresh?.Invoke();
    }

    private void DiscardStrategyJobModifierDraft()
    {
        _strategyJobModifierDraft = null;
        _strategyJobModifierBaselineSignature = null;
        _strategyJobModifierHookInstalled = false;
        _strategyJobModifierRefresh = null;
        _strategyBuiltInDraft = null;
        _strategyBuiltInDefaults = null;
        _strategyBuiltInInfo = null;
        _strategyBuiltInBaselineSignature = null;
        _strategyBuiltInRefresh = null;
    }

    private static void ReplaceWithCombo(DataGridView grid, string columnName, IReadOnlyList<string> options)
    {
        var existing = grid.Columns[columnName];
        var index = existing?.DisplayIndex ?? grid.Columns.Count;
        if (existing != null) grid.Columns.Remove(existing);
        var combo = new DataGridViewComboBoxColumn
        {
            Name = columnName,
            HeaderText = columnName,
            DataPropertyName = columnName,
            DataSource = options.ToList(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
            DisplayIndex = index
        };
        grid.Columns.Add(combo);
    }

    private IReadOnlyList<string> BuildConditionOptions()
    {
        // Strategy type is a full byte in Data.e5. Keep every value selectable
        // so a rule loaded from a MOD with an unknown type still round-trips.
        var result = new List<string>();
        for (var id = 0; id <= 0xFF; id++)
            result.Add($"类型 {id}：{StrategyTypeName(id) ?? "基底原值"}");
        if (_workspace.Documents.TryGetValue("strategies", out var strategies))
            foreach (DataRow row in strategies.Data.Rows)
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                result.Add($"ID {id}：{NameOf("strategies", id)}");
            }
        return result;
    }

    private string SeriesName(int id) => NameOf("job-series", id);

    private static string SideText(StrategyModifierSide side) => side == StrategyModifierSide.Caster ? "施法方" : "受击方";
    private static string KindText(StrategyModifierMatchKind kind) => kind == StrategyModifierMatchKind.StrategyId ? "策略ID" : "策略类型";

    private static int? NumericPrefix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var end = text.IndexOf('：', StringComparison.Ordinal);
        var token = end < 0 ? text.Trim() : text[..end].Trim();
        if (token.StartsWith("ID ", StringComparison.OrdinalIgnoreCase)) token = token[3..].Trim();
        if (token.StartsWith("类型 ", StringComparison.OrdinalIgnoreCase)) token = token[3..].Trim();
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private bool TryBuildRules(DataTable table, string version, out List<StrategyJobModifierRule> rules, out string error)
    {
        rules = [];
        error = string.Empty;
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var row = table.Rows[index];
            var sideText = Convert.ToString(row["作用侧"], CultureInfo.InvariantCulture) ?? string.Empty;
            var kindText = Convert.ToString(row["条件"], CultureInfo.InvariantCulture) ?? string.Empty;
            var conditionText = Convert.ToString(row["条件值"], CultureInfo.InvariantCulture) ?? string.Empty;
            var seriesText = Convert.ToString(row["兵种系"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (sideText is not ("受击方" or "施法方"))
            {
                error = $"第 {index + 1} 条规则的作用侧无效。";
                return false;
            }
            if (kindText is not ("策略类型" or "策略ID"))
            {
                error = $"第 {index + 1} 条规则的条件无效。";
                return false;
            }
            if (!conditionText.StartsWith(kindText == "策略ID" ? "ID " : "类型 ", StringComparison.Ordinal))
            {
                error = $"第 {index + 1} 条规则的条件值与条件类型不匹配。";
                return false;
            }
            var matchValue = NumericPrefix(conditionText);
            var series = NumericPrefix(seriesText);
            if (matchValue is null or < 0 or > 255)
            {
                error = $"第 {index + 1} 条规则的条件值无法解析。";
                return false;
            }
            if (series is null or < 0 or > 39)
            {
                error = $"第 {index + 1} 条规则的兵种系无法解析。";
                return false;
            }
            if (!int.TryParse(Convert.ToString(row["伤害%"], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var damage) || damage is < -100 or > 127)
            {
                error = $"第 {index + 1} 条规则的伤害加成必须为 -100..127。";
                return false;
            }
            if (!int.TryParse(Convert.ToString(row["命中%"], CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var hit) || hit is < -100 or > 100)
            {
                error = $"第 {index + 1} 条规则的命中加成必须为 -100..100。";
                return false;
            }
            var note = Convert.ToString(row["备注"], CultureInfo.InvariantCulture) ?? string.Empty;
            rules.Add(new StrategyJobModifierRule(
                sideText == "施法方" ? StrategyModifierSide.Caster : StrategyModifierSide.Receiver,
                kindText == "策略ID" ? StrategyModifierMatchKind.StrategyId : StrategyModifierMatchKind.StrategyType,
                (byte)matchValue.Value,
                (byte)series.Value,
                unchecked((sbyte)damage),
                unchecked((sbyte)hit),
                row["启用"] is not DBNull && Convert.ToBoolean(row["启用"], CultureInfo.InvariantCulture),
                note));
        }
        return true;
    }

    private static IReadOnlyList<StrategyJobModifierRule> ImportRules(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) throw new InvalidDataException("CSV 为空。");
        var header = lines[0].Split(',');
        if (header.Length < 8 || !header[0].Trim().Equals("Side", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CSV 首行必须为 Side,MatchKind,MatchValue,Series,DamagePercent,HitPercent,Enabled,Note。");
        var result = new List<StrategyJobModifierRule>();
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            var cells = SplitCsv(lines[index]);
            if (cells.Count < 8) throw new InvalidDataException($"第 {index + 1} 行列数不足。");
            if (!byte.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var side) || side > 1)
                throw new InvalidDataException($"第 {index + 1} 条 Side 必须为 0/1。");
            if (!byte.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind) || kind > 1)
                throw new InvalidDataException($"第 {index + 1} 条 MatchKind 必须为 0/1。");
            if (!byte.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var match))
                throw new InvalidDataException($"第 {index + 1} 条 MatchValue 无效。");
            if (!byte.TryParse(cells[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var series) || series > 39)
                throw new InvalidDataException($"第 {index + 1} 条 Series 必须为 0..39。");
            if (!sbyte.TryParse(cells[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var damage))
                throw new InvalidDataException($"第 {index + 1} 条 DamagePercent 无效。");
            if (!sbyte.TryParse(cells[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hit))
                throw new InvalidDataException($"第 {index + 1} 条 HitPercent 无效。");
            if (!bool.TryParse(cells[6], out var enabled) && cells[6] is not ("0" or "1"))
                throw new InvalidDataException($"第 {index + 1} 条 Enabled 无效。");
            if (cells[6] is "0" or "1") enabled = cells[6] == "1";
            var note = cells.Count > 7 ? cells[7] : string.Empty;
            result.Add(new StrategyJobModifierRule((StrategyModifierSide)side, (StrategyModifierMatchKind)kind,
                match, series, damage, hit, enabled, note));
        }
        if (result.Count > StrategyJobModifierRule.Capacity)
            throw new InvalidDataException($"规则超过 {StrategyJobModifierRule.Capacity} 条上限。");
        return result;
    }

    private static void ExportRules(string path, IReadOnlyList<StrategyJobModifierRule> rules)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Side,MatchKind,MatchValue,Series,DamagePercent,HitPercent,Enabled,Note");
        foreach (var rule in rules)
            builder.AppendLine(string.Join(',', (int)rule.Side, (int)rule.MatchKind, rule.MatchValue, rule.Series,
                rule.DamagePercent, rule.HitPercent, rule.Enabled ? 1 : 0, EscapeCsv(rule.Note)));
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (quoted)
            {
                if (ch == '"' && index + 1 < line.Length && line[index + 1] == '"') { builder.Append('"'); index++; }
                else if (ch == '"') quoted = false;
                else builder.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(builder.ToString()); builder.Clear(); }
            else builder.Append(ch);
        }
        result.Add(builder.ToString());
        return result;
    }
}
