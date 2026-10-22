using DataManager.App;
using DataManager.Core.Domain;
using System.Globalization;
using System.Text;
using System.Diagnostics;

namespace DataManager.Tests;

internal static class WinFormsSmokeTests
{
    public static void RunJobAttributesIntegration(string basePath)
    {
        // Use the supplied 6.5/6.6 bytes exactly, rather than recovering an
        // older fixture snapshot. Only this temporary core copy is editable.
        var copy = TestSupport.CreateCoreCopy(basePath, restoreKnown65Snapshot: false);
        using var form = new MainForm(openDefaultProject: false) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        try
        {
            form.Show();
            Application.DoEvents();
            TestSupport.Assert(string.IsNullOrEmpty(form.WorkspaceForSmokeTest.RootPath), "UI测试启动意外打开了真实默认基底");
            form.LoadProjectForSmokeTest(copy);
            Application.DoEvents();
            TestSupport.Assert(form.WorkspaceForSmokeTest.RootPath == copy, "延后触发的Shown覆盖了临时测试项目");
            CheckReboundJobAttributes(form, persistAndUndo: true);
            Console.WriteLine($"JOB_ATTRIBUTES_UI_TESTS_OK {form.WorkspaceForSmokeTest.ActiveProfile.ProfileId}/{form.WorkspaceForSmokeTest.ActiveProfile.VariantId}");
        }
        finally
        {
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges();
            form.Close();
            Directory.Delete(copy, true);
        }
    }

    public static void RunProjectIntegration(string basePath)
    {
        var copy = TestSupport.CreateCoreCopy(basePath);
        using var form = new MainForm(openDefaultProject: false) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        try
        {
            form.Show();
            form.LoadProjectForSmokeTest(copy);
            Application.DoEvents();
            TestSupport.Assert(form.WorkspaceForSmokeTest.RootPath == copy, "UI集成测试没有保持在临时项目中");
            TestSupport.Assert(form.BusinessEditorForSmokeTest.Visible && form.BusinessEditorForSmokeTest.CurrentKind == BusinessPageKind.Persons,
                "打开项目没有进入人物业务详情");
            var list = form.EntityListForSmokeTest;
            TestSupport.Assert(list.Items.Count == 1024 && list.Items[0]?.ToString()?.StartsWith("0：", StringComparison.Ordinal) == true,
                "人物左侧必须只显示 ID：名称");
            TestSupport.Assert(!Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Any(label => label.Text == "Army"),
                "人物业务详情没有隐藏 Army");
            TestSupport.Assert(!Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Any(label => label.Text is "级别" or "经验"),
                "人物业务详情没有隐藏级别和经验");
            TestSupport.Assert(form.BusinessEditorForSmokeTest.DetailTabs.TabCount == 1 &&
                Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Any(label => label.Text == "R形象编号") &&
                Descendants(form.BusinessEditorForSmokeTest).OfType<ComboBox>().Any(combo => combo.Items.Cast<object>().Select(item => item.ToString()).Contains("特殊暴击")),
                "人物详情应合并列传、形象指定、预览和普通/特殊暴击模式");
            var reusedPower = FindNumeric(form.BusinessEditorForSmokeTest, "武力");
            var times = new List<double>();
            for (var id = 1; id <= 20; id++)
            {
                var watch = Stopwatch.StartNew();
                list.SelectedIndex = id;
                Application.DoEvents();
                times.Add(watch.Elapsed.TotalMilliseconds);
                TestSupport.Assert(ReferenceEquals(reusedPower, FindNumeric(form.BusinessEditorForSmokeTest, "武力")),
                    $"人物 {id} 切换重新创建了固定字段控件");
                TestSupport.Assert(Convert.ToInt32(reusedPower.Value, CultureInfo.InvariantCulture) ==
                    Convert.ToInt32(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[id]["武力"], CultureInfo.InvariantCulture),
                    $"人物 {id} 复用控件没有加载当前实体值");
            }
            Console.WriteLine($"UI_PERSON_SWITCH_20 total={times.Sum():F0}ms p95={times.OrderBy(value => value).ElementAt(18):F0}ms");
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "切换实体控件复用产生了意外草稿");
            list.SelectedIndex = 0;
            var split = Descendants(form).OfType<SplitContainer>().Single(item => Descendants(item.Panel1).Contains(list));
            TestSupport.Assert(split.IsSplitterFixed && split.FixedPanel == FixedPanel.Panel1 && split.Panel1.Width >= 220,
                $"实体列表/详情双窗格必须固定且左侧完整可用：fixed={split.IsSplitterFixed}，panel={split.FixedPanel}，width={split.Panel1.Width}");
            TestSupport.Assert(form.ModuleTabsForSmokeTest.RectangleToScreen(form.ModuleTabsForSmokeTest.ClientRectangle).Bottom <=
                split.RectangleToScreen(split.ClientRectangle).Top + 1,
                "模块选择没有放在顶部页签区域");
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                form.Size = size;
                Application.DoEvents();
                TestSupport.Assert(split.Panel1.Width >= 220 && list.Width >= 200 && split.Panel2.Width > 700,
                    $"{size.Width}×{size.Height} 下固定详情和实体列表不足以完整显示");
            }

