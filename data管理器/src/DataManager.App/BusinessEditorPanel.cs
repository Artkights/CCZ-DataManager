using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Images;
using ImageManager.App;
using Ccz.ManagerUi;

namespace DataManager.App;

/// <summary>
/// Fixed right-hand editor for a selected logical entity. Every binding keeps
/// its physical table, row and field, so edits use the workspace's validated,
/// undoable transaction rather than writing bytes from the UI.
/// </summary>
public sealed partial class BusinessEditorPanel : UserControl, IEditorViewLifecycle
{
    private readonly LayoutScheduler _viewLayouts;
    private readonly List<(Control Owner, Action Arrange)> _viewLayoutActions = [];
    private bool _viewActive;
    private readonly List<Control> _previewAncestors = [];
    private readonly HashSet<System.Windows.Forms.Timer> _suspendedPreviewTimers = [];

    public void ActivateView()
    {
        if (IsDisposed || Disposing) return;
        _viewActive = true;
        UpdatePreviewActivity();
        ApplyDeferredResourceRefresh();
        AutoValidate = AutoValidate.Inherit;
        _viewLayouts.Activate();
    }

    public void DeactivateView()
    {
        _viewActive = false;
        UpdatePreviewActivity();
        AutoValidate = AutoValidate.Disable;
        _viewLayouts.Deactivate();
    }

    public void RequestViewLayout() => _viewLayouts.Request();

    // Module hosts can change effective visibility without notifying every
    // descendant. The page owns this barrier; preview controls keep their text,
    // selection and resource binding while releasing work and owned pixels.
    private void UpdatePreviewActivity()
    {
        if (_releaseCompleted || IsDisposed || Disposing) return;
        var active = _viewActive && Visible;
        void Apply(Control host)
        {
            foreach (Control child in host.Controls)
            {
                var visible = active && child.Visible;
                if (child is ResourcePreviewCard card) card.SetPreviewActive(visible);
                else if (child is RangePreviewPicker range) range.SetPreviewActive(visible);
                else if (child is SearchableIdComboBox combo) combo.SetPreviewActive(visible);
                Apply(child);
            }
        }
        Apply(this);
        if (!active)
        {
            foreach (var timer in _previewTimers.Where(timer => timer.Enabled))
            { _suspendedPreviewTimers.Add(timer); timer.Stop(); }
        }
        else
        {
            foreach (var timer in _suspendedPreviewTimers)
                if (_previewTimers.Contains(timer)) timer.Start();
            _suspendedPreviewTimers.Clear();
        }
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        BindPreviewAncestors();
    }

    private void BindPreviewAncestors()
    {
        ReleasePreviewAncestors();
        if (_releaseCompleted || IsDisposed || Disposing) return;
        for (var ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            _previewAncestors.Add(ancestor);
            ancestor.VisibleChanged += PreviewAncestorVisibleChanged;
            ancestor.ParentChanged += PreviewAncestorParentChanged;
        }
        UpdatePreviewActivity();
    }

    private void PreviewAncestorVisibleChanged(object? sender, EventArgs e) => UpdatePreviewActivity();
    private void PreviewAncestorParentChanged(object? sender, EventArgs e) => BindPreviewAncestors();
    private void ReleasePreviewAncestors()
    {
        foreach (var ancestor in _previewAncestors)
        {
            ancestor.VisibleChanged -= PreviewAncestorVisibleChanged;
            ancestor.ParentChanged -= PreviewAncestorParentChanged;
        }
        _previewAncestors.Clear();
    }

    private void RegisterViewLayout(Control owner, Action arrange)
    {
        var entry = (owner, arrange);
        _viewLayoutActions.Add(entry);
        owner.Disposed += (_, _) => _viewLayoutActions.Remove(entry);
    }

    private void ArrangeActiveView()
    {
        if (!_viewActive || _binding || _releaseCompleted || IsDisposed || Disposing) return;
        _tabs.RefreshHeader();
        PerformLayout();
        foreach (var entry in _viewLayoutActions.ToArray())
            if (!entry.Owner.IsDisposed && entry.Owner.Visible) entry.Arrange();
    }
    private sealed class PreviewNumericUpDown : NumericUpDown
    {
        public Func<bool>? ValidateInput { get; set; }

        protected override void ValidateEditText()
        {
            // Native focus changes validate before the shell's query handler.
            // Refuse invalid text before NumericUpDown can replace or clamp it.
            if (ValidateInput?.Invoke() != false) base.ValidateEditText();
        }

        protected override void UpdateEditText()
        {
            // NumericUpDown.OnLostFocus calls UpdateEditText directly, which
            // otherwise bypasses ValidateEditText and invokes ParseEditText.
            if (UserEdit && ValidateInput?.Invoke() == false) return;
            base.UpdateEditText();
        }

        protected override void OnValidating(System.ComponentModel.CancelEventArgs e)
        {
            base.OnValidating(e);
            if (ValidateInput?.Invoke() == false) e.Cancel = true;
        }
    }

