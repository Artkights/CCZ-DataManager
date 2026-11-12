using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed partial class MainForm : IWorkspaceDraftParticipant
{
    private readonly EngineTransformPanel _transformPanel = new() { Visible = false };
    private EngineGrowthDraftService? _growthDraft;
    private readonly Dictionary<string, EngineConstantEdit> _constantDraft = new(StringComparer.Ordinal);
    private string? _constantDraftSourceHash;
    private int? _selectedTransform;
    private bool _populatingTransform;
    private sealed record TransformChoice(int Id, string Display) { public override string ToString() => Display; }

    private EngineGrowthDraftService EnsureGrowthDraft()
    {
        var draft = EngineGrowthDraftService.For(_workspace);
        if (!ReferenceEquals(_growthDraft, draft))
        {
            if (_growthDraft != null) _growthDraft.Changed -= GrowthChanged;
            _growthDraft = draft; draft.Changed += GrowthChanged;
        }
        return draft;
    }

    private void GrowthChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;
        RefreshDirtyState();
        if (!_populatingTransform && IsEngineTab && EngineSubpage == EnginePage.Transform && !_transformPanel.HasPendingInput) PopulateTransformList(refreshReferences: false);
        UpdateDedicatedConstants();
    }

    private void ShowTransformView()
    {
        _engineDetail.Visible = false; _engineItemAssignments.Visible = false; _engineJobAssignments.Visible = false;
        _constantPanel.Visible = false; _grid.Visible = false; _businessEditor.Visible = false; _imageEditor.Visible = false;
        _entityList.Visible = true; _quickLookup.Visible = true;
        _engineOnlyWithCalls.Visible = false; _engineCancelScan.Visible = false;
        _engineFilterHost.Visible = true; _engineFilterHost.Height = 32;
        _transformPanel.Visible = true; _transformPanel.BringToFront(); _engineSubTabs.SendToBack();
        if (string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        try
        {
            var draft = EnsureGrowthDraft();
            PopulateTransformList();
            SetStatus(draft.CanEditRules ? "质变设定：按特效号编辑等级规则。" : "质变设定：" + draft.RulesReadOnlyReason, !draft.CanEditRules);
        }
        catch (Exception ex) { SetStatus("质变读取失败：" + ex.Message, true); }
    }

    private void PopulateTransformList(bool refreshReferences = true)
    {
        if (_populatingTransform || string.IsNullOrWhiteSpace(_workspace.RootPath)) return;
        _populatingTransform = true;
        try
        {
            var draft = EnsureGrowthDraft();
            if (refreshReferences) draft.RefreshReferences();
            var text = _engineEntityFilter.Text.Trim();
            var choices = draft.Slots.Select(slot => new TransformChoice(slot.Id, slot.Display))
                .Where(choice => text.Length == 0 || choice.Display.Contains(text, StringComparison.OrdinalIgnoreCase) || choice.Id.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            _suppressEngineSelection = true; _entityList.BeginUpdate();
            try
            {
                _entityList.Items.Clear(); _entityList.Items.AddRange(choices.Cast<object>().ToArray());
                var index = Array.FindIndex(choices, choice => choice.Id == _selectedTransform);
                if (index < 0 && choices.Length > 0) index = 0;
                _entityList.SelectedIndex = index;
                _selectedTransform = index >= 0 ? choices[index].Id : null;
                _quickLookup.SetOptions(draft.Slots.Select(slot => new IdNameOption(slot.Id, slot.Name)));
                if (_selectedTransform is { } selected) _quickLookup.SetBoundValue(selected.ToString());
            }
            finally { _entityList.EndUpdate(); _suppressEngineSelection = false; }
            _transformPanel.Bind(draft, _selectedTransform);
        }
        finally { _populatingTransform = false; }
    }

    private void SelectTransform(int id)
    {
        if (!_transformPanel.Flush()) return;
        var index = _entityList.Items.Cast<TransformChoice>().ToList().FindIndex(choice => choice.Id == id);
        if (index < 0) return;
        _selectedTransform = id;
        _suppressEngineSelection = true;
        try { _entityList.SelectedIndex = index; }
        finally { _suppressEngineSelection = false; }
        _transformPanel.Bind(EnsureGrowthDraft(), id); _quickLookup.SetBoundValue(id.ToString());
    }

    private void UpdateDedicatedConstants()
    {
        if (_growthDraft == null) return;
        var tiers = _growthDraft.TierEntries;
        _constantPanel.SetDedicatedValues(new Dictionary<string, string>
        {
            ["transform-rules"] = string.Join("；", _growthDraft.Slots.Where(slot => slot.Stages.Count > 0)
                .Select(slot => $"0x{slot.Id:X2}: " + string.Join(" / ", slot.Stages.OrderBy(stage => stage.Level).Select(stage => $"{stage.Level}级→{stage.Value}")))),
            ["tier-thresholds"] = string.Join(" / ", tiers.Skip(1).Select(entry => entry.Threshold)),
            ["tier-cap"] = tiers.Count.ToString(),
            ["tier-labels"] = string.Join(" / ", tiers.Select(entry => entry.Label))
        });
    }

    private void NavigateDedicatedConstant(object? sender, EngineConstantNavigationEventArgs e)
    {
        if (!PreparePendingEdits()) return;
        if (e.Target == "transform")
        {
            _moduleTabs.SelectedTab = _moduleTabs.TabPages.Cast<TabPage>().First(page => Equals(page.Tag, "__engine"));
            _engineSubTabs.SelectedIndex = (int)EnginePage.Transform;
        }
        else
        {
            var module = _workspace.Modules.FirstOrDefault(module => module.TableKeys.Contains("detailed-jobs"));
            if (module == null) return;
            _moduleTabs.SelectedTab = _moduleTabs.TabPages.Cast<TabPage>().First(page => Equals(page.Tag, module.Key));
            if (e.Target == "tier") _businessEditor.TierEditorForSmokeTest?.Focus();
        }
    }

    private bool StageConstant(EngineConstantEdit edit)
    {
        var before = _constantDraft.GetValueOrDefault(edit.Id);
        try
        {
            var edits = _constantDraft.Values.Where(value => value.Id != edit.Id).Append(edit).ToArray();
            var candidate = new EngineConstantService().PrepareBinaryEdit(_workspace, edits);
            var sourceHash = HashService.Sha256(candidate.BeforeBytes);
            if (_constantDraft.Count > 0 && _constantDraftSourceHash != sourceHash)
                throw new IOException("Ekd5.exe 在常量编辑后被外部修改，请重新读取项目。");
            _constantDraftSourceHash = sourceHash;
            _constantDraft[edit.Id] = edit;
            _workspace.Commands.Push(new DelegateEditCommand("编辑引擎常量 " + edit.Id,
                () => { if (before == null) _constantDraft.Remove(edit.Id); else _constantDraft[edit.Id] = before; _constantDraftSourceHash = sourceHash; _constantPanel.SetPendingEdits(_constantDraft); RefreshDirtyState(); },
                () => { _constantDraft[edit.Id] = edit; _constantDraftSourceHash = sourceHash; _constantPanel.SetPendingEdits(_constantDraft); RefreshDirtyState(); }));
            _workspace.RegisterDraftParticipant(this);
            RefreshDirtyState(); SetStatus("常量已加入草稿，顶部保存统一提交。"); return true;
        }
        catch (Exception ex) { SetStatus(ex.Message, true); return false; }
    }

    bool IWorkspaceDraftParticipant.HasPendingChanges => HasAnyEngineChanges || _constantDraft.Count > 0 || _battleFormulaPanel.HasPendingChanges;

    private IReadOnlyList<WorkspaceBinaryCandidate> BuildIntegratedCandidates()
    {
        var candidates = new List<WorkspaceBinaryCandidate>();
        if (HasAnyEngineChanges)
        {
            if (_engineReport == null) throw new InvalidDataException("引擎特效草稿缺少扫描基线。");
            var assignment = HasEngineAssignmentChanges && _engineAssignmentDraft is { } draft
                ? _engineAssignmentService.BuildPreview(_workspace, _engineReport, draft, _engineDetail.HasPendingEquipmentUnbind) : null;
            var detail = _engineDetail.HasPendingChanges ? _engineDetail.BuildPendingPreview(deferEquipmentFfGuard: assignment != null) : null;
            var errors = (detail?.Errors ?? []).Concat(assignment?.Errors ?? []).ToArray();
            if (errors.Length > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            candidates.AddRange(detail?.Candidates ?? []); candidates.AddRange(assignment?.Candidates ?? []);
        }
        var constantEdits = _constantDraft.Values.Concat(_battleFormulaPanel.PendingConstantEdits).ToArray();
        if (constantEdits.Length > 0)
        {
            foreach (var group in constantEdits.GroupBy(edit => edit.Id))
                if (group.Select(edit => edit.NumericValue).Distinct().Count() > 1)
                    throw new InvalidDataException("常量与公式草稿值不同：" + group.Key);
            var constantCandidate = new EngineConstantService().PrepareBinaryEdit(_workspace, constantEdits.DistinctBy(edit => edit.Id).ToArray());
            if (_constantDraft.Count > 0 && _constantDraftSourceHash != HashService.Sha256(constantCandidate.BeforeBytes))
                throw new IOException("Ekd5.exe 在常量编辑后被外部修改，请重新读取项目。");
            candidates.Add(constantCandidate);
        }
        if (candidates.Count > 0 && EngineEffectWriteService.IsGameRunning(_workspace))
            throw new IOException("游戏正在运行，请先退出游戏再保存引擎修改。");
        return EngineEffectTransactionService.MergeCandidates(candidates);
    }

    IReadOnlyList<PreparedBinaryEdit> IWorkspaceDraftParticipant.PrepareBinaryEdits() => BuildIntegratedCandidates()
        .Where(candidate => candidate.BeforeBytes.Length == candidate.AfterBytes.Length).Select(ToPreparedEdit).ToArray();

    IReadOnlyList<PreparedFileEdit> IWorkspaceDraftParticipant.PrepareFileEdits() => BuildIntegratedCandidates()
        .Where(candidate => candidate.BeforeBytes.Length != candidate.AfterBytes.Length)
        .Select(candidate => new PreparedFileEdit(Path.GetRelativePath(_workspace.RootPath, candidate.FilePath), HashService.Sha256(candidate.BeforeBytes),
            candidate.AfterBytes, candidate.Module, "引擎统一保存")).ToArray();

    private PreparedBinaryEdit ToPreparedEdit(WorkspaceBinaryCandidate candidate)
    {
        var patches = new List<PreparedBytePatch>();
        for (var offset = 0; offset < candidate.BeforeBytes.Length;)
        {
            if (candidate.BeforeBytes[offset] == candidate.AfterBytes[offset]) { offset++; continue; }
            var start = offset++;
            while (offset < candidate.BeforeBytes.Length && candidate.BeforeBytes[offset] != candidate.AfterBytes[offset]) offset++;
            patches.Add(new(start, candidate.BeforeBytes[start..offset], candidate.AfterBytes[start..offset]));
        }
        return new(Path.GetRelativePath(_workspace.RootPath, candidate.FilePath), HashService.Sha256(candidate.BeforeBytes), patches,
            candidate.Module, "引擎统一保存", bytes =>
            {
                foreach (var patch in patches)
                    if (!bytes.AsSpan((int)patch.Offset, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                        throw new InvalidDataException("引擎修改复读不一致。");
            });
    }

    void IWorkspaceDraftParticipant.AcceptSaved()
    {
        _constantDraft.Clear();
        _constantDraftSourceHash = null;
        _constantPanel.SetPendingEdits(_constantDraft);
        _engineDetail.DiscardPendingChanges(); DiscardEngineAssignmentChanges();
        _battleFormulaPanel.DiscardPendingChanges();
        _engineReportFingerprint = null; _engineAssignmentFingerprint = null;
    }
    void IWorkspaceDraftParticipant.Discard() => ((IWorkspaceDraftParticipant)this).AcceptSaved();

    Action IWorkspaceDraftParticipant.CaptureSaveRecovery()
    {
        var constants = _constantDraft.ToArray(); var constantHash = _constantDraftSourceHash;
        var detail = _engineDetail.CaptureDraftRecovery();
        var formula = _battleFormulaPanel.CaptureDraftRecovery();
        var assignments = _engineAssignmentDraft;
        var itemTargets = assignments?.ItemTargets.ToArray();
        var jobTargets = assignments?.JobTargets.Select(pair => new KeyValuePair<string, EngineEffectJobSlot[]>(pair.Key, pair.Value.ToArray())).ToArray();
        var values = assignments?.ItemValues.ToArray(); var selectors = assignments?.ItemSelectorTargets.ToArray();
        var specials = assignments?.SpecialItemIds.ToArray(); var prepared = _enginePreparedAssignments.ToArray();
        return () =>
        {
            _constantDraft.Clear(); foreach (var pair in constants) _constantDraft.Add(pair.Key, pair.Value);
            _constantDraftSourceHash = constantHash; _constantPanel.SetPendingEdits(_constantDraft);
            detail(); formula(); _engineAssignmentDraft = assignments;
            if (assignments != null)
            {
                assignments.Clear();
                foreach (var pair in itemTargets!) assignments.ItemTargets.Add(pair.Key, pair.Value);
                foreach (var pair in jobTargets!) assignments.JobTargets.Add(pair.Key, pair.Value);
                foreach (var pair in values!) assignments.ItemValues.Add(pair.Key, pair.Value);
                foreach (var pair in selectors!) assignments.ItemSelectorTargets.Add(pair.Key, pair.Value);
                assignments.SpecialItemIds.UnionWith(specials!);
            }
            _enginePreparedAssignments.Clear(); foreach (var pair in prepared) _enginePreparedAssignments.Add(pair.Key, pair.Value);
            RefreshDirtyState();
        };
    }

    internal EngineTransformPanel TransformForSmokeTest => _transformPanel;
    internal EngineGrowthDraftService GrowthForSmokeTest => EnsureGrowthDraft();
    internal bool CommitIntegratedForSmokeTest() => CommitPendingEdits().Saved;
    internal bool StageConstantForSmokeTest(EngineConstantEdit edit) => StageConstant(edit);
}
