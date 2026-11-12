using DataManager.Core.Editing;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly EngineConstantPanel _constantPanel = new() { Visible = false };

    private const EnginePage ConstantEngineSubpage = EnginePage.Constants;

    private void InitializeConstantsPanel()
    {
        _constantPanel.StageEdit = StageConstant;
        _constantPanel.NavigationRequested += NavigateDedicatedConstant;
        _constantPanel.StatusChanged += (_, e) => SetStatus(e.Message, e.Error);
    }

    /// <summary>常量使用引擎扫描的统一子页导航。</summary>
    private void ShowConstantView()
    {
        _businessEditor.Visible = false;
        _imageEditor.Visible = false;
        _grid.Visible = false;
        _engineDetail.Visible = false;
        _engineItemAssignments.Visible = false;
        _engineJobAssignments.Visible = false;
        _engineSubTabs.Visible = true;
        _engineSubTabs.SendToBack();
        _engineFilterHost.Visible = false;
        _entityList.Visible = false;
        _quickLookup.Visible = false;
        _constantPanel.Visible = true;
        _constantPanel.BringToFront();
        if (string.IsNullOrWhiteSpace(_workspace.RootPath))
        {
            _constantPanel.Reset();
            SetStatus("常量页：尚未打开项目。", true);
            return;
        }
        _constantPanel.Bind(_workspace);
        EnsureGrowthDraft();
        UpdateDedicatedConstants();
    }

    internal EngineConstantPanel ConstantsForSmokeTest => _constantPanel;
}
