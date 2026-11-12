using System.Globalization;
using System.Text;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly TabControl _engineSubTabs = new() { Dock = DockStyle.Top, Height = 34, Visible = false };
    private readonly EngineAssignmentPanel _engineItemAssignments = new(false) { Visible = false };
    private readonly EngineAssignmentPanel _engineJobAssignments = new(true) { Visible = false };
    private readonly TextBox _engineEntityFilter = new() { Width = 218, PlaceholderText = "筛选左侧编号或名称" };
    private readonly Dictionary<EnginePage, string> _engineEntityFilters = [];
    private readonly EngineEffectAssignmentService _engineAssignmentService = new();
    private EngineEffectAssignmentCatalog? _engineAssignmentCatalog;
    private EngineEffectAssignmentDraft? _engineAssignmentDraft;
    private EngineEffectScanReport? _engineAssignmentReport;
    private string? _engineAssignmentFingerprint;
    private int? _engineSelectedItem;
    private int? _engineSelectedJob;
    private EnginePage _enginePreviousSubpage;
    private bool _bindingEngineSubpage;
    private enum EnginePage { Effects, Items, Jobs, Transform, Constants }
    private EnginePage EngineSubpage => _engineSubTabs.SelectedTab?.Tag is EnginePage page ? page : EnginePage.Effects;
    private bool HasEngineAssignmentChanges => _engineAssignmentDraft?.HasPendingChanges == true;
    private bool HasAnyEngineChanges => _engineDetail.HasPendingChanges || HasEngineAssignmentChanges;

    private void InitializeEngineAssignmentNavigation()
    {
        _engineFilterHost.Controls.Add(_engineCancelScan);
        _engineCancelScan.Click += (_, _) => { CancelEngineScan(); _engineCancelScan.Visible = false; SetStatus("引擎扫描已取消；再次进入子页可重新扫描。"); };
        foreach (var (page, name) in new[] { (EnginePage.Effects, "特效管理"), (EnginePage.Items, "宝物特效"), (EnginePage.Jobs, "兵种特效"), (EnginePage.Transform, "质变设定"), (EnginePage.Constants, "常量") })
            _engineSubTabs.TabPages.Add(new TabPage(name) { Tag = page });
        _engineSubTabs.SelectedIndex = 0;
        _engineSubTabs.Selecting += (_, args) => { if (!PreparePendingEdits()) args.Cancel = true; };
        _engineSubTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_bindingEngineSubpage) return;
            _engineEntityFilters[_enginePreviousSubpage] = _engineEntityFilter.Text;
            _enginePreviousSubpage = EngineSubpage;
            _bindingEngineSubpage = true;
            try { _engineEntityFilter.Text = _engineEntityFilters.GetValueOrDefault(EngineSubpage, string.Empty); }
            finally { _bindingEngineSubpage = false; }
            if (IsEngineTab) ShowEngineScanView();
        };
        _engineEntityFilter.TextChanged += (_, _) =>
        {
            if (_bindingEngineSubpage || !IsEngineTab) return;
            if (EngineSubpage == EnginePage.Effects) EngineFilterChanged();
            else if (EngineSubpage == EnginePage.Transform) PopulateTransformList();
            else PopulateAssignmentEntityList();
        };
        foreach (var panel in new[] { _engineItemAssignments, _engineJobAssignments })
        {
            panel.PreparePoolSelection = row => PrepareEnginePoolSelection(panel.IsJob, row);
            panel.BindRequested += (_, e) => StageEngineAssignment(panel.IsJob, e.Row, remove: false);
            panel.UnbindRequested += (_, e) => StageEngineAssignment(panel.IsJob, e.Row, remove: true);
            panel.ValueRequested += (_, e) => StageEngineAssignmentValue(panel.IsJob, e.Row);
            panel.CommitRequested += (_, _) => TryCommitAllEngineChanges();
            panel.DiscardRequested += (_, _) =>
            {
                if (!HasEngineAssignmentChanges) return;
                if (MessageBox.Show(this, "撤销宝物和兵种页面中的全部分配草稿？", "撤销分配草稿", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                DiscardEngineAssignmentChanges();
                ShowEngineAssignmentView();
                RefreshDirtyState();
            };
        }
        _engineDetail.CommitAllRequested = _ => TryCommitAllEngineChanges();
    }

    private void ResetEngineAssignments()
    {
        CancelEngineScan();
        _engineReportFingerprint = null;
        _engineAssignmentFingerprint = null;
        _engineCancelScan.Visible = false;
        _engineAssignmentCatalog = null;
        _engineAssignmentDraft = null;
        _engineAssignmentReport = null;
        _engineSelectedItem = null;
        _engineSelectedJob = null;
        _engineEntityFilters.Clear();
        _enginePreparedAssignments.Clear();
        _engineItemAssignments.ShowEmpty("尚未打开项目。");
        _engineJobAssignments.ShowEmpty("尚未打开项目。");
    }

    private void EnsureEngineAssignmentCatalog()
    {
        if (_engineReport == null) return;
        if (HasEngineAssignmentChanges) return;
        var fingerprint = _engineAssignmentService.Fingerprint(_workspace, _engineReport);
        if (_engineAssignmentCatalog != null && _engineAssignmentFingerprint == fingerprint) return;
        _engineAssignmentCatalog = _engineAssignmentService.Read(_workspace, _engineReport);
        _engineAssignmentDraft = _engineAssignmentService.CreateDraft(_engineAssignmentCatalog);
        _engineAssignmentReport = _engineReport;
        _engineAssignmentFingerprint = fingerprint;
    }

    /// <summary>特效管理的两个特效号下拉框与宝物/兵种特效页使用同一份编号和名称。</summary>
    private void BindEngineEffectChoices()
    {
        if (_engineReport == null) return;
        try
        {
            EnsureEngineAssignmentCatalog();
            if (_engineAssignmentCatalog is not { } catalog) return;
            var draft = _engineAssignmentDraft ?? _engineAssignmentService.CreateDraft(catalog);
            var effects = _engineAssignmentService.GetEffects(draft);
            _engineDetail.BindEffectChoices(BuildItemEffectChoices(catalog, effects), BuildJobEffectChoices(catalog, effects));
        }
        catch (Exception ex) { SetStatus("特效号下拉列表读取失败：" + ex.Message, true); }
    }

    /// <summary>装备特效号的候选立即数，后缀显示拥有该 selector 的宝物名称。</summary>
    private static IReadOnlyList<EngineEffectChoiceItem> BuildItemEffectChoices(
        EngineEffectAssignmentCatalog catalog, IReadOnlyList<EngineEffectAssignmentEffect> effects)
    {
        var owners = catalog.Items
            .Where(item => item.SelectorFileOffset >= 0 && item.Selector is >= 0 and < 255 &&
                !IsPlaceholderItemName(item.Name))
            .GroupBy(item => item.Selector)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).ToArray());
        var ids = catalog.KnownItemSelectors
            .Concat(effects.SelectMany(effect => effect.ItemSelectors))
            .Where(id => id is >= 0 and < 255)
            .Distinct().Order().ToArray();
        return ids.Select(id => new EngineEffectChoiceItem(id,
            owners.TryGetValue(id, out var names) && names.Length > 0 ? string.Join("/", names) : "未分配")).ToArray();
    }

    private static bool IsPlaceholderItemName(string name)
        => string.IsNullOrWhiteSpace(name) || name is "空闲" or "空槽" or "未使用" or "保留" or "FF-End" or "End";

    /// <summary>兵种特效号的候选立即数，后缀显示拥有该特效行的兵种名称。</summary>
    private static IReadOnlyList<EngineEffectChoiceItem> BuildJobEffectChoices(
        EngineEffectAssignmentCatalog catalog, IReadOnlyList<EngineEffectAssignmentEffect> effects)
    {
        var jobs = catalog.Jobs.ToDictionary(job => job.Id, job => job.Name);
        return effects.Where(effect => effect.PersonalEffectId is >= 0 and < 255)
            .GroupBy(effect => effect.PersonalEffectId!.Value)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var names = group.SelectMany(effect => effect.JobSlots)
                    .Where(slot => slot.JobId != 255)
                    .SelectMany(slot => slot.AffectedJobIds.Count > 0 ? slot.AffectedJobIds : [slot.JobId])
                    .Distinct().Order()
                    .Select(id => jobs.TryGetValue(id, out var name) ? name : $"兵种 {id}")
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal).ToArray();
                return new EngineEffectChoiceItem(group.Key, names.Length > 0 ? string.Join("/", names) : "未分配");
            }).ToArray();
    }

    private void ShowEngineAssignmentView()
    {
        _engineDetail.Visible = false;
        _engineItemAssignments.Visible = EngineSubpage == EnginePage.Items;
        _engineJobAssignments.Visible = EngineSubpage == EnginePage.Jobs;
        var panel = EngineSubpage == EnginePage.Jobs ? _engineJobAssignments : _engineItemAssignments;
        panel.BringToFront();
        _engineSubTabs.SendToBack();
        _engineOnlyWithCalls.Visible = false;
        try
        {
            EnsureEngineAssignmentCatalog();
            PopulateAssignmentEntityList();
            BindSelectedEngineAssignment();
        }
        catch (Exception ex)
        {
            panel.ShowEmpty("分配数据读取失败：" + ex.Message);
            SetStatus("分配数据读取失败：" + ex.Message, true);
        }
    }

    private void PopulateAssignmentEntityList()
    {
        if (_engineAssignmentCatalog is not { } catalog) return;
        var isJob = EngineSubpage == EnginePage.Jobs;
        var current = isJob ? _engineSelectedJob : _engineSelectedItem;
        var entities = isJob ? catalog.Jobs.Select(job => new EngineAssignmentEntityChoice(job.Id, job.Name))
            : catalog.Items.Select(item => new EngineAssignmentEntityChoice(item.Id, item.Name));
        var text = _engineEntityFilter.Text.Trim();
        var choices = entities.Where(item => text.Length == 0 || item.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        _suppressEngineSelection = true;
        _entityList.BeginUpdate();
        try
        {
            _entityList.Items.Clear();
            _entityList.Items.AddRange(choices.Cast<object>().ToArray());
            var index = Array.FindIndex(choices, choice => choice.Id == current);
            if (index < 0 && !isJob && current == null) index = Array.FindIndex(choices, choice => choice.Id == 16);
            if (index < 0 && choices.Length > 0) index = 0;
            if (index >= 0) _entityList.SelectedIndex = index;
        }
        finally { _entityList.EndUpdate(); _suppressEngineSelection = false; }
        _quickLookup.SetOptions((isJob ? catalog.Jobs.Select(job => new IdNameOption(job.Id, job.Name))
            : catalog.Items.Select(item => new IdNameOption(item.Id, item.Name))));
        if (_entityList.SelectedItem is EngineAssignmentEntityChoice selected)
        {
            if (isJob) _engineSelectedJob = selected.Id; else _engineSelectedItem = selected.Id;
            _quickLookup.SetBoundValue(selected.Id.ToString(CultureInfo.InvariantCulture));
        }
        BindSelectedEngineAssignment();
    }

    private void BindSelectedEngineAssignment()
    {
        if (_engineAssignmentDraft is not { } draft || _engineAssignmentCatalog is not { } catalog) return;
        var isJob = EngineSubpage == EnginePage.Jobs;
        var id = isJob ? _engineSelectedJob : _engineSelectedItem;
        var panel = isJob ? _engineJobAssignments : _engineItemAssignments;
        if (id == null || _entityList.Items.Count == 0) { panel.ShowEmpty("没有符合筛选条件的对象。"); return; }
        var item = !isJob ? catalog.Items.FirstOrDefault(item => item.Id == id) : null;
        if (item != null && draft.ItemValues.TryGetValue(item.Id, out var draftValue)) item = item with { Value = draftValue };
        if (item != null && draft.ItemSelectorTargets.TryGetValue(item.Id, out var draftSelector)) item = item with { Selector = draftSelector };
        var job = isJob ? catalog.Jobs.FirstOrDefault(job => job.Id == id) : null;
        var effects = _engineAssignmentService.GetEffects(draft);
        var assigned = isJob ? _engineAssignmentService.GetJobAssignments(draft, id.Value) : _engineAssignmentService.GetItemAssignments(draft, id.Value);
        var assignedRows = new List<EngineAssignmentDisplayRow>();
        foreach (var effect in assigned)
        {
            if (isJob)
            {
                foreach (var slot in effect.JobSlots.Where(slot => !slot.IsFree && (slot.JobId == id || slot.AffectedJobIds.Contains(id.Value))))
                    assignedRows.Add(AssignmentRow(effect, true, item, job, slot, false));
            }
            else assignedRows.Add(AssignmentRow(effect, false, item, job, null, false));
        }
        var assignedKeys = assigned.Select(effect => effect.Key).ToHashSet(StringComparer.Ordinal);
        var pool = effects.Where(effect => (isJob ? effect.JobPoolVisible : effect.ItemPoolVisible) && !assignedKeys.Contains(effect.Key)).Select(effect => AssignmentRow(effect, isJob, item, job, null, true)).ToArray();
        var shared = isJob ? $"兵种匹配：{catalog.JobMatchMode}；{catalog.JobLayoutEvidence}"
            : item == null ? string.Empty : $"当前装备特效号：{item.Selector}；共用：" +
                (item.NeedsSpecialType && !draft.SpecialItemIds.Contains(item.Id) ? "普通类型尚未启用装备特效；分配时转换为同类特殊装备" :
                    item.Selector == 255 || item.SelectorFileOffset < 0 ? "未分配有效特效号" :
                    string.Join("、", catalog.Items.Where(other => other.SelectorFileOffset >= 0 &&
                        (!other.NeedsSpecialType || draft.SpecialItemIds.Contains(other.Id)) &&
                        draft.ItemSelectorTargets.GetValueOrDefault(other.Id, other.Selector) == item.Selector).Select(other => other.Display)));
        panel.BindEntity((isJob ? "兵种特效 · " + job?.Display : "宝物特效 · " + item?.Display), shared, assignedRows, pool, HasAnyEngineChanges);
        SetStatus($"{(isJob ? "兵种" : "宝物")} {id}：已分配 {assignedRows.Count} 项；特效池 {pool.Length} 项。双击可用条目加入草稿。" + (HasEngineAssignmentChanges ? " 分配草稿尚未写入。" : string.Empty));
    }

    private EngineAssignmentDisplayRow AssignmentRow(EngineEffectAssignmentEffect effect, bool isJob,
        EngineEffectItemEntity? item, EngineEffectJobEntity? job, EngineEffectJobSlot? slot, bool pool)
    {
        var number = effect.PersonalEffectId?.ToString(CultureInfo.InvariantCulture) ?? (effect.Kind == EngineEffectAssignmentKind.NativeHp ? "原生HP" : effect.Kind == EngineEffectAssignmentKind.NativeMp ? "原生MP" : "—");
        var name = pool && isJob ? effect.JobPoolDisplay : effect.Name;
        var canSelect = isJob ? effect.CanBindJob : effect.CanBindItem;
        var readOnlyReason = isJob ? effect.JobReadOnlyReason : effect.ItemReadOnlyReason;
        if (isJob && !canSelect)
            readOnlyReason = effect.Kind is EngineEffectAssignmentKind.NativeHp or EngineEffectAssignmentKind.NativeMp
                ? "此原生能力不使用兵种分配槽。"
                : _engineAssignmentCatalog?.JobLayoutVerified == false
                    ? _engineAssignmentCatalog.JobLayoutEvidence
                    : string.IsNullOrWhiteSpace(readOnlyReason) ? "尚未验证此特效的真实兵种分配槽。" : readOnlyReason;
        var valueInfo = DescribeItemValue(effect, item);
        var value = isJob ? slot?.Value.ToString(CultureInfo.InvariantCulture) ??
            (effect.SharedJobValue && effect.JobSlots.Count > 0 ? "共用N=" + effect.JobSlots[0].Value : "双击后填写")
            : valueInfo.Display;
        var description = string.IsNullOrWhiteSpace(effect.Description)
            ? string.Join("；", new[] { effect.Conditions, effect.ValueSource }.Where(text => !string.IsNullOrWhiteSpace(text)))
            : effect.Description;
        var details = new StringBuilder();
        details.AppendLine($"{number}：{effect.Name}").AppendLine(description);
        details.AppendLine("实现状态：" + effect.ImplementationStatus);
        details.AppendLine("来源：" + effect.Source).AppendLine("数值来源：" + effect.ValueSource);
        if (!isJob) details.AppendLine("装备值模式：" + valueInfo.Explanation);
        if (effect.BindingGroup != null) details.AppendLine("本绑定组关联特效：" + string.Join("、", effect.BindingGroup.EffectIds));
        if (effect.SharedJobValue) details.AppendLine("本行人物/兵种共用N；受影响人物ID：" + string.Join("、", effect.SharedPersonIds));
        if (item != null) details.AppendLine($"宝物：{item.Display}；{item.ValueSource}；共用特效号 {item.Selector}");
        if (effect.Conditions.Length > 0) details.AppendLine("生效条件：" + effect.Conditions);
        if (effect.ItemSelectors.Count > 0) details.AppendLine("装备匹配号：" + string.Join("、", effect.ItemSelectors));
        foreach (var point in effect.BindingPoints)
            details.AppendLine($"消费者：0x{point.CallAddress:X8}；入口 0x{point.EntryTarget:X8}；模式 {point.ValueMode}；叠加 {point.StackingMode}；{point.Pattern}");
        foreach (var binding in effect.JobSlots)
        {
            var nameForJob = _engineAssignmentCatalog?.Jobs.FirstOrDefault(candidate => candidate.Id == binding.JobId)?.Display ?? binding.JobId.ToString(CultureInfo.InvariantCulture);
            details.AppendLine(binding.IsFree ? $"槽 {binding.Slot + 1}：空闲（255）" : $"槽 {binding.Slot + 1}：{nameForJob}，N={binding.Value}；{binding.MatchScope}");
            if (!binding.IsFree) details.AppendLine("影响兵种：" + string.Join("、", binding.AffectedJobIds.Select(id => _engineAssignmentCatalog?.Jobs.FirstOrDefault(candidate => candidate.Id == id)?.Display ?? id.ToString(CultureInfo.InvariantCulture))));
            if (!binding.IsFree && binding.PotentialAffectedJobIds.Count > 0) details.AppendLine("切换为同族继承后可能影响：" + string.Join("、", binding.PotentialAffectedJobIds.Select(id => _engineAssignmentCatalog?.Jobs.FirstOrDefault(candidate => candidate.Id == id)?.Display ?? id.ToString(CultureInfo.InvariantCulture))));
        }
        if (slot != null && job != null) details.AppendLine(slot.JobId == job.Id ? "当前选择由本兵种槽直接命中。" : $"当前选择由兵种 {slot.JobId} 的槽继承命中；修改或移除会影响该槽列出的全部兵种。");
        if (!canSelect) details.AppendLine("只读原因：" + readOnlyReason);
        if (HasEngineAssignmentChanges) details.AppendLine("当前显示包含未写入草稿；提交前会展示实际文件差异与影响。");
        var occupiedByOther = !isJob && pool && CurrentItemOwners(effect).Count > 0;
        if (!isJob && CurrentItemOwners(effect).Count > 0) details.AppendLine("已分配给：" + string.Join("、", CurrentItemOwners(effect)));
        return new EngineAssignmentDisplayRow(effect.Key + (slot == null ? string.Empty : ":" + slot.Slot), effect.Key, number, name, value,
            description, details.ToString(), canSelect, occupiedByOther || (pool && isJob && effect.IsJobPoolFull), slot?.Slot,
            isJob ? slot?.Value : valueInfo.Editable ? item?.Value : null, isJob || valueInfo.Editable, readOnlyReason);
    }

    private void SelectEngineAssignmentEntity(int id)
    {
        if (_entityList.Items.Cast<object>().OfType<EngineAssignmentEntityChoice>().All(choice => choice.Id != id)) _engineEntityFilter.Clear();
        var index = _entityList.Items.Cast<object>().ToList().FindIndex(item => item is EngineAssignmentEntityChoice choice && choice.Id == id);
        if (index < 0) { SetStatus($"没有找到{(EngineSubpage == EnginePage.Jobs ? "兵种" : "宝物")} {id}。", true); return; }
        _suppressEngineSelection = true;
        try { _entityList.SelectedIndex = index; }
        finally { _suppressEngineSelection = false; }
        if (EngineSubpage == EnginePage.Jobs) _engineSelectedJob = id; else _engineSelectedItem = id;
        _quickLookup.SetBoundValue(id.ToString(CultureInfo.InvariantCulture));
        BindSelectedEngineAssignment();
    }

    private void StageEngineAssignment(bool isJob, EngineAssignmentDisplayRow row, bool remove)
    {
        if (_engineAssignmentDraft is not { } draft || !row.Selectable) return;
        if (!remove && !PrepareEnginePoolSelection(isJob, row)) return;
        EngineEffectAssignmentStageResult result;
        if (!isJob)
        {
            if (_engineSelectedItem is not { } itemId) return;
            result = remove ? _engineAssignmentService.StageItemUnbind(draft, row.Key) : _engineAssignmentService.StageItemBind(draft, row.Key, itemId);
        }
        else
        {
            if (_engineSelectedJob is not { } jobId) return;
            if (remove)
            {
                if (row.Slot is not { } slot) return;
                var effect = _engineAssignmentService.GetEffects(draft).FirstOrDefault(effect => effect.Key == row.Key);
                var binding = effect?.JobSlots.FirstOrDefault(binding => binding.Slot == slot);
                if (binding == null || !ConfirmEngineSlotRemoval(effect!.Name, binding, jobId)) return;
                result = _engineAssignmentService.StageJobUnbind(draft, row.Key, slot);
            }
            else
            {
                var effect = _engineAssignmentService.GetEffects(draft).FirstOrDefault(effect => effect.Key == row.Key);
                if (effect == null) return;
                var replacement = _enginePreparedAssignments.GetValueOrDefault(PreparationKey(true, effect));
                var value = effect.SharedJobValue ? effect.JobSlots[0].Value : RequestEngineAssignmentValue(effect.Name, null);
                if (value is not { } enteredValue) return;
                result = _engineAssignmentService.StageJobBind(draft, row.Key, jobId, enteredValue, replacement);
            }
        }
        if (!result.Success) MessageBox.Show(this, result.Message, "特效分配", MessageBoxButtons.OK, MessageBoxIcon.Information);
        BindSelectedEngineAssignment();
        RefreshDirtyState();
        SetStatus(result.Message, !result.Success);
    }

    private void StageEngineAssignmentValue(bool isJob, EngineAssignmentDisplayRow row)
    {
        if (_engineAssignmentDraft is not { } draft || !row.Selectable || !row.CanEditValue) return;
        var effect = draft.Catalog.Effects.FirstOrDefault(effect => effect.Key == row.Key);
        if (isJob && effect?.SharedJobValue == true && !ConfirmEngineAssignment("修改本行共用N",
                "此值由本行人物/兵种共用。人物ID：" + string.Join("、", effect.SharedPersonIds) + "。继续修改？")) return;
        var value = RequestEngineAssignmentValue(row.Name, row.NumericValue);
        if (value is not { } enteredValue) return;
        var result = isJob && row.Slot is { } slot ? _engineAssignmentService.StageJobValue(draft, row.Key, slot, enteredValue)
            : !isJob && _engineSelectedItem is { } item ? _engineAssignmentService.StageItemValue(draft, item, enteredValue)
            : EngineEffectAssignmentStageResult.Failed("没有可修改的数值来源。");
        if (!result.Success) MessageBox.Show(this, result.Message, "特效数值", MessageBoxButtons.OK, MessageBoxIcon.Information);
        BindSelectedEngineAssignment();
        RefreshDirtyState();
        SetStatus(result.Message, !result.Success);
    }

    private void DiscardEngineAssignmentChanges() { _engineAssignmentDraft?.Clear(); _enginePreparedAssignments.Clear(); }

    private bool TryCommitAllEngineChanges()
    {
        try
        {
            var result = CommitPendingEdits();
            SetStatus(string.Join(Environment.NewLine, result.Messages), !result.Saved);
            if (!result.Saved) MessageBox.Show(this, string.Join(Environment.NewLine, result.Messages), "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshDirtyState();
            return result.Saved;
        }
        catch (Exception ex) { SetStatus(ex.Message, true); return false; }
    }

    private static IReadOnlyList<EngineEffectWriteChange> UnifiedEngineChanges(IReadOnlyList<WorkspaceBinaryCandidate> candidates)
    {
        var changes = new List<EngineEffectWriteChange>();
        foreach (var candidate in candidates)
        {
            for (var offset = 0; offset < candidate.AfterBytes.Length;)
            {
                if (offset < candidate.BeforeBytes.Length && candidate.BeforeBytes[offset] == candidate.AfterBytes[offset]) { offset++; continue; }
                var start = offset++;
                while (offset < candidate.AfterBytes.Length && (offset >= candidate.BeforeBytes.Length || candidate.BeforeBytes[offset] != candidate.AfterBytes[offset])) offset++;
                changes.Add(new EngineEffectWriteChange { EffectId = -1, FilePath = candidate.FilePath, FileOffset = start, DisplayName = candidate.Module,
                    BeforeBytes = candidate.BeforeBytes.Skip(start).Take(offset - start).ToArray(), AfterBytes = candidate.AfterBytes[start..offset] });
            }
        }
        return changes;
    }

    internal TabControl EngineSubTabsForSmokeTest => _engineSubTabs;
    internal EngineAssignmentPanel EngineItemsForSmokeTest => _engineItemAssignments;
    internal EngineAssignmentPanel EngineJobsForSmokeTest => _engineJobAssignments;
    internal EngineEffectAssignmentDraft? EngineAssignmentDraftForSmokeTest => _engineAssignmentDraft;
    internal void SelectEngineAssignmentForSmokeTest(int id) => SelectEngineAssignmentEntity(id);
    private sealed record EngineAssignmentEntityChoice(int Id, string Name) { public override string ToString() => $"{Id}：{Name}"; }
}
