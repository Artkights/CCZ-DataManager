using System.Globalization;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;
using ImageManager.App;

namespace DataManager.App;

/// <summary>
/// Right-hand effect details for the engine page. Every stored field is editable;
/// derived facts (function entry, status, call address) stay read-only and are
/// shown beside the selected call site.
/// </summary>
public sealed partial class EngineEffectDetailPanel : UserControl
{
    private readonly EngineEffectSlotService _slots = new();
    private readonly Dictionary<int, EngineEffectScanRow> _rowsById = [];
    private readonly Dictionary<EngineEffectCallIdentity, EngineEffectCallEdit> _pendingCalls = [];
    private ProjectWorkspace? _workspace;
    private EngineEffectScanReport? _report;
    private IReadOnlyDictionary<int, EngineEffectReferenceSummary> _references =
        new Dictionary<int, EngineEffectReferenceSummary>();
    private Dictionary<uint, EngineEffectRootInfo> _rootInfos = [];
    private Dictionary<int, List<EngineEffectItemEntity>>? _itemSelectors;
    private IReadOnlyList<EngineEffectChoiceItem> _itemChoices = [];
    private IReadOnlyList<EngineEffectChoiceItem> _jobChoices = [];
    private EngineEffectScanRow? _row;
    private EngineEffectCallSite? _selectedCall;
    private EngineEffectCallScope _scope;
    private string? _pendingName;
    private byte? _pendingSuffix;
    private string? _pendingDescription;
    private string _baseTitle = string.Empty;
    private string _suffixVersion = string.Empty;
    private bool _binding;

    public EngineEffectDetailPanel()
    {
        Dock = DockStyle.Fill;
        BuildLayout();
        BuildChoiceItems();
        WireLayoutEvents();
    }

    public event EventHandler? PendingChangesChanged;
    public event EventHandler? WriteCompleted;
    public bool HasPendingChanges => _pendingCalls.Count > 0 || _pendingName != null || _pendingSuffix != null || _pendingDescription != null;
    internal Action CaptureDraftRecovery()
    {
        var calls = _pendingCalls.ToArray(); var name = _pendingName; var suffix = _pendingSuffix; var description = _pendingDescription;
        return () =>
        {
            _pendingCalls.Clear(); foreach (var pair in calls) _pendingCalls.Add(pair.Key, pair.Value);
            _pendingName = name; _pendingSuffix = suffix; _pendingDescription = description;
            _binding = true;
            try
            {
                if (name != null) _nameEditor.Editor.Text = name;
                if (description != null) _descriptionEditor.Editor.Text = description;
                if (suffix is { } value) SelectSuffix(value);
            }
            finally { _binding = false; }
            BindCurrentCall(_selectedCall, _scope); UpdatePendingUi();
        };
    }
    public int? CurrentEffectId => _row?.EffectId;
    public uint? CurrentCallAddress => _selectedCall?.CallAddress;
    public int? LastFollowEffectId { get; private set; }
    public uint? LastWrittenCallAddress { get; private set; }
    public bool IsPending(int effectId) => CurrentEffectId == effectId && HasPendingChanges;

    internal int CallRowCountForSmokeTest => _callsGrid.Rows.Count;
    internal string TitleForSmokeTest => _title.Text;
    internal string SuffixForSmokeTest => _suffixLabel.Text;
    internal DataGridView CallsGridForSmokeTest => _callsGrid;
    internal DataGridView UnassignedGridForSmokeTest => _unassignedGrid;
    internal DataGridView InternalGridForSmokeTest => _internalGrid;
    internal DataGridView DerivedGridForSmokeTest => _derivedGrid;
    internal DataGridView ReferenceGridForSmokeTest => _referenceGrid;
    internal string ReferenceSummaryForSmokeTest => _referenceSummary.Text;
    internal DataGridView CallPathsGridForSmokeTest => _callPathsGrid;
    internal TextBox EvidenceBoxForSmokeTest => _evidenceBox;
    internal string CallEmptyTextForSmokeTest => _callsEmpty.Text;
    internal bool CallEmptyVisibleForSmokeTest => _callsEmpty.Visible;
    internal TabControl CallsTabsForSmokeTest => _callsTabs;
    internal Button WriteCallButtonForSmokeTest => _writeCallButton;
    internal ByteTextEditor NameEditorForSmokeTest => _nameEditor;
    internal ByteTextEditor DescriptionEditorForSmokeTest => _descriptionEditor;
    internal ComboBox SuffixComboForSmokeTest => _suffixCombo;
    internal ComboBox EntryComboForSmokeTest => _entryCombo;
    internal ComboBox ValueModeComboForSmokeTest => _valueModeCombo;
    internal ComboBox StackingComboForSmokeTest => _stackingCombo;
    internal ComboBox ItemIdComboForSmokeTest => _itemIdCombo;
    internal ComboBox PersonalIdComboForSmokeTest => _personalIdCombo;
    internal string CallFieldLabelsForSmokeTest => string.Join("|",
        _callGrid.Controls.Cast<Control>().OfType<Label>().Select(label => label.Text));
    internal int? ItemIdForSmokeTest
    {
        get => EngineEffectChoiceCombo.Selected(_itemIdCombo);
        set => EngineEffectChoiceCombo.Select(_itemIdCombo, value);
    }
    internal Button WriteButtonForSmokeTest => _writeButton;
    internal Button RevertButtonForSmokeTest => _revertButton;
    internal bool BodyVisibleForSmokeTest => _bodyLayout.Visible;
    internal Size BodySizeForSmokeTest => _bodyLayout.Size;
    internal int TextRowHeightForSmokeTest => (int)_textRow.Height;
    internal int CallRowHeightForSmokeTest => (int)_callRow.Height;
    internal int CallsSectionHeightForSmokeTest => _bodyLayout.GetControlFromPosition(0, 2)?.Height ?? -1;
    internal int TextSectionHeightForSmokeTest => _bodyLayout.GetControlFromPosition(0, 0)?.Height ?? -1;
    internal int CallSectionHeightForSmokeTest => _bodyLayout.GetControlFromPosition(0, 1)?.Height ?? -1;

    internal void SelectCallForSmokeTest(int index)
    {
        if (index < 0 || index >= _callsGrid.Rows.Count) return;
        _callsTabs.SelectedTab = _effectCallsPage;
        _callsGrid.ClearSelection();
        _callsGrid.CurrentCell = _callsGrid.Rows[index].Cells[0];
        _callsGrid.Rows[index].Selected = true;
        BindCurrentCall(_callsGrid.Rows[index].Tag as EngineEffectCallSite, EngineEffectCallScope.Effect);
    }

