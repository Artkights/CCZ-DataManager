using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Images;
using ImageManager.App;

namespace DataManager.App;

/// <summary>
/// Fixed right-hand editor for a selected logical entity. Every binding keeps
/// its physical table, row and field, so edits use the workspace's validated,
/// undoable transaction rather than writing bytes from the UI.
/// </summary>
public sealed partial class BusinessEditorPanel : UserControl
{
    private static IReadOnlyList<string> EquipmentColumns => ItemTypeService.EquipmentColumns;
    private static readonly string[] TerrainNames =
    [
        "平原", "草原", "树林", "荒地", "山地", "岩山", "山崖", "雪原", "桥梁", "浅滩",
        "沼泽", "池塘", "小河", "大河", "栅栏", "城墙", "城内", "城门", "城池", "关隘",
        "鹿砦", "村庄", "兵营", "民居", "宝物库", "水池", "火", "船", "祭坛", "地下"
    ];
    private static readonly string[] StrategyTypes =
    [
        "火系", "水系", "地系", "风系", "眩晕", "诱惑", "谍报", "压迫/威吓", "咒骂/挑拨",
        "钝兵/钝队", "虚脱/厌战", "士气提升", "防御提升", "攻击提升", "爆发提升", "谎报",
        "中毒", "定身", "封咒", "补给", "MP补给", "觉醒", "回归", "天气", "八阵图", "四神",
        "霸气", "强行", "衰气", "疾行", "诅咒", "精妙", "连击", "伪报", "纵火", "修筑",
        "诱敌", "瞬移", "雷系", "撞心", "扩展策略A", "扩展策略B", "扩展策略C"
    ];
    private static readonly string[] AttackRanges = ["四格", "九宫", "近中程弓形", "中程弓形", "远程弓形", "原版少见", "没羽箭", "一转炮车", "二三转炮车", "一转弓骑", "全屏"];
    private static readonly string[] PierceRanges = ["不穿透", "十字穿透", "九宫穿透", "大没羽箭", "蛇矛", "长蛇矛/穿六", "大大没羽箭"];
    private readonly ProjectWorkspace _workspace;
    private readonly ResourceCatalogAdapter _resources;
    private readonly bool _ownsResources;
    private bool _releaseCompleted;
    private bool _disposingDetailPages;
    private readonly object _resourceOptionUiGate = new();
    private readonly Queue<Action> _resourceOptionUiUpdates = new();
    private bool _resourceOptionUiQueued;
    private int _resourceOptionUiHandleGeneration;
    private readonly BusinessDataService _business;
    private readonly SinglePageTabControl _tabs = new() { Dock = DockStyle.Fill, Multiline = true };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 2, 4, 2), Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 2, 4, 2), AutoEllipsis = true, Visible = false };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly ToolTip _diagnosticTips = new();
    private readonly LookupIndexCache _lookup = new();
    private readonly List<Func<bool>> _flushers = [];
    private readonly List<Action> _resourcePickerRefreshers = [];
    private readonly List<Image> _images = [];
    private readonly List<System.Windows.Forms.Timer> _previewTimers = [];
    private BusinessPageKind _kind;
    private int _id;
    private bool _binding;
    private int _editRevision;
    private Action? _refreshItemPermissions;
    private Action? _itemSemanticRefresh;
    private ExtendDataEditorPanel? _extendEditor;

    public BusinessEditorPanel(ProjectWorkspace workspace, ResourceCatalogAdapter? resources = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _ownsResources = resources == null;
        _resources = resources ?? new ResourceCatalogAdapter();
        _business = new BusinessDataService(workspace);
        Dock = DockStyle.Fill;
        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(_title);
        _errors.ContainerControl = this;
    }

    public event EventHandler? Edited;
    public event EventHandler? PendingChangesChanged;
    public event EventHandler? SaveRequested;
    public event EventHandler? ExtendCategoryChanged;
    public TabControl DetailTabs => _kind == BusinessPageKind.ExtendData && _extendEditor != null ? _extendEditor.DetailTabs : _tabs;
    public ExtendDataEditorPanel? ExtendEditor => _extendEditor;
    public IReadOnlyList<IdNameOption> ExtendEntityOptions => _extendEditor?.GetEntityOptions() ?? [];
    public BusinessPageKind CurrentKind => _kind;
    public int CurrentId => _kind == BusinessPageKind.ExtendData && _extendEditor != null ? _extendEditor.CurrentId : _id;

    public void Bind(BusinessPageKind kind, int id)
    {
        _binding = true;
        SuspendLayout();
        _tabs.SuspendLayout();
        try
        {
            _lookup.Prune(_workspace.Documents.Values);
            if (kind == BusinessPageKind.ExtendData && _kind == kind && _extendEditor is { Visible: true })
            {
                _extendEditor.BindEntity(id);
                return;
            }
            if (_extendEditor != null) _extendEditor.Visible = false;
            _tabs.Visible = true;
            _title.Visible = false;
            if (TryRebindCompactEntity(kind, id)) return;
            BeginPreviewBinding();
            _entityRebinders.Clear();
            _resourcePickerRefreshers.Clear();
            _compactLayoutSignature = null;
            _kind = kind;
            _id = id;
            _errors.Clear();
            _status.Visible = false;
            _refreshItemPermissions = null;
            _itemSemanticRefresh = null;
            _accessoryDraftRefresh = null;
            _flushers.Clear();
            _pendingChecks.Clear();
            foreach (var timer in _previewTimers) timer.Dispose();
            _previewTimers.Clear();
            foreach (var image in _images) image.Dispose();
            _images.Clear();
            DisposeDetailPages();
            var tableKeys = _business.GetAvailableTableKeys(kind);
            _compactTableKeys = tableKeys;
            var name = EntityName(kind, id);
            _title.Text = $"{id.ToString(CultureInfo.InvariantCulture)}：{(string.IsNullOrWhiteSpace(name) ? "未命名" : name)}";
            _status.Text = "编辑后进入未保存草稿；顶部保存会统一提交关联文件。";
            if (kind == BusinessPageKind.ExtendData)
            {
                _tabs.Visible = false;
                _title.Visible = false;
                if (_extendEditor == null)
                {
                    _extendEditor = new ExtendDataEditorPanel(_workspace) { Dock = DockStyle.Fill };
                    _extendEditor.Edited += (_, _) => OnEdited();
                    _extendEditor.PendingChangesChanged += (_, _) => PendingChangesChanged?.Invoke(this, EventArgs.Empty);
                    _extendEditor.CategoryChanged += (_, _) => ExtendCategoryChanged?.Invoke(this, EventArgs.Empty);
                    Controls.Add(_extendEditor);
                }
                _extendEditor.Visible = true;
                _extendEditor.BringToFront();
                _extendEditor.BindEntity(id);
                return;
            }
            if (BuildCompactEntity(kind, id)) { _compactLayoutKind = kind; _compactLayoutSignature = CompactLayoutSignature(kind, id); return; }
            foreach (var key in tableKeys)
            {
                if (kind == BusinessPageKind.DetailedJobs && key == "strategies" || kind == BusinessPageKind.Items && key == "detailed-job-growth") continue;
                if (key is "person-critical-quotes" or "person-retreat-quotes" or "person-critical-special-map" or "strategy-terrain-availability" or "job-series-attributes" or "job-series-accessory-groups" || key.StartsWith("item-effect-names", StringComparison.Ordinal)) continue;
                var document = _workspace.GetDocument(key);
                var match = FindRow(document, id);
                if (match == null) continue;
                if (key == "detailed-job-growth")
                {
                    AddGrowthTab(document, match.Value.Row, match.Value.Index);
                    AddEquipmentPermissionTab(document, match.Value.Row, match.Value.Index);
                }
                else if (key == "strategies")
                {
                    AddFieldTab(document, match.Value.Row, match.Value.Index, field => !IsLearningField(field.Name), "基本设定");
                    if (kind == BusinessPageKind.Strategies) AddStrategyLearningTab(document, match.Value.Row, match.Value.Index);
                }
                else AddFieldTab(document, match.Value.Row, match.Value.Index, null);
            }
            if (kind == BusinessPageKind.JobSeries) AddJobAttributesTab(id);
            if (kind == BusinessPageKind.Persons) AddQuoteTab(id);
            if (kind == BusinessPageKind.DetailedJobs) AddJobLearningTab(id);
            if (kind == BusinessPageKind.Items) AddItemEquipmentTab();
            if (kind == BusinessPageKind.Strategies) AddTerrainTab();
            if (kind is BusinessPageKind.Persons or BusinessPageKind.DetailedJobs or BusinessPageKind.Items or BusinessPageKind.Strategies) AddPreviewTab();
            if (_tabs.TabPages.Count == 0) AddNoticeTab("数据", "当前实体没有可读取的数据。请查看扫描诊断中的表能力与文件状态。");
        }
        finally { _binding = false; _tabs.RefreshHeader(); _tabs.ResumeLayout(true); ResumeLayout(true); }
    }

    /// <summary>Validates and commits pending text/grid values before a save or selection change.</summary>
    public bool Flush()
    {
        if (_extendEditor != null && !_extendEditor.Flush()) return false;
        foreach (var flusher in _flushers.ToArray())
            if (!flusher()) { PendingChangesChanged?.Invoke(this, EventArgs.Empty); return false; }
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool PreparePendingEdits() => Flush();

    private void AddFieldTab(TableDocument document, DataRow row, int rowIndex, Func<TableFieldDefinition, bool>? include, string? title = null)
    {
        var tab = NewTab(title ?? document.Definition.DisplayName);
        var layout = FormLayout();
        tab.Controls.Add(layout);
        AddEvidence(layout, document);
        foreach (var field in document.Definition.Fields)
        {
            if (include != null && !include(field)) continue;
            if (_kind == BusinessPageKind.Persons && document.Definition.Key == "persons" && field.Name is "Army" or "暴击台词" or "级别" or "经验") continue;
            var label = ItemFieldLabel(document, rowIndex, field.Name) ?? FieldLabel(document.Definition.Key, field.Name);
            var editor = CreateEditor(document, row, rowIndex, field);
            AddFormRow(layout, label, editor);
        }
    }

    private Control CreateEditor(TableDocument document, DataRow row, int rowIndex, TableFieldDefinition field)
    {
        var editable = document.Definition.IsWriteSupported && field.Policy == FieldPolicy.Editable;
        var current = ReadFieldValue(document, rowIndex, field);
        var lastSubmitted = Convert.ToString(current, CultureInfo.InvariantCulture) ?? string.Empty;
        var originalKey = document.Definition.Key;
        var originalFieldName = field.Name;
        var originalPhysicalRow = rowIndex;
        var active = true;
        void Register(Control control, Action load)
        {
            _entityRebinders.Add(() =>
            {
                var selected = ResolveEditorBinding(originalKey, originalFieldName, originalPhysicalRow);
                active = selected != null;
                if (selected == null) { control.Enabled = false; return; }
                document = selected.Document; row = selected.Row; rowIndex = selected.RowIndex; field = selected.Field;
                editable = document.Definition.IsWriteSupported && field.Policy == FieldPolicy.Editable;
                current = ReadFieldValue(document, rowIndex, field);
                lastSubmitted = Convert.ToString(current, CultureInfo.InvariantCulture) ?? string.Empty;
                load();
            });
        }
        bool Commit(Control control, object? value)
        {
            if (_binding || !editable || !active) return true;
            var submitted = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (submitted == lastSubmitted) return true;
            try
            {
                var applied = _workspace.ApplyCompositeTransaction($"编辑 {_title.Text} / {field.Name}", [new(document.Definition.Key, rowIndex, field.Name, value)]);
                lastSubmitted = submitted;
                _errors.SetError(control, string.Empty);
                if (applied.Applied)
                {
                    OnEdited();
                    if (_kind == BusinessPageKind.Items && field.Name is "宝物类型" or "装备特效号" &&
                        IsHandleCreated && CompactLayoutSignature(_kind, _id) != _compactLayoutSignature)
                    {
                        var editedId = _id;
                        BeginInvoke(() => { if (!IsDisposed && _kind == BusinessPageKind.Items && _id == editedId) Bind(_kind, editedId); });
                    }
                    if (field.Name is "头像" or "R形象ID" or "S形象ID" or "兵种" or "图标" or "策略图标" or "动画ID") RefreshCompactPreviews();
                    if (field.Name == "兵种") foreach (var refresh in _resourcePickerRefreshers) refresh();
                }
                return true;
            }
            catch (Exception ex) { ShowEditorError(control, ex.Message); return false; }
        }
        if (field.Kind == FieldKind.FixedString || field.Kind == FieldKind.RawBytes)
        {
            if (field.Kind == FieldKind.RawBytes && field.Name is "头像" or "图标" or "策略图标")
            {
                var raw = new TextBox { Text = NumericValue(current ?? string.Empty, 0, true).ToString(CultureInfo.InvariantCulture), Dock = DockStyle.Fill, ReadOnly = true };
                Register(raw, () => { raw.Enabled = true; raw.Text = NumericValue(current ?? string.Empty, 0, true).ToString(CultureInfo.InvariantCulture); });
                return raw;
            }
            if (field.Kind == FieldKind.RawBytes)
            {
                var raw = new TextBox { Dock = DockStyle.Fill, Text = Convert.ToString(current, CultureInfo.InvariantCulture), ReadOnly = !editable };
                bool SaveRaw() => Commit(raw, raw.Text);
                raw.Validating += (_, args) => args.Cancel = !SaveRaw(); _flushers.Add(SaveRaw);
                raw.TextChanged += (_, _) => NotifyPendingChanges();
                _pendingChecks.Add(() => active && editable && raw.Text != lastSubmitted);
                Register(raw, () => { raw.ReadOnly = !editable; raw.Text = Convert.ToString(current, CultureInfo.InvariantCulture); });
                return raw;
            }
            var text = new ByteTextEditor { Dock = DockStyle.Fill, Height = field.Size >= 100 ? 132 : 48 };
            var box = text.Editor;
            box.ReadOnly = !editable; box.Multiline = field.Size >= 100;
            box.ScrollBars = box.Multiline ? ScrollBars.Vertical : ScrollBars.None;
            text.CompactBudget = !box.Multiline;
            text.Bind(ConstraintForRow(document, rowIndex, field), Convert.ToString(current, CultureInfo.InvariantCulture));
            bool SaveText()
            {
                if (!active || !editable || !box.InputGuard.HasPendingChanges) return true;
                if (!box.InputGuard.Prepare()) { ShowEditorError(box, box.InputGuard.RejectionMessage ?? "文本无法保存。"); return false; }
                if (!Commit(box, box.Text)) return false;
                box.InputGuard.MarkSubmitted(); return true;
            }
            box.Validating += (_, args) => args.Cancel = !SaveText();
            box.InputGuard.StateChanged += (_, _) => { if (!_binding) NotifyPendingChanges(); };
            box.InputGuard.InputRejected += (_, _) => ShowEditorError(box, box.InputGuard.RejectionMessage ?? "本次输入已拒绝。", focus: false);
            _pendingChecks.Add(() => active && editable && box.InputGuard.HasPendingChanges);
            _flushers.Add(SaveText);
            Register(text, () => { text.Enabled = true; box.ReadOnly = !editable; text.Bind(ConstraintForRow(document, rowIndex, field), Convert.ToString(current, CultureInfo.InvariantCulture)); });
            return text;
        }
        var flags = GetFlags(document.Definition.Key, field.Name);
        if (flags != null)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            var original = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
            var checkboxes = flags.Select((name, bit) => new CheckBox { Text = name, AutoSize = true, Margin = new Padding(2, 1, 2, 1), Checked = (original & (1 << bit)) != 0, Enabled = editable }).ToArray();
            for (var bit = 0; bit < checkboxes.Length; bit++)
            {
                var check = checkboxes[bit];
                panel.Controls.Add(check);
                _diagnosticTips.SetToolTip(check, $"位标记 0x{1 << bit:X2}");
                check.CheckedChanged += (_, _) => Commit(panel, checkboxes.Select((item, bit) => item.Checked ? 1 << bit : 0).Sum());
            }
            Register(panel, () =>
            {
                panel.Enabled = true;
                var value = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
                for (var bit = 0; bit < checkboxes.Length; bit++) { checkboxes[bit].Enabled = editable; checkboxes[bit].Checked = (value & (1 << bit)) != 0; }
            });
            return panel;
        }
        if (field.Name == "宝物图鉴" || EquipmentColumns.Contains(field.Name))
        {
            var check = new CheckBox { AutoSize = true, Checked = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture) != 0, Enabled = editable, Text = "启用" };
            check.CheckedChanged += (_, _) => Commit(check, check.Checked ? 1 : 0);
            Register(check, () => { check.Enabled = editable; check.Checked = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture) != 0; });
            return check;
        }
        var options = GetOptions(document, rowIndex, field);
        if (options != null)
        {
            var idOnly = ResourceKindForField(document.Definition.Key, field.Name) is
                ResourceBindingKind.Portrait or ResourceBindingKind.CharacterR or ResourceBindingKind.CharacterS;
            var combo = new SearchableIdComboBox { Dock = DockStyle.Fill, Enabled = editable, IdOnly = idOnly };
            combo.SetOptions(options);
            var isRangeField = field.Name is "攻击范围" or "施法范围" or "穿透" or "穿透范围";
            RangePreviewPicker? rangePicker = isRangeField ? new RangePreviewPicker(combo) : null;
            if (rangePicker != null) rangePicker.FieldName = field.Name;
            CancellationTokenSource? optionCancellation = null;
            string? previewContext = null;
            combo.Disposed += (_, _) => { optionCancellation?.Cancel(); optionCancellation?.Dispose(); optionCancellation = null; };
            void LoadResourceOptions(ResourceBindingKind kind, int job, int turn)
            {
                optionCancellation?.Cancel(); optionCancellation?.Dispose();
                var request = optionCancellation = new CancellationTokenSource();
                var token = request.Token;
                var rootPath = _workspace.RootPath;
                var resourceRevision = _resources.Revision;
                var beforeText = combo.Text;
                bool CanApply() => !_releaseCompleted && !_disposingDetailPages && !IsDisposed && !Disposing &&
                    !combo.IsDisposed && !combo.Disposing && combo.FindForm() is not { Disposing: true } &&
                    !token.IsCancellationRequested && ReferenceEquals(optionCancellation, request) &&
                    rootPath == _workspace.RootPath && resourceRevision == _resources.Revision &&
                    _workspace.Documents.TryGetValue(document.Definition.Key, out var currentDocument) &&
                    ReferenceEquals(document, currentDocument);
                _ = ReadOptionsAsync();
                async Task ReadOptionsAsync()
                {
                    try
                    {
                        // A host can close/reopen forms without an installed
                        // WinForms SynchronizationContext. Only read resources
                        // here; every control access is explicitly marshalled.
                        var values = await _resources.GetIdOptionsAsync(rootPath, kind, job, turn, token).ConfigureAwait(false);
                        PostResourceOptionUpdate(() =>
                        {
                            if (!CanApply()) return;
                            var text = combo.Text;
                            combo.SetOptions(values.Where(value => value.Id >= field.Minimum && value.Id <= field.Maximum)
                                .Select(value => new IdNameOption(value.Id, value.Name)), preserveEditSelection: true);
                            combo.SetBoundValue(text, preserveEditSelection: true);
                            if (text == beforeText && combo.TryGetSelectedId(out var selectedId))
                                combo.SetBoundValue(combo.Options.FirstOrDefault(value => value.Id == selectedId) is { } option
                                    ? combo.GetDisplayText(option) : text,
                                    preserveEditSelection: true);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        PostResourceOptionUpdate(() =>
                        {
                            if (CanApply()) _diagnosticTips.SetToolTip(combo, "资源列表暂不可用：" + ex.Message);
                        });
                    }
                }
            }
            void SetPreviewLoader(Func<int, CancellationToken, Task<Bitmap?>> loader)
            {
                // Keep the owner-drawn popup thumbnails on the searchable combo
                // and use the same decoder for the closed range preview.
                combo.SetPreviewLoader(loader);
                rangePicker?.SetPreviewLoader(loader);
            }
            void RefreshRangeLoader()
            {
                if (ResourceKindForField(document.Definition.Key, field.Name) is { } resourceKind)
                {
                    var job = resourceKind == ResourceBindingKind.CharacterS ? EntityNumber("persons", "兵种") : 0;
                    var turn = resourceKind == ResourceBindingKind.CharacterS && job <= 59 ? job % 3 + 1 : 1;
                    var context = $"{_workspace.RootPath}|{resourceKind}|{job}|{turn}|{_resources.Revision}";
                    if (previewContext == context) return;
                    previewContext = context;
                    if (!idOnly)
                        SetPreviewLoader((id, token) => _resources.DecodeBindingThumbnailAsync(_workspace.RootPath,
                            resourceKind, id, new Size(48, 48), job, turn, token));
                    LoadResourceOptions(resourceKind, job, turn);
                    return;
                }
                if (field.Name is not ("攻击范围" or "施法范围" or "穿透" or "穿透范围")) return;
                var attack = field.Name is "攻击范围" or "施法范围";
                var rangeContext = $"{_workspace.RootPath}|{attack}|{_resources.Revision}";
                if (previewContext == rangeContext) return;
                previewContext = rangeContext;
                SetPreviewLoader((id, token) => _resources.DecodeRangeFirstFrameAsync(_workspace.RootPath, attack, id, new Size(48, 48), token));
            }
            var number = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
            var selected = options.FirstOrDefault(item => item.Id == number);
            string EquipmentDisplay(int value, IReadOnlyList<IdNameOption> choices)
                => idOnly ? value.ToString(CultureInfo.InvariantCulture) : choices.FirstOrDefault(item => item.Id == value)?.Display ??
                    (document.Definition.Key == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue
                        ? $"{value}：原值（类别不匹配）" : $"{value}：未确认");
            combo.SetBoundValue(EquipmentDisplay(number, options));
            RefreshRangeLoader();
            if (ResourceKindForField(document.Definition.Key, field.Name) != null) _resourcePickerRefreshers.Add(RefreshRangeLoader);
            bool SaveCombo()
            {
                if (!combo.TryGetSelectedId(out var id))
                {
                    _errors.SetError(combo, "请输入数字 ID，或从列表中选择。");
                    return false;
                }
                return Commit(combo, id);
            }
            combo.SelectionChangeCommitted += (_, _) => SaveCombo();
            combo.UnknownValueSubmitted += (_, _) => SaveCombo();
            combo.Validating += (_, args) => args.Cancel = !SaveCombo();
            _flushers.Add(SaveCombo);
            combo.TextChanged += (_, _) => NotifyPendingChanges();
            _pendingChecks.Add(() => active && editable && (!combo.TryGetSelectedId(out var selectedId) || selectedId.ToString(CultureInfo.InvariantCulture) != lastSubmitted));
            var editorControl = (Control?)rangePicker ?? combo;
            Register(editorControl, () =>
            {
                editorControl.Enabled = true;
                options = ResourceKindForField(document.Definition.Key, field.Name) != null && previewContext != null
                    ? combo.Options : GetOptions(document, rowIndex, field) ?? [];
                combo.SetOptions(options); combo.Enabled = editable;
                // A resource edit keeps the fixed entity controls alive. Clear
                // only this combo's thumbnails so its next draw uses the new file.
                number = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
                selected = options.FirstOrDefault(item => item.Id == number);
                combo.SetBoundValue(EquipmentDisplay(number, options));
                RefreshRangeLoader();
                rangePicker?.RefreshPreview();
            });
            if (document.Definition.Key is "items-low" or "items-high" or "items-extended" && field.Name == "宝物类型")
                RegisterEquipmentTypeBinding(combo, () =>
                {
                    var selectedId = combo.TryGetSelectedId(out var currentId) ? currentId : Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
                    var refreshed = GetOptions(document, rowIndex, field) ?? [];
                    combo.SetOptions(refreshed);
                    combo.SetBoundValue(refreshed.FirstOrDefault(option => option.Id == selectedId)?.Display ?? $"{selectedId}：未确认");
                });
            return editorControl;
        }
        var numeric = new NumericUpDown { Width = 180, Minimum = Math.Min(field.Minimum, Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture)), Maximum = Math.Max(field.Maximum, Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture)), Value = Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture), Enabled = editable, ThousandsSeparator = false };
        numeric.ValueChanged += (_, _) => Commit(numeric, field.Kind == FieldKind.UInt32 ? (object)(long)numeric.Value : (int)numeric.Value);
        _flushers.Add(() => Commit(numeric, field.Kind == FieldKind.UInt32 ? (object)(long)numeric.Value : (int)numeric.Value));
        Register(numeric, () =>
        {
            var value = Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture);
            numeric.Minimum = Math.Min(field.Minimum, value); numeric.Maximum = Math.Max(field.Maximum, value);
            numeric.Value = value; numeric.Enabled = editable;
        });
        if (document.Definition.Key is "items-low" or "items-high" or "items-extended" && field.Name == "价格（/100）")
        {
            var pricePanel = new Panel { Width = 300, Height = numeric.PreferredSize.Height };
            var priceHint = new Label { Dock = DockStyle.Fill, Text = "255表示不可买卖", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0), AutoEllipsis = true };
            numeric.Dock = DockStyle.Left; numeric.Width = 88;
            pricePanel.Controls.Add(priceHint); pricePanel.Controls.Add(numeric);
            _diagnosticTips.SetToolTip(numeric, "价格单位为100；255表示不可买卖。");
            _diagnosticTips.SetToolTip(priceHint, "255表示不可买卖");
            return pricePanel;
        }
        if (document.Definition.Key == "persons" && field.Name is "初始HP" or "初始MP")
        {
            var limit = field.Name == "初始HP" ? 500 : 250;
            var hint = new Label { Dock = DockStyle.Right, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(3, 0, 0, 0) };
            var host = new Panel { Height = numeric.PreferredSize.Height, Width = 180 };
            numeric.Dock = DockStyle.Fill;
            host.Controls.Add(numeric); host.Controls.Add(hint);
            void RefreshRuntimeHint()
            {
                var verified = RuntimeConsumersVerified();
                var exceeds = numeric.Value > limit;
                hint.Text = verified ? exceeds ? $"→{limit}" : $"≤{limit}" : $"参{limit}";
                hint.Width = TextRenderer.MeasureText(hint.Text, hint.Font).Width + 7;
                hint.ForeColor = exceeds ? Color.DarkOrange : Color.DimGray;
                var evidence = verified ? "已验证：当前引擎与本地核对的消费者一致。" : "参考策略：当前引擎尚未匹配消费者证据，实际行为需在目标游戏确认。";
                var description = $"磁盘可保存范围 {field.Minimum}～{field.Maximum}。本地核对的引擎读取后将 {field.Name} 超过 {limit} 的值按 {limit} 处理。当前保存值 {numeric.Value}。{evidence}";
                _diagnosticTips.SetToolTip(hint, description); _diagnosticTips.SetToolTip(numeric, description);
            }
            numeric.ValueChanged += (_, _) => RefreshRuntimeHint(); hint.FontChanged += (_, _) => RefreshRuntimeHint();
            _entityRebinders.Add(RefreshRuntimeHint); RefreshRuntimeHint();
            return host;
        }
        return numeric;
    }

    private void AddEquipmentPermissionTab(TableDocument document, DataRow row, int rowIndex)
    {
        var tab = NewTab("装备许可");
        var layout = FormLayout();
        tab.Controls.Add(layout);
        AddEvidence(layout, document);
        foreach (var name in EquipmentColumns)
        {
            var field = document.Definition.Fields.FirstOrDefault(item => item.Name == name);
            if (field != null) AddFormRow(layout, name, CreateEditor(document, row, rowIndex, field));
        }
    }

    private void AddGrowthTab(TableDocument document, DataRow row, int rowIndex)
    {
        var tab = NewTab("兵种成长");
        var data = new DataTable();
        var fields = document.Definition.Fields.Where(field => !EquipmentColumns.Contains(field.Name) && field.Name != "攻击范围").ToArray();
        foreach (var field in fields) data.Columns.Add(field.Name, typeof(int));
        data.Rows.Add(fields.Select(field => ReadFieldValue(document, rowIndex, field)).ToArray());
        var bindings = fields.ToDictionary(field => (0, field.Name), field => field.Name);
        var grid = MappedGrid(document, rowIndex, data, bindings);
        grid.Dock = DockStyle.Top; grid.Height = 92; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        var range = document.Definition.Fields.FirstOrDefault(field => field.Name == "攻击范围");
        var layout = FormLayout(); layout.Dock = DockStyle.Top;
        AddEvidence(layout, document);
        if (range != null) AddFormRow(layout, "攻击范围", CreateEditor(document, row, rowIndex, range));
        tab.Controls.Add(layout); tab.Controls.Add(grid);
    }

    private DataGridView MappedGrid(TableDocument document, int physicalRow, DataTable data, IReadOnlyDictionary<(int Row, string Column), string> bindings)
    {
        var grid = GridFor(data); grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = !document.Definition.IsWriteSupported || !bindings.Keys.Any(key => key.Column == column.DataPropertyName);
        var submittedValues = bindings.ToDictionary(binding => binding.Key, binding => Convert.ToString(data.Rows[binding.Key.Row][binding.Key.Column], CultureInfo.InvariantCulture));
        bool CommitGrid()
        {
            if (_binding || !document.Definition.IsWriteSupported) return true;
            if (!grid.EndEdit()) return false;
            try
            {
                var edits = bindings.Where(binding => submittedValues[binding.Key] != Convert.ToString(data.Rows[binding.Key.Row][binding.Key.Column], CultureInfo.InvariantCulture))
                    .Select(binding => new CompositeCellEdit(document.Definition.Key, physicalRow, binding.Value, data.Rows[binding.Key.Row][binding.Key.Column])).ToArray();
                if (edits.Length == 0) return true;
                if (_workspace.ApplyCompositeTransaction($"编辑 {_title.Text} / {document.Definition.DisplayName}", edits).Applied) OnEdited();
                foreach (var binding in bindings) submittedValues[binding.Key] = Convert.ToString(data.Rows[binding.Key.Row][binding.Key.Column], CultureInfo.InvariantCulture);
                _errors.SetError(grid, string.Empty); return true;
            }
            catch (Exception ex) { ShowEditorError(grid, ex.Message); return false; }
        }
        grid.CellValidating += (_, args) =>
        {
            if (_binding || args.RowIndex < 0 || !bindings.TryGetValue((args.RowIndex, grid.Columns[args.ColumnIndex].DataPropertyName), out var field)) return;
            try { _workspace.ValidateCellValue(document, physicalRow, field, args.FormattedValue); grid.Rows[args.RowIndex].Cells[args.ColumnIndex].ErrorText = string.Empty; }
            catch (Exception ex) { args.Cancel = true; grid.Rows[args.RowIndex].Cells[args.ColumnIndex].ErrorText = ex.Message; ShowEditorError(grid, ex.Message, false); }
        };
        grid.CellEndEdit += (_, _) => CommitGrid();
        grid.CurrentCellDirtyStateChanged += (_, _) => NotifyPendingChanges();
        grid.EditingControlShowing += (_, args) =>
        {
            if (args.Control is TextBox box) { box.TextChanged -= PendingGridTextChanged; box.TextChanged += PendingGridTextChanged; }
        };
        void PendingGridTextChanged(object? sender, EventArgs args) => NotifyPendingChanges();
        _pendingChecks.Add(() => grid.IsCurrentCellDirty || bindings.Any(binding => submittedValues[binding.Key] != Convert.ToString(data.Rows[binding.Key.Row][binding.Key.Column], CultureInfo.InvariantCulture)));
        _flushers.Add(CommitGrid);
        return grid;
    }

    private void AddAccessoryGroupsOnly() => AddJobAttributesTab(_id, true);

    private void AddJobAttributesTab(int jobSeriesId, bool groupsOnly = false)
    {
        if (!groupsOnly && _workspace.Documents.TryGetValue("job-series-attributes", out var attributes))
        {
            var field = attributes.Definition.Fields.FirstOrDefault(candidate => candidate.Name == $"兵种系 {jobSeriesId}");
            if (field != null)
            {
                var tab = NewTab("兵种属性"); var layout = FormLayout(); tab.Controls.Add(layout); AddEvidence(layout, attributes);
                for (var index = 0; index < Math.Min(JobAttributeNames.Length, attributes.Data.Rows.Count); index++)
                    AddFormRow(layout, JobAttributeNames[index], CreateEditor(attributes, attributes.Data.Rows[index], index, field));
            }
        }
        AddAccessoryGroupEditor();
    }

    private void AddQuoteTab(int personId)
    {
        var tab = NewTab("暴击/撤退台词");
        BuildQuoteEditor(tab, personId);
    }

    private void AddItemEquipmentTab()
    {
        if (!_workspace.Documents.TryGetValue("detailed-job-growth", out var growth)) return;
            var item = new[] { "items-low", "items-high", "items-extended" }.Where(_workspace.Documents.ContainsKey)
            .Select(key => _workspace.GetDocument(key)).Select(document => (document, match: FindRow(document, _id))).FirstOrDefault(item => item.match != null);
        if (item.match == null) return;
        var typeField = item.document.Definition.Fields.FirstOrDefault(field => field.Name == "宝物类型");
        if (typeField != null)
        {
            var typeTab = NewTab("装备类型"); var typeLayout = FormLayout(); typeTab.Controls.Add(typeLayout);
            AddFormRow(typeLayout, ItemFieldLabel(item.document, item.match.Value.Index, typeField.Name) ?? "装备类型", CreateEditor(item.document, item.match.Value.Row, item.match.Value.Index, typeField));
            AddFormRow(typeLayout, "类型语义", Notice(ItemTypeService.Resolve(item.document, item.match.Value.Index).Description));
            AddFormRow(typeLayout, "共享许可", Notice("普通武器/防具按类型码直接关联许可槽；辅助/道具不使用此许可。修改类型后进入可装备部队子页会按当前草稿刷新。"));
        }
        var tab = NewTab("可装备部队");
        string? loadedSignature = null;
        void RenderPermissions()
        {
            var semantics = ItemTypeService.Resolve(item.document, item.match.Value.Index);
            var signature = $"{semantics.Category}|{semantics.TypeValue}|{semantics.PermissionColumn}";
            if (loadedSignature == signature) return;
            loadedSignature = signature;
            foreach (var control in tab.Controls.Cast<Control>().ToArray()) control.Dispose();
            tab.Controls.Clear();
            if (semantics.PermissionColumn is not { } column)
            {
                tab.Controls.Add(Notice($"当前物品为{semantics.CategoryName}，没有对应的 26 类装备许可。{semantics.Description}"));
                return;
            }
            var field = growth.Definition.Fields.FirstOrDefault(candidate => candidate.Name == column);
            if (field == null) return;
            var layout = FormLayout(); tab.Controls.Add(layout);
            AddFormRow(layout, "装备类型", new Label { Text = $"{semantics.TypeValue}：{column}（同类型宝物共享许可）", AutoSize = true });
            foreach (DataRow row in growth.Data.Rows)
            {
                var id = Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture);
                AddFormRow(layout, $"{id}：{NameOf("detailed-jobs", id)}", CreateEditor(growth, row, growth.Data.Rows.IndexOf(row), field));
            }
        }
        RenderPermissions();
        _refreshItemPermissions = RenderPermissions;
        tab.VisibleChanged += (_, _) => { if (tab.Visible) RenderPermissions(); };
    }

    private void AddJobLearningTab(int jobId, Control? host = null)
    {
        if (!_workspace.Documents.TryGetValue("strategies", out var strategies)) return;
        var fieldName = $"兵种{jobId:D2}学习等级";
        if (!strategies.Data.Columns.Contains(fieldName)) return;
        var tab = host ?? NewTab("习得策略");
        var table = new DataTable(); table.Columns.Add("策略ID", typeof(int)); table.Columns.Add("策略名称"); table.Columns.Add("习得等级", typeof(int));
        foreach (DataRow row in strategies.Data.Rows)
        {
            var level = Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture);
            if (level > 0) table.Rows.Add(row["ID"], row["名称"], level);
        }
        var grid = GridFor(table); grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.Columns["策略ID"].ReadOnly = true; grid.Columns["策略名称"].ReadOnly = true;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, WrapContents = true };
        var select = new SearchableIdComboBox { Width = 185 }; select.SetOptions(OptionsFor("strategies"));
        var levelEditor = new NumericUpDown { Minimum = 1, Maximum = 255, Value = 1, Width = 54 };
        var add = new Button { Text = "新增/更新", AutoSize = true }; var remove = new Button { Text = "删除", AutoSize = true };
        bar.Controls.Add(select); bar.Controls.Add(levelEditor); bar.Controls.Add(add); bar.Controls.Add(remove);
        tab.Controls.Add(grid); tab.Controls.Add(bar);
        string LearningSignature() => string.Join("\u001f", table.Rows.Cast<DataRow>().Where(row => row.RowState != DataRowState.Deleted).Select(row => $"{row["策略ID"]}:{row["习得等级"]}"));
        var lastSubmitted = LearningSignature();
        bool SaveTable()
        {
            if (_binding) return true;
            if (!grid.EndEdit()) return false;
            var signature = LearningSignature();
            if (signature == lastSubmitted) return true;
            try
            {
                var lookup = new Dictionary<int, int>();
                foreach (DataRow row in table.Rows)
                {
                    if (row.RowState == DataRowState.Deleted) continue;
                    var id = Convert.ToInt32(row["策略ID"], CultureInfo.InvariantCulture);
                    var level = Convert.ToInt32(row["习得等级"], CultureInfo.InvariantCulture);
                    if (level is < 1 or > 255) throw new InvalidDataException("习得等级范围为 1..255；删除项目会写回 0。");
                    if (!lookup.TryAdd(id, level)) throw new InvalidDataException($"重复策略 ID：{id}");
                }
                var edits = strategies.Data.Rows.Cast<DataRow>().Select((row, index) => new CompositeCellEdit("strategies", index, fieldName, lookup.GetValueOrDefault(Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture), 0))).ToArray();
                if (_workspace.ApplyCompositeTransaction($"编辑兵种 {jobId} 习得策略", edits).Applied) OnEdited();
                lastSubmitted = signature;
                _errors.SetError(grid, string.Empty); return true;
            }
            catch (Exception ex) { ShowEditorError(grid, ex.Message); return false; }
        }
        add.Click += (_, _) =>
        {
            if (!select.TryGetSelectedId(out var id) || FindRow(strategies, id) == null) { ShowEditorError(select, "请选择当前项目存在的策略 ID。"); return; }
            var row = table.Rows.Cast<DataRow>().FirstOrDefault(item => Convert.ToInt32(item["策略ID"], CultureInfo.InvariantCulture) == id);
            if (row == null) table.Rows.Add(id, NameOf("strategies", id), (int)levelEditor.Value);
            else row["习得等级"] = (int)levelEditor.Value;
            SaveTable();
        };
        remove.Click += (_, _) =>
        {
            foreach (var selected in grid.SelectedRows.Cast<DataGridViewRow>().ToArray()) if (selected.DataBoundItem is DataRowView view) table.Rows.Remove(view.Row);
            SaveTable();
        };
        grid.CellEndEdit += (_, _) => SaveTable();
        grid.CurrentCellDirtyStateChanged += (_, _) => NotifyPendingChanges();
        grid.EditingControlShowing += (_, args) =>
        {
            if (args.Control is TextBox box) { box.TextChanged -= PendingLearningTextChanged; box.TextChanged += PendingLearningTextChanged; }
        };
        void PendingLearningTextChanged(object? sender, EventArgs args) => NotifyPendingChanges();
        _pendingChecks.Add(() => grid.IsCurrentCellDirty || LearningSignature() != lastSubmitted);
        _flushers.Add(SaveTable);
        _entityRebinders.Add(() =>
        {
            jobId = _id;
            fieldName = $"兵种{jobId:D2}学习等级";
            table.BeginLoadData();
            try
            {
                table.Clear();
                if (strategies.Data.Columns.Contains(fieldName))
                    foreach (DataRow row in strategies.Data.Rows)
                    {
                        var level = Convert.ToInt32(row[fieldName], CultureInfo.InvariantCulture);
                        if (level > 0) table.Rows.Add(row["ID"], row["名称"], level);
                    }
            }
            finally { table.EndLoadData(); }
            select.SetOptions(OptionsFor("strategies")); select.SetBoundValue(string.Empty); levelEditor.Value = 1;
            lastSubmitted = LearningSignature();
        });
    }

    private void AddStrategyLearningTab(TableDocument strategies, DataRow strategy, int rowIndex)
    {
        var tab = NewTab("兵种学习等级"); var layout = FormLayout(); tab.Controls.Add(layout);
        foreach (var field in strategies.Definition.Fields.Where(field => IsLearningField(field.Name)))
        {
            var match = Regex.Match(field.Name, @"兵种(\d+)学习等级");
            var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            AddFormRow(layout, $"{id}：{NameOf("detailed-jobs", id)}", CreateEditor(strategies, strategy, rowIndex, field));
        }
    }

    private void AddTerrainTab()
    {
        var tab = NewTab("施展地形");
        if (!_workspace.Documents.TryGetValue("strategy-terrain-availability", out var terrain))
        {
            tab.Controls.Add(Notice("当前 Profile 未提供可读取的施展地形布局证据。")); return;
        }
        var layout = FormLayout(); tab.Controls.Add(layout); AddEvidence(layout, terrain);
        AddFormRow(layout, "类型掩码", new Label { Text = "全局地形设置：火/水/风/地四类策略共享；不按策略 ID 存储。", AutoSize = true });
        for (var index = 0; index < terrain.Data.Rows.Count; index++)
        {
            var field = terrain.Definition.Fields[0];
            AddFormRow(layout, index < TerrainNames.Length ? $"{index}：{TerrainNames[index]}" : index.ToString(CultureInfo.InvariantCulture), CreateEditor(terrain, terrain.Data.Rows[index], index, field));
        }
    }

    private void AddPreviewTab()
    {
        var tab = NewTab("关联预览");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(12) };
        tab.Controls.Add(flow);
        var loadedRevision = -1;
        tab.Enter += (_, _) =>
        {
            if (loadedRevision == _editRevision) return;
            foreach (var timer in _previewTimers) timer.Dispose();
            _previewTimers.Clear();
            foreach (var control in flow.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (var image in _images) image.Dispose();
            _images.Clear();
            loadedRevision = _editRevision;
            LoadPreview();
        };
        void LoadPreview()
        {
        void Preview(string caption, string key, int logicalId)
        {
            var group = new GroupBox { Text = $"{caption} / {logicalId}", Width = 270, Height = 250, Margin = new Padding(8) };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(242, 244, 248) };
            group.Controls.Add(picture); flow.Controls.Add(group);
            try
            {
                var image = _resources.DecodeFirstFrame(_workspace.RootPath, key, logicalId, new Size(240, 210));
                if (image == null) group.Controls.Add(new Label { Text = $"未找到 {key} 的对应资源", Dock = DockStyle.Bottom, Height = 42 });
                else { picture.Image = image; _images.Add(image); }
            }
            catch (Exception ex) { group.Controls.Add(new Label { Text = ex.Message, Dock = DockStyle.Bottom, Height = 55, AutoEllipsis = true }); }
        }
        void Animated(string caption, Func<ResourcePreviewAnimation> decode)
        {
            var group = new GroupBox { Text = caption, Width = 300, Height = 290, Margin = new Padding(8) };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(242, 244, 248) };
            var description = new Label { Dock = DockStyle.Bottom, Height = 62, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter };
            var toggle = new Button { Dock = DockStyle.Bottom, Height = 27, Text = "暂停" };
            group.Controls.Add(picture); group.Controls.Add(description); group.Controls.Add(toggle); flow.Controls.Add(group);
            try
            {
                var animation = decode();
                _images.AddRange(animation.Frames);
                description.Text = animation.Mapping + $"；{animation.Frames.Count} 帧";
                if (animation.Frames.Count == 0) { toggle.Enabled = false; return; }
                if (animation.Frames.Count == 1) { toggle.Enabled = false; toggle.Text = "第一帧"; }
                picture.Image = animation.Frames[0];
                var index = 0;
                var timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(animation.IntervalMs, 40, 2000), Enabled = animation.Frames.Count > 1 };
                _previewTimers.Add(timer);
                timer.Tick += (_, _) => { if (_tabs.SelectedTab == tab) { index = (index + 1) % animation.Frames.Count; picture.Image = animation.Frames[index]; } };
                toggle.Click += (_, _) => { timer.Enabled = !timer.Enabled; toggle.Text = timer.Enabled ? "暂停" : "播放"; };
            }
            catch (Exception ex) { description.Text = ex.Message; toggle.Enabled = false; }
        }
        void Icon(string caption, string key, int logicalId)
        {
            var group = new GroupBox { Text = $"{caption} / {logicalId}", Width = 270, Height = 250, Margin = new Padding(8) };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(242, 244, 248) };
            group.Controls.Add(picture); flow.Controls.Add(group);
            try
            {
                var frames = _resources.DecodeFrames(_workspace.RootPath, key, logicalId);
                _images.AddRange(frames);
                picture.Image = frames.LastOrDefault();
                if (frames.Count == 0) group.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 40, Text = $"未找到 {key} 对应图标" });
            }
            catch (Exception ex) { group.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 40, Text = ex.Message }); }
        }
        int Value(string key, string field, int fallback = 0)
        {
            if (!_workspace.Documents.TryGetValue(key, out var document)) return fallback;
            var match = FindRow(document, _id);
            var definition = document.Definition.Fields.FirstOrDefault(item => item.Name == field);
            return match == null || definition == null ? fallback : NumericValue(ReadFieldValue(document, match.Value.Index, definition) ?? string.Empty, fallback, definition.Kind == FieldKind.RawBytes);
        }
        if (_kind == BusinessPageKind.Persons)
        {
            var rVerified = _workspace.Documents.TryGetValue("person-r-assign", out var rAssign) && rAssign.Definition.Capability == TableCapability.Editable && FindRow(rAssign, _id) != null;
            var sVerified = _workspace.Documents.TryGetValue("person-s-assign", out var sAssign) && sAssign.Definition.Capability == TableCapability.Editable && FindRow(sAssign, _id) != null;
            if (rVerified) Preview("R形象", "Pmapobj.e5", Value("person-r-assign", "R形象ID"));
            else flow.Controls.Add(Notice("人物 R 指定未提供已验证布局证据，无法确认关联资源。"));
            var job = Value("persons", "兵种");
            if (sVerified) Animated("S形象", () => _resources.DecodeCharacterS(_workspace.RootPath, Value("person-s-assign", "S形象ID"), job));
            else
            {
                flow.Controls.Add(Notice("人物 S 指定未提供已验证布局证据，以下仅为默认兵种参考。"));
                Animated("默认兵种参考", () => _resources.DecodeCharacterS(_workspace.RootPath, 0, job));
            }
            Preview("头像", "Face", Value("persons", "头像"));
        }
        else if (_kind == BusinessPageKind.DetailedJobs)
        {
            flow.FlowDirection = FlowDirection.TopDown; flow.WrapContents = false;
            for (var faction = 0; faction < 3; faction++)
            {
                var slot = faction;
                Animated($"{new[] { "我军", "友军", "敌军" }[slot]} · S_{_id * 3 + slot + 1:000}",
                    () => _resources.DecodeJobSFirstFrame(_workspace.RootPath, _id, slot));
            }
        }
        else if (_kind == BusinessPageKind.Items)
        {
            var itemKey = new[] { "items-low", "items-high", "items-extended" }.FirstOrDefault(key => _workspace.Documents.TryGetValue(key, out var document) && FindRow(document, _id) != null);
            var key = HasResource("Item.e5") ? "Item.e5" : "Itemicon.dll";
            if (itemKey != null) Icon("宝物图标", key, Value(itemKey, "图标"));
        }
        else if (_kind == BusinessPageKind.Strategies)
        {
            Icon("策略图标", HasResource("Mtem.e5") ? "Mtem.e5" : "Mgcicon.dll", Value("strategies", "策略图标"));
            var small = Value("strategy-small-animation", "动画ID", 255);
            var large = Value("strategy-big-animation", "动画ID", 255);
            Animated("小动画", () => _resources.DecodeStrategyAnimation(_workspace.RootPath, false, small));
            Animated("大动画", () => _resources.DecodeStrategyAnimation(_workspace.RootPath, true, large));
        }
        flow.Controls.Add(new Label { Text = "资源新增、替换、删除在图片设定页完成。关联预览按当前字段 ID 读取。", Width = 560, Height = 44, Margin = new Padding(10) });
        }
    }

    private bool HasResource(string key)
    {
        try { return _resources.Scan(_workspace.RootPath).Resources.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private IReadOnlyList<IdNameOption>? GetOptions(TableDocument document, int rowIndex, TableFieldDefinition field)
    {
        var tableKey = document.Definition.Key;
        if (ResourceKindForField(tableKey, field.Name) != null) return [];
        if (tableKey == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue)
            return PersonEquipmentService.GetOptions(_workspace.Documents, field.Name, field.Minimum, field.Maximum)
                .Select(option => new IdNameOption(option.Id, option.Name)).ToArray();
        if (tableKey == "job-series-attributes") return JobAttributeOptions(rowIndex);
        if (tableKey is "items-low" or "items-high" or "items-extended")
        {
            var semantics = ItemTypeService.Resolve(document, rowIndex);
            if (semantics.Category == ItemCategory.Accessory && field.Name == "能力成长")
                return OptionsFor("job-series").Where(option => option.Id < 255).Concat([new IdNameOption(255, "所有兵种")]).ToArray();
            var legacyAuxiliary = !semantics.IsCategoryMarker && semantics.Category is not (ItemCategory.Weapon or ItemCategory.Armor);
            var effectField = semantics.IsCategoryMarker || semantics.Category is ItemCategory.Weapon or ItemCategory.Armor
                ? "装备特效号" : "宝物类型";
            if (field.Name == effectField && semantics.Category == ItemCategory.Consumable)
                return ConsumableEffectCatalog.GetAll().Select(effect => new IdNameOption(effect.Id, effect.Name)).ToArray();
            if (field.Name == "装备特效号" && legacyAuxiliary)
                return [new(2, "辅助装备类别"), new(3, "道具/消耗品类别"), new(0, "预留/未指定"), new(102, "预留"), new(255, "空/保留")];
            if (field.Name == "宝物类型")
            {
                if (legacyAuxiliary)
                {
                    try
                    {
                        return ItemEffectOptions();
                    }
                    catch { }
                }
                return ItemTypeService.GetOptions(semantics, _workspace).Select(option => new IdNameOption(option.Id, option.Name)).ToArray();
            }
        }
        if (field.Name == "装备特效号")
        {
            return ItemEffectOptions();
        }
        if (!string.IsNullOrWhiteSpace(field.ReferenceTable))
        {
            var options = OptionsFor(field.ReferenceTable);
            return options;
        }
        if (field.Name == "策略类型") return StrategyTypes.Select((name, id) => new IdNameOption(id, name)).ToArray();
        if (field.Name == "施展对象") return [new(0, "敌方"), new(1, "我方"), new(2, "全屏/天气"), new(5, "全屏己方气合"), new(6, "全屏对方反气合")];
        if (field.Name is "攻击范围" or "施法范围") return AttackRanges.Select((name, id) => new IdNameOption(id, name)).ToArray();
        if (field.Name is "穿透" or "穿透范围") return PierceRanges.Select((name, id) => new IdNameOption(id, name)).ToArray();
        return null;
    }

    private static ResourceBindingKind? ResourceKindForField(string tableKey, string fieldName) => (tableKey, fieldName) switch
    {
        ("person-r-assign", "R形象ID") => ResourceBindingKind.CharacterR,
        ("person-s-assign", "S形象ID") => ResourceBindingKind.CharacterS,
        ("persons", "头像") => ResourceBindingKind.Portrait,
        ("items-low" or "items-high" or "items-extended", "图标") => ResourceBindingKind.ItemIcon,
        _ => null
    };

    private string? ItemFieldLabel(TableDocument document, int rowIndex, string field)
    {
        if (document.Definition.Key is not ("items-low" or "items-high" or "items-extended")) return null;
        var semantics = ItemTypeService.Resolve(document, rowIndex);
        if (field == "宝物图鉴") return "宝物图鉴";
        if (field == "初始能力") return semantics.Category switch
        {
            ItemCategory.Accessory => "特殊效果值", ItemCategory.Consumable => "效果参数（低字节）", _ => "初始属性"
        };
        if (field == "装备特效值") return semantics.Category == ItemCategory.Consumable ? "效果参数（高字节）" : "特殊效果值";
        if (field == "能力成长" && semantics.Category == ItemCategory.Accessory) return "适用兵种";
        if (field is not ("宝物类型" or "装备特效号")) return null;
        if (semantics.IsCategoryMarker) return field == "宝物类型" ? "物品类别" : "实际特效号";
        if (semantics.Category is ItemCategory.Weapon or ItemCategory.Armor) return null;
        return field == "宝物类型" ? "实际特效号（类型字节）" : "类别标记（特效字节）";
    }

    private IReadOnlyList<IdNameOption> OptionsFor(string tableKey)
        => _lookup.ReferenceOptions(tableKey, _workspace.Documents);

    private string NameOf(string key, int id)
        => _workspace.Documents.TryGetValue(key, out var document) && FindRow(document, id) is { } match && document.Data.Columns.Contains("名称")
            ? Convert.ToString(match.Row["名称"], CultureInfo.InvariantCulture) ?? string.Empty : "未确认";

    private (DataRow Row, int Index)? FindRow(TableDocument document, int id) => _lookup.Find(document, id);

    private static object? ReadFieldValue(TableDocument document, int rowIndex, TableFieldDefinition field)
    {
        if (document.Data.Columns.Contains(field.Name))
        {
            var value = document.Data.Rows[rowIndex][field.Name];
            return value == DBNull.Value ? null : value;
        }
        // Preserve fields intentionally have no editable DataColumn. Their
        // original bytes still provide the image IDs/level/experience shown
        // in the business view, without making those fields writable.
        var offset = checked((int)(document.Definition.DataOffset + (long)rowIndex * document.Definition.RowSize));
        return BinaryValueCodec.Decode(document.OriginalFileBytes.AsSpan(offset, document.Definition.RowSize), field);
    }

    private static int NumericValue(object value, int fallback, bool raw = false)
    {
        if (value is int number) return number;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!raw && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        var hex = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length is > 0 and <= 8 && hex.Length % 2 == 0)
        {
            try { var bytes = Convert.FromHexString(hex); return bytes.Select((item, index) => item << (8 * index)).Sum(); }
            catch (FormatException) { }
        }
        return fallback;
    }

    private static string[]? GetFlags(string key, string field)
        => key switch
        {
            "strategy-learning" => ["四系", "降能力", "妨碍", "补给", "升能力", "气候", "绝", "四神"],
            "strategy-extension" => ["策略模仿", "辅助穿透", "保留1", "保留2", "保留3", "保留4", "保留5", "保留6"],
            "strategy-terrain-availability" => ["火", "水", "风", "地"],
            _ => null
        };

    private static string FieldLabel(string key, string field)
        => field == "内容" ? key switch
        {
            "strategy-damage-type" => "是否伤血",
            "strategy-damage-ratio" => "伤害系数",
            "strategy-hit-rate" => "命中上限",
            "strategy-learning" => "效果索引",
            "strategy-battle-ai" => "战场AI策略",
            "strategy-training-ai" => "练武场AI策略",
            "strategy-extension" => "策略扩展标记",
            _ => field
        } : field == "移动力成长" ? "移动成长" : field;

    private static bool IsLearningField(string field) => Regex.IsMatch(field, @"^兵种\d+学习等级$");
    private TabPage NewTab(string title) { var tab = new TabPage(title) { Padding = new Padding(8), AutoScroll = true }; _tabs.TabPages.Add(tab); return tab; }
    private void AddNoticeTab(string title, string message) => NewTab(title).Controls.Add(Notice(message));
    private static Label Notice(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 105, Padding = new Padding(8), AutoEllipsis = true };
    private static TableLayoutPanel FormLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(2), GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return layout;
    }
    private static void AddFormRow(TableLayoutPanel layout, string text, Control editor)
    {
        var index = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(132, 0), Margin = new Padding(1, 1, 2, 1) }, 0, index);
        editor.Margin = new Padding(1, 0, 4, 0); layout.Controls.Add(editor, 1, index);
    }
    private void AddEvidence(TableLayoutPanel layout, TableDocument document)
    {
        if (document.Definition.IsWriteSupported) return;
        var label = new Label { Text = "只读：当前布局未开放写入", AutoSize = true, ForeColor = Color.DarkOrange, Margin = new Padding(3) };
        _diagnosticTips.SetToolTip(label, document.Definition.CapabilityEvidence);
        var index = layout.RowCount++; layout.Controls.Add(label, 0, index); layout.SetColumnSpan(label, 2);
    }
    private static DataGridView GridFor(DataTable table)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, BindingContext = new BindingContext(), DataSource = table, AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AllowUserToResizeRows = false };
        SelectionAppearance.Configure(grid);
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.DataError += (_, args) => { args.ThrowException = false; args.Cancel = true; };
        return grid;
    }
    private void OnEdited() { _editRevision++; _refreshItemPermissions?.Invoke(); _itemSemanticRefresh?.Invoke(); _status.Visible = false; _status.Text = "修改已加入草稿；请使用顶部保存写回。"; Edited?.Invoke(this, EventArgs.Empty); }
    private void PostResourceOptionUpdate(Action update)
    {
        lock (_resourceOptionUiGate)
        {
            if (_releaseCompleted) return;
            _resourceOptionUiUpdates.Enqueue(update);
        }
        QueueResourceOptionUpdates();
    }
    private void QueueResourceOptionUpdates()
    {
        int handleGeneration;
        lock (_resourceOptionUiGate)
        {
            if (_releaseCompleted || IsDisposed || Disposing || !IsHandleCreated ||
                _resourceOptionUiQueued || _resourceOptionUiUpdates.Count == 0) return;
            _resourceOptionUiQueued = true;
            handleGeneration = _resourceOptionUiHandleGeneration;
        }
        try { BeginInvoke((Action)(() => ApplyResourceOptionUpdates(handleGeneration))); }
        catch (InvalidOperationException)
        {
            // The owner HWND can disappear between the check and BeginInvoke.
            // Retain pending results for its next HandleCreated, never create a
            // handle on the worker or enter a control's disposal path there.
            lock (_resourceOptionUiGate)
                if (handleGeneration == _resourceOptionUiHandleGeneration) _resourceOptionUiQueued = false;
        }
    }
    private void ApplyResourceOptionUpdates(int handleGeneration)
    {
        Action[] updates;
        lock (_resourceOptionUiGate)
        {
            if (handleGeneration != _resourceOptionUiHandleGeneration) return;
            updates = _resourceOptionUiUpdates.ToArray();
            _resourceOptionUiUpdates.Clear();
            _resourceOptionUiQueued = false;
        }
        if (_releaseCompleted || IsDisposed || Disposing) return;
        foreach (var update in updates) update();
        QueueResourceOptionUpdates();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        lock (_resourceOptionUiGate)
        {
            // An accepted BeginInvoke can still belong to an HWND that is
            // subsequently destroyed. Its callback must not consume or reset
            // the queue after the replacement HWND posts a new callback.
            _resourceOptionUiHandleGeneration++;
            _resourceOptionUiQueued = false;
        }
        base.OnHandleDestroyed(e);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Binding can finish its resource read before this panel is shown.
        // OnHandleCreated runs on the owner thread and drains those results.
        QueueResourceOptionUpdates();
    }
    private void DisposeDetailPages()
    {
        if (_tabs.IsDisposed) return;
        var pages = _tabs.TabPages.Cast<TabPage>().ToArray();
        var previous = _disposingDetailPages; _disposingDetailPages = true;
        _tabs.SuspendLayout();
        try
        {
            // Removing the complete collection first prevents disposal of a
            // selected page from activating another page that is also being
            // destroyed, recreating its child handles and starting new work.
            _tabs.TabPages.Clear();
            foreach (var page in pages) page.Dispose();
        }
        finally { _tabs.ResumeLayout(false); _disposingDetailPages = previous; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_releaseCompleted)
        {
            lock (_resourceOptionUiGate)
            {
                _releaseCompleted = true;
                _resourceOptionUiUpdates.Clear();
                _resourceOptionUiQueued = false;
                _resourceOptionUiHandleGeneration++;
            }
            DisposeDetailPages();
            _previewGeneration++;
            _previewCancellation.Cancel(); _previewCancellation.Dispose();
            foreach (var timer in _previewTimers) timer.Dispose(); _previewTimers.Clear();
            foreach (var image in _images) image.Dispose(); _images.Clear();
            _errors.Dispose(); _diagnosticTips.Dispose(); _lookup.Dispose();
            if (_ownsResources) _resources.Dispose();
        }
        base.Dispose(disposing);
    }
}
