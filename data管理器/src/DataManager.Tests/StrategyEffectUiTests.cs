using System.Globalization;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

/// <summary>Exercises the real effect editor event, confirmation and entity rebind paths.</summary>
internal static class StrategyEffectUiTests
{
    internal static void Run(string basePath)
    {
        var repository = TestSupport.RepositoryRootFromBase(basePath);
        foreach (var name in new[] { "洗隋", "加强版6.5未加密版", "复刻版6.6山贼" })
        {
            var source = Path.Combine(repository, "基底", name);
            var originalHashes = Known65FixtureSnapshot.CoreNames.ToDictionary(file => file,
                file => HashService.Sha256(ProjectFileLocator.Resolve(source, file)));
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            MainForm? form = null;
            try
            {
                // Seed legacy values only in the isolated copy. They must be
                // visible on their own rows, never offered to another row.
                var executable = ProjectFileLocator.Resolve(root, "Ekd5.exe");
                var image = File.ReadAllBytes(executable);
                image[0xA2D70 + 90] = 0x2A;
                image[0xA2D70 + 78] = 0x90;
                File.WriteAllBytes(executable, image);
                var before = Snapshot(root);
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
                var workspace = form.WorkspaceForSmokeTest;
                var effects = workspace.GetDocument(StrategyEffectClassService.TableKey);
                var prompts = new List<string>();
                var accept = false;
                panel.StrategyEffectConfirmationForSmokeTest = warning => { prompts.Add(warning); return accept; };
                panel.Bind(BusinessPageKind.Strategies, 0); Settle(form);
                var combo = EffectCombo(panel);
                AssertLabels(combo);
                TestSupport.Assert(combo.Options.All(option => option.Id != 0x2A && option.Id != 0x90),
                    "其他策略出现了仅原记录可保留的默认别名或90选项。");
                var original = Value(effects, 0);
                var next = original == 9 ? 8 : 9;

                SelectAsUser(combo, next); Settle(form);
                TestSupport.Assert(prompts.Count == 1 && prompts[0].Contains("策略ID 0", StringComparison.Ordinal),
                    "跨族选择没有经过真实UI确认事件。");
                TestSupport.Assert(Value(effects, 0) == original && !workspace.IsDirty && !panel.HasPendingChanges &&
                    combo.TryGetSelectedId(out var denied) && denied == original,
                    "拒绝效果确认后没有恢复原值，或留下草稿/待提交输入。");
                TestSupport.Assert(panel.Flush() && prompts.Count == 1, "拒绝后的Flush重复提示。");
                AssertSnapshot(root, before, "拒绝确认时写入了游戏文件。");

                accept = true;
                SelectAsUser(combo, next); Settle(form);
                var changes = workspace.Documents.Values.SelectMany(document => document.EnumerateChanges()).ToArray();
                TestSupport.Assert(prompts.Count == 2 && changes.Length == 1 &&
                    changes[0].Table.Key == StrategyEffectClassService.TableKey && changes[0].RowIndex == 0 &&
                    changes[0].Field.Name == StrategyEffectClassService.FieldName && Value(effects, 0) == next,
                    "接受确认没有仅生成一个效果类字段草稿。");
                TestSupport.Assert(!panel.HasPendingChanges && combo.Text == combo.Options.Single(option => option.Id == next).Display,
                    "接受选择后显示文本或待提交状态不正确。");
                for (var repeat = 0; repeat < 3; repeat++)
                    TestSupport.Assert(panel.Flush() && prompts.Count == 2, "重复Flush再次弹出效果确认。");
                AssertSnapshot(root, before, "接受效果下拉选择在保存前写入了游戏文件。");

                panel.Bind(BusinessPageKind.Strategies, 90); Settle(form);
                combo = EffectCombo(panel); AssertLabels(combo);
                TestSupport.Assert(combo.Text == "42：默认伤害" && combo.Options.Single(option => option.Id == 0x2A).Name == "默认伤害",
                    "实体重绑定没有显示现有默认伤害别名。");
                TestSupport.Assert(panel.Flush() && prompts.Count == 2, "保留默认伤害原值触发了确认。");
                panel.Bind(BusinessPageKind.Strategies, 78); Settle(form);
                combo = EffectCombo(panel); AssertLabels(combo);
                TestSupport.Assert(combo.Text == "144：未识别效果" && combo.Options.Any(option => option.Id == 0x90) &&
                    combo.Options.All(option => option.Id != 0x2A), "实体重绑定丢失90原值或沿用上一实体的默认别名候选。");
                TestSupport.Assert(panel.Flush() && prompts.Count == 2, "保留90原值触发了确认。");
                panel.Bind(BusinessPageKind.Strategies, 0); Settle(form);
                combo = EffectCombo(panel); AssertLabels(combo);
                TestSupport.Assert(combo.TryGetSelectedId(out var rebound) && rebound == next &&
                    combo.Options.All(option => option.Id != 0x2A && option.Id != 0x90) && panel.Flush() && prompts.Count == 2,
                    "返回已编辑实体丢失草稿、泄漏保留项或重复确认。");
                AssertSnapshot(root, before, "实体切换在保存前写入了游戏文件。");

                var saved = workspace.Save();
                TestSupport.Assert(saved.Saved && saved.ChangedFields == 1 && saved.ChangedBytes == 1,
                    "实际UI效果草稿没有以单字段单字节保存：" + string.Join("；", saved.Messages));
                foreach (var pair in before)
                {
                    var after = File.ReadAllBytes(ProjectFileLocator.Resolve(root, pair.Key));
                    TestSupport.Assert(after.Length == pair.Value.Length, "效果UI保存改变了文件长度。");
                    var offsets = Enumerable.Range(0, after.Length).Where(index => after[index] != pair.Value[index]).ToArray();
                    TestSupport.Assert(pair.Key.Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)
                            ? offsets.SequenceEqual([checked((int)effects.Definition.DataOffset)]) : offsets.Length == 0,
                        "效果UI保存改变了目标单字节以外的数据：" + pair.Key);
                }
                TestSupport.Assert(File.ReadAllBytes(executable)[checked((int)effects.Definition.DataOffset)] == next,
                    "效果UI保存后的磁盘复读值错误。");
                Console.WriteLine($"STRATEGY_EFFECT_UI_FIXTURE_OK {name} exact-label deny accept repeat-flush rebind single-byte");
            }
            finally { form?.Dispose(); Directory.Delete(root, true); }
            foreach (var pair in originalHashes)
                TestSupport.Assert(HashService.Sha256(ProjectFileLocator.Resolve(source, pair.Key)) == pair.Value,
                    "效果UI测试改变了原始基底：" + name + "/" + pair.Key);
        }
        Console.WriteLine("STRATEGY_EFFECT_UI_TESTS_OK originals-unchanged");
    }

    private static SearchableIdComboBox EffectCombo(BusinessEditorPanel panel)
    {
        var section = Descendants(panel.DetailTabs.SelectedTab!).Single(control => control.Name == "StrategyEffectClassField");
        var controls = Descendants(section).ToArray();
        TestSupport.Assert(controls.OfType<Label>().Count() == 1 && controls.OfType<Label>().Single().Text == "效果索引" &&
            controls.OfType<ComboBox>().Count() == 1 && !controls.Any(control => control is TextBoxBase or LinkLabel),
            "效果索引字段应只包含一个标签和一个下拉框。");
        return controls.OfType<SearchableIdComboBox>().Single();
    }

    private static void AssertLabels(SearchableIdComboBox combo)
    {
        TestSupport.Assert(!combo.IdOnly && !combo.RangePreviewMode && combo.Options.Count > 0 &&
            combo.Options.All(option => !string.IsNullOrWhiteSpace(option.Name) &&
                combo.GetDisplayText(option) == option.Id.ToString(CultureInfo.InvariantCulture) + "：" + option.Name),
            "效果候选项未严格显示ID：名称。");
    }

    private static void SelectAsUser(SearchableIdComboBox combo, int id)
    {
        combo.SetBoundValue(string.Empty, filterOptions: true);
        combo.SelectedItem = combo.Items.Cast<DataManager.App.IdNameOption>().Single(option => option.Id == id);
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(combo, [EventArgs.Empty]);
    }

    private static int Value(TableDocument document, int row)
        => Convert.ToInt32(document.Data.Rows[row][StrategyEffectClassService.FieldName], CultureInfo.InvariantCulture);
    private static Dictionary<string, byte[]> Snapshot(string root)
        => Known65FixtureSnapshot.CoreNames.ToDictionary(name => name, name => File.ReadAllBytes(ProjectFileLocator.Resolve(root, name)));
    private static void AssertSnapshot(string root, IReadOnlyDictionary<string, byte[]> expected, string message)
    {
        foreach (var pair in expected)
            TestSupport.Assert(File.ReadAllBytes(ProjectFileLocator.Resolve(root, pair.Key)).SequenceEqual(pair.Value), message + pair.Key);
    }
    private static void Settle(MainForm form) { Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
