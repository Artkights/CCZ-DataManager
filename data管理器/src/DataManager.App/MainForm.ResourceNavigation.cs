using DataManager.Core.Models;

namespace DataManager.App;

public sealed partial class MainForm
{
    private string _lastResourceCategory = "R";

    private async void LocatePersonImage(string category, int personId, int imageId)
    {
        try
        {
            if (category is not ("Face" or "R") || imageId < 0) return;
            var tableKey = category == "Face" ? "persons" : "person-r-assign";
            var field = category == "Face" ? "头像" : "R形象ID";
            // Locating is a read-only operation. Baseline/variant profiles are
            // verified without requiring dynamically generated evidence text.
            if (!_workspace.Documents.TryGetValue(tableKey, out var document) ||
                document.Definition.Capability == TableCapability.Unavailable ||
                document.Definition.VerificationLevel == VerificationLevel.InheritedUnverified ||
                !document.Data.Columns.Contains(field))
                throw new InvalidOperationException("当前人物图片指定未通过布局验证，不能据此定位形象。");
            var root = _workspace.RootPath;
            _lastResourceCategory = category;
            var tab = _moduleTabs.TabPages.Cast<TabPage>().Single(page => Equals(page.Tag, "__resources"));
            _moduleTabs.SelectedTab = tab;
            if (!_resourceModule || _workspace.RootPath != root) return;
            var label = category == "Face" ? "头像" : "R 形象";
            var located = await _imageEditor.LocateLogicalImageAsync(category, imageId);
            if (_workspace.RootPath != root || !_resourceModule) return;
            SetStatus(located ? $"按人物 {personId} 当前草稿定位至{label} {imageId}。"
                : $"未找到{label} {imageId}，请检查当前项目的图片资源。", !located);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            SetStatus("人物图片定位失败：" + ex.Message, true);
        }
    }
}
