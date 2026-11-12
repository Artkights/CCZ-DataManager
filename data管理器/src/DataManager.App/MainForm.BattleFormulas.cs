using DataManager.Core.Editing;
using DataManager.Core.Scanner;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly EngineBattleFormulaPanel _battleFormulaPanel = new() { Visible = false };
    private const string FormulaTabKey = "__formulas";
    private bool IsFormulaTab => _moduleTabs.SelectedTab?.Tag as string == FormulaTabKey;
    private string? _formulaSelectionId;
    private bool _suppressFormulaSelection;

    private void InitializeBattleFormulaPanel()
    {
        _battleFormulaPanel.CommitAllRequested = () => CommitPendingEdits().Saved;
        _battleFormulaPanel.StatusChanged += (_, e) => SetStatus(e.Message, e.Error);
        _battleFormulaPanel.PendingChangesChanged += (_, _) => RefreshDirtyState();
    }

    /// <summary>公式设定是独立顶级页；左侧用全局列表，右侧显示计算过程与可改常量。</summary>
    private void ShowFormulaSettingsView()
    {
        _businessEditor.Visible = false;
        _imageEditor.Visible = false;
        _grid.Visible = false;
        _engineDetail.Visible = false;
        _engineSubTabs.Visible = false;
        _constantPanel.Visible = false;
        _engineItemAssignments.Visible = false;
        _engineJobAssignments.Visible = false;
        _engineFilterHost.Visible = false;
        _resourcePreview.Visible = false;
        _entityList.Visible = true;
        _quickLookup.Visible = false;
        _battleFormulaPanel.Visible = true;
        _battleFormulaPanel.BringToFront();

        PopulateFormulaList();
        var fallback = EngineBattleFormulaService.Definitions[0].Id;
        var selected = _formulaSelectionId
            ?? _battleFormulaPanel.CurrentFormulaId
            ?? fallback;
        if (!_battleFormulaPanel.TrySwitchFormula(selected, out _))
        {
            RestoreFormulaSelection();
            return;
        }
        SelectFormulaInList(selected);

        if (string.IsNullOrWhiteSpace(_workspace.RootPath))
        {
            _battleFormulaPanel.Bind(null, selected);
            SetStatus("公式设定：尚未打开项目，常量只读。", true);
            return;
        }

        _battleFormulaPanel.Bind(_workspace, selected);
        SetStatus($"公式设定：当前基底 {_workspace.ActiveProfile.Profile.Version}，选择左侧公式修改常量。");
    }

    private void PopulateFormulaList()
    {
        _suppressFormulaSelection = true;
        try
        {
            _entityList.Items.Clear();
            foreach (var definition in EngineBattleFormulaService.Definitions)
                _entityList.Items.Add(new BattleFormulaChoice(
                    definition.Id, definition.Category, definition.DisplayName));
        }
        finally { _suppressFormulaSelection = false; }
    }

    private void SelectFormulaInList(string formulaId)
    {
        _suppressFormulaSelection = true;
        try
        {
            var item = _entityList.Items.Cast<BattleFormulaChoice>()
                .FirstOrDefault(choice => choice.Id == formulaId);
            if (item != null) _entityList.SelectedItem = item;
        }
        finally { _suppressFormulaSelection = false; }
    }

    private void RestoreFormulaSelection() => SelectFormulaInList(
        _battleFormulaPanel.CurrentFormulaId ?? EngineBattleFormulaService.Definitions[0].Id);

    internal bool ConfirmLeaveFormula()
    {
        if (!_battleFormulaPanel.HasPendingChanges) return true;
        if (!_battleFormulaPanel.PreparePendingEdits()) return false;
        var result = MessageBox.Show(this, "公式常量尚未写入。是否先写入？", "公式设定",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) return TryCommitFormulaChanges();
        _battleFormulaPanel.DiscardPendingChanges();
        return true;
    }

    private bool TryCommitFormulaChanges()
    {
        if (!_battleFormulaPanel.HasPendingChanges) return true;
        if (!_battleFormulaPanel.PreparePendingEdits()) return false;
        if (!_battleFormulaPanel.TryCommit(out var message))
        {
            SetStatus(message, true);
            MessageBox.Show(this, message, "公式常量写入", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        SetStatus(message);
        return true;
    }

    internal EngineBattleFormulaPanel BattleFormulasForSmokeTest => _battleFormulaPanel;

    internal void SelectFormulaForSmokeTest(string formulaId)
    {
        SelectFormulaInList(formulaId);
        if (_entityList.SelectedItem is not BattleFormulaChoice choice) return;
        if (!_battleFormulaPanel.TrySwitchFormula(choice.Id, out _)) return;
        _formulaSelectionId = choice.Id;
        _battleFormulaPanel.Bind(_workspace, choice.Id);
    }
}

public sealed record BattleFormulaChoice(string Id, string Category, string DisplayName)
{
    public override string ToString()
    {
        var shortName = DisplayName.Contains('（', StringComparison.Ordinal)
            ? DisplayName[..DisplayName.IndexOf('（', StringComparison.Ordinal)]
            : DisplayName;
        return $"{Category} · {shortName}";
    }
}
