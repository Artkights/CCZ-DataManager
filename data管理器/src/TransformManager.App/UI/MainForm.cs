namespace TransformManager.App;

public sealed class MainForm : Form
{
    private readonly ListView _list = new();
    private readonly DataGridView _ruleGrid = new();
    private readonly DataGridView _previewGrid = new();
    private readonly NumericUpDown _coverage = new();
    private readonly CheckBox _noCoverage = new();
    private readonly Label _extraInfo = new();
    private readonly ListBox _refs = new();
    private readonly Label _valueHint = new();
    private readonly ToolStripLabel _baseInfo = new();
    private readonly ToolStripLabel _status = new();
    private readonly GroupBox _extraGroup = new();
    private readonly ToolStripButton _write = new("写入");
    private readonly ToolStripButton _discard = new("放弃修改");
    private readonly Button _addStage = new();
    private readonly Button _removeStage = new();
    private SplitContainer? _split;
    private TableLayoutPanel? _rightLayout;
    private bool _splitterMoved;

    private TreasureTransformService? _service;
    private readonly Dictionary<int, EditState> _edits = new();
    private int _selectedId = -1;
    private bool _loading;
    private bool _dirty;

    private sealed record EditState(byte Coverage, List<RuleStage> Stages);

    public MainForm(string? initialRoot)
    {
        Text = "质变管理器";
        Font = new Font("Microsoft YaHei", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 720);
        Size = new Size(Math.Min(1024, area.Width - 24), Math.Min(720, area.Height - 24));
        MinimumSize = new Size(820, 560);
        KeyPreview = true;
        BuildUi();
        if (!string.IsNullOrWhiteSpace(initialRoot)) LoadBase(initialRoot);
        FormClosing += OnClosing;
    }