    private static IReadOnlyList<string> EquipmentColumns => ItemTypeService.EquipmentColumns;
    private static IReadOnlyList<string> TerrainNames => RecordPresentationService.TerrainNames;
    // These are naming dictionaries only.  They deliberately do not define
    // the candidate domain: custom bases can add IDs beyond the known names.
    private static readonly IReadOnlyDictionary<int, string> KnownStrategyTypes =
        new Dictionary<int, string>
        {
            [0] = "火系", [1] = "水系", [2] = "地系", [3] = "风系", [4] = "眩晕", [5] = "诱惑", [6] = "谍报",
            [7] = "压迫/威吓", [8] = "咒骂/挑拨", [9] = "钝兵/钝队", [10] = "虚脱/厌战", [11] = "士气提升",
            [12] = "防御提升", [13] = "攻击提升", [14] = "爆发提升", [15] = "谎报", [16] = "中毒", [17] = "定身",
            [18] = "封咒", [19] = "补给", [20] = "MP补给", [21] = "觉醒", [22] = "回归", [23] = "天气",
            [24] = "八阵图", [25] = "四神", [26] = "霸气", [27] = "强行", [28] = "衰气", [29] = "疾行",
            [30] = "诅咒", [31] = "精妙", [32] = "连击", [33] = "伪报", [34] = "纵火", [35] = "修筑",
            [36] = "诱敌", [37] = "瞬移", [38] = "雷系", [39] = "撞心", [40] = "扩展策略A", [41] = "扩展策略B", [42] = "扩展策略C"
        };
    // 6.6 样本中这些 ID 与 6.5 的固定名称不同；只覆盖已从 Data.e5 逐行核对的值。
    private static readonly IReadOnlyDictionary<int, string> KnownStrategyTypes66 =
        new Dictionary<int, string>
        {
            [0x1F] = "精进", [0x20] = "凤求凰", [0x21] = "反间", [0x24] = "无情铁手",
            [0x26] = "落雷", [0x29] = "换位", [0x2A] = "号令"
        };
    private static readonly IReadOnlyDictionary<int, string> KnownAttackRanges =
        new Dictionary<int, string>
        {
            [0] = "四格", [1] = "九宫", [2] = "近中程弓形", [3] = "中程弓形", [4] = "远程弓形",
            [5] = "原版少见", [6] = "没羽箭", [7] = "一转炮车", [8] = "二三转炮车", [9] = "一转弓骑", [10] = "全屏"
        };
    private static readonly IReadOnlyDictionary<int, string> KnownPierceRanges =
        new Dictionary<int, string>
        {
            [0] = "不穿透", [1] = "十字穿透", [2] = "九宫穿透", [3] = "大没羽箭", [4] = "蛇矛",
            [5] = "长蛇矛/穿六", [6] = "大大没羽箭"
        };
    private readonly ProjectWorkspace _workspace;
    private readonly ResourceCatalogAdapter _resources;
    private readonly WorkspaceRangeCatalog? _rangeCatalog;
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

