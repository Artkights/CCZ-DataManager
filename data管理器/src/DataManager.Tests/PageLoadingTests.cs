using System.Data;
using System.Diagnostics;
using System.Reflection;
using DataManager.App;
using DataManager.Core.Editing;

namespace DataManager.Tests;

internal static class PageLoadingTests
{
    internal static void Run(string basePath)
    {
        CheckDataViewFilter();
        CheckCancelledWorkspace(basePath);
        var copy = TestSupport.CreateCoreCopy(basePath);
        using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000), ShowInTaskbar = false, Size = new Size(1280, 720) };
        try
        {
            form.Show();
            var coldLoad = Stopwatch.StartNew();
            form.LoadProjectForSmokeTest(copy);
            Application.DoEvents();
            coldLoad.Stop();
            Console.WriteLine($"UI_COLD_LOAD {coldLoad.Elapsed.TotalMilliseconds:F2}ms dpi={form.DeviceDpi} font={form.Font.Name}/{form.Font.SizeInPoints} boundary=initial-person-controls background-images-not-awaited");
            var panels = new Dictionary<string, BusinessEditorPanel>();
            var pages = new Dictionary<string, TabPage>();
            var modules = new[] { "人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据" };
            foreach (var key in modules)
            {
                form.SelectModuleForSmokeTest(key);
                Application.DoEvents();
                var panel = form.BusinessEditorForSmokeTest;
                panels[key] = panel;
                panel.DetailTabs.SelectedIndex = panel.DetailTabs.TabPages.Count - 1;
                pages[key] = panel.DetailTabs.SelectedTab!;
            }
            var times = new List<double>();
            var eventDelays = new List<double>();
            using var measuredProcess = Process.GetCurrentProcess();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            measuredProcess.Refresh();
            var privateMemoryBefore = measuredProcess.PrivateMemorySize64;
            for (var cycle = 0; cycle < 5; cycle++)
                foreach (var key in modules)
                {
                    var watch = Stopwatch.StartNew();
                    form.SelectModuleForSmokeTest(key);
                    Application.DoEvents();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    TestSupport.Assert(ReferenceEquals(panels[key], form.BusinessEditorForSmokeTest), key + "往返重建了业务面板");
                    TestSupport.Assert(ReferenceEquals(pages[key], form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab), key + "往返丢失了子页签或控件树");
                    var eventClock = Stopwatch.StartNew();
                    form.BeginInvoke((Action)(() => eventDelays.Add(eventClock.Elapsed.TotalMilliseconds)));
                    Application.DoEvents();
                }
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "重复模块导航产生了草稿");
            TestSupport.Assert(panels.Values.All(panel => !panel.IsDisposed), "缓存中的业务面板被意外释放");
            Console.WriteLine($"UI_MODULE_SWITCH_40 total={times.Sum():F0}ms p95={times.Order().ElementAt(37):F0}ms panels={panels.Count} reused=true");
            TestSupport.Assert(eventDelays.Count == times.Count, "缓存切页期间投递的 UI 事件未处理完成");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); measuredProcess.Refresh();
            Console.WriteLine($"UI_EVENT_DELAY_40 p95={eventDelays.Order().ElementAt(37):F2}ms max={eventDelays.Max():F2}ms private-bytes={privateMemoryBefore}->{measuredProcess.PrivateMemorySize64}");

            form.SelectModuleForSmokeTest("人物");
            form.ShowPhysicalTableForSmokeTest("persons");
            var grid = form.PhysicalGridForSmokeTest;
            grid.Columns["武力"].Width = 219;
            var widths = grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width).ToArray();
            for (var index = 0; index < Math.Min(grid.RowCount, 600); index += 47)
            {
                grid.FirstDisplayedScrollingRowIndex = index;
                Application.DoEvents();
                TestSupport.Assert(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width).SequenceEqual(widths), "滚动改变了物理表列宽");
            }
            grid.CurrentCell = grid.Rows[500].Cells["武力"];
            grid.FirstDisplayedScrollingRowIndex = 470;
            Application.DoEvents();
            var rememberedRow = grid.FirstDisplayedScrollingRowIndex;
            var rememberedCell = (grid.CurrentCell!.RowIndex, grid.CurrentCell.OwningColumn.Name);
            form.SelectModuleForSmokeTest("策略");
            form.SelectModuleForSmokeTest("人物");
            TestSupport.Assert(grid.Visible && grid.Columns["武力"].Width == 219, "切页未恢复物理表选择或用户列宽");
            TestSupport.Assert(grid.FirstDisplayedScrollingRowIndex == rememberedRow &&
                (grid.CurrentCell!.RowIndex, grid.CurrentCell.OwningColumn.Name) == rememberedCell,
                "切页未恢复物理表滚动位置或当前单元格");
            CheckReferenceChoices(form);

            var lookup = new LookupIndexCache();
            using (lookup)
            {
                var document = form.WorkspaceForSmokeTest.GetDocument("persons");
                var options = lookup.Options(document);
                var row = lookup.Find(document, 1)!.Value;
                var power = document.Data.Rows[1]["武力"];
                document.Data.Rows[1]["武力"] = Convert.ToInt32(power) + 1;
                TestSupport.Assert(ReferenceEquals(options, lookup.Options(document)), "数值变化使名称列表失效");
                TestSupport.Assert(ReferenceEquals(row.Row, lookup.Find(document, 1)!.Value.Row), "数值变化破坏ID索引");
                document.Data.Rows[1]["武力"] = power;
                var name = document.Data.Rows[1]["名称"];
                document.Data.Rows[1]["名称"] = "缓存测试";
                TestSupport.Assert(!ReferenceEquals(options, lookup.Options(document)) && lookup.Options(document).Any(option => option.Id == 1 && option.Name == "缓存测试"), "名称变化未刷新选项");
                document.Data.Rows[1]["名称"] = name;
            }
            Console.WriteLine("PAGE_LOADING_TESTS_OK module-cache/filter/physical-widths/lookup/cancel");
        }
        finally
        {
            foreach (var panel in panelsForCleanup(form)) panel.DiscardPendingChanges();
            // Tests change temporary cells directly, so restore before Close
            // rather than displaying a save prompt on the offscreen form.
            form.WorkspaceForSmokeTest.ReloadLoadedDocuments();
            form.Close();
            Directory.Delete(copy, true);
        }
    }

    private static void CheckReferenceChoices(MainForm form)
    {
        form.SelectModuleForSmokeTest("人物"); form.ShowPhysicalTableForSmokeTest("persons");
        var workspace = form.WorkspaceForSmokeTest;
        var persons = workspace.GetDocument("persons");
        var grid = form.PhysicalGridForSmokeTest;
        var field = persons.Definition.Fields.First(field => field.Policy == DataManager.Core.Models.FieldPolicy.Editable && field.ReferenceTable == "detailed-jobs");
        var referenced = workspace.GetDocument(field.ReferenceTable!);
        var configure = typeof(MainForm).GetMethod("ConfigureReferenceColumns", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var combo = (DataGridViewComboBoxColumn)grid.Columns[field.Name];
        var choices = combo.DataSource;
        var numeric = persons.Data.Rows[0]["武力"];
        persons.Data.Rows[0]["武力"] = Convert.ToInt32(numeric) ^ 1;
        configure.Invoke(form, [persons]);
        TestSupport.Assert(ReferenceEquals(combo, grid.Columns[field.Name]) && ReferenceEquals(choices, combo.DataSource),
            "非名称数值变化重建了引用列或下拉选项");
        persons.Data.Rows[0]["武力"] = numeric;
        var referencedName = referenced.Data.Rows[0]["名称"];
        referenced.Data.Rows[0]["名称"] = "引用缓存测试";
        configure.Invoke(form, [persons]);
        TestSupport.Assert(ReferenceEquals(combo, grid.Columns[field.Name]) && !ReferenceEquals(choices, combo.DataSource) &&
            ((System.Collections.IEnumerable)combo.DataSource).Cast<object>().Any(choice =>
                Convert.ToString(choice.GetType().GetProperty("Display")!.GetValue(choice))!.Contains("引用缓存测试", StringComparison.Ordinal)),
            "引用名称变化未更新既有下拉列");
        referenced.Data.Rows[0]["名称"] = referencedName;
        configure.Invoke(form, [persons]);
        var restoredChoices = combo.DataSource;
        form.SelectModuleForSmokeTest("策略"); form.SelectModuleForSmokeTest("人物");
        TestSupport.Assert(ReferenceEquals(restoredChoices, ((DataGridViewComboBoxColumn)grid.Columns[field.Name]).DataSource),
            "往返物理页重建了未变化的引用选项");
        workspace.ReloadLoadedDocuments();
        typeof(MainForm).GetMethod("RefreshCurrentView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null);
        var reloaded = workspace.GetDocument("persons");
        var reloadedCombo = (DataGridViewComboBoxColumn)grid.Columns[field.Name];
        TestSupport.Assert(!ReferenceEquals(restoredChoices, reloadedCombo.DataSource) && !ReferenceEquals(persons, reloaded),
            "工作区重载后继续使用旧引用选项缓存");
        Console.WriteLine("REFERENCE_CHOICES_CACHE_OK numeric-stable/name-refresh/table-reload");
    }

    private static IEnumerable<BusinessEditorPanel> panelsForCleanup(MainForm form)
        => ((Dictionary<DataManager.Core.Domain.BusinessPageKind, BusinessEditorPanel>)typeof(MainForm)
            .GetField("_businessEditors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!).Values;

    private static void CheckDataViewFilter()
    {
        var table = new DataTable();
        table.Columns.Add("ID", typeof(int)); table.Columns.Add("说明]\\", typeof(string)); table.Columns.Add("值", typeof(int));
        table.Rows.Add(10, "", 0); table.Rows.Add(42, "  ", 0); table.Rows.Add(99, "测试", 0); table.Rows.Add(117, "0", 1);
        var method = typeof(MainForm).GetMethod("NonEmptyRowFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
        table.DefaultView.RowFilter = (string)method.Invoke(null, [table])!;
        TestSupport.Assert(table.DefaultView.Cast<DataRowView>().Select(row => (int)row["ID"]).SequenceEqual([99, 117]), "空行过滤语义或列名转义错误");
        table.DefaultView[0]["值"] = 7;
        TestSupport.Assert((int)table.Rows[2]["值"] == 7 && (int)table.Rows[0]["值"] == 0, "筛选后的编辑没有定位实际数据行");
        table.DefaultView.RowFilter = string.Empty;
        TestSupport.Assert(table.DefaultView.Count == 4, "取消过滤未恢复全部行");
    }

    private static void CheckCancelledWorkspace(string basePath)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var candidate = new ProjectWorkspace();
        try { candidate.Open(basePath, cancellationToken: cancellation.Token); throw new InvalidOperationException("已取消的项目仍然被加载"); }
        catch (OperationCanceledException) { }
        TestSupport.Assert(candidate.RootPath.Length == 0 && candidate.Documents.Count == 0, "取消读取修改了工作区");
    }
}
