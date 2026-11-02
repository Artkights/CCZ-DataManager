using DataManager.Core.Editing;

namespace DataManager.App;

public sealed partial class MainForm
{
    private readonly EngineConstantPanel _constantPanel = new() { Visible = false };

    private const int ConstantEngineSubpage = 3;

    private void InitializeConstantsPanel()
    {
        _constantPanel.StatusChanged += (_, e) => SetStatus(e.Message, e.Error);
    }

    /// <summary>常量是引擎扫描的第 4 个子页，和特效管理共用同一套左侧与子页导航。</summary>
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
    }

    internal EngineConstantPanel ConstantsForSmokeTest => _constantPanel;
}
