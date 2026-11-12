using System.Globalization;
using DataManager.App;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class ExtendDataBindingRegressionTests
{
    public static void Run(MainForm form)
    {
        var editor = form.BusinessEditorForSmokeTest.ExtendEditor!;
        var workspace = form.WorkspaceForSmokeTest;
        var service = new ExtendDataService(workspace);
        editor.SearchText = string.Empty;
        editor.DetailTabs.SelectedIndex = 0;
        form.QueryForSmokeTest(string.Empty);
        TestSupport.Assert(editor.BindEntity(0), "无法选择缓存回归的人物");
        Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 0;
        var value = Find<TextBox>(editor, "ExtendEffectValue0");
        var effect = Find<SearchableIdComboBox>(editor, "ExtendEffectId0");
        var options = editor.GetEntityOptions();
        value.Select(0, value.TextLength);
        var selection = (value.SelectionStart, value.SelectionLength);
        for (var index = 0; index < 5; index++)
            TestSupport.Assert(editor.BindEntity(0), "重复选择相同人物失败");
        TestSupport.Assert(ReferenceEquals(options, editor.GetEntityOptions()) && ReferenceEquals(effect, Find<SearchableIdComboBox>(editor, "ExtendEffectId0")) &&
            (value.SelectionStart, value.SelectionLength) == selection && !editor.HasPendingChanges,
            "相同版本人物重绑重置了输入选择或候选控件");

        Rename("persons", 0, "缓存人物", () =>
        {
            editor.BindEntity(0);
            TestSupport.Assert(editor.GetEntityOptions().Single(option => option.Id == 0).Name == "缓存人物" &&
                Find<Label>(editor, "ExtendEntityTitle").Text == "0：缓存人物", "人物名称草稿没有使主体候选和标题失效");
        });
        editor.BindEntity(0);

        var effectVersion = service.GetBindingRevision(ExtendDataCategory.Effects);
        Rename("strategies", 5, "缓存策略", () =>
        {
            TestSupport.Assert(service.GetBindingRevision(ExtendDataCategory.Effects) == effectVersion,
                "无关策略名称编辑使人物特效绑定失效");
            editor.DetailTabs.SelectedIndex = 2;
            form.QueryForSmokeTest(string.Empty);
            editor.BindEntity(0);
            Find<ComboBox>(editor, "ExtendPage").SelectedIndex = 0;
            var strategy = Find<SearchableIdComboBox>(editor, "ExtendStrategyId0");
            TestSupport.Assert(strategy.Options.Single(option => option.Id == 5).Name == "缓存策略", "策略下拉没有读取最新名称草稿");
            Rename("strategies", 5, "再次改名", () =>
            {
                editor.BindEntity(0);
                TestSupport.Assert(ReferenceEquals(strategy, Find<SearchableIdComboBox>(editor, "ExtendStrategyId0")) &&
                    strategy.Options.Single(option => option.Id == 5).Name == "再次改名", "已创建策略控件没有刷新名称候选");
                var count = Find<TextBox>(editor, "ExtendStrategyCount0");
                count.Text = "999";
                TestSupport.Assert(!editor.BindEntity(0) && count.Text == "999", "相同人物的快速绑定绕过了无效输入校验");
                editor.DiscardPendingChanges();
                TestSupport.Assert(!editor.HasPendingChanges && count.Text == service.ReadInt(ExtendDataCategory.Strategies, 0, "策略0次数").ToString(CultureInfo.InvariantCulture),
                    "强制刷新未丢弃过期输入或没有读取工作区数据");
            });
            editor.BindEntity(0);
            TestSupport.Assert(strategy.Options.Single(option => option.Id == 5).Name == "缓存策略", "撤销名称后策略候选没有恢复");
        });

        editor.DetailTabs.SelectedIndex = 3;
        form.QueryForSmokeTest(string.Empty);
        editor.BindEntity(0);
        var item = Find<SearchableIdComboBox>(editor, "ExtendForgeItem0");
        Rename("items-low", 0, "缓存装备", () =>
        {
            editor.BindEntity(0);
            TestSupport.Assert(ReferenceEquals(item, Find<SearchableIdComboBox>(editor, "ExtendForgeItem0")) &&
                item.Options.Single(option => option.Id == 0).Name == "缓存装备", "锻造已创建物品下拉没有刷新名称候选");
            TestSupport.Assert(Find<SearchableIdComboBox>(editor, "ExtendForgeItem7").Options.Any(option => option.Id == ushort.MaxValue),
                "刷新候选时丢弃未知UInt16原值");
        });
        editor.BindEntity(0);
        Console.WriteLine("EXTEND_BINDING_REGRESSION_OK stable-input dependent-names undo invalid-input unknown-id");

        void Rename(string key, int id, string name, Action verify)
        {
            var document = workspace.GetDocument(key);
            var row = id - document.Definition.BeginId;
            workspace.ApplyCompositeTransaction("候选版本回归", [new CompositeCellEdit(key, row, "名称", name)]);
            try { verify(); }
            finally { TestSupport.Assert(workspace.Commands.Undo() != null, "候选名称回归未注册撤销"); }
        }
    }

    private static T Find<T>(ExtendDataEditorPanel editor, string name) where T : Control
        => Descendants(editor.DetailTabs.SelectedTab!).OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
}