    internal void SelectDerivedRowForSmokeTest(int index)
    {
        if (index < 0 || index >= _derivedGrid.Rows.Count) return;
        _callsTabs.SelectedTab = _derivedCallsPage;
        _derivedGrid.ClearSelection();
        _derivedGrid.CurrentCell = _derivedGrid.Rows[index].Cells[0];
        _derivedGrid.Rows[index].Selected = true;
        BindCurrentCall(_derivedGrid.Rows[index].Tag as EngineEffectCallSite, EngineEffectCallScope.Derived);
    }

    internal bool SelectCallByAddress(uint address)
    {
        if (SelectGridRow(_unassignedGrid, address, EngineEffectCallScope.Unassigned, _unassignedCallsPage)) return true;
        if (SelectGridRow(_internalGrid, address, EngineEffectCallScope.Internal, _internalCallsPage)) return true;
        if (SelectGridRow(_derivedGrid, address, EngineEffectCallScope.Derived, _derivedCallsPage)) return true;
        return SelectGridRow(_callsGrid, address, EngineEffectCallScope.Effect, _effectCallsPage);
    }

    private bool SelectGridRow(DataGridView grid, uint address, EngineEffectCallScope scope, TabPage page)
    {
        for (var index = 0; index < grid.Rows.Count; index++)
        {
            if (grid.Rows[index].Tag is not EngineEffectCallSite call || call.CallAddress != address) continue;
            _callsTabs.SelectedTab = page;
            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[index].Cells[0];
            grid.Rows[index].Selected = true;
            BindCurrentCall(call, scope);
            return true;
        }
        return false;
    }

    public void BindReport(ProjectWorkspace workspace, EngineEffectScanReport report)
    {
        _workspace = workspace;
        _report = report;
        _rowsById.Clear();
        foreach (var row in report.Effects) _rowsById[row.EffectId] = row;
        _rootInfos = report.RootInfos.ToDictionary(info => info.Address);
        _itemSelectors = null;
        RebuildSuffixChoices(report.EngineVersion);
        _itemChoices = report.Effects.OrderBy(row => row.EffectId).Select(row => new EngineEffectChoiceItem(row.EffectId,
            string.IsNullOrWhiteSpace(row.Name) ? $"未命名特效 {row.EffectId}" : row.Name)).ToArray();
        _jobChoices = _itemChoices;
        var previousBinding = _binding;
        _binding = true;
        try
        {
            EngineEffectChoiceCombo.Fill(_itemIdCombo, _itemChoices);
            EngineEffectChoiceCombo.Fill(_personalIdCombo, _jobChoices);
        }
        finally { _binding = previousBinding; }
        _references = new EngineEffectReferenceService().Build(workspace);
        FillUnassignedCalls(report);
        FillInternalCalls(report);
        FillDerivedCalls(report);
    }

    /// <summary>Item and job numbers share the same names and numbering as the two assignment pools.</summary>
    public void BindEffectChoices(IReadOnlyList<EngineEffectChoiceItem> items, IReadOnlyList<EngineEffectChoiceItem> jobs)
    {
        if (items.Count > 0) _itemChoices = items;
        if (jobs.Count > 0) _jobChoices = jobs;
        if (_report == null) return;
        var item = EngineEffectChoiceCombo.Selected(_itemIdCombo);
        var personal = EngineEffectChoiceCombo.Selected(_personalIdCombo);
        var previousBinding = _binding;
        _binding = true;
        try
        {
            EngineEffectChoiceCombo.Fill(_itemIdCombo, _itemChoices);
            EngineEffectChoiceCombo.Fill(_personalIdCombo, _jobChoices);
            EngineEffectChoiceCombo.Select(_itemIdCombo, item ?? _selectedCall?.ItemEffectId);
            EngineEffectChoiceCombo.Select(_personalIdCombo, personal ?? _selectedCall?.PersonalEffectId);
        }
        finally { _binding = previousBinding; }
    }

    public void Reset()
    {
        _workspace = null;
        _report = null;
        _rowsById.Clear();
        _rootInfos.Clear();
        _itemSelectors = null;
        _itemChoices = [];
        _jobChoices = [];
        _binding = true;
        try
        {
            _itemIdCombo.Items.Clear();
            _personalIdCombo.Items.Clear();
            _suffixCombo.Items.Clear();
            _suffixVersion = string.Empty;
        }
        finally { _binding = false; }
        _references = new Dictionary<int, EngineEffectReferenceSummary>();
        ClearPendingCore();
        _row = null;
        _selectedCall = null;
        _scope = EngineEffectCallScope.None;
        _baseTitle = string.Empty;
        ShowEmpty("尚未打开项目。");
        UpdatePendingUi();
    }

    public void ShowEmpty(string message)
    {
        _row = null;
        _selectedCall = null;
        _scope = EngineEffectCallScope.None;
        _baseTitle = string.Empty;
        _binding = true;
        try
        {
            _title.Text = "引擎扫描";
            _suffixLabel.Text = string.Empty;
            _stateLabel.Text = string.Empty;
            _body.Visible = true;
            _bodyLayout.Visible = true;
            _emptyLabel.Visible = true;
            _emptyLabel.Text = message;
            _emptyLabel.BringToFront();
            _callsGrid.Rows.Clear();
            _unassignedGrid.Rows.Clear();
            _internalGrid.Rows.Clear();
            _derivedGrid.Rows.Clear();
            _referenceGrid.Rows.Clear();
            _callPathsGrid.Rows.Clear();
            _callsEmpty.Visible = false;
            _unassignedEmpty.Visible = false;
            _internalEmpty.Visible = false;
            _derivedEmpty.Visible = false;
            _referencesEmpty.Visible = false;
            _callPathsEmpty.Visible = false;
            _referenceSummary.Text = "只读汇总 Effect.e5 人物槽与 Effectmsg.e5 成长参数；写入请到“拓展数据”页。";
            _writeCallButton.Enabled = false;
            _writeDerivedButton.Enabled = false;
            _evidenceBox.Text = "选择调用点后显示反汇编证据。";
            _effectCallsPage.Text = "调用点（0）";
            _unassignedCallsPage.Text = "未归属调用（0）";
            _internalCallsPage.Text = "引擎内部（0）";
            _derivedCallsPage.Text = "派生/表驱动/循环（0）";
            _referencesPage.Text = "跨文件引用（0）";
            _callPathsPage.Text = "调用链（0）";
            SetTextSectionEnabled(false);
        }
        finally { _binding = false; }
        UpdatePendingUi();
    }

