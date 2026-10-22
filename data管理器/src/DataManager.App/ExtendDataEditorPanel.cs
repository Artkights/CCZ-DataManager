using System.Globalization;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using CoreIdNameOption = DataManager.Core.Domain.IdNameOption;

namespace DataManager.App;

/// <summary>Edits the four independent extension files through the workspace transaction service.</summary>
public sealed partial class ExtendDataEditorPanel : UserControl
{
    private readonly ProjectWorkspace _workspace;
    private readonly ExtendDataService _service;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Name = "ExtendDataCategories" };
    private readonly Dictionary<ExtendDataCategory, CategoryState> _states = new();
    private List<FieldBinding> _fields => State.Fields;
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly ToolTip _tips = new();
    private Label _message => State.Message;
    private List<ForgeRow> _forgeRows => State.ForgeRows;
    private bool _binding, _flushing;
    private TextBox? _rawEvidence { get => State.RawEvidence; set => State.RawEvidence = value; }
    private TextBox? _materialCount { get => State.MaterialCount; set => State.MaterialCount = value; }

    public ExtendDataEditorPanel(ProjectWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _service = new ExtendDataService(workspace);
        Dock = DockStyle.Fill;
        Name = "ExtendDataEditor";
        AutoScaleMode = AutoScaleMode.Font;
        DoubleBuffered = true;
        _errors.ContainerControl = this;
        foreach (var category in Enum.GetValues<ExtendDataCategory>())
        {
            var page = new TabPage(CategoryLabel(category)) { Name = "ExtendCategory" + category, Padding = new Padding(6), Tag = category };
            _states[category] = new CategoryState(page);
            _tabs.TabPages.Add(page);
        }
        Controls.Add(_tabs);
        _tabs.Selecting += (_, args) =>
        {
            if (_binding) return;
            if (!Flush()) { args.Cancel = true; return; }
            RememberScroll();
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _tabs.SelectedTab?.Tag is not ExtendDataCategory category) return;
            Category = category;
            RenderCurrent();
            CategoryChanged?.Invoke(this, EventArgs.Empty);
        };
        RenderCurrent();
    }

    public event EventHandler? CategoryChanged;
    public event EventHandler? Edited;
    public event EventHandler? PendingChangesChanged;
    public TabControl DetailTabs => _tabs;
    public ExtendDataCategory Category { get; private set; } = ExtendDataCategory.Effects;
    public int CurrentId => State.Id;
    public bool HasPendingChanges => _fields.Any(field => field.IsPending);
    /// <summary>The shell owns the entity filter; its text is remembered independently for each category.</summary>
    public string SearchText { get => State.SearchText; set => State.SearchText = value ?? string.Empty; }
    private CategoryState State => _states[Category];

    public IReadOnlyList<IdNameOption> GetEntityOptions() => _service.GetEntityOptions(Category).Select(option => new IdNameOption(option.Id, option.Name)).ToArray();

    public bool BindEntity(int id)
    {
        if (!Flush()) return false;
        RememberScroll();
        State.Id = id;
        RenderCurrent();
        return true;
    }

    public bool Flush()
    {
        if (_binding || _flushing || !HasPendingChanges) return true;
        _flushing = true;
        try
        {
            var edits = new Dictionary<string, object?>(StringComparer.Ordinal);
            Control? invalid = null;
            foreach (var field in _fields.Where(field => field.IsPending))
            {
                try { edits[field.Name] = field.Read(); _errors.SetError(field.Control, string.Empty); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException or InvalidOperationException)
                {
                    _errors.SetError(field.Control, ex.Message);
                    invalid ??= field.Control;
                }
            }
            if (invalid != null)
            {
                SetMessage("输入未通过检查，已保留当前内容。请修正红色提示后切换或保存。", error: true);
                invalid.Focus();
                return false;
            }
            ApplySlotDefaults(edits);
            var before = _service.GetRaw(Category, CurrentId);
            _service.ApplyEdits(Category, CurrentId, edits, $"编辑{CategoryLabel(Category)} {CurrentId}");
            var changed = !before.AsSpan().SequenceEqual(_service.GetRaw(Category, CurrentId));
            _binding = true;
            try { foreach (var field in _fields) field.Reload(); }
            finally { _binding = false; }
            RefreshDiagnostics();
            if (changed) Edited?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            SetMessage(ex.Message, error: true);
            if (_fields.FirstOrDefault(field => field.IsPending) is { } first) _errors.SetError(first.Control, ex.Message);
            return false;
        }
        finally { _flushing = false; PendingChangesChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void DiscardPendingChanges()
    {
        _errors.Clear();
        RefreshFromWorkspace();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebinds after undo, save or reload without submitting obsolete editor text.</summary>
    public void RefreshFromWorkspace()
    {
        RenderCurrent();
    }

    public void ResetProject()
    {
        _binding = true;
        try
        {
            foreach (var state in _states.Values)
            {
                state.ClearView();
                state.Id = 0; state.Page = 0; state.SearchText = string.Empty; state.Advanced = false; state.EvidenceExpanded = false; state.ScrollPosition = Point.Empty;
            }
            Category = ExtendDataCategory.Effects; _tabs.SelectedIndex = 0;
        }
        finally { _binding = false; }
        RenderCurrent();
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderCurrent()
    {
        _binding = true;
        SuspendLayout();
        State.Tab.SuspendLayout();
        try
        {
            _errors.Clear();
            var status = _service.GetStatus(Category);
            _workspace.Documents.TryGetValue(ExtendDataService.TableKey(Category), out var source);
            if (State.Built && ReferenceEquals(source, State.SourceDocument) && status.Available == State.Available)
            {
                ReloadCurrent();
                return;
            }
            State.ClearView();
            State.SourceDocument = source;
            State.Available = status.Available;
            var scroll = new BufferedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Name = "ExtendDataScroll" };
            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(4), Name = "ExtendDataBody" };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(body); State.Tab.Controls.Add(scroll);
            State.Scroll = scroll;
            var viewState = State;
            scroll.Scroll += (_, _) => viewState.ScrollPosition = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y);
            void FitBodyWidth()
            {
                var width = Math.Max(1, scroll.ClientSize.Width);
                if (body.MinimumSize.Width == width && body.MaximumSize.Width == width) return;
                body.MinimumSize = new Size(width, 0);
                body.MaximumSize = new Size(width, 0);
                foreach (var grid in body.Controls.OfType<TableLayoutPanel>()) FitGridWidth(body, grid);
            }
            scroll.ClientSizeChanged += (_, _) => FitBodyWidth();
            FitBodyWidth();
            if (!status.Available)
            {
                AddBlock(body, new Label { Text = status.Message, AutoSize = true, MaximumSize = new Size(850, 0), Padding = new Padding(12), Name = "ExtendDataUnavailable" });
                State.Built = true;
                return;
            }
            var options = GetEntityOptions();
            if (!options.Any(option => option.Id == CurrentId)) State.Id = options.FirstOrDefault()?.Id ?? 0;
            var name = options.FirstOrDefault(option => option.Id == CurrentId)?.Name;
            State.Title = new Label { Text = $"{CurrentId}：{name ?? "未命名"}", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Padding = new Padding(2, 3, 2, 7), Name = "ExtendEntityTitle" };
            AddBlock(body, State.Title);
            AddBlock(body, new Label { Text = ".e5 是初始配置；已有 .EXS 存档可能沿用存档内数据。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 7), Name = "ExtendSaveDataHelp" });
            switch (Category)
            {
                case ExtendDataCategory.Effects: BuildEffects(body); break;
                case ExtendDataCategory.Growth: BuildGrowth(body); break;
                case ExtendDataCategory.Strategies: BuildStrategies(body); break;
                case ExtendDataCategory.Forge: BuildForge(body); break;
            }
            AddBlock(body, _message);
            BuildEvidence(body);
            State.Built = true;
            ReloadCurrent();
        }
        catch (Exception ex)
        {
            var label = new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "当前分类无法读取：" + ex.Message, Padding = new Padding(12), Name = "ExtendDataUnavailable" };
            State.Tab.Controls.Add(label);
        }
        finally
        {
            _binding = false;
            State.Tab.ResumeLayout(true);
            ResumeLayout(true);
            if (State.Scroll is { IsDisposed: false } scroll) scroll.AutoScrollPosition = State.ScrollPosition;
            // Keep the first text editor from receiving automatic focus when a
            // category is rebuilt; this also keeps native text visible in a
            // screenshot and lets users start from the category toolbar.
            if (!ContainsFocus) _tabs.Select();
        }
    }

    private void RememberScroll()
    {
        if (State.Scroll is { IsDisposed: false } scroll)
            State.ScrollPosition = new Point(-scroll.AutoScrollPosition.X, -scroll.AutoScrollPosition.Y);
    }

    private void ReloadCurrent()
    {
        if (!State.Available) return;
        var options = GetEntityOptions();
        if (!options.Any(option => option.Id == CurrentId)) State.Id = options.FirstOrDefault()?.Id ?? 0;
        if (State.Title != null) State.Title.Text = $"{CurrentId}：{options.FirstOrDefault(option => option.Id == CurrentId)?.Name ?? "未命名"}";
        foreach (var reload in State.Rebinders) reload();
        foreach (var field in _fields) field.Reload();
        RefreshDiagnostics();
    }

    private void BuildEffects(TableLayoutPanel body)
    {
        AddBlock(body, BuildPager(26, 10, 256));
        var grid = SlotGrid([52, 0, 90, 80, 95, 50]);
        var advanced = new CheckBox { AutoSize = true, Text = "显示经验与剩余回合", Checked = State.Advanced, Name = "ExtendEffectAdvanced", Margin = new Padding(3, 3, 3, 7) };
        advanced.CheckedChanged += (_, _) =>
        {
            if (_binding) return;
            if (!Flush()) { _binding = true; advanced.Checked = State.Advanced; _binding = false; return; }
            State.Advanced = advanced.Checked;
            UpdateAdvancedColumns();
        };
        AddBlock(body, advanced);
        AddBlock(body, new Label { Text = "效果值 / 经验：0–255；剩余回合 255 表示永久。", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(3, 0, 3, 5) });
        AddHeaders(grid, ["槽位", "特效（输入名称或 ID 搜索）", "效果值", "经验", "剩余回合", ""]);
        void UpdateAdvancedColumns()
        {
            grid.SuspendLayout();
            try
            {
                var scale = Font.Size / (SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).Size;
                grid.ColumnStyles[3].Width = State.Advanced ? 80 * scale : 0;
                grid.ColumnStyles[4].Width = State.Advanced ? 95 * scale : 0;
                foreach (Control control in grid.Controls)
                    if (grid.GetColumn(control) is 3 or 4) control.Visible = State.Advanced && (grid.GetRow(control) == 0 || State.Page * 10 + grid.GetRow(control) - 1 < 256);
            }
            finally { grid.ResumeLayout(true); }
        }
        var choices = _service.GetEffectOptions();
        for (var index = 0; index < 10; index++)
        {
            var visibleIndex = index;
            int Slot() => State.Page * 10 + visibleIndex;
            var row = index + 1;
            var label = SlotLabel(Slot());
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(IdInput(() => $"槽{Slot()}特效号", choices, 255, $"ExtendEffectId{index}", () => Slot() < 256), 1, row);
            grid.Controls.Add(NumberInput(() => $"槽{Slot()}特效值", 0, 255, $"ExtendEffectValue{index}", () => Slot() < 256), 2, row);
            grid.Controls.Add(NumberInput(() => $"槽{Slot()}经验", 0, 255, $"ExtendEffectExperience{index}", () => Slot() < 256), 3, row);
            var turns = NumberInput(() => $"槽{Slot()}剩余回合", 0, 255, $"ExtendEffectTurns{index}", () => Slot() < 256);
            _tips.SetToolTip(turns, "255 表示不按回合递减；其它原值按文件保留。");
            grid.Controls.Add(turns, 4, row);
            var clear = SmallButton("清空", $"ExtendEffectClear{index}");
            clear.Click += (_, _) => ClearEffect(Slot());
            grid.Controls.Add(clear, 5, row);
            State.Rebinders.Add(() =>
            {
                label.Text = (Slot() + 1).ToString("D3", CultureInfo.InvariantCulture);
                foreach (Control control in grid.Controls)
                    if (grid.GetRow(control) == row) control.Visible = Slot() < 256;
            });
        }
        State.Rebinders.Add(() => { advanced.Checked = State.Advanced; UpdateAdvancedColumns(); });
        AddBlock(body, grid);
    }

    private void BuildStrategies(TableLayoutPanel body)
    {
        AddBlock(body, BuildPager(4, 8, 32));
        AddBlock(body, new Label { Text = "次数 255 表示无限；已有次数原样保留，主动新增时默认 255。", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(3, 0, 3, 7) });
        var grid = SlotGrid([52, 0, 120, 50]);
        AddHeaders(grid, ["槽位", "策略（输入名称或 ID 搜索）", "次数 / 原始值", ""]);
        var choices = _service.GetStrategyOptions();
        for (var index = 0; index < 8; index++)
        {
            var visibleIndex = index;
            int Slot() => State.Page * 8 + visibleIndex;
            var label = SlotLabel(Slot());
            grid.Controls.Add(label, 0, index + 1);
            State.Rebinders.Add(() => label.Text = (Slot() + 1).ToString("D3", CultureInfo.InvariantCulture));
            grid.Controls.Add(IdInput(() => $"策略{Slot()}ID", choices, 255, $"ExtendStrategyId{index}"), 1, index + 1);
            var count = NumberInput(() => $"策略{Slot()}次数", 0, 255, $"ExtendStrategyCount{index}");
            _tips.SetToolTip(count, "新增策略默认 255；已有策略的原始次数不会因切换选择而重置。");
            grid.Controls.Add(count, 2, index + 1);
            var clear = SmallButton("清空", $"ExtendStrategyClear{index}");
            clear.Click += (_, _) =>
            {
                if (!Flush()) return;
                RunOperation(() => _service.SetStrategy(CurrentId, Slot(), 255));
            };
            grid.Controls.Add(clear, 3, index + 1);
        }
        AddBlock(body, grid);
    }

    private void BuildGrowth(TableLayoutPanel body)
    {
        AddBlock(body, new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "每个特效对应 8 个成长配置字节。编号 255 为文件尾部保留记录。", Padding = new Padding(2, 2, 2, 8) });
        var grid = SlotGrid([155, 0]);
        var labels = new[] { "允许成长（非零启用）", "吃书初始效果值", "效果值成长上限", "临时书持续回合", "成长书经验加成", "攻击经验", "受击经验", "击杀经验" };
        for (var i = 0; i < 8; i++)
        {
            grid.Controls.Add(new Label { Text = labels[i], AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 8, 7) }, 0, i);
            if (i == 0)
            {
                var enabled = new CheckBox { Name = "ExtendGrowthEnabled", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 14, 5), Enabled = CurrentId != 255 };
                void Load()
                {
                    var original = _service.ReadInt(Category, CurrentId, "Enabled");
                    enabled.Checked = original != 0;
                    enabled.Text = $"允许成长（原值 {original}）";
                    enabled.Enabled = CurrentId != 255;
                }
                Load();
                Register("Enabled", enabled, () => enabled.Checked.ToString(), () => enabled.Checked ? 1 : 0, Load);
                enabled.CheckedChanged += (_, _) => NotifyPending();
                grid.Controls.Add(enabled, 1, i);
                continue;
            }
            var input = NumberInput(i == 0 ? "Enabled" : $"Param{i}", 0, 255, "ExtendGrowth" + i);
            State.Rebinders.Add(() => input.Enabled = CurrentId != 255);
            grid.Controls.Add(input, 1, i);
        }
        AddBlock(body, grid);
    }

    private Control BuildPager(int pages, int pageSize, int total)
    {
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 5) };
        var previous = SmallButton("上一页", "ExtendPreviousPage");
        var next = SmallButton("下一页", "ExtendNextPage");
        var combo = new ComboBox { Name = "ExtendPage", DropDownStyle = ComboBoxStyle.DropDownList, Width = ScaleWidth(260), IntegralHeight = false, MaxDropDownItems = 15, Margin = new Padding(3) };
        State.Page = Math.Clamp(State.Page, 0, pages - 1);
        for (var page = 0; page < pages; page++) combo.Items.Add($"第 {page + 1} / {pages} 页    槽位 {page * pageSize + 1}–{Math.Min((page + 1) * pageSize, total)}");
        combo.SelectedIndex = State.Page;
        previous.Enabled = State.Page > 0; next.Enabled = State.Page < pages - 1;
        State.Rebinders.Add(() =>
        {
            combo.SelectedIndex = State.Page;
            previous.Enabled = State.Page > 0; next.Enabled = State.Page < pages - 1;
        });
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || combo.SelectedIndex == State.Page) return;
            if (!Flush()) { _binding = true; combo.SelectedIndex = State.Page; _binding = false; return; }
            State.Page = combo.SelectedIndex;
            RenderCurrent();
        };
        previous.Click += (_, _) => combo.SelectedIndex = Math.Max(0, State.Page - 1);
        next.Click += (_, _) => combo.SelectedIndex = Math.Min(pages - 1, State.Page + 1);
        toolbar.Controls.AddRange([previous, combo, next]);
        var compact = SmallButton("整理槽位", "ExtendCompact");
        _tips.SetToolTip(compact, "按现有顺序将非空槽前移，整条记录一起移动；一次撤销即可恢复。");
        compact.Click += (_, _) => { if (Flush()) RunOperation(() => _service.Compact(Category, CurrentId)); };
        toolbar.Controls.Add(compact);
        return toolbar;
    }

    private void ClearEffect(int slot)
    {
        if (Flush()) RunOperation(() => _service.SetEffect(CurrentId, slot, 255, 0));
    }

    private void RunOperation(Action action)
    {
        try { var before = _service.GetRaw(Category, CurrentId); action(); var changed = !before.AsSpan().SequenceEqual(_service.GetRaw(Category, CurrentId)); RenderCurrent(); if (changed) Edited?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { SetMessage(ex.Message, error: true); }
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private SearchableIdComboBox IdInput(string field, IReadOnlyList<CoreIdNameOption> options, long maximum, string name)
        => IdInput(() => field, options, maximum, name);

    private SearchableIdComboBox IdInput(Func<string> field, IReadOnlyList<CoreIdNameOption> options, long maximum, string name, Func<bool>? active = null)
    {
        var combo = new SearchableIdComboBox { Dock = DockStyle.Fill, Name = name, Margin = new Padding(4, 5, 14, 5), MinimumSize = new Size(120, 0) };
        var current = active?.Invoke() == false ? 255 : _service.ReadInt(Category, CurrentId, field());
        var choices = options.Select(option => new IdNameOption(option.Id, option.Name)).ToList();
        if (!choices.Any(option => option.Id == current)) choices.Add(new IdNameOption(current, maximum == ushort.MaxValue && (current & 255) == 255 ? "无（保留原值）" : "未确认"));
        combo.SetOptions(choices);
        string Display(int id)
        {
            if (!choices.Any(option => option.Id == id))
            {
                choices.Add(new IdNameOption(id, maximum == ushort.MaxValue && (id & 255) == 255 ? "无（保留原值）" : "未确认"));
                combo.SetOptions(choices);
            }
            return choices.First(option => option.Id == id).Display;
        }
        combo.SetBoundValue(Display(current));
        var binding = Register(field, combo, () => combo.Text, () =>
        {
            if (!combo.TryGetSelectedId(out var id) || id < 0 || id > maximum) throw new InvalidDataException($"请选择名称或输入 0–{maximum} 的 ID。");
            return id;
        }, () => combo.SetBoundValue(Display(_service.ReadInt(Category, CurrentId, field()))), active);
        combo.TextChanged += (_, _) => NotifyPending();
        combo.SelectionChangeCommitted += (_, _) => { if (!_binding) Flush(); };
        combo.UnknownValueSubmitted += (_, _) => { if (!_binding) Flush(); };
        combo.Validating += (_, args) => { if (!_binding && binding.IsPending) args.Cancel = !Flush(); };
        return combo;
    }

    private TextBox NumberInput(string field, long minimum, long maximum, string name)
        => NumberInput(() => field, minimum, maximum, name);

    private TextBox NumberInput(Func<string> field, long minimum, long maximum, string name, Func<bool>? active = null)
    {
        var box = new TextBox { Name = name, Dock = DockStyle.Fill, Margin = new Padding(4, 5, 14, 5) };
        void Load() => box.Text = _service.ReadLong(Category, CurrentId, field()).ToString(CultureInfo.InvariantCulture);
        if (active?.Invoke() != false) Load();
        Register(field, box, () => box.Text, () => ParseNumber(box.Text, minimum, maximum), Load, active);
        WireText(box);
        return box;
    }

    private static long ParseNumber(string text, long minimum, long maximum)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new InvalidDataException($"请输入 {minimum}–{maximum} 的整数。");
        return value;
    }

    private FieldBinding Register(string field, Control control, Func<string> text, Func<object?> read, Action load)
        => Register(() => field, control, text, read, load);

    private FieldBinding Register(Func<string> field, Control control, Func<string> text, Func<object?> read, Action load, Func<bool>? active = null)
    {
        var binding = new FieldBinding(field, control, text, read, load, active);
        _fields.Add(binding);
        return binding;
    }

    private void WireText(TextBox box)
    {
        box.TextChanged += (_, _) => NotifyPending();
        box.Validating += (_, args) => { if (!_binding && HasPendingChanges) args.Cancel = !Flush(); };
        box.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) { Flush(); args.Handled = true; args.SuppressKeyPress = true; } };
    }

    private void ApplySlotDefaults(Dictionary<string, object?> edits)
    {
        if (Category == ExtendDataCategory.Effects)
        {
            for (var slot = State.Page * 10; slot < Math.Min(256, State.Page * 10 + 10); slot++)
            {
                var idField = $"槽{slot}特效号";
                if (!edits.TryGetValue(idField, out var rawId)) continue;
                var id = Convert.ToInt32(rawId, CultureInfo.InvariantCulture);
                var old = _service.ReadInt(Category, CurrentId, idField);
                if (id == 255 && old != 255)
                {
                    edits[$"槽{slot}特效值"] = 0; edits[$"槽{slot}经验"] = 0; edits[$"槽{slot}剩余回合"] = 255;
                }
                else if (old == 255 && id != 255)
                {
                    edits.TryAdd($"槽{slot}经验", 0); edits.TryAdd($"槽{slot}剩余回合", 255);
                }
            }
        }
        else if (Category == ExtendDataCategory.Strategies)
        {
            for (var slot = State.Page * 8; slot < State.Page * 8 + 8; slot++)
            {
                var idField = $"策略{slot}ID";
                if (!edits.TryGetValue(idField, out var rawId)) continue;
                var id = Convert.ToInt32(rawId, CultureInfo.InvariantCulture);
                var old = _service.ReadInt(Category, CurrentId, idField);
                if (id == 255 && old != 255) edits[$"策略{slot}次数"] = 0;
                else if (old == 255 && id != 255) edits.TryAdd($"策略{slot}次数", 255);
            }
        }
    }

    private void BuildEvidence(TableLayoutPanel body)
    {
        var toggle = new CheckBox { Text = "查看原始字节、草稿与来源", AutoSize = true, Name = "ExtendEvidenceToggle", Margin = new Padding(3, 6, 3, 5), Checked = State.EvidenceExpanded };
        _rawEvidence = new TextBox { Name = "ExtendRawEvidence", Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Height = 130, Visible = State.EvidenceExpanded, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
        toggle.CheckedChanged += (_, _) =>
        {
            State.EvidenceExpanded = toggle.Checked;
            if (_rawEvidence != null) _rawEvidence.Visible = toggle.Checked;
            if (toggle.Checked && State.EvidenceDirty) RefreshEvidence();
        };
        AddBlock(body, toggle); AddBlock(body, _rawEvidence);
    }

    private void RefreshDiagnostics()
    {
        var diagnostics = _service.GetDiagnostics(Category, CurrentId);
        SetMessage(diagnostics.Count == 0 ? "编辑进入未保存草稿；使用顶部保存统一写入，撤销可恢复本次操作。" : string.Join("  ", diagnostics));
        State.EvidenceDirty = true;
        if (State.EvidenceExpanded) RefreshEvidence();
    }

    private void RefreshEvidence()
    {
        if (_rawEvidence == null) return;
        var document = _service.GetDocument(Category);
        var definition = document.Definition;
        var offset = definition.DataOffset + (long)(CurrentId - definition.BeginId) * definition.RowSize;
        var nameEvidence = Category is ExtendDataCategory.Effects or ExtendDataCategory.Growth ? "\r\n名称来源：" + _service.GetEffectNameSource().Message : string.Empty;
        static string Hex(byte[] bytes) => string.Join(Environment.NewLine, bytes.Chunk(32).Select(chunk => Convert.ToHexString(chunk)));
        _rawEvidence.Text = $"文件：{document.FilePath}\r\n表：{definition.Key}；布局：{_workspace.ActiveProfile.LayoutRevision}；记录宽度：{definition.RowSize} 字节\r\n物理偏移：0x{offset:X}（{offset}）\r\n状态：{_service.GetStatus(Category).Message}{nameEvidence}\r\n来源 SHA-256：{document.OriginalFileHash}\r\n原始：\r\n{Hex(_service.GetRaw(Category, CurrentId, false))}\r\n草稿：\r\n{Hex(_service.GetRaw(Category, CurrentId))}";
        State.EvidenceDirty = false;
    }

    private void SetMessage(string message, bool error = false) { _message.Text = message; _message.ForeColor = error ? Color.Firebrick : Color.DimGray; }
    private void NotifyPending() { if (!_binding) PendingChangesChanged?.Invoke(this, EventArgs.Empty); }
    private static Label SlotLabel(int slot) => new() { Text = (slot + 1).ToString("D3", CultureInfo.InvariantCulture), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(5, 7, 4, 7), Name = "ExtendSlot" + slot };
    private static Button SmallButton(string text, string name) => new() { Text = text, Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3), Padding = new Padding(2, 0, 2, 0) };
    private TableLayoutPanel SlotGrid(float[] widths)
    {
        var grid = new TableLayoutPanel { ColumnCount = widths.Length, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0), Name = "ExtendSlotGrid" };
        var scale = Font.Size / (SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).Size;
        foreach (var width in widths) grid.ColumnStyles.Add(new ColumnStyle(width == 0 ? SizeType.Percent : SizeType.Absolute, width == 0 ? 100 : width * scale));
        return grid;
    }
    private static void AddHeaders(TableLayoutPanel grid, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) grid.Controls.Add(new Label { Text = headers[i], AutoSize = true, ForeColor = Color.FromArgb(80, 89, 103), Anchor = AnchorStyles.Left, Margin = new Padding(4, 4, 4, 7) }, i, 0);
    }
    private int ScaleWidth(int width) => (int)Math.Ceiling(width * Font.Size / (SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).Size);
    private static void FitGridWidth(TableLayoutPanel body, TableLayoutPanel grid)
    {
        var width = Math.Max(1, body.ClientSize.Width - body.Padding.Horizontal - grid.Margin.Horizontal);
        grid.MinimumSize = new Size(width, 0);
        grid.MaximumSize = new Size(width, 0);
    }
    private static void AddBlock(TableLayoutPanel body, Control control)
    {
        var row = body.RowCount++;
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(control, 0, row);
        if (control is TableLayoutPanel grid) FitGridWidth(body, grid);
    }
    private static string CategoryLabel(ExtendDataCategory category) => category switch { ExtendDataCategory.Effects => "人物特效", ExtendDataCategory.Growth => "特效成长", ExtendDataCategory.Strategies => "策略学习", ExtendDataCategory.Forge => "锻造配方", _ => category.ToString() };

    private sealed class CategoryState(TabPage tab)
    {
        public TabPage Tab { get; } = tab;
        public int Id { get; set; }
        public int Page { get; set; }
        public string SearchText { get; set; } = string.Empty;
        public bool Advanced { get; set; }
        public bool Built { get; set; }
        public bool Available { get; set; }
        public TableDocument? SourceDocument { get; set; }
        public Panel? Scroll { get; set; }
        public Point ScrollPosition { get; set; }
        public Label? Title { get; set; }
        public List<FieldBinding> Fields { get; } = [];
        public List<Action> Rebinders { get; } = [];
        public List<ForgeRow> ForgeRows { get; } = [];
        public TextBox? RawEvidence { get; set; }
        public TextBox? MaterialCount { get; set; }
        public bool EvidenceExpanded { get; set; }
        public bool EvidenceDirty { get; set; } = true;
        public Label Message { get; private set; } = NewMessage();

        public void ClearView()
        {
            foreach (Control control in Tab.Controls.Cast<Control>().ToArray()) control.Dispose();
            if (!Message.IsDisposed) Message.Dispose();
            Message = NewMessage();
            Fields.Clear(); Rebinders.Clear(); ForgeRows.Clear();
            RawEvidence = null; MaterialCount = null; Scroll = null; Title = null;
            SourceDocument = null; Built = false; EvidenceDirty = true;
        }

        private static Label NewMessage() => new() { AutoSize = true, MaximumSize = new Size(1000, 0), ForeColor = Color.DimGray, Padding = new Padding(3, 5, 3, 5), Name = "ExtendDataMessage" };
    }
    private sealed class FieldBinding(Func<string> name, Control control, Func<string> text, Func<object?> read, Action load, Func<bool>? active)
    {
        private string _submitted = text();
        public string Name => name();
        public Control Control { get; } = control;
        public bool IsPending => active?.Invoke() != false && Control.Enabled && !string.Equals(text(), _submitted, StringComparison.Ordinal);
        public object? Read() => read();
        public void Reload()
        {
            if (active != null) Control.Enabled = active();
            if (active?.Invoke() != false) load();
            _submitted = text();
        }
    }
    private sealed class BufferedScrollPanel : Panel
    {
        public BufferedScrollPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _errors.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