    private void BuildUi()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Font,
            Padding = new Padding(6, 4, 6, 4)
        };
        var choose = new ToolStripButton("选择基底文件夹");
        choose.Click += (_, _) => ChooseBase();
        var rescan = new ToolStripButton("重新扫描");
        rescan.Click += (_, _) => { if (_service != null) LoadBase(_service.Base.RootPath); };
        _write.Click += (_, _) => WriteAll();
        _discard.Click += (_, _) => DiscardCurrent();
        _baseInfo.AutoSize = true;
        _baseInfo.Text = "尚未选择基底。";
        _status.AutoSize = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.DimGray;
        _status.Text = "请选择基底文件夹。";
        strip.Items.AddRange(new ToolStripItem[]
        {
            choose, rescan, new ToolStripSeparator(), _write, _discard, new ToolStripSeparator(), _baseInfo, _status
        });

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        _split = split;
        split.SplitterMoved += (_, _) => _splitterMoved = true;
        Shown += (_, _) =>
        {
            try { split.SplitterDistance = Math.Max(240, (int)(split.Width * 0.27)); } catch { }
        };
        Resize += (_, _) =>
        {
            if (_splitterMoved || _split == null || _split.Width <= 0) return;
            try { _split.SplitterDistance = Math.Max(240, (int)(_split.Width * 0.27)); } catch { }
        };
        WindowState = FormWindowState.Maximized;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("特效号  名称  （引用）", 300);
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        split.Panel1.Controls.Add(_list);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(4)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        _rightLayout = layout;
        split.Panel2.Controls.Add(layout);

        var ruleGroup = new GroupBox
        {
            Text = "等级规则（例如 5 级→5、9 级→9；没填的等级按原版）",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        _ruleGrid.Dock = DockStyle.Fill;
        _ruleGrid.AllowUserToAddRows = true;
        _ruleGrid.AllowUserToDeleteRows = true;
        _ruleGrid.RowHeadersVisible = false;
        _ruleGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _ruleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "等级", FillWeight = 30 });
        _ruleGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "变成多少", FillWeight = 70 });
        _ruleGrid.CellValueChanged += (_, _) => { if (!_loading) { MarkDirty(); UpdatePreview(); } };
        _ruleGrid.UserDeletingRow += (_, _) => { if (!_loading) { MarkDirty(); UpdatePreview(); } };
        var ruleButtons = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        _addStage.Text = "添加一条";
        _addStage.Width = 90;
        _addStage.Top = 3;
        _addStage.Click += (_, _) => { _ruleGrid.Rows.Add(); MarkDirty(); };
        _removeStage.Text = "删除选中";
        _removeStage.Width = 90;
        _removeStage.Left = 98;
        _removeStage.Top = 3;
        _removeStage.Click += (_, _) =>
        {
            if (_ruleGrid.CurrentRow is { IsNewRow: false } row) { _ruleGrid.Rows.Remove(row); MarkDirty(); UpdatePreview(); }
        };
        ruleButtons.Controls.AddRange([_addStage, _removeStage]);
        ruleGroup.Controls.Add(_ruleGrid);
        ruleGroup.Controls.Add(ruleButtons);

        var valueGroup = new GroupBox
        {
            Text = "原版单值（没有规则时使用）",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        var valueLabel = new Label { Text = "特效值：", Left = 10, Top = 28, AutoSize = true };
        _coverage.Left = 90;
        _coverage.Top = 24;
        _coverage.Width = 80;
        _coverage.Minimum = 0;
        _coverage.Maximum = 254;
        _coverage.ValueChanged += (_, _) => { if (!_loading) { MarkDirty(); UpdatePreview(); } };
        _noCoverage.Text = "0xFE 不覆盖（用原公式）";
        _noCoverage.Left = 170;
        _noCoverage.Top = 26;
        _noCoverage.Width = 250;
        _noCoverage.CheckedChanged += (_, _) =>
        {
            _coverage.Enabled = !_noCoverage.Checked;
            if (!_loading) { MarkDirty(); UpdatePreview(); }
        };
        var valueHint = _valueHint;
        valueHint.Text = "这里填的是到达等级后直接替换的值。";
        valueHint.Left = 10;
        valueHint.Top = 58;
        valueHint.Width = 520;
        valueHint.ForeColor = Color.DimGray;
        valueGroup.Controls.AddRange([valueLabel, _coverage, _noCoverage, valueHint]);

        _extraGroup.Text = "6.6 后缀类型（只读；同时控制 ≥16 级 ×1.5）";
        _extraGroup.Dock = DockStyle.Fill;
        _extraGroup.Margin = new Padding(4);
        _extraGroup.Padding = new Padding(8);
        _extraInfo.Left = 10;
        _extraInfo.Top = 4;
        _extraInfo.Width = 880;
        _extraInfo.Height = 34;
        _extraInfo.ForeColor = Color.DimGray;
        var extraHint = new Label
        {
            Text = "该字节与成长门控共用：0–6 应用 ×1.5，≥7 跳过；修改会改变装备后缀显示，本工具不再写入。",
            Left = 10,
            Top = 40,
            Width = 900,
            ForeColor = Color.Firebrick
        };
        _extraGroup.Controls.AddRange([_extraInfo, extraHint]);

        var previewGroup = new GroupBox
        {
            Text = "效果预览",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        _previewGrid.Dock = DockStyle.Fill;
        _previewGrid.ReadOnly = true;
        _previewGrid.AllowUserToAddRows = false;
        _previewGrid.RowHeadersVisible = false;
        _previewGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "等级", FillWeight = 25 });
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "特效值", FillWeight = 35 });
        _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "来源", FillWeight = 40 });
        previewGroup.Controls.Add(_previewGrid);

        var refsGroup = new GroupBox
        {
            Text = "引用这个特效号的武器/防具（修改会影响它们全部）",
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8)
        };
        _refs.Dock = DockStyle.Fill;
        refsGroup.Controls.Add(_refs);

        layout.Controls.Add(ruleGroup, 0, 0);
        layout.Controls.Add(valueGroup, 0, 1);
        layout.Controls.Add(_extraGroup, 0, 2);
        layout.Controls.Add(previewGroup, 0, 3);
        layout.Controls.Add(refsGroup, 0, 4);

        Controls.Add(split);
        Controls.Add(strip);
        KeyDown += OnKeyDown;
    }

    private void ChooseBase()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含 Ekd5.exe 与 Data.e5 的基底文件夹" };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadBase(dialog.SelectedPath);
    }

    private void LoadBase(string root)
    {
        try
        {
            var keep = _selectedId;
            _service = TreasureTransformService.Load(root);
            _edits.Clear();
            _dirty = false;
            var game = _service.Base;
            _baseInfo.Text = $"已识别：{game.VersionText} ｜ {new DirectoryInfo(game.RootPath).Name}";
            string stageText = game.NativeStages.Count > 0
                ? $"原生变化段：{string.Join(" → ", game.NativeStages.Select(v => v + " 级"))}（只读）"
                : "原生变化段：未识别";
            _list.Items.Clear();
            foreach (var slot in _service.Slots)
                _list.Items.Add(new ListViewItem(slot.Display) { Tag = slot.Id });
            bool readable = game.CanEdit && _service.ContractErrors.Count == 0;
            bool rulesAvailable = readable && game.Cave != null;
            _write.Enabled = readable;
            _coverage.Enabled = readable && !_noCoverage.Checked;
            _noCoverage.Enabled = readable;
            _addStage.Enabled = rulesAvailable;
            _removeStage.Enabled = rulesAvailable;
            _ruleGrid.ReadOnly = !rulesAvailable;
            _extraGroup.Visible = game.Version == EngineVersion.V66;
            if (_rightLayout != null)
                _rightLayout.RowStyles[2].Height = game.Version == EngineVersion.V66 ? 100 : 0;
            _status.Text = stageText + "；只管理武器/防具（辅助装备/道具不参与质变）；" + (readable
                ? (rulesAvailable ? "可以修改，写入前会自动备份。" : "可以改原版单值；当前基底没有可用代码洞，等级规则不可用。")
                : (game.ReadOnlyReason.Length > 0 ? game.ReadOnlyReason : string.Join("；", _service.ContractErrors)));
            if (_list.Items.Count > 0)
            {
                int index = keep >= 0 ? _list.Items.Cast<ListViewItem>().ToList().FindIndex(item => (int)item.Tag! == keep) : 0;
                _list.Items[Math.Max(0, index)].Selected = true;
                _list.Select();
            }
        }
        catch (Exception ex)
        {
            Log("load failed: " + ex);
            _status.Text = "扫描失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Log(string message)
        => File.AppendAllText(Path.Combine(Path.GetTempPath(), "transform-ui.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");

    private EffectSlot? SelectedSlot => _service?.Slots.FirstOrDefault(slot => slot.Id == _selectedId);

    private void OnSelectionChanged()
    {
        if (_loading || _service == null) return;
        CommitCurrentUi();
        if (_list.SelectedItems.Count == 0) { _selectedId = -1; return; }
        _selectedId = (int)_list.SelectedItems[0].Tag!;
        LoadSlot();
    }

    private void LoadSlot()
    {
        var slot = SelectedSlot;
        if (slot == null) return;
        _loading = true;
        var edit = _edits.TryGetValue(slot.Id, out var state)
            ? state
            : new EditState(slot.Coverage, slot.Stages.ToList());
        _noCoverage.Checked = edit.Coverage == 0xFE;
        _coverage.Value = Math.Min(254, edit.Coverage == 0xFE ? 0 : edit.Coverage);
        _coverage.Enabled = _service!.Base.CanEdit && !_noCoverage.Checked;
        _extraInfo.Text = $"当前后缀类型：0x{slot.Extra:X2}  {SuffixLabel(slot.Extra)}\r\n" +
                          "0–6 应用 ×1.5，≥7 跳过；本工具只读，不会改动该字节。";
        _ruleGrid.Rows.Clear();
        foreach (var stage in edit.Stages.OrderByDescending(stage => stage.Level))
            _ruleGrid.Rows.Add(stage.Level, stage.Value);
        _refs.Items.Clear();
        foreach (var item in slot.Refs)
            _refs.Items.Add($"#{item.Id}  {item.Name} · {item.CategoryName}");
        if (slot.Refs.Count == 0) _refs.Items.Add("（没有武器/防具引用这个特效号）");
        _valueHint.Text = $"当前武器/防具初始特效值：{slot.Refs.FirstOrDefault()?.EffectValue ?? 0}；这里填的是到达等级后直接替换的值。";
        _loading = false;
        UpdatePreview();
    }

    private void CommitCurrentUi()
    {
        var slot = SelectedSlot;
        if (slot == null) return;
        var stages = ReadStages(out _);
        byte coverage = _noCoverage.Checked ? (byte)0xFE : (byte)_coverage.Value;
        _edits[slot.Id] = new EditState(coverage, stages);
    }

    private List<RuleStage> ReadStages(out string error)
    {
        error = "";
        var result = new List<RuleStage>();
        var levels = new HashSet<int>();
        foreach (DataGridViewRow row in _ruleGrid.Rows)
        {
            if (row.IsNewRow) continue;
            string levelText = row.Cells[0].Value?.ToString() ?? "";
            string valueText = row.Cells[1].Value?.ToString() ?? "";
            if (levelText.Length == 0 && valueText.Length == 0) continue;
            if (!int.TryParse(levelText, out int level) || level is < 1 or > 99)
            {
                error = "等级必须是 1–99 的整数。";
                return new List<RuleStage>();
            }
            if (!int.TryParse(valueText, out int value) || value is < 0 or > 255)
            {
                error = "数值必须是 0–255 的整数。";
                return new List<RuleStage>();
            }
            if (!levels.Add(level))
            {
                error = $"等级 {level} 重复了。";
                return new List<RuleStage>();
            }
            result.Add(new RuleStage(level, value));
        }
        if (result.Count > RuleCompiler.MaxStagesPerEffect)
        {
            error = $"一个特效号最多 {RuleCompiler.MaxStagesPerEffect} 条规则。";
            return new List<RuleStage>();
        }
        return result.OrderByDescending(stage => stage.Level).ToList();
    }

    private void UpdatePreview()
    {
        var slot = SelectedSlot;
        if (slot == null || _service == null) return;
        var stages = ReadStages(out string error);
        if (error.Length > 0)
        {
            _status.Text = error;
            _previewGrid.Rows.Clear();
            return;
        }
        byte coverage = _noCoverage.Checked ? (byte)0xFE : (byte)_coverage.Value;
        var rows = _service.Preview(slot.Id, stages, coverage, ExtraMode.Auto);
        _previewGrid.Rows.Clear();
        foreach (var row in rows)
        {
            int index = _previewGrid.Rows.Add(row.Level, row.Value, row.Source);
            if (row.Source == "规则") _previewGrid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(255, 244, 206);
        }
        _status.Text = "预览使用第一件引用道具的初始特效值作为基准。";
    }

    private void MarkDirty()
    {
        _dirty = true;
        _status.Text = "有未保存的修改。按 Ctrl+S 或点“写入”。";
    }

    private void DiscardCurrent()
    {
        if (_service == null || _selectedId < 0) return;
        _edits.Remove(_selectedId);
        var slot = SelectedSlot;
        if (slot != null)
        {
            var original = new EditState(slot.Coverage, slot.Stages.ToList());
            _edits[_selectedId] = original;
        }
        _dirty = false;
        LoadSlot();
        _status.Text = "已放弃当前特效号的修改。";
    }

    private void WriteAll()
    {
        if (_service == null) return;
        CommitCurrentUi();
        var edits = new List<SlotEdit>();
        foreach (var (effectId, state) in _edits)
        {
            var slot = _service.Find(effectId);
            if (slot == null) continue;
            bool coverageSame = state.Coverage == slot.Coverage;
            bool stagesSame = StagesEqual(state.Stages, slot.Stages);
            // 只提交真正变化的槽位，避免“点开看看再写入”顺带改写未编辑的字节。
            if (coverageSame && stagesSame) continue;
            edits.Add(new SlotEdit(effectId, coverageSame ? null : state.Coverage,
                stagesSame ? null : state.Stages));
        }
        if (edits.Count == 0)
        {
            _status.Text = "没有需要写入的修改。";
            return;
        }
        var outcome = _service.Apply(edits);
        MessageBox.Show(this, outcome.Message, outcome.Success ? "写入成功" : "写入失败",
            MessageBoxButtons.OK, outcome.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (outcome.Success)
        {
            _dirty = false;
            int keep = _selectedId;
            LoadBase(_service.Base.RootPath);
            _selectedId = keep;
        }
        _status.Text = outcome.Message;
    }

    private static bool StagesEqual(IReadOnlyList<RuleStage> left, IReadOnlyList<RuleStage> right)
        => left.Count == right.Count &&
           left.All(stage => right.Any(other => other.Level == stage.Level && other.Value == stage.Value));

    private static string SuffixLabel(byte value) => value switch
    {
        0x00 => "无后缀（显示 --）",
        0x01 => "+N（255 显示“杀”）",
        0x02 => "+N%",
        0x03 => "-N%",
        0x04 => "N%",
        0x05 => "N",
        0x06 => "N次",
        0x07 => "策略名",
        0x08 => "属性名",
        0x09 => "五属性组合",
        0x0A => "位掩码 0x3F",
        0x0B => "位掩码 0x1F",
        0x0C => "范围/形状名",
        0x0D => "阵型/范围名",
        0x0E => "地形名",
        0x0F => "攻击/策略/ALL",
        0x10 => "攻击/策略/ALL（补丁扩展）",
        0x11 => "策略名（补丁扩展）",
        0x13 => "名称解析（补丁扩展）",
        _ => $"保留值 0x{value:X2}"
    };

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S) { WriteAll(); e.Handled = true; }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty) return;
        var result = MessageBox.Show(this, "有未保存的修改，确定关闭吗？", "质变管理器",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) e.Cancel = true;
    }
}
