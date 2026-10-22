using DataManager.App;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class EffectsRemovalTests
{
    private static readonly string[] RemovedTables = ["class-effects", "personal-effects", "effect-descriptions"];

    public static void Run(string source)
    {
        var registry = new ProfileRegistry();
        foreach (var profile in registry.Profiles)
        foreach (var variant in profile.Variants)
        {
            var resolved = registry.Resolve(profile.Id, variant.Id);
            TestSupport.Assert(!resolved.Modules.Any(module => module.Key == "人物/套装特效"), "已删除的模块仍在布局中");
            TestSupport.Assert(!resolved.Tables.Any(table => RemovedTables.Contains(table.Key)), "已删除的专用物理表仍在布局中");
            var names = resolved.Tables.Single(table => table.Key == "effect-names");
            TestSupport.Assert(names.Capability == TableCapability.ReadOnly && names.Module != "人物/套装特效",
                "共享名称表恢复了删除页的写入能力或模块");
        }
        TestSupport.Assert((int)BusinessPageKind.Items == 3 && (int)BusinessPageKind.Shop == 5 &&
            (int)BusinessPageKind.ExtendData == 7 && (int)BusinessPageKind.PhysicalTables == 8,
            "删除页面改变了其余业务页的数值身份");

        CheckProject(source);
        var root = TestSupport.RepositoryRootFromBase(source);
        var source66 = Path.Combine(root, "基底", "复刻版6.6山贼 国庆");
        if (Directory.Exists(source66)) CheckProject(source66);
        Console.WriteLine("EFFECTS_REMOVAL_TESTS_OK modules/physical-tables/read-only/shared-lookups/retained-pages/stable-enum");
    }

    private static void CheckProject(string source)
    {
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1280, 720)
        };
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Effect.e5"), Enumerable.Repeat((byte)255, 1024).ToArray());
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            var workspace = form.WorkspaceForSmokeTest;
            TestSupport.Assert(!form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Any(tab => Equals(tab.Tag, "人物/套装特效")),
                "顶部仍显示已删除的页面");
            TestSupport.Assert(!workspace.Definitions.Any(table => RemovedTables.Contains(table.Key)) &&
                !workspace.Documents.Keys.Any(RemovedTables.Contains), "专用表仍可在普通物理表入口读取");
            var names = workspace.GetDocument("effect-names");
            TestSupport.Assert(!names.Definition.IsWriteSupported, "运行时消费者探测恢复了共享名称表的可写权限");
            var field = names.Definition.Fields.Single().Name;
            var rejected = false;
            try { workspace.ApplyCompositeTransaction("拒绝共享名称写入", [new("effect-names", 0, field, "拒写")]); }
            catch (InvalidOperationException) { rejected = true; }
            TestSupport.Assert(rejected && !workspace.IsDirty, "共享名称表允许创建写入草稿");

            foreach (var module in new[] { "人物", "兵种系", "兵种", "宝物", "策略", "商店" })
            {
                form.SelectModuleForSmokeTest(module); Application.DoEvents();
                TestSupport.Assert(form.BusinessEditorForSmokeTest.DetailTabs.TabCount > 0 &&
                    form.EntityListForSmokeTest.Items.Count > 0 && !form.StatusForSmokeTest.Contains("详情读取失败", StringComparison.Ordinal),
                    "保留页面绑定失败：" + module);
            }
            // Item names have a separate native pool; deleting the assignment editor
            // must preserve that lookup as well as the extension's personal/job pool.
            TestSupport.Assert(new EffectNamePoolService().BuildLookup(workspace, EffectNameChannel.Item).Count > 0,
                "宝物装备特效名称丢失");
            var expected = new EffectNamePoolService().BuildLookup(workspace, EffectNameChannel.PersonalJob);
            TestSupport.Assert(expected.Count > 0, "人物/兵种共享特效名称无法读取");
            form.SelectModuleForSmokeTest("拓展数据"); Application.DoEvents();
            var editor = form.BusinessEditorForSmokeTest.ExtendEditor!;
            var combo = Descendants(editor).OfType<SearchableIdComboBox>().Single(control => control.Name == "ExtendEffectId0");
            var named = expected.First();
            TestSupport.Assert(combo.Options.Single(option => option.Id == named.Key).Name == named.Value &&
                combo.Options.Single(option => option.Id == 255).Name == "无", "拓展人物特效下拉丢失名称或空槽");
            TestSupport.Assert(!workspace.IsDirty, "查看保留页面产生了意外草稿");
        }
        finally
        {
            form.BusinessEditorForSmokeTest.DiscardPendingChanges(); form.Close();
            Directory.Delete(root, true);
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