    public BusinessEditorPanel(ProjectWorkspace workspace, ResourceCatalogAdapter? resources = null,
        WorkspaceRangeCatalog? rangeCatalog = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _ownsResources = resources == null;
        _resources = resources ?? new ResourceCatalogAdapter();
        _rangeCatalog = rangeCatalog;
        _business = new BusinessDataService(workspace);
        AutoScaleMode = AutoScaleMode.Inherit;
        Dock = DockStyle.Fill;
        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(_title);
        _errors.ContainerControl = this;
        _viewLayouts = new LayoutScheduler(this, ArrangeActiveView);
        VisibleChanged += (_, _) => { if (Visible) ActivateView(); else DeactivateView(); };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdatePreviewActivity(); RequestViewLayout();
            // Native tab visibility can settle after SelectedIndexChanged.
            if (IsHandleCreated && !_releaseCompleted)
                BeginInvoke((Action)UpdatePreviewActivity);
        };
    }

    public event EventHandler? Edited;
    public event EventHandler? PendingChangesChanged;
    public event EventHandler? SaveRequested;
    public event Action<int, int, int>? SImageLocationRequested;
    public event Action<string, int, int>? PersonImageLocationRequested;
    public event EventHandler? ExtendCategoryChanged;
    public TabControl DetailTabs => _kind == BusinessPageKind.ExtendData && _extendEditor != null ? _extendEditor.DetailTabs : _tabs;
    public ExtendDataEditorPanel? ExtendEditor => _extendEditor;
    public IReadOnlyList<IdNameOption> ExtendEntityOptions => _extendEditor?.GetEntityOptions() ?? [];
    public BusinessPageKind CurrentKind => _kind;
    public int CurrentId => _kind == BusinessPageKind.ExtendData && _extendEditor != null ? _extendEditor.CurrentId : _id;

    /// <summary>
    /// Rebuilds resource-backed candidate lists after Hitarea/Effarea or another
    /// image archive has changed.  The callbacks retain the active text and
    /// selection, so an in-progress numeric edit is not overwritten.
    /// </summary>
    public void RefreshResourcePickers()
    {
        if (_releaseCompleted || IsDisposed || Disposing) return;
        if (!Visible) { _resourceRefreshPending = true; return; }
        _resourceRefreshPending = false;
        foreach (var refresh in _resourcePickerRefreshers.ToArray())
        {
            try { refresh(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) when (IsDisposed || Disposing) { }
        }
    }

    public void Bind(BusinessPageKind kind, int id)
    {
        if (CanReuseStableBinding(kind, id))
        {
            RefreshChangedResourceBinding();
            RequestViewLayout();
            return;
        }
        _binding = true;
        SuspendLayout();
        _tabs.SuspendLayout();
        try
        {
            _lookup.Prune(_workspace.Documents.Values);
            // A cached module is effectively invisible while its owner is
            // inactive; that does not invalidate its bound editor.
            if (kind == BusinessPageKind.ExtendData && _kind == kind && _extendEditor != null)
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
            _rangePickerRefreshers.Clear();
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
            _suspendedPreviewTimers.Clear();
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
                    _extendEditor = new ExtendDataEditorPanel(_workspace) { Dock = DockStyle.Fill, AutoScaleMode = AutoScaleMode.Inherit };
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
        finally
        {
            _binding = false;
            RecordStableBinding();
            _tabs.RefreshHeader();
            _tabs.ResumeLayout(true);
            ResumeLayout(true);
            UpdatePreviewActivity();
            RequestViewLayout();
        }
    }

    /// <summary>Validates and commits pending text/grid values before a save or selection change.</summary>
    public bool Flush()
    {
        LastValidationError = null;
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
                    if (field.Name == "兵种") RefreshResourcePickers();
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowEditorError(control, $"{document.Definition.DisplayName} ID={_id} / {field.Name}：{ex.Message}");
                return false;
            }
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
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Name = $"Flags_{document.Definition.Key}" };
            var original = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
            var mask = flags.Aggregate(0, (value, flag) => value | (1 << flag.Bit));
            var checkboxes = flags.Select(flag => new CheckBox
            {
                Text = flag.Name,
                Name = $"Flag_{document.Definition.Key}_{flag.Bit}",
                AutoSize = true,
                Margin = new Padding(2, 1, 2, 1),
                Checked = (original & (1 << flag.Bit)) != 0,
                Enabled = editable
            }).ToArray();
            var loading = false;
            for (var index = 0; index < checkboxes.Length; index++)
            {
                var check = checkboxes[index];
                var flag = flags[index];
                panel.Controls.Add(check);
                var tip = string.IsNullOrWhiteSpace(flag.Tip) ? $"位标记 0x{1 << flag.Bit:X2}" : $"位标记 0x{1 << flag.Bit:X2}；{flag.Tip}";
                _diagnosticTips.SetToolTip(check, tip);
                check.CheckedChanged += (_, _) =>
                {
                    if (loading) return;
                    var value = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
                    var selected = 0;
                    for (var item = 0; item < checkboxes.Length; item++)
                        if (checkboxes[item].Checked) selected |= 1 << flags[item].Bit;
                    var next = document.Definition.Key == StrategyExtensionSemantics.TableKey
                        ? StrategyExtensionSemantics.PreserveReserved((byte)value, selected)
                        : (value & ~mask) | selected;
                    Commit(panel, next);
                };
            }
            Register(panel, () =>
            {
                panel.Enabled = true;
                var value = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
                loading = true;
                try
                {
                    for (var index = 0; index < checkboxes.Length; index++)
                    {
                        checkboxes[index].Enabled = editable;
                        checkboxes[index].Checked = (value & (1 << flags[index].Bit)) != 0;
                    }
                }
                finally { loading = false; }
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
        var isE5RangeField = IsE5RangeField(document.Definition.Key, field.Name);
        var isNativeRangeField = IsNativeRangeField(document.Definition.Key, field.Name);
        var isRangeField = isE5RangeField || isNativeRangeField;
        var options = GetOptions(document, rowIndex, field);
        if (options != null)
        {
            var idOnly = ResourceKindForField(document.Definition.Key, field.Name) is
                ResourceBindingKind.Portrait or ResourceBindingKind.CharacterR or ResourceBindingKind.CharacterS;
            var combo = new SearchableIdComboBox
            {
                Dock = DockStyle.Fill,
                Enabled = editable,
                IdOnly = idOnly,
                RangePreviewMode = isRangeField
            };
            combo.SetOptions(options);
            RangePreviewPicker? rangePicker = isRangeField ? new RangePreviewPicker(combo) : null;
            if (rangePicker != null)
            {
                rangePicker.FieldName = field.Name;
                rangePicker.MinimumId = (int)field.Minimum;
                rangePicker.MaximumId = (int)field.Maximum;
            }
            // A detached new editor reports Visible=true. Keep it inactive
            // until Bind finishes attaching it to the actual selected page.
            combo.SetPreviewActive(false);
            rangePicker?.SetPreviewActive(false);
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
            void RefreshRangeOptions()
            {
                if (!isRangeField) return;
                // Range drafts, project reloads and image replacement can all
                // change this picker. Keep the text being edited while reading
                // the current definition catalog and compatible table values.
                var refreshed = GetOptions(document, rowIndex, field) ?? [];
                options = refreshed;
                var text = combo.Text;
                combo.SetOptions(refreshed, preserveEditSelection: true);
                if (!string.IsNullOrWhiteSpace(text)) combo.SetBoundValue(text, preserveEditSelection: true);
            }
            void RefreshRangeLoader()
            {
                RefreshRangeLoaderCore(true);
            }
            void RefreshRangeLoaderCore(bool refreshOptions)
            {
                if (refreshOptions) RefreshRangeOptions();
                if (ResourceKindForField(document.Definition.Key, field.Name) is { } resourceKind)
                {
                    var job = resourceKind == ResourceBindingKind.CharacterS ? EntityNumber("persons", "兵种") : 0;
                    var turn = resourceKind == ResourceBindingKind.CharacterS && job <= 59 ? job % 3 + 1 : 1;
                    var context = $"{_workspace.RootPath}|{resourceKind}|{job}|{turn}|{_resources.Revision}";
                    if (previewContext == context) return;
                    previewContext = context;
                    var previewRoot = _workspace.RootPath;
                    SetPreviewLoader((id, token) => _resources.DecodeBindingThumbnailAsync(previewRoot,
                        resourceKind, id, new Size(48, 48), job, turn, token));
                    LoadResourceOptions(resourceKind, job, turn);
                    return;
                }
                if (!isRangeField) return;
                var attack = field.Name is "攻击范围" or "施法范围";
                var rangeRoot = _workspace.RootPath;
                var hasRangeDraft = CurrentRangeSnapshot(attack) != null;
                var rangeContext = $"{rangeRoot}|{attack}|{_resources.Revision}|{(hasRangeDraft ? _rangeCatalog!.Revision : -1)}";
                if (previewContext == rangeContext) return;
                previewContext = rangeContext;
                SetPreviewLoader(hasRangeDraft
                    ? (id, token) => _rangeCatalog!.DecodePreviewAsync(attack, id, token)
                    : (id, token) => _resources.DecodeRangeNativeFrameAsync(rangeRoot, attack, id, token));
            }
            var number = Convert.ToInt32(current ?? 0, CultureInfo.InvariantCulture);
            var selected = options.FirstOrDefault(item => item.Id == number);
            string EquipmentDisplay(int value, IReadOnlyList<IdNameOption> choices)
                => idOnly || isRangeField ? value.ToString(CultureInfo.InvariantCulture) : choices.FirstOrDefault(item => item.Id == value)?.Display ??
                    (document.Definition.Key == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue
                        ? $"{value}：原值（类别不匹配）" : $"{value}：未确认");
            combo.SetBoundValue(EquipmentDisplay(number, options));
            RefreshRangeLoader();
            if (ResourceKindForField(document.Definition.Key, field.Name) != null || isRangeField)
                _resourcePickerRefreshers.Add(RefreshRangeLoader);
            if (isRangeField)
                _rangePickerRefreshers.Add((field.Name is "攻击范围" or "施法范围" ? RangeManager.App.RangeKind.Attack : RangeManager.App.RangeKind.Pierce,
                    change =>
                    {
                        if (change.Reset) { RefreshRangeLoaderCore(true); return; }
                        if (change.DirectoryChanged) RefreshRangeOptions();
                        var rangeKind = field.Name is "攻击范围" or "施法范围" ? RangeManager.App.RangeKind.Attack : RangeManager.App.RangeKind.Pierce;
                        var ids = change.AffectedKeys.Where(key => key.Kind == rangeKind).Select(key => key.Id).ToHashSet();
                        combo.InvalidatePreviewIds(ids);
                        rangePicker?.InvalidatePreviewIds(ids);
                    }));
            bool SaveCombo()
            {
                if (_binding || !editable || !active || _confirmingStrategyEffect) return true;
                if (!combo.TryGetSelectedId(out var id))
                {
                    ShowEditorError(combo, $"{document.Definition.DisplayName} ID={_id} / {field.Name}：请输入数字 ID，或从列表中选择。");
                    return false;
                }
                if (document.Definition.Key == StrategyEffectClassService.TableKey &&
                    id.ToString(CultureInfo.InvariantCulture) != lastSubmitted)
                {
                    try { _workspace.ValidateCellValue(document, rowIndex, field.Name, id); }
                    catch (Exception ex) { ShowEditorError(combo, ex.Message); return false; }
                    if (!ConfirmStrategyEffect(combo, rowIndex, id))
                    {
                        var wasBinding = _binding;
                        _binding = true;
                        try
                        {
                            var previous = Convert.ToInt32(document.Data.Rows[rowIndex][field.Name], CultureInfo.InvariantCulture);
                            combo.SetBoundValue(EquipmentDisplay(previous, options));
                        }
                        finally { _binding = wasBinding; }
                        NotifyPendingChanges();
                        return true;
                    }
                }
                return Commit(combo, id);
            }
            combo.SelectionChangeCommitted += (_, _) => SaveCombo();
            combo.UnknownValueSubmitted += (_, _) => SaveCombo();
            combo.Validating += (_, args) => args.Cancel = !SaveCombo();
            _flushers.Add(SaveCombo);
            combo.TextChanged += (_, _) => NotifyPendingChanges();
            RegisterPreviewInput(document.Definition.Key, field.Name, combo, () => active && combo.Enabled &&
                combo.TryGetSelectedId(out var value) && value >= field.Minimum && value <= field.Maximum ? value : null);
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
        var numeric = new PreviewNumericUpDown { Width = 180, Minimum = Math.Min(field.Minimum, Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture)), Maximum = Math.Max(field.Maximum, Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture)), Value = Convert.ToDecimal(current ?? 0, CultureInfo.InvariantCulture), Enabled = editable, ThousandsSeparator = false };
        var hasLivePreview = RegisterPreviewInput(document.Definition.Key, field.Name, numeric, () => active && numeric.Enabled &&
            int.TryParse(numeric.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value >= field.Minimum && value <= field.Maximum ? value : null);
        if (hasLivePreview)
        {
            numeric.TextChanged += (_, _) => NotifyPendingChanges();
            // Reading NumericUpDown.Value validates typed text. Inspect Text
            // here so checking dirty state cannot submit a preview-only input.
            _pendingChecks.Add(() => active && editable && (!int.TryParse(numeric.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != lastSubmitted));
        }
        numeric.ValueChanged += (_, _) => Commit(numeric, field.Kind == FieldKind.UInt32 ? (object)(long)numeric.Value : (int)numeric.Value);
        bool ValidatePreviewNumber()
        {
            if (_binding || !editable || !active) return true;
            // Value validates and clamps native edit-box text. Parse the text
            // first so queries/saves cannot silently discard an invalid input.
            var text = numeric.Text.Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                var normalized = number.ToString(CultureInfo.InvariantCulture);
                // Existing out-of-range legacy values remain an unchanged no-op.
                if (normalized == lastSubmitted) { _errors.SetError(numeric, string.Empty); return true; }
                if (number >= field.Minimum && number <= field.Maximum)
                { _errors.SetError(numeric, string.Empty); return true; }
            }
            // Focusing the UpDown container can move focus away from its native
            // edit child and invoke NumericUpDown's clamping validation.
            ShowEditorError(numeric, $"{field.Name}请输入 {field.Minimum}..{field.Maximum} 范围内的整数。", focus: false);
            return false;
        }
        bool SavePreviewNumber()
        {
            if (!ValidatePreviewNumber()) return false;
            if (_binding || !editable || !active) return true;
            var number = long.Parse(numeric.Text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return Commit(numeric, field.Kind == FieldKind.UInt32 ? (object)number : (int)number);
        }
        if (hasLivePreview) numeric.ValidateInput = ValidatePreviewNumber;
        _flushers.Add(hasLivePreview ? SavePreviewNumber :
            () => Commit(numeric, field.Kind == FieldKind.UInt32 ? (object)(long)numeric.Value : (int)numeric.Value));
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
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
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
            AddFormRow(layout, index < TerrainNames.Count ? $"{index}：{TerrainNames[index]}" : index.ToString(CultureInfo.InvariantCulture), CreateEditor(terrain, terrain.Data.Rows[index], index, field));
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
            _suspendedPreviewTimers.Clear();
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
        if (document.Definition.Key == StrategyEffectClassService.TableKey)
            return StrategyEffectClassService.GetOptions(_workspace, rowIndex)
                .Select(option => new IdNameOption(option.Id, option.Name)).ToArray();
        var tableKey = document.Definition.Key;
        if (ResourceKindForField(tableKey, field.Name) != null) return [];
        if (tableKey == "persons" && PersonEquipmentService.CategoryForField(field.Name).HasValue)
            return PersonEquipmentService.GetOptions(_workspace.Documents, field.Name, field.Minimum, field.Maximum)
                .Select(option => new IdNameOption(option.Id, option.Name)).ToArray();
        if (tableKey == "job-series-attributes") return JobAttributeOptions(document, rowIndex, field);
        if (tableKey is "items-low" or "items-high" or "items-extended")
        {
            var semantics = ItemTypeService.Resolve(document, rowIndex);
            if (semantics.Category == ItemCategory.Accessory && field.Name == "能力成长")
                return OptionsFor("job-series").Where(option => option.Id < 255).Concat([new IdNameOption(255, "所有兵种")]).ToArray();
            var legacyAuxiliary = !semantics.IsCategoryMarker && semantics.Category is not (ItemCategory.Weapon or ItemCategory.Armor);
            var effectField = semantics.IsCategoryMarker || semantics.Category is ItemCategory.Weapon or ItemCategory.Armor
                ? "装备特效号" : "宝物类型";
            if (field.Name == effectField && semantics.Category == ItemCategory.Consumable)
                return ConsumableEffectOptions(document, field);
            if (field.Name == "装备特效号" && legacyAuxiliary)
                return [new(2, "辅助装备类别"), new(3, "道具/消耗品类别"), new(0, "预留/未指定"), new(102, "预留"), new(255, "空/保留")];
            if (field.Name == "宝物类型")
            {
                if (legacyAuxiliary)
                {
                    try
                    {
                        return ItemEffectOptions(document, field);
                    }
                    catch { }
                }
                return ItemTypeService.GetOptions(semantics, _workspace).Select(option => new IdNameOption(option.Id, option.Name)).ToArray();
            }
        }
        if (field.Name == "装备特效号")
        {
            return ItemEffectOptions(document, field);
        }
        // Resolve semantic fields before generic reference-table handling.  A
        // profile may add a reference table to a byte field, but that does not
        // change whether the value is an E5 range, an Ekd5 native shape, or a
        // strategy/target code.
        if (tableKey.Equals("strategies", StringComparison.OrdinalIgnoreCase) && field.Name == "策略类型")
            return StrategyTypeOptions(document, rowIndex, field);
        if (tableKey.Equals("strategies", StringComparison.OrdinalIgnoreCase) && field.Name == "施展对象")
            return StrategyTargetOptions(document, field);
        if (IsE5RangeField(tableKey, field.Name))
            return RangeOptions(document, rowIndex, field, attack: field.Name is "攻击范围" or "施法范围");
        if (field.Name == "穿透" && tableKey.Equals("detailed-job-penetration", StringComparison.OrdinalIgnoreCase))
            return NativePenetrationOptions(document, rowIndex, field);
        if (!string.IsNullOrWhiteSpace(field.ReferenceTable))
        {
            var options = OptionsFor(field.ReferenceTable);
            return options;
        }
        return null;
    }

    private static bool IsE5RangeField(string tableKey, string fieldName)
        => (tableKey == "detailed-job-growth" && fieldName == "攻击范围") ||
           (tableKey is "strategies" or "finishing-moves" &&
            fieldName is "攻击范围" or "施法范围" or "穿透范围");

    private static bool IsNativeRangeField(string tableKey, string fieldName)
        => tableKey.Equals("detailed-job-penetration", StringComparison.OrdinalIgnoreCase) && fieldName == "穿透";

    private IReadOnlyList<IdNameOption> RangeOptions(TableDocument document, int rowIndex,
        TableFieldDefinition field, bool attack)
    {
        if (WorkspaceRangeOptions(document, rowIndex, field, attack) is { } liveOptions) return liveOptions;
        var minimum = ClampToInt(field.Minimum, int.MinValue);
        var maximum = ClampToInt(field.Maximum, int.MaxValue);
        var current = TryReadInt(document, rowIndex, field.Name);
        var known = attack ? KnownAttackRanges : KnownPierceRanges;
        // A missing or unreadable E5 resource does not prove that the old
        // seven/eleven-item lists are the real domain.  Keep the verified
        // labels and values already present in the base, but only synthesize
        // the complete 0..N-1 range when the resource scan supplies N.
        int? count = null;
        try
        {
            var key = attack ? "Hitarea.e5" : "Effarea.e5";
            var resource = _resources.Scan(_workspace.RootPath).Resources
                .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (resource is { EntryCount: > 0 }) count = resource.EntryCount;
        }
        catch
        {
            // A missing or malformed image catalog must not prevent the data
            // page from opening. The base value is still retained below.
        }

        var snapshot = new OptionCatalogService().Build(new OptionCatalogRequest(
            _workspace.ActiveProfile.ProfileId,
            _workspace.ActiveProfile.VariantId,
            _workspace.ActiveProfile.Profile.Version,
            ExecutableFingerprint(),
            document.Definition.Key,
            field.Name,
            attack ? document.Definition.Key == "detailed-job-growth"
                ? OptionCatalogKind.LegacyDataAttackRange : OptionCatalogKind.E5AttackRange
                : OptionCatalogKind.E5PierceRange,
            minimum,
            maximum,
            current,
            ReadColumnValues(document, field.Name),
            known,
            count,
            ResourceAvailable: count.HasValue));
        return snapshot.Options.Select(option => new IdNameOption(option.Id, option.Label)).ToArray();
    }

    private IReadOnlyList<IdNameOption> NativePenetrationOptions(TableDocument document, int rowIndex,
        TableFieldDefinition field)
    {
        if (WorkspaceRangeOptions(document, rowIndex, field, attack: false) is { } liveOptions) return liveOptions;
        var minimum = ClampToInt(field.Minimum, int.MinValue);
        var maximum = ClampToInt(field.Maximum, int.MaxValue);
        var current = TryReadInt(document, rowIndex, field.Name);
        var nativeDomain = NativeRangeDomainReader.Read(_workspace.RootPath,
            _workspace.ActiveProfile.ProfileId, _workspace.ActiveProfile.VariantId);
        var nativeMaximum = nativeDomain.PierceMaximum >= 0
            ? nativeDomain.PierceMaximum
            : NativeRangeDomain.PierceMaximum(_workspace.ActiveProfile.ProfileId);
        var actualValues = ReadColumnValues(document, field.Name)
            .Concat(nativeDomain.JobPierceValues)
            .Distinct()
            .ToArray();
        var snapshot = new OptionCatalogService().Build(new OptionCatalogRequest(
            _workspace.ActiveProfile.ProfileId,
            _workspace.ActiveProfile.VariantId,
            _workspace.ActiveProfile.Profile.Version,
            string.IsNullOrWhiteSpace(nativeDomain.ExecutableHash) ? ExecutableFingerprint() : nativeDomain.ExecutableHash,
            document.Definition.Key,
            field.Name,
            OptionCatalogKind.NativeJobShape,
            minimum,
            maximum,
            current,
            actualValues,
            KnownPierceRanges,
            NativeMaximum: nativeMaximum));
        return snapshot.Options.Select(option => new IdNameOption(option.Id, option.Label)).ToArray();
    }

    private WorkspaceRangeCatalogSnapshot? CurrentRangeSnapshot(bool attack)
    {
        if (_rangeCatalog == null || !_rangeCatalog.HasSession ||
            !string.Equals(Path.TrimEndingDirectorySeparator(_rangeCatalog.RootPath),
                Path.TrimEndingDirectorySeparator(_workspace.RootPath), StringComparison.OrdinalIgnoreCase)) return null;
        var snapshot = _rangeCatalog.GetSnapshot(attack);
        return snapshot.Available ? snapshot : null;
    }

    private IReadOnlyList<IdNameOption>? WorkspaceRangeOptions(TableDocument document, int rowIndex,
        TableFieldDefinition field, bool attack)
    {
        if (CurrentRangeSnapshot(attack) is not { } snapshot) return null;
        // A saved-history restore replaces workspace documents before hidden
        // cached pages are rebound. Read compatibility values from that new
        // document, never from the page's previous table snapshot.
        if (_workspace.Documents.TryGetValue(document.Definition.Key, out var liveDocument)) document = liveDocument;
        var known = attack ? KnownAttackRanges : KnownPierceRanges;
        var entries = snapshot.Entries.ToDictionary(entry => entry.Id);
        var current = TryReadInt(document, rowIndex, field.Name);
        // Only actual bindings define new choices. Existing table values are
        // retained for custom bases, including reserved values such as FF.
        var ids = new SortedSet<int>(snapshot.Ids.Concat(ReadColumnValues(document, field.Name))
            .Where(id => id >= field.Minimum && id <= field.Maximum));
        if (current is { } value) ids.Add(value);
        return ids.Select(id => new IdNameOption(id, known.TryGetValue(id, out var label) ? label
            : entries.TryGetValue(id, out var entry) ? entry.Name : "基底原值/保留")).ToArray();
    }

    private static IReadOnlyList<IdNameOption> ConsumableEffectOptions(TableDocument document, TableFieldDefinition field)
    {
        var actual = document.Data.Columns.Contains(field.Name)
            ? document.Data.Rows.Cast<DataRow>()
                .Where(row => row.RowState != DataRowState.Deleted)
                .Select(row => TryConvertInt(row[field.Name], out var value) ? (int?)value : null)
                .Where(value => value.HasValue).Select(value => value!.Value).ToArray()
            : [];
        var known = ConsumableEffectCatalog.GetAll(actual).ToDictionary(effect => effect.Id, effect => effect.Name);
        var values = new SortedSet<int>(known.Keys);
        // 0xFF is used as an empty/reserved marker by item records in
        // several profiles.  Keep it discoverable even when the current
        // table happens not to contain one yet; the byte remains editable.
        values.Add(0xFF);
        values.UnionWith(actual);
        values.RemoveWhere(id => id < field.Minimum || id > field.Maximum);
        return values.Select(id => new IdNameOption(id,
            known.TryGetValue(id, out var name) ? name : $"未确认道具特效 0x{id:X2}（基底原值）")).ToArray();
    }

    private static void AddColumnValues(ISet<int> values, TableDocument document, string fieldName,
        int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        if (!document.Data.Columns.Contains(fieldName)) return;
        foreach (DataRow row in document.Data.Rows)
            if (row.RowState != DataRowState.Deleted && TryConvertInt(row[fieldName], out var value) &&
                value >= minimum && value <= maximum) values.Add(value);
    }

    private static IReadOnlyList<int> ReadColumnValues(TableDocument document, string fieldName)
    {
        if (!document.Data.Columns.Contains(fieldName)) return [];
        return document.Data.Rows.Cast<DataRow>()
            .Where(row => row.RowState != DataRowState.Deleted)
            .Select(row => TryConvertInt(row[fieldName], out var value) ? (int?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
    }

    private string ExecutableFingerprint()
        => _workspace.ProfileMatch?.CoreFileFingerprints.TryGetValue("Ekd5.exe", out var hash) == true
            ? hash : string.Empty;

    private static IReadOnlyList<IdNameOption> StrategyTargetOptions(TableDocument document, TableFieldDefinition field)
    {
        var known = new Dictionary<int, string>
        {
            [0] = "敌方", [1] = "我方", [2] = "全屏/天气"
        };
        var minimum = ClampToInt(field.Minimum, int.MinValue);
        var maximum = ClampToInt(field.Maximum, int.MaxValue);
        var ids = new SortedSet<int>(known.Keys);
        AddColumnValues(ids, document, field.Name);
        ids.RemoveWhere(id => id < minimum || id > maximum);
        return ids.Select(id => new IdNameOption(id,
            known.TryGetValue(id, out var name) ? name : $"施展对象 {id}（基底原值）")).ToArray();
    }

    private IReadOnlyList<IdNameOption> StrategyTypeOptions(TableDocument document, int rowIndex,
        TableFieldDefinition field)
    {
        var minimum = ClampToInt(field.Minimum, int.MinValue);
        var maximum = ClampToInt(field.Maximum, int.MaxValue);
        var ids = new SortedSet<int>(KnownStrategyTypes.Keys);
        if (_workspace.ActiveProfile.ProfileId == "ccz-6.6") ids.UnionWith(KnownStrategyTypes66.Keys);
        ids.RemoveWhere(id => id < minimum || id > maximum);

        if (document.Data.Columns.Contains(field.Name))
        {
            foreach (DataRow row in document.Data.Rows)
            {
                if (row.RowState == DataRowState.Deleted) continue;
                if (!TryConvertInt(row[field.Name], out var value)) continue;
                if (value >= minimum && value <= maximum) ids.Add(value);
            }
        }
        if (TryReadInt(document, rowIndex, field.Name) is { } current) ids.Add(current);
        return ids.Select(id => new IdNameOption(id, StrategyTypeName(id) ?? $"策略类型 {id}（基底原值）")).ToArray();
    }

    private string? StrategyTypeName(int id)
    {
        if (_workspace.ActiveProfile.ProfileId == "ccz-6.6" && KnownStrategyTypes66.TryGetValue(id, out var name66))
            return name66;
        return KnownStrategyTypes.TryGetValue(id, out var name) ? name : null;
    }

    private static string RangeName(bool attack, int id, int? previewEntryCount = null)
    {
        var known = attack ? KnownAttackRanges : KnownPierceRanges;
        var name = known.TryGetValue(id, out var knownName)
            ? knownName
            : $"基底范围 {id}（图号 {id + 1}；基底原值/未确认）";
        if (previewEntryCount is { } count && (id < 0 || id >= count))
            name += "（无图）";
        return name;
    }

    private static int ClampToInt(long value, int fallback)
        => value < int.MinValue ? int.MinValue : value > int.MaxValue ? int.MaxValue : (int)value;

    private static int? TryReadInt(TableDocument document, int rowIndex, string fieldName)
    {
        if (rowIndex < 0 || rowIndex >= document.Data.Rows.Count || !document.Data.Columns.Contains(fieldName)) return null;
        return TryConvertInt(document.Data.Rows[rowIndex][fieldName], out var value) ? value : null;
    }

    private static bool TryConvertInt(object? value, out int result)
    {
        try
        {
            if (value == null || value == DBNull.Value) { result = 0; return false; }
            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception) when (value is not null)
        {
            result = 0;
            return false;
        }
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

    private readonly record struct FlagChoice(string Name, int Bit, string? Tip = null);

    private static FlagChoice[]? GetFlags(string key, string field)
        => key switch
        {
            "strategy-learning" =>
            [
                new("四系", 0), new("降能力", 1), new("妨碍", 2), new("补给", 3),
                new("升能力", 4), new("气候", 5), new("绝", 6), new("四神", 7)
            ],
            "strategy-extension" =>
            [
                new("策略模仿", 0, "6.6：bit0=1 时，该策略可被策略模仿/AI 选用；bit0=0 时不会被选择。"),
                new("辅助穿透", 1, "6.6：bit1=1 且拥有特效168(0xA8) 时，按穿透码查 0x45D343 表增强（01/02/03/08/07/05/06/05/06/02/0A）；bit1=0 不增强。")
            ],
            "strategy-terrain-availability" =>
            [
                new("火", 0), new("水", 1), new("风", 2), new("地", 3)
            ],
            _ => null
        };

    private static string FieldLabel(string key, string field)
        => field == "内容" ? key switch
        {
            "strategy-damage-type" => "是否伤血",
            "strategy-damage-ratio" => "伤害系数",
            "strategy-hit-rate" => "命中上限",
            "strategy-learning" => "类型索引",
            "strategy-effect-class" => "效果索引",
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
            ReleasePreviewAncestors();
            _suspendedPreviewTimers.Clear();
            _viewLayouts.Dispose();
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