    public void BindEffect(int effectId)
    {
        if (_workspace == null || _report == null)
        {
            ShowEmpty("尚未打开项目。");
            return;
        }
        if (!_rowsById.TryGetValue(effectId, out var row))
        {
            ShowEmpty("当前扫描没有这个特效。");
            return;
        }
        ClearPendingCore();
        _row = row;
        _scope = EngineEffectCallScope.Effect;
        _binding = true;
        try
        {
            _body.Visible = true;
            _bodyLayout.Visible = true;
            _emptyLabel.Visible = false;
            _baseTitle = $"{row.EffectId.ToString(CultureInfo.InvariantCulture)}：" +
                         (string.IsNullOrWhiteSpace(row.Name) ? "未命名" : row.Name);
            _suffixLabel.Text = row.SuffixByte is { } suffix
                ? $"后缀 {EngineEffectSuffixLabels.Label(_report.EngineVersion, suffix)}" +
                  (EngineEffectSuffixLabels.Display(_report.EngineVersion, suffix) is { Length: > 0 } sample
                      ? $"（示例 {sample}）" : string.Empty)
                : "无名称槽";
            _stateLabel.Text = row.OnlyUnreachableCalls
                ? "仅死代码调用（运行时不会触发）"
                : row.CallSiteDetails.Count > 0
                    ? row.DerivedCallDetails.Count > 0 || row.TableCallDetails.Count > 0 ? "静态 + 派生解析" : "静态解析"
                    : row.DerivedCallDetails.Count > 0 || row.TableCallDetails.Count > 0 ? "仅派生解析" : "无静态调用点";
            _stateLabel.ForeColor = row.OnlyUnreachableCalls
                ? Color.Firebrick
                : row.HasCalls ? Color.SeaGreen : Color.DimGray;

            var nameConstraint = new TextFieldConstraint(16, Math.Max(0, row.NameCapacity), true,
                "16 字节名称槽；保留 +0x0F 后缀字节。");
            _nameEditor.Bind(nameConstraint, row.Name);
            _nameEditor.Editor.Select(0, 0);
            SelectSuffix(row.SuffixByte ?? 0);

            var descriptionConstraint = new TextFieldConstraint(200, 199, false,
                "Imsg 200 字节说明槽；末字节保留 NUL 结束符。");
            _descriptionEditor.Bind(descriptionConstraint, row.Description);
            _descriptionEditor.Editor.Select(0, 0);

            SetTextSectionEnabled(true);
            FillCalls(row);
            FillReferences(row);
            if (_callsGrid.Rows.Count > 0)
            {
                _callsGrid.CurrentCell = _callsGrid.Rows[0].Cells[0];
                _callsGrid.Rows[0].Selected = true;
            }
        }
        finally { _binding = false; }
        var firstCall = row.CallSiteDetails.FirstOrDefault() ?? row.DerivedCallDetails.FirstOrDefault() ??
                        row.TableCallDetails.FirstOrDefault() ?? row.LoopCallDetails.FirstOrDefault();
        BindCurrentCall(firstCall,
            firstCall?.Pattern is EngineEffectCallPattern.WrapperDerived or EngineEffectCallPattern.TableDerived
                or EngineEffectCallPattern.LoopDerived or EngineEffectCallPattern.NestedDerived
                ? EngineEffectCallScope.Derived
                : EngineEffectCallScope.Effect);
        UpdatePendingUi();
    }

    public void DiscardPendingChanges()
    {
        if (!HasPendingChanges) return;
        var id = CurrentEffectId;
        ClearPendingCore();
        UpdatePendingUi();
        if (id is { } value) BindEffect(value);
    }

    private void BuildChoiceItems()
    {
        foreach (var target in EngineEffectScanService.KnownEntryTargets)
            _entryCombo.Items.Add(new EntryChoice(target,
                $"0x{target:X8} {EngineEffectScanService.EntryNames[target]}"));
        for (var value = 0; value <= byte.MaxValue; value++)
            _valueModeCombo.Items.Add(new ModeChoice(value, value switch
            {
                0 => "0 读数值",
                1 => "1 返回开关",
                _ => $"{value.ToString(CultureInfo.InvariantCulture)} 未验证"
            }));
        for (var value = 0; value <= 3; value++)
            _stackingCombo.Items.Add(new StackingChoice(value, $"{value.ToString(CultureInfo.InvariantCulture)} {StackingText(value)}"));
    }

    /// <summary>The suffix selector differs per base; rebuild it whenever a scan report is bound.</summary>
    private void RebuildSuffixChoices(string version)
    {
        var previous = _binding;
        _binding = true;
        try
        {
            _suffixCombo.Items.Clear();
            _suffixVersion = version;
            var maximum = EngineEffectSuffixLabels.MaximumSuffix(version);
            for (var value = 0; value <= maximum; value++)
            {
                var sample = EngineEffectSuffixLabels.Display(version, value);
                var text = sample.Length > 0
                    ? $"{EngineEffectSuffixLabels.Label(version, value)}（示例 {sample}）"
                    : EngineEffectSuffixLabels.Label(version, value);
                _suffixCombo.Items.Add(new SuffixChoice((byte)value, text));
            }
        }
        finally { _binding = previous; }
    }

