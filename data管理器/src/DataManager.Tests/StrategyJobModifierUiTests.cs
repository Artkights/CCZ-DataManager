using DataManager.App;
using DataManager.Core.Domain;

namespace DataManager.Tests;

internal static class StrategyJobModifierUiTests
{
    internal static void Run(string basePath)
    {
        var repository = basePath;
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
        {
            var source = FindSourceFolder(repository, name);
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            var executable = Path.Combine(root, "Ekd5.exe");
            var before = File.ReadAllBytes(executable);
            MainForm? form = null;
            try
            {
                form = new MainForm(false)
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                    ShowInTaskbar = false, MaximumSize = new Size(4096, 2160)
                };
                form.Show();
                form.LoadProjectForSmokeTest(root);
                form.SelectModuleForSmokeTest("策略");
                Settle(form);
                var panel = form.BusinessEditorForSmokeTest;
                panel.Bind(BusinessPageKind.Strategies, 0);
                Settle(form);
                var tab = panel.DetailTabs.TabPages.Cast<TabPage>()
                    .FirstOrDefault(page => page.AccessibleName == "策略专项");
                TestSupport.Assert(tab != null, name + "：未找到“策略专项”子页。");
                panel.DetailTabs.SelectedTab = tab;
                Settle(form);
                var grid = Descendants(tab!).OfType<DataGridView>()
                    .FirstOrDefault(control => control.Name == "StrategyJobModifierGrid");
                TestSupport.Assert(grid != null && grid.Columns.Count == 9,
                    name + "：策略专项网格缺失或列数不是 9。");
                TestSupport.Assert(Descendants(tab!).OfType<Control>()
                    .Any(control => control.Name == "StrategyBuiltInFireEnabled"),
                    name + "：缺少可编辑内置规则区。");

                var add = Descendants(tab!).OfType<Button>().Single(button => button.Name == "StrategyJobModifierAdd");
                var remove = Descendants(tab!).OfType<Button>().Single(button => button.Name == "StrategyJobModifierDelete");
                var apply = Descendants(tab!).OfType<Button>().Single(button => button.Name == "StrategyJobModifierApply");
                var fireEnabled = Descendants(tab!).OfType<CheckBox>().Single(control => control.Name == "StrategyBuiltInFireEnabled");
                var fireTrigger = Descendants(tab!).OfType<ComboBox>().Single(control => control.Name == "StrategyBuiltInFireTrigger");
                var fireSeries = Descendants(tab!).OfType<ComboBox>().Single(control => control.Name == "StrategyBuiltInFireSeries");
                var fireBonus = Descendants(tab!).OfType<NumericUpDown>().Single(control => control.Name == "StrategyBuiltInFireBonus");
                var charmEnabled = Descendants(tab!).OfType<CheckBox>().Single(control => control.Name == "StrategyBuiltInCharmEnabled");
                var charmDamage = Descendants(tab!).OfType<ComboBox>().Single(control => control.Name == "StrategyBuiltInCharmDamage");
                var charmHit = Descendants(tab!).OfType<ComboBox>().Single(control => control.Name == "StrategyBuiltInCharmHit");
                var charmSeries = Descendants(tab!).OfType<ComboBox>().Single(control => control.Name == "StrategyBuiltInCharmSeries");
                StrategyJobModifierApplyRequest? captured = null;
                panel.StrategyJobModifierApplyForSmokeTest = request =>
                {
                    captured = request;
                    return true;
                };

                TestSupport.Assert(grid!.Rows.Count == 0, name + "：新工程不应有自定义规则。");
                add.PerformClick();
                Settle(form);
                TestSupport.Assert(grid.Rows.Count == 1 && panel.HasPendingChanges,
                    name + "：新增规则没有生成草稿/待保存状态。");
                grid.Rows[0].Cells["伤害%"].Value = 55;
                grid.EndEdit();
                fireEnabled.Checked = true;
                fireTrigger.Text = "类型 1：水";
                fireSeries.SelectedIndex = 12;
                fireBonus.Value = 50;
                charmEnabled.Checked = true;
                charmDamage.Text = "21";
                charmHit.Text = "22";
                charmSeries.SelectedIndex = 13;
                TestSupport.Assert(panel.Flush(), name + "：编辑规则后 Flush 失败：" + panel.LastValidationError);
                apply.PerformClick();
                Settle(form);
                TestSupport.Assert(captured is { Rules.Count: 1, BuiltIn: not null }
                    && captured.Rules[0].DamagePercent == 55
                    && captured.Rules[0].Side == StrategyModifierSide.Receiver
                    && captured.BuiltIn!.FireTriggerType == 1
                    && captured.BuiltIn.FireSeries == 12
                    && captured.BuiltIn.FireBonus == 50
                    && captured.BuiltIn.CharmDamageValue == 21
                    && captured.BuiltIn.CharmHitValue == 22
                    && captured.BuiltIn.CharmSeries == 13,
                    name + "：应用回调没有收到编辑后的规则/内置设置。");
                TestSupport.Assert(File.ReadAllBytes(executable).SequenceEqual(before),
                    name + "：仅编辑草稿就写入了 EXE。");

                grid.Rows[0].Selected = true;
                remove.PerformClick();
                Settle(form);
                TestSupport.Assert(grid.Rows.Count == 0, name + "：删除规则后网格仍有行。");

                var workspace = form.WorkspaceForSmokeTest;
                var e2eRules = new[]
                {
                    new StrategyJobModifierRule(StrategyModifierSide.Receiver, StrategyModifierMatchKind.StrategyType,
                        0, 0x1E, 40, 0, true, "end-to-end")
                };
                var e2eBuiltIn = new StrategyBuiltInRuleSettings(true, 1, 0x0C, 50, true, 21, 22, 0x0D);
                TestSupport.Assert(form.StageStrategyJobModifierForSmokeTest(
                        new StrategyJobModifierApplyRequest(e2eRules, true, e2eBuiltIn)),
                    name + "：MainForm 未接受策略专项候选。");
                var saved = workspace.Save();
                TestSupport.Assert(saved.Saved, name + "：策略专项保存失败：" + string.Join("；", saved.Messages));
                var after = File.ReadAllBytes(executable);
                TestSupport.Assert(after.Length == before.Length + StrategyJobModifierRule.SectionSize,
                    name + "：保存后没有追加 .strh 节。");
                var hookState = new DataManager.Core.Scanner.StrategyHookSectionService().Read(after);
                TestSupport.Assert(hookState is { HookInstalled: true } && hookState.Rules.Count == 1,
                    name + "：保存后的 .strh 状态无法复读。");
                var builtInState = new DataManager.Core.Scanner.StrategyBuiltInRuleService().Read(after);
                TestSupport.Assert(builtInState.Supported
                    && builtInState.Current.FireSeries == 0x0C
                    && builtInState.Current.FireBonus == 50
                    && builtInState.Current.CharmDamageValue == 21
                    && builtInState.Current.CharmHitValue == 22
                    && builtInState.Current.CharmSeries == 0x0D,
                    name + "：保存后的内置条目值无法复读。");
                File.Copy(executable, executable + ".installed", true);

                TestSupport.Assert(form.StageStrategyJobModifierForSmokeTest(
                        new StrategyJobModifierApplyRequest([], false, null)),
                    name + "：MainForm 未接受卸载候选。");
                var uninstallSaved = workspace.Save();
                TestSupport.Assert(uninstallSaved.Saved, name + "：卸载保存失败：" + string.Join("；", uninstallSaved.Messages));
                var restored = new DataManager.Core.Scanner.StrategyHookSectionService().Read(File.ReadAllBytes(executable));
                TestSupport.Assert(restored is { HookInstalled: false },
                    name + "：卸载 Hook 后状态仍为已安装。");
                Console.WriteLine($"STRATEGY_JOB_MODIFIER_UI_OK {name} tab-grid-add-edit-apply-delete-e2e");
            }
            finally
            {
                form?.Dispose();
                if (Environment.GetEnvironmentVariable("STRH_TEST_KEEP") == "1")
                    Console.WriteLine("STRH_TEST_ROOT " + root);
                else
                    Directory.Delete(root, true);
            }
        }
        Console.WriteLine("STRATEGY_JOB_MODIFIER_UI_TESTS_OK three bases");
    }

    private static string FindSourceFolder(string repository, string name)
    {
        var root = Path.Combine(repository, name);
        if (File.Exists(Path.Combine(root, "Ekd5.exe"))) return root;
        var nested = Path.Combine(root, name);
        if (File.Exists(Path.Combine(nested, "Ekd5.exe"))) return nested;
        throw new InvalidOperationException("找不到基底目录：" + root);
    }

    private static void Settle(MainForm form) { Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