            foreach (var (key, kind) in new[]
            {
                ("人物", BusinessPageKind.Persons), ("兵种系", BusinessPageKind.JobSeries), ("兵种", BusinessPageKind.DetailedJobs),
                ("宝物", BusinessPageKind.Items), ("策略", BusinessPageKind.Strategies),
                ("人物/套装特效", BusinessPageKind.Effects), ("商店", BusinessPageKind.Shop), ("拓展数据", BusinessPageKind.ExtendData)
            })
            {
                form.SelectModuleForSmokeTest(key);
                Application.DoEvents();
                TestSupport.Assert(form.BusinessEditorForSmokeTest.CurrentKind == kind && form.BusinessEditorForSmokeTest.DetailTabs.TabCount > 0 &&
                    !form.StatusForSmokeTest.Contains("详情读取失败", StringComparison.Ordinal),
                    $"业务页绑定失败：{key}；{form.StatusForSmokeTest}");
                if (kind == BusinessPageKind.ExtendData)
                    TestSupport.Assert(form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Cast<TabPage>().Select(tab => tab.Text)
                        .SequenceEqual(["人物特效", "特效成长", "策略学习", "锻造配方"]), "缺少拓展文件时仍必须显示固定四个子页及缺失说明");
                if (kind == BusinessPageKind.Items)
                {
                    TestSupport.Assert(!form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Cast<TabPage>().Any(tab => tab.Text == "装备类型"), "宝物页仍有重复的装备类型子页");
                    var permissions = form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "可装备部队");
                    var matrix = Descendants(permissions).OfType<TableLayoutPanel>().Single(table => table.ColumnCount == 4 && table.RowCount == 20);
                    TestSupport.Assert(matrix.Controls.OfType<CheckBox>().Count() == 80, "宝物可装备部队必须为四列、每列二十个");
                    TestSupport.Assert(Descendants(form.BusinessEditorForSmokeTest).OfType<SearchableIdComboBox>().Any(combo => combo.Options.Any(option => option.Id == 255 && option.Name == "无")),
                        "宝物特效号没有提供 255：无");
                }
                if (kind == BusinessPageKind.Strategies)
                {
                    var learning = form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "兵种学习等级");
                    form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab = learning;
                    Application.DoEvents();
                    TestSupport.Assert(Descendants(learning).OfType<NumericUpDown>().Count() == 80 &&
                        !Descendants(learning).OfType<DataGridView>().Any(), "策略学习页没有使用八十个统一数值编辑框");
                    TestSupport.Assert(Descendants(learning).OfType<TableLayoutPanel>().Any(table => table.ColumnCount == 4), "策略学习等级没有分为四列");
                }
                if (kind == BusinessPageKind.JobSeries)
                {
                    TestSupport.Assert(new[] { "移动声音", "移动速度", "攻击声音", "远程兵种", "攻击延迟", "兵种类型", "策略伤害", "参与围攻" }
                        .All(name => Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Any(label => label.Text == name)), "兵种系属性缺少中文语义标签");
                    var terrain = Descendants(form.BusinessEditorForSmokeTest).Single(control => control.Name == "JobSeriesTerrain");
                    var labels = Descendants(terrain).OfType<Label>().Select(label => label.Text).ToArray();
                    TestSupport.Assert(labels.All(label => !label.Any(char.IsDigit)) && labels.Count(label => label == "宝物库") == 1 &&
                        Descendants(terrain).OfType<NumericUpDown>().Count() == 60,
                        "兵种属性应将三十项地形与对应发挥、移动消耗成对显示");
                }
                if (kind is BusinessPageKind.Persons or BusinessPageKind.DetailedJobs or BusinessPageKind.Items or BusinessPageKind.Shop)
                    TestSupport.Assert(form.BusinessEditorForSmokeTest.DetailTabs.TabCount <= (kind == BusinessPageKind.Items ? 2 : 1), $"{key} 仍拆分了过多小页");
                SavePageSnapshots(form, key);
            }
            form.SelectModuleForSmokeTest("人物");
            list.SelectedIndex = 17;
            Application.DoEvents();
            TestSupport.Assert(form.BusinessEditorForSmokeTest.CurrentId == 17, "左侧人物选择没有绑定右侧详情");
            var powerLabel = Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Single(label => label.Text == "武力");
            var layout = (TableLayoutPanel)powerLabel.Parent!;
            var powerEditor = layout.GetControlFromPosition(1, layout.GetPositionFromControl(powerLabel).Row) as NumericUpDown;
            TestSupport.Assert(powerEditor != null, "人物五维没有使用可编辑数值控件");
            var originalPower = Convert.ToInt32(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[17]["武力"], CultureInfo.InvariantCulture);
            var unrelatedPower = Convert.ToInt32(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            powerEditor!.Value = originalPower == 255 ? 254 : originalPower + 1;
            TestSupport.Assert(form.WorkspaceForSmokeTest.IsDirty, "业务控件编辑没有进入工作区事务");
            TestSupport.Assert(Convert.ToInt32(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["武力"], CultureInfo.InvariantCulture) == unrelatedPower,
                "复用人物字段控件误写了上一个人物");
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(Convert.ToInt32(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[17]["武力"], CultureInfo.InvariantCulture) == originalPower,
                "业务控件草稿无法撤回");
            form.SelectModuleForSmokeTest("人物");

            CheckReboundMatrix(form);
            CheckReboundJobAttributes(form);
            CheckAccessoryGroupEditor(form);
            form.SelectModuleForSmokeTest("人物");

            var exports = form.ExportBusinessCsvForSmokeTest(Path.Combine(copy, "csv"));
            TestSupport.Assert(exports.Any(item => item.TableKey == "persons") && exports.Any(item => item.TableKey == "person-biography"),
                "人物业务 CSV 没有按物理表批量导出");
            var files = exports.ToDictionary(item => item.TableKey, item => item.Path);
            TestSupport.Assert(form.ImportBusinessCsvForSmokeTest(files).ChangedCells == 0, "业务 CSV 原样导回产生了变更");
            var beforeName = Convert.ToString(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["名称"], CultureInfo.InvariantCulture);
            File.WriteAllText(files["persons"], "ID,名称\r\n0,界面烟测\r\n", new UTF8Encoding(true));
            var imported = form.ImportBusinessCsvForSmokeTest(files);
            TestSupport.Assert(imported.ChangedCells == 1 && form.WorkspaceForSmokeTest.IsDirty, "业务 CSV 导入没有形成统一草稿");
            form.UndoDraftForSmokeTest();
            TestSupport.Assert(Convert.ToString(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["名称"], CultureInfo.InvariantCulture) == beforeName &&
                !form.WorkspaceForSmokeTest.IsDirty, "业务 CSV 事务无法整体撤回");
        }
        finally
        {
            // Tests operate on drafts only; prevent a failure from prompting
            // for a write while the off-screen form is disposed.
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges();
            form.Close();
            Directory.Delete(copy, true);
        }
    }

    private static NumericUpDown FindNumeric(Control root, string labelText)
    {
        var label = Descendants(root).OfType<Label>().Single(label => label.Text == labelText);
        var layout = (TableLayoutPanel)label.Parent!;
        return (NumericUpDown)layout.GetControlFromPosition(1, layout.GetPositionFromControl(label).Row)!;
    }

    private static void CheckReboundMatrix(MainForm form)
    {
        form.SelectModuleForSmokeTest("策略");
        var tabs = form.BusinessEditorForSmokeTest.DetailTabs;
        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "兵种学习等级");
        Application.DoEvents();
        var editors = Descendants(tabs.SelectedTab).OfType<NumericUpDown>().ToArray();
        TestSupport.Assert(editors.Length == 80, "策略学习矩阵没有完整保留八十个数值编辑框");
        form.EntityListForSmokeTest.SelectedIndex = 17;
        Application.DoEvents();
        TestSupport.Assert(editors.SequenceEqual(Descendants(tabs.SelectedTab).OfType<NumericUpDown>()),
            "切换策略重新创建了固定学习矩阵");
        var document = form.WorkspaceForSmokeTest.GetDocument("strategies");
        var learningField = document.Definition.Fields.First(field => field.Name.StartsWith("兵种", StringComparison.Ordinal) && field.Name.EndsWith("学习等级", StringComparison.Ordinal)).Name;
        var before = Convert.ToInt32(document.Data.Rows[17][learningField], CultureInfo.InvariantCulture);
        var unrelated = Convert.ToInt32(document.Data.Rows[0][learningField], CultureInfo.InvariantCulture);
        // Keep focus in the native numeric editor so Flush must collect its
        // pending text, as the top-level Save action does in the application.
        editors[0].Focus();
        editors[0].Text = (before == 255 ? 254 : before + 1).ToString(CultureInfo.InvariantCulture);
        TestSupport.Assert(form.BusinessEditorForSmokeTest.Flush(), "策略学习矩阵无法统一提交");
        TestSupport.Assert(Convert.ToInt32(document.Data.Rows[17][learningField], CultureInfo.InvariantCulture) != before &&
            Convert.ToInt32(document.Data.Rows[0][learningField], CultureInfo.InvariantCulture) == unrelated,
            "策略学习矩阵复用后没有只写当前策略");
        form.UndoDraftForSmokeTest();
        TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "策略学习矩阵草稿没有整体撤回");
    }

    private static void CheckReboundJobAttributes(MainForm form, bool persistAndUndo = false)
    {
        form.SelectModuleForSmokeTest("兵种系");
        form.EntityListForSmokeTest.SelectedIndex = 0;
        Application.DoEvents();
        var labels = new[] { "移动声音", "移动速度", "攻击声音", "远程兵种", "攻击延迟", "兵种类型", "策略伤害", "参与围攻" };
        Control Editor(string labelText)
        {
            var fieldLabel = Descendants(form.BusinessEditorForSmokeTest).OfType<Label>().Single(item => item.Text == labelText);
            var layout = (TableLayoutPanel)fieldLabel.Parent!;
            return layout.GetControlFromPosition(1, layout.GetPositionFromControl(fieldLabel).Row)!;
        }
        int Value(Control editor) => editor switch
        {
            NumericUpDown number => (int)number.Value,
            SearchableIdComboBox picker when picker.TryGetSelectedId(out var id) => id,
            _ => throw new InvalidOperationException("兵种属性没有可读取的数值编辑器")
        };
        foreach (var (label, names) in new[]
        {
            ("移动声音", new[] { "马蹄声", "车轮声", "脚步声", "无声", "引擎声", "预设1", "预设2" }),
            ("移动速度", new[] { "快速", "慢速" }),
            ("攻击声音", new[] { "剑", "其他", "爆炸1", "爆炸2", "火箭筒", "枪声（连射）", "枪声（点射）", "火焰喷射", "毒气喷射" }),
            ("兵种类型", new[] { "全能", "文官", "武将" })
        })
        {
            var picker = (SearchableIdComboBox)Editor(label);
            TestSupport.Assert(picker.Options.Select(option => option.Id).SequenceEqual(Enumerable.Range(0, names.Length)) &&
                picker.Options.Select(option => option.Name).SequenceEqual(names), label + "枚举与确认定义不一致");
        }
        var workspace = form.WorkspaceForSmokeTest;
        var document = workspace.GetDocument("job-series-attributes");
        var baseline = new int[8, 40];
        for (var row = 0; row < 8; row++)
            for (var id = 0; id < 40; id++)
                baseline[row, id] = Convert.ToInt32(document.Data.Rows[row][$"兵种系 {id}"], CultureInfo.InvariantCulture);
        var editors = labels.Select(Editor).ToArray();
        var attributeGroup = Descendants(form.BusinessEditorForSmokeTest).Single(group => group.Name == "JobSeriesAttributes");
        foreach (var id in new[] { 0, 7, 8, 20, 39, 0 })
        {
            form.EntityListForSmokeTest.SelectedIndex = id;
            Application.DoEvents();
            TestSupport.Assert(string.IsNullOrEmpty(attributeGroup.Text), "兵种属性仍显示重复的实体标题");
            TestSupport.Assert(ReferenceEquals(attributeGroup, Descendants(form.BusinessEditorForSmokeTest)
                .Single(group => group.Name == "JobSeriesAttributes")), $"兵种系{id}切换重建了属性面板");
            for (var row = 0; row < labels.Length; row++)
            {
                var current = Editor(labels[row]);
                TestSupport.Assert(ReferenceEquals(editors[row], current), $"兵种系{id}/{labels[row]}切换重建了控件");
                TestSupport.Assert(Value(current) == baseline[row, id], $"兵种系{id}/{labels[row]}仍显示上一兵种系的值");
            }
            TestSupport.Assert(!workspace.IsDirty, $"兵种系{id}纯切换意外生成草稿");
        }

        // Edit all eight attributes after crossing the transposed table's row
        // boundary. Every intermediate draft must change one current column,
        // without touching any of the other 39 job-series columns.
        const int editedId = 20;
        form.EntityListForSmokeTest.SelectedIndex = editedId;
        Application.DoEvents();
        var expected = (int[,])baseline.Clone();
        var executableBefore = File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe"));
        for (var row = 0; row < labels.Length; row++)
        {
            var editor = Editor(labels[row]);
            var next = baseline[row, editedId] == 0 ? 1 : 0;
            if (editor is NumericUpDown number) number.Value = next;
            else ((SearchableIdComboBox)editor).Text = next.ToString(CultureInfo.InvariantCulture);
            TestSupport.Assert(form.BusinessEditorForSmokeTest.Flush(), $"{labels[row]}无法提交草稿");
            expected[row, editedId] = next;
            CheckAllValues(expected, workspace.GetDocument("job-series-attributes"), labels[row] + "草稿");
        }
        if (persistAndUndo)
        {
            TestSupport.Assert(workspace.Save().Saved, "兵种属性八字段保存失败");
            var reread = new DataManager.Core.Editing.ProjectWorkspace();
            reread.Open(workspace.RootPath, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
            CheckAllValues(expected, reread.GetDocument("job-series-attributes"), "保存后重新打开");
            TestSupport.Assert(workspace.UndoSaved().Success, "兵种属性保存历史无法撤回");
            TestSupport.Assert(File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe")).SequenceEqual(executableBefore),
                "兵种属性历史撤回后EXE没有逐字节恢复");
        }
        else
            for (var row = 0; row < labels.Length; row++) form.UndoDraftForSmokeTest();
        CheckAllValues(baseline, workspace.GetDocument("job-series-attributes"), "撤回后");
        TestSupport.Assert(!workspace.IsDirty, "兵种属性草稿没有整体撤回");
        Console.WriteLine("UI_JOB_ATTRIBUTES ids=0,7,8,20,39 fields=8 reused=true isolatedWrites=8 saveReadback=" + persistAndUndo);

        static void CheckAllValues(int[,] values, DataManager.Core.Models.TableDocument current, string phase)
        {
            for (var attribute = 0; attribute < 8; attribute++)
                for (var id = 0; id < 40; id++)
                    TestSupport.Assert(Convert.ToInt32(current.Data.Rows[attribute][$"兵种系 {id}"], CultureInfo.InvariantCulture) == values[attribute, id],
                        $"{phase}串写或丢失：属性{attribute}/兵种系{id}");
        }
    }

    private static void CheckAccessoryGroupEditor(MainForm form)
    {
        var workspace = form.WorkspaceForSmokeTest;
        var service = new AccessoryJobGroupService();
        var original = service.Read(workspace);
        var before = File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe"));
        TabPage Page() => form.BusinessEditorForSmokeTest.DetailTabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "辅助分组");
        void ShowPage() { form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab = Page(); Application.DoEvents(); }
        ShowPage();
        var page = Page();
        var grid = Descendants(page).OfType<DataGridView>().Single();
        var checks = Descendants(page).OfType<CheckBox>().ToArray();
        TestSupport.Assert(checks.Length == 40 && grid.Columns[1].HeaderText == "兵种系名称", "辅助分组未显示40项名称勾选和名称表头");
        TestSupport.Assert(checks.All(check => !check.Text.Contains('：')), "辅助分组复选框仍显示ID串");
        Descendants(page).OfType<Button>().Single(button => button.Text == "新增空白分组").PerformClick();
        TestSupport.Assert(grid.RowCount == original.Groups.Count + 1 && checks.All(check => !check.Checked), "新增分组没有生成空白草稿");
        Descendants(page).OfType<Button>().Single(button => button.Text == "校验保存").PerformClick();
        TestSupport.Assert(File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe")).SequenceEqual(before), "空白辅助分组校验失败却写入EXE");
        checks[0].Checked = true;
        TestSupport.Assert(Convert.ToString(grid.CurrentRow!.Cells[1].Value) == checks[0].Text, "勾选未更新分组名称");
        grid.CurrentCell = grid.Rows[0].Cells[0]; Application.DoEvents();
        TestSupport.Assert(checks.Select((check, id) => (check.Checked, id)).Where(item => item.Checked).Select(item => item.id)
            .SequenceEqual(original.Groups[0].JobSeriesIds.Order()), "选组未载入对应勾选状态");
        grid.CurrentCell = grid.Rows[grid.RowCount - 1].Cells[0]; Application.DoEvents();
        TestSupport.Assert(checks[0].Checked && checks.Skip(1).All(check => !check.Checked), "切换分组丢失勾选草稿");
        form.SelectModuleForSmokeTest("人物"); form.SelectModuleForSmokeTest("兵种系"); ShowPage();
        page = Page(); checks = Descendants(page).OfType<CheckBox>().ToArray();
        TestSupport.Assert(checks[0].Checked && checks.Skip(1).All(check => !check.Checked), "跨模块切换丢失辅助分组草稿");
        Descendants(page).OfType<Button>().Single(button => button.Text == "校验保存").PerformClick();
        var reread = service.Read(workspace);
        TestSupport.Assert(reread.Groups.Count == original.Groups.Count + 1 && reread.Groups[^1].JobSeriesIds.SequenceEqual([0]), "辅助分组勾选保存后复读失败");
        TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(Path.Combine(workspace.RootPath, "Ekd5.exe")).SequenceEqual(before), "辅助分组UI保存不能逐字节撤回");
    }

    private static void SavePageSnapshots(MainForm form, string module)
    {
        var directory = Environment.GetEnvironmentVariable("DATAMANAGER_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var tabs = form.BusinessEditorForSmokeTest.DetailTabs;
        foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
        {
            form.Size = size;
            foreach (TabPage tab in tabs.TabPages)
            {
                tabs.SelectedTab = tab; Application.DoEvents();
                // Form.DrawToBitmap includes the non-client title and frame.
                // A bitmap sized to ClientSize would cut off the bottom rows.
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                var safeModule = module.Replace('/', '-');
                var safeTab = tab.Text.Replace('/', '-');
                bitmap.Save(Path.Combine(directory, $"{safeModule}-{safeTab}-{form.Width}x{form.Height}.png"));
                var clipped = Descendants(tab).Where(control => control.Visible && control is NumericUpDown or ComboBox or TextBox or Button or CheckBox)
                    .Where(IsClippedByAncestor)
                    .Select(control => $"{control.GetType().Name}:{control.Text} bounds={control.Bounds} parent={control.Parent!.ClientSize}").ToArray();
                TestSupport.Assert(clipped.Length == 0, $"UI_LAYOUT_CLIP {module}/{tab.Text}/{form.Width}: {string.Join("; ", clipped)}");
                foreach (var grid in Descendants(tab).OfType<DataGridView>().Where(grid => grid.Visible && grid.ScrollBars == ScrollBars.None && grid.RowCount > 0))
                {
                    var last = grid.GetRowDisplayRectangle(grid.RowCount - 1, false);
                    TestSupport.Assert(last.Height > 0 && last.Bottom <= grid.ClientSize.Height,
                        $"矩阵末行显示不完整：{module}/{tab.Text}/{form.Width}，{last}，可用 {grid.ClientSize}");
                }
                if (module == "人物")
                {
                    var mode = Descendants(tab).OfType<ComboBox>().Single(combo => combo.Items.Cast<object>().Any(item => item?.ToString() == "特殊暴击"));
                    var selected = mode.SelectedIndex;
                    mode.SelectedIndex = 0; Application.DoEvents();
                    TestSupport.Assert(!Descendants(tab).Where(control => control.Visible && control is TextBox or ComboBox or NumericUpDown or Button or CheckBox).Any(IsClippedByAncestor),
                        "普通暴击模式的三句台词被裁切");
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(directory, $"人物-普通暴击-{form.Width}x{form.Height}.png"));
                    mode.SelectedIndex = selected; Application.DoEvents();
                }
            }
        }
        tabs.SelectedIndex = 0;
    }

    private static bool IsClippedByAncestor(Control control)
    {
        if (control.Parent == null) return false;
        var screenBounds = control.Parent.RectangleToScreen(control.Bounds);
        for (Control? ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            var bounds = ancestor.RectangleToClient(screenBounds);
            if (bounds.Top < -2 || bounds.Left < -2 || bounds.Bottom > ancestor.ClientSize.Height + 2 || bounds.Right > ancestor.ClientSize.Width + 2)
                return true;
        }
        return false;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public static void Run(string? snapshotPath = null)
    {
        using var form = new MainForm(openDefaultProject: false);
        TestSupport.Assert(form.Text.Contains("6.5/6.6", StringComparison.Ordinal), "主窗口标题没有声明双 Profile");
        TestSupport.Assert(form.MinimumSize.Width >= 1000 && form.KeyPreview, "主窗口基本交互配置缺失");
        if (string.IsNullOrWhiteSpace(snapshotPath)) return;

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(snapshotPath);
        form.Close();
    }
}
