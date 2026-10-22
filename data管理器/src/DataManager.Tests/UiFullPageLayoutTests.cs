using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DataManager.App;
using DataManager.Images;

namespace DataManager.Tests;

/// <summary>Exercise the actual native forms against unmodified 6.5/6.6 fixture bytes.</summary>
internal static class UiFullPageLayoutTests
{
    internal static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, MaximumSize = new Size(4096, 2160)
        };
        var results = new List<string> { $"Source={Path.GetFullPath(source)}；只编辑独立临时副本，保留原始样本字节。" };
        try
        {
            CopyPreviewResources(source, root);
            Console.WriteLine("FULL_PAGE_OPEN " + source);
            form.Show(); form.LoadProjectForSmokeTest(root); Pump(100);
            results.Add($"Profile={form.WorkspaceForSmokeTest.ActiveProfile.ProfileId}/{form.WorkspaceForSmokeTest.ActiveProfile.VariantId}; DPI={form.DeviceDpi}");
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                form.Size = size;
                if (form.Size != size)
                    SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                Pump(100);
                TestSupport.Assert(form.Size == size, $"窗口没有达到截图尺寸：{form.Size}/{size}。");
                NumericUpDown? referenceNumeric = null;
                foreach (var module in new[] { "人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据" })
                {
                    Console.WriteLine($"FULL_PAGE_SWITCH {size.Width}x{size.Height}/{module}; dirty={form.WorkspaceForSmokeTest.IsDirty}; pending={form.BusinessEditorForSmokeTest.HasPendingChanges}");
                    form.SelectModuleForSmokeTest(module); Pump(80);
                    Console.WriteLine($"FULL_PAGE_SWITCHED {size.Width}x{size.Height}/{module}");
                    var editor = form.BusinessEditorForSmokeTest;
                    foreach (TabPage page in editor.DetailTabs.TabPages)
                    {
                        Console.WriteLine($"FULL_PAGE_SELECT {module}/{page.Text}");
                        editor.DetailTabs.SelectedTab = page; Pump(50);
                        // Record the current page before assertions so layout
                        // failures retain the precise geometry for visual review.
                        if (module == "人物" && size.Width == 1280) Pump(350);
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        var name = $"{module.Replace('/', '_')}-{page.Text.Replace('/', '_')}-{size.Width}x{size.Height}.png";
                        bitmap.Save(Path.Combine(output, name), ImageFormat.Png);
                        Console.WriteLine("FULL_PAGE_SAVED " + name);
                        TestSupport.Assert(!Descendants(page).OfType<GroupBox>().Any(group => group.Visible && !string.IsNullOrEmpty(group.Text)),
                            $"{module}/{page.Text}仍显示占用页面的分组标题。");
                        if (module == "兵种系" && page.Text == "兵种属性")
                        {
                            CheckJobSeries(page, results, size);
                            referenceNumeric = Descendants(page).OfType<NumericUpDown>().First();
                        }
                        if (module == "策略")
                        {
                            TestSupport.Assert(!Descendants(page).OfType<Label>().Any(label => label.Visible &&
                                label.Text is "施展范围" or "策略设定" or "效果与拓展" or "策略说明"),
                                "策略详情仍显示截图中要求删除的分组标题。");
                            if (page.Text == "兵种学习等级") CheckLearning(page, referenceNumeric!, results, size);
                        }
                        results.Add($"{module}/{page.Text}/{size.Width}×{size.Height}：分组标题清理通过，已记录完整窗口截图。");
                    }
                }
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "纯布局和页面切换产生了意外草稿。");
            }
            File.WriteAllLines(Path.Combine(output, "全页布局检查.txt"), results);
            Console.WriteLine("UI_FULL_PAGE_LAYOUT_TESTS_OK " + output);
        }
        catch (Exception error)
        {
            // Log before cleanup, so an unrelated unsaved-input close prompt
            // cannot hide the assertion which first caused the test to fail.
            Console.Error.WriteLine("FULL_PAGE_FAILURE " + error);
            throw;
        }
        finally
        {
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            // This test owns an isolated form and never offers to save it.
            // Dispose releases all cached business editors without raising the
            // product's interactive FormClosing confirmation on assertion failure.
            form.Dispose(); Directory.Delete(root, true);
        }
    }

    private static void CheckJobSeries(TabPage page, List<string> results, Size size)
    {
        var body = Descendants(page).OfType<TableLayoutPanel>().Single(table => table.Name == "JobSeriesColumns");
        TestSupport.Assert(body.ColumnCount == 5, "兵种属性没有排成五个主列。");
        var tables = DataMatrices(page).OrderBy(table => ScreenBounds(table).Left).ToArray();
        TestSupport.Assert(tables.Length == 5, $"兵种属性应包含五个独立矩阵，实际{tables.Length}个。");
        var expected = new[] { 20, 20, 15, 15, 9 };
        for (var index = 0; index < tables.Length; index++)
        {
            var table = tables[index];
            TestSupport.Assert(table.RowCount - 1 == expected[index], $"兵种属性第{index + 1}列应为{expected[index]}行，实际{table.RowCount - 1}行。");
            string[] expectedHeaders = index < 2 ? ["兵种系", "克制"] : index < 4 ? ["地形", "发挥", "消耗"] : ["属性", "设定"];
            TestSupport.Assert(Headers(table).SequenceEqual(expectedHeaders),
                $"兵种属性第{index + 1}列字段顺序错误：{string.Join('/', Headers(table))}。");
            CheckFullHeightRows(table, page, "兵种属性");
        }
        var bounds = tables.Select(ScreenBounds).ToArray();
        TestSupport.Assert(bounds.Max(rectangle => rectangle.Top) - bounds.Min(rectangle => rectangle.Top) <= 2 &&
            bounds.Max(rectangle => rectangle.Bottom) - bounds.Min(rectangle => rectangle.Bottom) <= 2,
            "兵种属性五列没有对齐整页上下边界。");
        var terrain = Descendants(page).Single(control => control.Name == "JobSeriesTerrain");
        TestSupport.Assert(Descendants(terrain).OfType<NumericUpDown>().Count() == 60, "三十项地形的发挥和消耗没有完整保留。");
        TestSupport.Assert(Descendants(page).OfType<ScrollableControl>().All(control => !control.HorizontalScroll.Visible && !control.VerticalScroll.Visible),
            "兵种属性没有完整放入页面，仍出现滚动条。");
        UiOptimizationReportTests.CheckJobSeriesLayout((BusinessEditorPanel)page.Parent!.Parent!);
        results.Add($"兵种属性/{size.Width}×{size.Height}：20/20/15/15/9行，地形两字段成对、五列等高、行距均分、无滚动条。");
    }

    private static void CheckLearning(TabPage page, NumericUpDown reference, List<string> results, Size size)
    {
        var numerics = Descendants(page).OfType<NumericUpDown>().ToArray();
        TestSupport.Assert(numerics.Length == 80 && !Descendants(page).OfType<DataGridView>().Any(),
            "策略学习等级没有使用八十个原生数值编辑框。");
        var tables = DataMatrices(page).ToArray();
        TestSupport.Assert(tables.Length == 4 && tables.All(table => table.RowCount == 21), "策略学习等级没有排为四列各二十行。");
        TestSupport.Assert(numerics.All(numeric => numeric.BorderStyle == reference.BorderStyle &&
            numeric.TextAlign == reference.TextAlign && numeric.DecimalPlaces == reference.DecimalPlaces &&
            numeric.ThousandsSeparator == reference.ThousandsSeparator && numeric.Minimum == reference.Minimum &&
            numeric.Maximum == reference.Maximum), "策略学习等级与兵种属性数值框的类型或格式不一致。");
        foreach (var table in tables) CheckFullHeightRows(table, page, "策略学习等级");
        results.Add($"兵种学习等级/{size.Width}×{size.Height}：80个NumericUpDown，四列各20行，边框/对齐/数字格式/范围与兵种属性一致。");
    }

    private static void CheckFullHeightRows(TableLayoutPanel table, TabPage page, string caption)
    {
        var heights = table.GetRowHeights().Skip(1).ToArray();
        var textEditors = table.Controls.OfType<ByteTextEditor>().ToArray();
        TestSupport.Assert(heights.Length > 0 && (textEditors.Length > 0 || heights.Max() - heights.Min() <= 1),
            $"{caption}行距没有等分：{string.Join(',', heights)}。");
        foreach (var editor in textEditors)
            TestSupport.Assert(heights[table.GetRow(editor) - 1] >= editor.MinimumSize.Height,
                $"{caption}名称和字节说明没有完整放入本行：{editor.MinimumSize}/{string.Join(',', heights)}。");
        TestSupport.Assert(Math.Abs(table.Bottom - table.Parent!.ClientSize.Height) <= 2 && table.Top <= 2,
            $"{caption}矩阵没有填满所属列：{table.Bounds}/{table.Parent.ClientSize}。");
        foreach (var control in table.Controls.Cast<Control>().Where(control => control is NumericUpDown or ComboBox))
        {
            var bounds = ScreenBounds(control);
            for (var ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                var client = ancestor.RectangleToScreen(ancestor.ClientRectangle); client.Inflate(1, 1);
                TestSupport.Assert(client.Contains(bounds), $"{caption}输入框超出页面边界：{bounds}/{client}。");
                if (ReferenceEquals(ancestor, page)) break;
            }
        }
    }

    private static IEnumerable<TableLayoutPanel> DataMatrices(Control root) => Descendants(root).OfType<TableLayoutPanel>()
        .Where(table => table.RowCount > 1 && table.ColumnCount is 2 or 3 &&
            table.GetControlFromPosition(0, 0) is Label && table.GetControlFromPosition(0, 1) is Label &&
            table.GetControlFromPosition(1, 1) is NumericUpDown or ByteTextEditor or ComboBox);

    private static IEnumerable<string> Headers(TableLayoutPanel table) => Enumerable.Range(0, table.ColumnCount)
        .Select(column => table.GetControlFromPosition(column, 0)!.Text);

    private static void CopyPreviewResources(string source, string target)
    {
        using var adapter = new ResourceCatalogAdapter();
        foreach (var path in adapter.Scan(source).Resources.Where(resource => resource.Key is "Face" or "Tou" or "Fb.e5" or "Pmapobj.e5" or "SComposite" or "Item.e5" or "Itemicon.dll" or
                "Hitarea.e5" or "Effarea.e5" or "Mgcicon.dll" or "Mtem.e5" or "Meff.e5" || resource.Key.StartsWith("Mcall:", StringComparison.Ordinal))
            .SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(source, path);
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
            var destination = Path.Combine(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, true);
        }
    }

    private static Rectangle ScreenBounds(Control control) => control.Parent!.RectangleToScreen(control.Bounds);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