    private void WireLayoutEvents()
    {
        _nameEditor.Editor.InputGuard.StateChanged += (_, _) => CaptureNamePending();
        _descriptionEditor.Editor.InputGuard.StateChanged += (_, _) => CaptureDescriptionPending();
        _suffixCombo.SelectedIndexChanged += (_, _) => CaptureSuffixPending();
        _entryCombo.SelectedIndexChanged += (_, _) => CaptureCallPending();
        _valueModeCombo.SelectedIndexChanged += (_, _) => CaptureCallPending();
        _stackingCombo.SelectedIndexChanged += (_, _) => CaptureCallPending();
        _itemIdCombo.SelectedIndexChanged += (_, _) => CaptureCallPending();
        _personalIdCombo.SelectedIndexChanged += (_, _) => CaptureCallPending();
        _callsGrid.SelectionChanged += (_, _) =>
        {
            if (_binding) return;
            var call = _callsGrid.CurrentRow?.Tag as EngineEffectCallSite;
            if (call == null && _callsGrid.Rows.Count > 0) return;
            BindCurrentCall(call, EngineEffectCallScope.Effect);
        };
        _callsTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_binding) return;
            if (_callsTabs.SelectedTab == _unassignedCallsPage)
                SelectFirst(_unassignedGrid, EngineEffectCallScope.Unassigned);
            else if (_callsTabs.SelectedTab == _internalCallsPage)
                SelectFirst(_internalGrid, EngineEffectCallScope.Internal);
            else if (_callsTabs.SelectedTab == _derivedCallsPage)
                SelectFirst(_derivedGrid, EngineEffectCallScope.Derived);
            else if (_callsTabs.SelectedTab == _effectCallsPage)
                SelectFirst(_callsGrid, EngineEffectCallScope.Effect);
        };
        _writeButton.Click += (_, _) => TryCommitPending(FindForm());
        _revertButton.Click += (_, _) =>
        {
            if (!HasPendingChanges) return;
            if (MessageBox.Show(FindForm(), "放弃当前特效的未写入修改？", "引擎扫描",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            DiscardPendingChanges();
        };
        _writeCallButton.Click += (_, _) => WriteSelectedUnassignedCall();
        _writeDerivedButton.Click += (_, _) => WriteSelectedDerivedCall();
        FontChanged += (_, _) => FitSections();
        DpiChangedAfterParent += (_, _) => FitSections();
        HandleCreated += (_, _) => FitSections();
    }

    private void SelectFirst(DataGridView grid, EngineEffectCallScope scope)
    {
        if (grid.Rows.Count == 0)
        {
            BindCurrentCall(null, scope);
            return;
        }
        grid.ClearSelection();
        grid.CurrentCell = grid.Rows[0].Cells[0];
        grid.Rows[0].Selected = true;
        BindCurrentCall(grid.Rows[0].Tag as EngineEffectCallSite, scope);
    }

    private void FillCalls(EngineEffectScanRow row)
    {
        _callsGrid.Rows.Clear();
        var calls = row.CallSiteDetails.Concat(row.DerivedCallDetails).Concat(row.TableCallDetails)
            .Concat(row.LoopCallDetails).ToArray();
        foreach (var call in calls)
        {
            var index = _callsGrid.Rows.Add(
                $"0x{call.CallAddress:X8}",
                call.FunctionEntry is { } entry ? $"0x{entry:X8}" : "—",
                PatternText(call),
                call.EntryName,
                StackingText(call.StackingMode),
                ValueModeText(call.ValueMode),
                ValueText(call.ItemEffectId),
                PersonalText(call),
                CallStatusText(call));
            var gridRow = _callsGrid.Rows[index];
            gridRow.Tag = call;
            foreach (DataGridViewCell cell in gridRow.Cells) cell.ToolTipText = call.DisassemblyText;
        }
        _effectCallsPage.Text = $"调用点（{calls.Length}）";
        _callsEmpty.Visible = calls.Length == 0;
        if (_callsEmpty.Visible) { _callsEmpty.Text = NoCallText(row); _callsEmpty.BringToFront(); }
    }

    private string NoCallText(EngineEffectScanRow row)
    {
        var lines = new List<string>();
        if (_references.TryGetValue(row.EffectId, out var reference) &&
            reference.PersonAssignments.Count > 0)
        {
            var names = string.Join("、", reference.PersonAssignments
                .Select(item => $"{item.PersonName}（槽 {item.Slot}，值 {item.Value}）")
                .Take(4));
            lines.Add($"Effect.e5 已分配给 {reference.PersonAssignments.Count} 处人物：{names}" +
                      (reference.PersonAssignments.Count > 4 ? "…" : string.Empty));
        }
        if (row.AssignmentCovered)
            lines.Add(AssignmentRowText(row));
        if (lines.Count > 0)
        {
            lines.Add("运行期由通用分派链按分配数据执行，没有专属调用点；写入入口在“特效分配”页。");
        }
        else if (SelectorsFor(row.EffectId) is { Count: > 0 } items)
        {
            var names = string.Join("、", items.Take(4).Select(item => $"{item.Id}:{item.Name}"));
            lines.Add($"装备 selector 引用：物品 {names}" + (items.Count > 4 ? "…" : string.Empty));
            lines.Add("物品行的特效号字段在运行期由通用分派执行；改 selector 请到“宝物特效”页。");
        }
        else
        {
            lines.Add(string.IsNullOrWhiteSpace(row.Name)
                ? "空名称槽（保留）"
                : "已定义，当前数据未引用（名称/后缀/说明可写）");
        }
        var chain = _report?.Diagnostics.FirstOrDefault(line =>
            line.Contains("通用分派链", StringComparison.Ordinal));
        var state = string.Join(Environment.NewLine, lines);
        return chain == null ? state : state + Environment.NewLine + Environment.NewLine + chain;
    }

    /// <summary>静态分配表行原文：行号=特效号，行内是人物槽与兵种槽。</summary>
    private static string AssignmentRowText(EngineEffectScanRow row)
    {
        static string Slots(IReadOnlyList<int> ids, IReadOnlyList<int> values, string empty) =>
            ids.Count == 0
                ? empty
                : string.Join("、", ids.Select((id, index) =>
                    $"{id}（值 {(index < values.Count ? values[index] : 0)}）"));
        return $"静态分配表行（文件+0x{row.AssignmentRowOffset:X}）：人物槽 " +
               Slots(row.AssignmentPersonIds, row.AssignmentPersonValues, "空") +
               "；兵种槽 " + Slots(row.AssignmentJobIds, row.AssignmentJobValues, "空");
    }

    /// <summary>装备 selector → 物品明细，只在打开“无调用点”页时惰性构建一次。</summary>
    private List<EngineEffectItemEntity> SelectorsFor(int effectId)
    {
        if (_itemSelectors == null)
        {
            _itemSelectors = [];
            try
            {
                var catalog = new EngineEffectAssignmentService().Read(_workspace!, _report!);
                foreach (var item in catalog.Items)
                {
                    if (item.SelectorFileOffset < 0 || item.NeedsSpecialType || item.Selector is < 0 or >= 255)
                        continue;
                    if (!_itemSelectors.TryGetValue(item.Selector, out var list))
                    {
                        list = [];
                        _itemSelectors[item.Selector] = list;
                    }
                    list.Add(item);
                }
            }
            catch (Exception)
            {
                // 布局未识别时不阻断详情页；退化为“无装备 selector 证据”。
            }
        }
        return _itemSelectors.TryGetValue(effectId, out var items) ? items : [];
    }
    private void FillReferences(EngineEffectScanRow row)
    {
        _referenceGrid.Rows.Clear();
        _binding = true;
        try
        {
            if (!_references.TryGetValue(row.EffectId, out var summary))
            {
                _referenceSummary.Text = "当前项目没有可读取的 Effect.e5 / Effectmsg.e5 拓展表；" +
                                         "跨文件引用不可用，请在“拓展数据”页确认表状态。";
                _referencesEmpty.Text = "没有跨文件引用";
                _referencesEmpty.Visible = true;
                _referencesEmpty.BringToFront();
                _referencesPage.Text = "跨文件引用（0）";
                return;
            }
            _referenceSummary.Text = summary.Growth.Summary + Environment.NewLine +
                $"人物槽引用 {summary.PersonAssignments.Count} 处（Effect.e5，行 ID = 人物 ID；只读）。" +
                (row.AssignmentCovered
                    ? Environment.NewLine + AssignmentRowText(row) + "（Ekd5.exe 静态分配表，可在“特效分配”页写）"
                    : string.Empty);
            foreach (var item in summary.PersonAssignments)
            {
                var index = _referenceGrid.Rows.Add(
                    item.PersonId.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(item.PersonName) ? "未命名人物" : item.PersonName,
                    item.Slot.ToString(CultureInfo.InvariantCulture),
                    item.Value.ToString(CultureInfo.InvariantCulture),
                    item.Experience.ToString(CultureInfo.InvariantCulture),
                    item.RemainingTurns.ToString(CultureInfo.InvariantCulture));
                var gridRow = _referenceGrid.Rows[index];
                gridRow.Tag = item;
                foreach (DataGridViewCell cell in gridRow.Cells)
                    cell.ToolTipText = $"Effect.e5 行 {item.PersonId}（{item.PersonName}）{item.SlotDisplay}";
            }
            _referencesEmpty.Text = "没有人物槽引用";
            _referencesEmpty.Visible = summary.PersonAssignments.Count == 0;
            if (_referencesEmpty.Visible) _referencesEmpty.BringToFront();
            _referencesPage.Text = $"跨文件引用（{summary.PersonAssignments.Count}）";
        }
        finally { _binding = false; }
    }

    private void FillCallPaths(EngineEffectCallSite? call)
    {
        _callPathsGrid.Rows.Clear();
        var paths = call?.CallerPaths ?? [];
        if (paths.Count > 0)
        {
            _binding = true;
            try
            {
                var index = 0;
                foreach (var path in paths)
                {
                    index++;
                    var root = path.RootEntry is { } rootEntry
                        ? $"0x{rootEntry:X8}" + (path.RootIsEntryPoint
                            ? "（PE 入口）"
                            : _rootInfos.TryGetValue(rootEntry, out var rootInfo)
                                ? $"（{rootInfo.Kind}）"
                                : string.Empty)
                        : "—";
                    var status = path.PathLimitReached ? "路径上限"
                        : path.IsCycle ? "调用环"
                        : path.UnknownCaller ? "调用者未知"
                        : path.Truncated ? "深度上限"
                        : path.RootEntry != null ? "到 E8 根" : "未解析";
                    var rowIndex = _callPathsGrid.Rows.Add(
                        index.ToString(CultureInfo.InvariantCulture),
                        path.Nodes.Count.ToString(CultureInfo.InvariantCulture),
                        root,
                        status,
                        path.Summary);
                    var row = _callPathsGrid.Rows[rowIndex];
                    row.Tag = path;
                    foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = path.Summary;
                }
            }
            finally { _binding = false; }
        }
        _callPathsEmpty.Visible = paths.Count == 0;
        if (_callPathsEmpty.Visible) _callPathsEmpty.BringToFront();
        _callPathsPage.Text = call == null
            ? "调用链（0）"
            : $"调用链（{paths.Count} 条 / {call.ConsumerRootCount} 个 E8 根" +
              (call.ReachesProcessEntry ? "，含 PE 入口" : string.Empty) + "）";
    }

    private void FillDerivedCalls(EngineEffectScanReport report)
    {
        _derivedGrid.Rows.Clear();
        _binding = true;
        try
        {
            foreach (var call in report.DerivedCalls.Concat(report.TableCalls).Concat(report.LoopCalls))
            {
                var index = _derivedGrid.Rows.Add(
                    $"0x{call.CallAddress:X8}",
                    call.WrapperStubCallAddress is { } stub ? $"0x{stub:X8}" : "—",
                    call.WrapperStubEntry is { } entry ? $"0x{entry:X8}" : "—",
                    ArgumentSummary(call),
                    CallStatusText(call),
                    ReasonSummary(call));
                var row = _derivedGrid.Rows[index];
                row.Tag = call;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = call.DisassemblyText;
            }
        }
        finally { _binding = false; }
        var total = report.DerivedCalls.Count + report.TableCalls.Count + report.LoopCalls.Count;
        _derivedCallsPage.Text = $"派生/表驱动/循环（{total}）";
        _derivedEmpty.Visible = total == 0;
        if (_derivedEmpty.Visible) _derivedEmpty.BringToFront();
        _writeDerivedButton.Enabled = false;
    }

    private void FillUnassignedCalls(EngineEffectScanReport report)
    {
        _unassignedGrid.Rows.Clear();
        _binding = true;
        try
        {
            foreach (var call in report.UnassignedCalls)
            {
                var index = _unassignedGrid.Rows.Add(
                    $"0x{call.CallAddress:X8}",
                    call.FunctionEntry is { } entry ? $"0x{entry:X8}" : "—",
                    PatternText(call.Pattern),
                    call.EntryName,
                    ArgumentSummary(call),
                    ReasonSummary(call));
                var row = _unassignedGrid.Rows[index];
                row.Tag = call;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = call.DisassemblyText;
            }
        }
        finally { _binding = false; }
        _unassignedCallsPage.Text = $"未归属调用（{report.UnassignedCalls.Count}）";
        _unassignedEmpty.Visible = report.UnassignedCalls.Count == 0;
        if (_unassignedEmpty.Visible) _unassignedEmpty.BringToFront();
        _writeCallButton.Enabled = false;
    }

    private void FillInternalCalls(EngineEffectScanReport report)
    {
        _internalGrid.Rows.Clear();
        _binding = true;
        try
        {
            foreach (var call in report.InternalCalls)
            {
                var index = _internalGrid.Rows.Add(
                    $"0x{call.CallAddress:X8}",
                    call.FunctionEntry is { } entry ? $"0x{entry:X8}" : "—",
                    call.EntryName,
                    call.InternalReason ?? "引擎入口函数体内调用");
                var row = _internalGrid.Rows[index];
                row.Tag = call;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = call.DisassemblyText;
            }
        }
        finally { _binding = false; }
        _internalCallsPage.Text = $"引擎内部（{report.InternalCalls.Count}）";
        _internalEmpty.Visible = report.InternalCalls.Count == 0;
        if (_internalEmpty.Visible) _internalEmpty.BringToFront();
    }

    private void UnassignedSelectionChanged()
    {
        if (_binding) return;
        var call = _unassignedGrid.CurrentRow?.Tag as EngineEffectCallSite;
        if (call == null && _unassignedGrid.Rows.Count > 0) return;
        BindCurrentCall(call, EngineEffectCallScope.Unassigned);
    }

    private void InternalSelectionChanged()
    {
        if (_binding) return;
        var call = _internalGrid.CurrentRow?.Tag as EngineEffectCallSite;
        if (call == null && _internalGrid.Rows.Count > 0) return;
        BindCurrentCall(call, EngineEffectCallScope.Internal);
    }

    private void DerivedSelectionChanged()
    {
        if (_binding) return;
        var call = _derivedGrid.CurrentRow?.Tag as EngineEffectCallSite;
        if (call == null && _derivedGrid.Rows.Count > 0) return;
        BindCurrentCall(call, EngineEffectCallScope.Derived);
    }

    private void BindCurrentCall(EngineEffectCallSite? call, EngineEffectCallScope scope)
    {
        var previous = _binding;
        _binding = true;
        try
        {
            _selectedCall = call;
            _scope = call == null ? EngineEffectCallScope.None : scope;
            var effect = _scope == EngineEffectCallScope.Effect && call != null;
            var derived = _scope == EngineEffectCallScope.Derived && call != null;
            var unassigned = _scope == EngineEffectCallScope.Unassigned && call != null;
            var editable = effect || derived;
            // Loop expansion and nested wrappers are always read-only: the id is
            // computed by a counter/base, so a single immediate cannot pin it.
            var immutablePattern = call is
            {
                Pattern: EngineEffectCallPattern.LoopDerived or EngineEffectCallPattern.NestedDerived
            };
            SetTextSectionEnabled(editable);
            _entryCombo.Enabled = effect;
            _valueModeCombo.Enabled = editable && !immutablePattern && call!.ValuePush.WritePlan.IsWritable;
            _stackingCombo.Enabled = editable && !immutablePattern && call!.StackingPush.WritePlan.IsWritable;
            _itemIdCombo.Enabled = editable && !immutablePattern && call!.ItemPush.WritePlan.IsWritable;
            _personalIdCombo.Enabled = editable && !immutablePattern && call!.PersonalPush.WritePlan.IsWritable;
            _writeCallButton.Enabled = unassigned;
            _writeDerivedButton.Enabled = derived && call!.EffectRange.Count == 0 &&
                call!.Pattern is not (EngineEffectCallPattern.TableDerived or EngineEffectCallPattern.LoopDerived
                    or EngineEffectCallPattern.NestedDerived);
            if (call == null)
            {
                _callSectionLabel.Text = "当前调用点 0x00000000（没有选中调用点）";
                _functionEntryLabel.Text = "—";
                _entryCombo.SelectedIndex = -1;
                _valueModeCombo.SelectedIndex = -1;
                _stackingCombo.SelectedIndex = -1;
                _itemIdCombo.SelectedIndex = -1;
                _personalIdCombo.SelectedIndex = -1;
                UpdateEvidence(null);
                FillCallPaths(null);
                return;
            }
            _callSectionLabel.Text = scope switch
            {
                EngineEffectCallScope.Effect => $"当前调用点 0x{call.CallAddress:X8}",
                EngineEffectCallScope.Unassigned => $"未归属调用 0x{call.CallAddress:X8}（只读展示，写入请用“写入此调用”）",
                EngineEffectCallScope.Internal => $"引擎内部调用 0x{call.CallAddress:X8}",
                EngineEffectCallScope.Derived => $"派生调用 0x{call.CallAddress:X8}" +
                    (call.WrapperStubCallAddress is { } stub ? $"（桩内 0x{stub:X8}）" : string.Empty),
                _ => $"调用点 0x{call.CallAddress:X8}"
            };
            var edit = scope is EngineEffectCallScope.Effect or EngineEffectCallScope.Derived
                ? _pendingCalls.GetValueOrDefault(call.Identity)
                : null;
            _functionEntryLabel.Text = call.FunctionEntry is { } entry ? $"0x{entry:X8}" : "—";
            SelectEntry(edit?.EntryTarget ?? call.EntryTarget);
            _valueModeCombo.SelectedIndex = edit?.ValueMode ?? call.ValueMode ?? -1;
            _stackingCombo.SelectedIndex = edit?.StackingMode ?? call.StackingMode ?? -1;
            EngineEffectChoiceCombo.Select(_itemIdCombo, edit?.ItemEffectId ?? call.ItemEffectId ?? 0);
            EngineEffectChoiceCombo.Select(_personalIdCombo, edit?.PersonalEffectId ?? call.PersonalEffectId ?? 0);
            UpdateEvidence(call);
            FillCallPaths(call);
        }
        finally { _binding = previous; }
    }

    private void SetTextSectionEnabled(bool enabled)
    {
        _nameEditor.Enabled = enabled && _row?.HasNameSlot == true;
        _suffixCombo.Enabled = enabled && _row?.HasNameSlot == true && _row.SuffixByte != null;
        _descriptionEditor.Enabled = enabled && _row?.HasDescriptionSlot == true;
    }

    private void UpdateEvidence(EngineEffectCallSite? call)
    {
        if (call == null)
        {
            _evidenceBox.Text = "选择调用点后显示反汇编证据。";
            return;
        }
        var builder = new StringBuilder();
        builder.AppendLine($"调用点 0x{call.CallAddress:X8}  入口 {call.EntryName}  " +
                           $"函数 {(call.FunctionEntry is { } entry ? $"0x{entry:X8}（+0x{call.FunctionEntryDistance:X}）" : "—")}  " +
                           $"桩类型 {PatternText(call.Pattern)}  状态 {StatusText(call.Status)}");
        if (call.FunctionEntry is { } functionEntry &&
            _report?.FunctionRoles.FirstOrDefault(role => role.Entry == functionEntry) is { } functionRole)
        {
            builder.AppendLine($"函数角色：{functionRole.Role}；{functionRole.Evidence}");
            var branchPath = functionRole.BranchPaths.FirstOrDefault(path => path.CallAddress == call.CallAddress);
            if (branchPath != null)
            {
                builder.AppendLine($"判定路径（{branchPath.TargetName}）：{branchPath.Summary}");
                if (branchPath.PathCount > 0)
                    builder.AppendLine($"路径合并：{branchPath.MultiPathSummary}");
                if (branchPath.ConditionGroups.Count > 0)
                {
                    builder.AppendLine($"条件组（DNF）{branchPath.ConditionGroups.Count} 组" +
                                       (branchPath.ConditionGroupsTruncated ? "（达上限）" : string.Empty) + "：");
                    foreach (var group in branchPath.ConditionGroups.Take(8))
                        builder.AppendLine("  " + group.Summary);
                    if (branchPath.ConditionGroups.Count > 8)
                        builder.AppendLine($"  …其余 {branchPath.ConditionGroups.Count - 8} 组见导出证据。");
                }
                if (branchPath.ConditionDecisionSummary.Length > 0)
                    builder.AppendLine("决策点与组合覆盖：" + branchPath.ConditionDecisionSummary);
                if (branchPath.Conditions.Count > 0)
                    builder.AppendLine("条件来源：" + JoinConditionSources(branchPath.Conditions));
                if (branchPath.OptionalConditions.Count > 0)
                    builder.AppendLine("路径特有守卫：" + JoinConditionSources(branchPath.OptionalConditions));
                if (branchPath.UpstreamConditions.Count > 0)
                    builder.AppendLine("上游条件来源：" + JoinConditionSources(branchPath.UpstreamConditions));
                if (branchPath.UpstreamOptionalConditions.Count > 0)
                    builder.AppendLine("上游路径特有守卫：" +
                                       JoinConditionSources(branchPath.UpstreamOptionalConditions));
                if (branchPath.UpstreamPathCount > 1)
                    builder.AppendLine($"上游路径合并：{branchPath.UpstreamMultiPathSummary}");
                if (branchPath.UpstreamConditionGroups.Count > 0)
                {
                    builder.AppendLine($"上游条件组（DNF）{branchPath.UpstreamConditionGroups.Count} 组" +
                                       (branchPath.UpstreamConditionGroupsTruncated ? "（达上限）" : string.Empty) + "：");
                    foreach (var group in branchPath.UpstreamConditionGroups.Take(8))
                        builder.AppendLine("  " + group.Summary);
                    if (branchPath.UpstreamConditionGroups.Count > 8)
                        builder.AppendLine($"  …其余 {branchPath.UpstreamConditionGroups.Count - 8} 组见导出证据。");
                }
                if (branchPath.UpstreamConditionDecisionSummary.Length > 0)
                    builder.AppendLine("上游决策点与组合覆盖：" + branchPath.UpstreamConditionDecisionSummary);
                if (branchPath.InboundJumpAddress is { } jump)
                    builder.AppendLine($"入边跳转：0x{jump:X8} {branchPath.InboundJumpKind}" +
                                       (branchPath.InboundJumpFunction is { } owner
                                           ? $"（发起函数 0x{owner:X8}）"
                                           : string.Empty) +
                                       (branchPath.UpstreamConditions.Count > 0
                                           ? $"；上游守卫 {branchPath.UpstreamConditions.Count} 个"
                                           : string.Empty));
            }
        }
        if (!string.IsNullOrWhiteSpace(call.TriggerSummary))
            builder.AppendLine("触发条件摘要：" + call.TriggerSummary);
        if (!string.IsNullOrWhiteSpace(call.Reachability)) builder.AppendLine("可达性：" + call.Reachability);
        if (call.CallerPaths.Count > 0)
        {
            builder.AppendLine($"E8 调用链 {call.CallerPaths.Count} 条，E8 根 {call.ConsumerRootCount} 个" +
                               (call.ReachesProcessEntry ? "（含 PE 入口）" : string.Empty) + "：");
            foreach (var path in call.CallerPaths.Take(8)) builder.AppendLine("  " + path.Summary);
            if (call.CallerPaths.Count > 8)
                builder.AppendLine($"  …其余 {call.CallerPaths.Count - 8} 条见“调用链”页签。");
        }
        if (call.ConsumerRoots.Count > 0)
        {
            builder.AppendLine("E8 根类型：");
            foreach (var rootAddress in call.ConsumerRoots.Take(8))
            {
                if (_rootInfos.TryGetValue(rootAddress, out var rootInfo))
                    builder.AppendLine("  " + rootInfo.Evidence);
                else
                    builder.AppendLine($"  0x{rootAddress:X8}：未分类");
            }
        }
        if (!string.IsNullOrWhiteSpace(call.InternalReason)) builder.AppendLine("内部：" + call.InternalReason);
        builder.AppendLine($"调用字节 {Hex(call.CallInstruction.RawBytes)}  目标 0x{call.EntryTarget:X8}");
        builder.AppendLine(call.DisassemblyText);
        if (call.Callees.Count > 0)
            builder.AppendLine("函数体调用：" + string.Join("；", call.Callees.Select(item =>
                $"0x{item.Address:X8}→0x{item.Target:X8}" +
                (item.TargetName.Length > 0 ? $"（{item.TargetName}）" : string.Empty))));
        if (!string.IsNullOrWhiteSpace(call.ConsumerSummary)) builder.AppendLine("消费点：" + call.ConsumerSummary);
        _evidenceBox.Text = builder.ToString();
        _evidenceBox.Select(0, 0);
    }

    private static string JoinConditionSources(IReadOnlyList<EngineEffectBranchCondition> conditions)
        => string.Join("；", conditions.Select(condition =>
            $"{condition.Comparison ?? condition.Instruction} → {condition.Operand}={condition.OperandKind}" +
            (condition.OperandOrigin.Length > 0 ? $"（{condition.OperandOrigin}）" : string.Empty)));

    private void WriteSelectedUnassignedCall()
    {
        if (_workspace == null || _report == null) return;
        if (_unassignedGrid.CurrentRow?.Tag is not EngineEffectCallSite call) return;
        WriteCall(call);
    }

    private void WriteSelectedDerivedCall()
    {
        if (_workspace == null || _report == null) return;
        if (_derivedGrid.CurrentRow?.Tag is not EngineEffectCallSite call) return;
        WriteCall(call);
    }

    private void WriteCall(EngineEffectCallSite call)
    {
        using var dialog = new EngineEffectCallWriteDialog(_workspace!, _report!, call, _itemChoices, _jobChoices);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        LastFollowEffectId = null;
        LastWrittenCallAddress = call.CallAddress;
        WriteCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureNamePending()
    {
        if (_binding || _scope is not (EngineEffectCallScope.Effect or EngineEffectCallScope.Derived) ||
            _row == null) return;
        var text = _nameEditor.Editor.Text;
        _pendingName = _row.HasNameSlot && !text.Equals(_row.Name, StringComparison.Ordinal) ? text : null;
        UpdatePendingUi();
    }

    private void CaptureDescriptionPending()
    {
        if (_binding || _scope is not (EngineEffectCallScope.Effect or EngineEffectCallScope.Derived) ||
            _row == null) return;
        var text = _descriptionEditor.Editor.Text;
        _pendingDescription = _row.HasDescriptionSlot && !text.Equals(_row.Description, StringComparison.Ordinal) ? text : null;
        UpdatePendingUi();
    }

    private void CaptureSuffixPending()
    {
        if (_binding || _scope is not (EngineEffectCallScope.Effect or EngineEffectCallScope.Derived) ||
            _row?.SuffixByte is not { } original) return;
        _pendingSuffix = _suffixCombo.SelectedItem is SuffixChoice choice && choice.Value != original
            ? choice.Value
            : null;
        UpdatePendingUi();
    }

    private void CaptureCallPending()
    {
        if (_binding || _scope is not (EngineEffectCallScope.Effect or EngineEffectCallScope.Derived) ||
            _selectedCall == null) return;
        var site = _selectedCall;
        int? valueMode = _valueModeCombo.SelectedIndex >= 0 && _valueModeCombo.SelectedIndex != site.ValueMode
            ? _valueModeCombo.SelectedIndex : null;
        int? stacking = _stackingCombo.SelectedIndex >= 0 && _stackingCombo.SelectedIndex != site.StackingMode
            ? _stackingCombo.SelectedIndex : null;
        var itemId = EngineEffectChoiceCombo.Selected(_itemIdCombo);
        var personalId = EngineEffectChoiceCombo.Selected(_personalIdCombo);
        int? item = itemId != null && itemId != (site.ItemEffectId ?? 0) ? itemId : null;
        int? personal = personalId != null && personalId != (site.PersonalEffectId ?? 0) ? personalId : null;
        uint? entry = _entryCombo.SelectedItem is EntryChoice choice && choice.Target != site.EntryTarget
            ? choice.Target : null;
        var edit = new EngineEffectCallEdit
        {
            Identity = site.Identity,
            CallAddress = site.CallAddress,
            TableIndex = site.Pattern == EngineEffectCallPattern.TableDerived ? site.TableIndex : null,
            ValueMode = valueMode,
            StackingMode = stacking,
            ItemEffectId = item,
            PersonalEffectId = personal,
            EntryTarget = entry
        };
        if (valueMode is null && stacking is null && item is null && personal is null && entry is null)
            _pendingCalls.Remove(site.Identity);
        else
            _pendingCalls[site.Identity] = edit;
        UpdatePendingUi();
    }

    private void SelectSuffix(int value)
    {
        for (var index = 0; index < _suffixCombo.Items.Count; index++)
            if (_suffixCombo.Items[index] is SuffixChoice choice && choice.Value == value)
            {
                _suffixCombo.SelectedIndex = index;
                return;
            }
        if (value is < 0 or > byte.MaxValue)
        {
            _suffixCombo.SelectedIndex = -1;
            return;
        }
        // Historical slots can hold a code outside the base's verified range
        // (6.5 id 254 stores 0x64). Keep it visible but do not add it as a new choice.
        var previous = _binding;
        _binding = true;
        try
        {
            _suffixCombo.Items.Add(new SuffixChoice((byte)value,
                $"{EngineEffectSuffixLabels.Label(_suffixVersion, value)}（当前槽原始值）"));
            _suffixCombo.SelectedIndex = _suffixCombo.Items.Count - 1;
        }
        finally { _binding = previous; }
    }

    private void SelectEntry(uint target)
    {
        for (var index = 0; index < _entryCombo.Items.Count; index++)
            if (_entryCombo.Items[index] is EntryChoice choice && choice.Target == target)
            {
                _entryCombo.SelectedIndex = index;
                return;
            }
        _entryCombo.SelectedIndex = -1;
    }

    private void ClearPendingCore()
    {
        _pendingCalls.Clear();
        _pendingName = null;
        _pendingSuffix = null;
        _pendingDescription = null;
    }

    private void UpdatePendingUi()
    {
        var pending = HasPendingChanges;
        if (_row != null)
        {
            var name = _pendingName ?? _row.Name;
            _baseTitle = $"{_row.EffectId.ToString(CultureInfo.InvariantCulture)}：" +
                         (string.IsNullOrWhiteSpace(name) ? "未命名" : name);
        }
        _title.Text = (_baseTitle.Length == 0 ? "引擎扫描" : _baseTitle) + (pending ? " *" : string.Empty);
        _writeButton.Enabled = pending && _row != null;
        _revertButton.Enabled = pending && _row != null;
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ArgumentSummary(EngineEffectCallSite call)
        => $"值 {ValueText(call.ValueMode)} / 叠 {ValueText(call.StackingMode)} / " +
           $"装 {ValueText(call.ItemEffectId)} / 个人 {PersonalText(call)}";

    private static string ReasonSummary(EngineEffectCallSite call)
    {
        var reason = FirstBlockedReason(call);
        return reason == null ? StatusText(call.Status) : $"{StatusText(call.Status)}：{reason}";
    }

    private static string? FirstBlockedReason(EngineEffectCallSite call)
    {
        var arguments = new (string Name, EngineEffectPushArgument Argument)[]
        {
            ("值", call.ValuePush), ("叠加", call.StackingPush), ("装备", call.ItemPush), ("个人", call.PersonalPush)
        };
        foreach (var (name, argument) in arguments)
            if (!argument.WritePlan.IsWritable && argument.WritePlan.BlockReason != null)
                return $"{name} {argument.WritePlan.BlockReason}";
        return null;
    }

    internal static string PatternTextForDialog(EngineEffectCallPattern pattern) => PatternText(pattern);

    private static string PatternText(EngineEffectCallPattern pattern) => pattern switch
    {
        EngineEffectCallPattern.DirectStub => "直接桩",
        EngineEffectCallPattern.ComputedStub => "计算桩",
        EngineEffectCallPattern.WrapperPassThrough => "上层参数",
        EngineEffectCallPattern.WrapperDerived => "派生包装",
        EngineEffectCallPattern.TableDerived => "表驱动",
        EngineEffectCallPattern.LoopDerived => "循环展开",
        EngineEffectCallPattern.NestedDerived => "二级包装",
        EngineEffectCallPattern.EngineInternal => "引擎内部",
        _ => "未知"
    };

    private static string PatternText(EngineEffectCallSite call)
        => call.EffectRange.Count > 0 ? "范围派生" : PatternText(call.Pattern);

    private static string PersonalText(EngineEffectCallSite call)
        => call.PersonalEffectId is { } value ? value.ToString(CultureInfo.InvariantCulture)
            : call.EffectRange.Count > 0 ? $"{call.EffectRange[0]}–{call.EffectRange[^1]}"
            : "动态";

    private static string ValueText(int? value)
        => value is { } item ? item.ToString(CultureInfo.InvariantCulture) : "动态";

    private static string Hex(byte[] bytes)
        => bytes.Length == 0 ? "—" : string.Join(" ", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private enum EngineEffectCallScope
    {
        None,
        Effect,
        Unassigned,
        Internal,
        Derived
    }
}
