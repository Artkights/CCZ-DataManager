using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DataManager.App;
using DataManager.Core.Domain;
using DataManager.Images;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Control = System.Windows.Forms.Control;

namespace DataManager.Tests;

internal static class UiOptimizationReportTests
{
    public static void Run(string source, string output, bool personOnly = false)
    {
        Directory.CreateDirectory(output);
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false,
            MaximumSize = new Size(4096, 2160), Size = new Size(1500, 900) };
        try
        {
            using (var adapter = new ResourceCatalogAdapter())
                foreach (var path in adapter.Scan(source).Resources.Where(resource => resource.Key is "Face" or "Fb.e5" or "Pmapobj.e5" or "SComposite" or "Item.e5" or "Itemicon.dll")
                    .SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(source, path);
                    if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
                    var target = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, true);
                }
            var scenarioRoot = Path.Combine(source, "RS");
            if (Directory.Exists(scenarioRoot))
                foreach (var path in Directory.EnumerateFiles(scenarioRoot, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(root, "RS", Path.GetRelativePath(scenarioRoot, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target, true);
                }
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            if (!personOnly) CheckWorkbookUi(form, root);
            var timings = new List<string> { $"Source={Path.GetFullPath(source)}; 按原始字节复制，未恢复历史快照。", $"DPI={form.DeviceDpi}; Win32DPI={GetDpiForWindow(form.Handle)}; Font={form.Font.Name}/{form.Font.SizeInPoints}; AutoScaleMode={form.AutoScaleMode}; 单位 ms；P95 使用 nearest-rank；读取与写回均使用临时副本。" };
            foreach (var header in timings) Console.WriteLine(header);
            foreach (var module in personOnly ? new[] { "人物" } : new[] { "人物", "兵种系", "兵种", "宝物" })
            {
                var tab = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().FirstOrDefault(page => Equals(page.Tag, module) || page.Text == module);
                TestSupport.Assert(tab != null, "没有找到页面：" + module);
                form.SelectModuleForSmokeTest((string)tab!.Tag!); Pump(200);
                WaitForPictures(form.BusinessEditorForSmokeTest);
                foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
                {
                    form.Size = size;
                    if (form.Size != size) SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                    Pump(100);
                    TestSupport.Assert(form.Size == size, $"截图窗口被桌面限制为{form.Size}，没有达到要求的{size}。");
                    if (module == "人物") File.WriteAllLines(Path.Combine(output, $"人物布局-{size.Width}x{size.Height}.txt"),
                        Descendants(form.BusinessEditorForSmokeTest).Where(control => control is Panel or TableLayoutPanel)
                            .Select(control => $"{control.GetType().Name}/{control.Name}: Bounds={control.Bounds}, Client={control.ClientSize}, Min={control.MinimumSize}, Font={control.Font.SizeInPoints}, DPI={control.DeviceDpi}" +
                                (control is ScrollableControl scroll ? $", ScrollMin={scroll.AutoScrollMinSize}, Display={scroll.DisplayRectangle}, H={scroll.HorizontalScroll.Visible}, V={scroll.VerticalScroll.Visible}" : string.Empty)));
                    var tabs = form.BusinessEditorForSmokeTest.DetailTabs;
                    for (var pageIndex = 0; pageIndex < 1; pageIndex++)
                    {
                        tabs.SelectedIndex = pageIndex; Pump(80);
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        var name = module.Replace('/', '_');
                        bitmap.Save(Path.Combine(output, $"{name}-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                    }
                    if (module == "人物")
                    {
                        CheckUsablePersonFields(form.BusinessEditorForSmokeTest);
                        CheckPersonScrolling(form.BusinessEditorForSmokeTest, size);
                        CheckPersonLayout(form.BusinessEditorForSmokeTest,
                            form.WorkspaceForSmokeTest.ActiveProfile.ProfileId.Equals("ccz-6.6", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (module == "兵种系") CheckJobSeriesLayout(form.BusinessEditorForSmokeTest);
                    else if (module == "宝物") CheckEquipmentTypeLayout(form.BusinessEditorForSmokeTest);
                }
                form.Size = new Size(1280, 720); Pump(100);
                if (module == "人物")
                {
                    CheckUsablePersonFields(form.BusinessEditorForSmokeTest);
                    CheckPersonScrolling(form.BusinessEditorForSmokeTest, form.Size);
                    using (var returned = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(returned, new Rectangle(Point.Empty, form.Size));
                        returned.Save(Path.Combine(output, "人物-1920缩回1280x720.png"), ImageFormat.Png);
                    }
                    var personContent = Descendants(form.BusinessEditorForSmokeTest).Single(control => control.Name == "PersonPageContent");
                    var personViewport = (Panel)personContent.Parent!;
                    personViewport.AutoScrollPosition = new Point(0, personViewport.VerticalScroll.Maximum); Pump(50);
                    using (var bottom = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bottom, new Rectangle(Point.Empty, form.Size));
                        bottom.Save(Path.Combine(output, "人物-1280x720-滚动底部.png"), ImageFormat.Png);
                    }
                    personViewport.AutoScrollPosition = Point.Empty; Pump(50);
                }
                var list = form.EntityListForSmokeTest; var count = Math.Min(12, list.Items.Count);
                for (var i = 0; i < count; i++) { list.SelectedIndex = i; Application.DoEvents(); }
                Pump(150); Collect(); var before = GdiCount();
                var times = new List<double>();
                for (var index = 0; index < 60; index++)
                {
                    var watch = Stopwatch.StartNew(); list.SelectedIndex = (index + 1) % count; Application.DoEvents(); watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds);
                }
                Pump(150); Collect(); var after = GdiCount(); times.Sort();
                var result = $"{module}: n={times.Count}, P50={times[(int)Math.Ceiling(times.Count * .5) - 1]:F2}, P95={times[(int)Math.Ceiling(times.Count * .95) - 1]:F2}, max={times[^1]:F2}, GDI={before}->{after} (delta={after-before})";
                timings.Add(result); Console.WriteLine(result);
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, module + "快切产生草稿；表=" + string.Join(',', form.WorkspaceForSmokeTest.Documents.Values.Where(document => document.IsDirty).Select(document => document.Definition.Key)));
            }
            File.WriteAllLines(Path.Combine(output, "性能实测.txt"), timings);
            Console.WriteLine("UI_OPTIMIZATION_REPORT_OK " + output);
        }
        finally
        {
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges(); form.Close(); Directory.Delete(root, true);
        }
    }

    private static void CheckWorkbookUi(MainForm form, string root)
    {
        var path = Path.Combine(root, "人物-UI.xlsx"); Await(form.ExportWorkbookForSmokeTest(BusinessPageKind.Persons, path));
        TestSupport.Assert(form.Enabled && !form.UseWaitCursor && File.Exists(path), "工作簿导出后未恢复界面状态");
        TestSupport.Assert(Await(form.ImportWorkbookForSmokeTest(BusinessPageKind.Persons, path, false)) == 0, "工作簿UI原样预检不为零");
        using (var package = SpreadsheetDocument.Open(path, true))
        {
            var part = package.WorkbookPart!; var sheet = part.Workbook.Sheets!.Elements<Sheet>().First(s => s.State == null || s.State.Value == SheetStateValues.Visible);
            var data = ((WorksheetPart)part.GetPartById(sheet.Id!)).Worksheet.GetFirstChild<SheetData>()!;
            var field = data.Elements<Row>().First().Elements<Cell>().Single(c => c.InnerText == "武力");
            var column = new string(field.CellReference!.Value!.TakeWhile(char.IsLetter).ToArray());
            var target = data.Elements<Row>().Single(r => r.RowIndex?.Value == 3).Elements<Cell>().Single(c => c.CellReference?.Value == column + "3");
            target.DataType = CellValues.Number; target.RemoveAllChildren(); target.CellValue = new CellValue("79");
        }
        var before = form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["武力"];
        var changed = Convert.ToInt32(before) == 79 ? 0 : 1;
        TestSupport.Assert(Await(form.ImportWorkbookForSmokeTest(BusinessPageKind.Persons, path, false)) == changed && !form.WorkspaceForSmokeTest.IsDirty, "工作簿UI预检修改草稿");
        TestSupport.Assert(Await(form.ImportWorkbookForSmokeTest(BusinessPageKind.Persons, path, true)) == changed && form.Enabled && !form.UseWaitCursor, "工作簿UI应用或恢复状态失败");
        if (changed > 0) { form.UndoDraftForSmokeTest(); TestSupport.Assert(Equals(form.WorkspaceForSmokeTest.GetDocument("persons").Data.Rows[0]["武力"], before), "工作簿UI无法一条撤回"); }
        using var review = new WorkbookImportPreviewDialog(new DataManager.Core.Workbooks.PageWorkbookImportPreview());
        TestSupport.Assert(review.Controls.OfType<DataGridView>().Single().Columns.Count == 6, "工作簿变更预览列不完整");
        Console.WriteLine("WORKBOOK_UI_SMOKE_OK export/preflight/apply/undo/busy-state");
    }

    internal static void CheckUsablePersonFields(BusinessEditorPanel editor)
    {
        var basic = Descendants(editor).Single(control => control.Name == "PersonBasicFields");
        Control Field(string name)
        {
            var label = Descendants(basic).OfType<Label>().Single(control => control.Text == name);
            var table = (TableLayoutPanel)label.Parent!; var position = table.GetPositionFromControl(label);
            return table.GetControlFromPosition(position.Column + 1, position.Row)!;
        }
        var name = (ByteTextEditor)Field("名称");
        var nameMinimum = TextRenderer.MeasureText("夏侯惇", name.Editor.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 12;
        TestSupport.Assert(name.Editor.ClientSize.Width >= nameMinimum, $"人物姓名内部输入区不足：{name.Editor.ClientSize.Width}<{nameMinimum}");
        foreach (var key in new[] { "初始HP", "初始MP" })
        {
            var numeric = Descendants(Field(key)).OfType<NumericUpDown>().Single();
            var text = Descendants(numeric).OfType<TextBox>().Single();
            var required = TextRenderer.MeasureText("65535", numeric.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 8;
            TestSupport.Assert(text.ClientSize.Width >= required, $"{key}内部数字输入区不足：{text.ClientSize.Width}<{required}");
        }
        foreach (var key in new[] { "默认武器", "默认防具", "默认辅助" })
        {
            var combo = (ComboBox)Field(key);
            var required = TextRenderer.MeasureText("255：空物品槽", combo.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + SystemInformation.VerticalScrollBarWidth + 8;
            TestSupport.Assert(combo.ClientSize.Width >= required, $"{key}下拉输入区不足：{combo.ClientSize.Width}<{required}");
        }
    }

    private static void CheckPersonLayout(BusinessEditorPanel editor, bool is66)
    {
        var basic = Descendants(editor).Single(control => control.Name == "PersonBasicFields");
        Label LabelFor(string text) => Descendants(basic).OfType<Label>().Single(label => label.Text == text);
        var name = ScreenBounds(LabelFor("名称"));
        var job = ScreenBounds(LabelFor("兵种"));
        TestSupport.Assert(Math.Abs(name.Top + name.Height / 2 - job.Top - job.Height / 2) <= 2,
            $"人物兵种没有与名称放在第一行：名称={name}，兵种={job}");
        var hp = ScreenBounds(LabelFor("初始HP"));
        var mp = ScreenBounds(LabelFor("初始MP"));
        TestSupport.Assert(hp.Top >= job.Bottom && mp.Top >= hp.Bottom,
            $"人物初始HP/MP没有依次排在兵种下方：兵种={job}，HP={hp}，MP={mp}");

        var previews = Descendants(editor).OfType<TableLayoutPanel>().Single(table => table.Name == "PersonPreviewColumns");
        var expected = is66 ? 4 : 3;
        TestSupport.Assert(previews.ColumnCount == expected, $"人物预览应为一行{expected}列，实际{previews.ColumnCount}列。");
        var widths = previews.GetColumnWidths();
        // Percentage columns may assign their final rounding pixels to the last column.
        TestSupport.Assert(widths.Length == expected && widths.Max() - widths.Min() < expected,
            "人物预览列没有均分可用宽度：" + string.Join(',', widths));
        var cards = Descendants(previews).OfType<ResourcePreviewCard>().OrderBy(card => ScreenBounds(card).Left).ToArray();
        TestSupport.Assert(cards.Length == expected && cards.Select(card => ScreenBounds(card).Top).Distinct().Count() == 1,
            $"人物预览没有完整排为一行{expected}张图片。");
        TestSupport.Assert(cards.Max(card => card.Width) - cards.Min(card => card.Width) < expected,
            "人物预览图片区域宽度不相等。");
        foreach (var (caption, column, span) in new[] { ("头像", 0, is66 ? 2 : 1), ("R形象编号", expected - 2, 1), ("S形象编号", expected - 1, 1) })
        {
            var label = Descendants(previews).OfType<Label>().Single(control => control.Text == caption);
            Control header = label;
            while (header.Parent != previews) header = header.Parent!;
            var position = previews.GetPositionFromControl(header);
            TestSupport.Assert(position.Row == 0 && position.Column == column && previews.GetColumnSpan(header) == span,
                $"{caption}编号没有放在对应预览上方，列={position.Column}，跨列={previews.GetColumnSpan(header)}。");
            var input = Descendants(header).Single(control => control is ComboBox or NumericUpDown);
            var inputBounds = ScreenBounds(input);
            var labelBounds = ScreenBounds(label);
            var first = ScreenBounds(cards[column]);
            var last = ScreenBounds(cards[column + span - 1]);
            TestSupport.Assert(labelBounds.Bottom <= inputBounds.Top + 1 && inputBounds.Bottom <= first.Top,
                $"{caption}的文字、编号和预览没有从上到下完整排列。");
            TestSupport.Assert(Math.Abs(inputBounds.Left + inputBounds.Width / 2 - (first.Left + last.Right) / 2) <= 2,
                $"{caption}编号没有在对应{span}列预览区域上方居中：输入={inputBounds}，图片={first}/{last}。");
            AssertInsideAncestors(input, previews, "人物预览编号");
        }
    }

    internal static void CheckJobSeriesLayout(BusinessEditorPanel editor)
    {
        var page = editor.DetailTabs.SelectedTab!;
        var controls = Descendants(page).Where(control => control.Visible).ToArray();
        var columns = controls.OfType<TableLayoutPanel>().Single(table => table.Name == "JobSeriesColumns");
        TestSupport.Assert(columns.ColumnCount == 5, $"兵种系应为五列，实际{columns.ColumnCount}列。");
        TestSupport.Assert(controls.OfType<ScrollableControl>().Prepend(page)
            .All(scroll => !scroll.HorizontalScroll.Visible && !scroll.VerticalScroll.Visible),
            "兵种系可见页仍有滚动条，内容没有完整放入页面。");
        var numerics = controls.OfType<NumericUpDown>().ToArray();
        TestSupport.Assert(numerics.Length > 0, "兵种系没有显示数值编辑框。");
        foreach (var numeric in numerics)
        {
            AssertInsideAncestors(numeric, page, "兵种系数值框");
            var digits = TextRenderer.MeasureText("255", numeric.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            var text = Descendants(numeric).OfType<TextBox>().Single();
            var spinner = numeric.Controls.Cast<Control>().Where(child => child is not TextBox)
                .Select(child => child.Width).DefaultIfEmpty(SystemInformation.VerticalScrollBarWidth).Max();
            TestSupport.Assert(text.ClientSize.Width >= digits + 2 && numeric.ClientSize.Width >= digits + spinner + 4,
                $"兵种系数值框不能完整显示三位数字和增减按钮：框={numeric.ClientSize}，文字区={text.ClientSize}，所需数字={digits}，按钮={spinner}。");
        }
        for (var index = 0; index < numerics.Length; index++)
            foreach (var other in numerics.Skip(index + 1))
            {
                var overlap = Rectangle.Intersect(ScreenBounds(numerics[index]), ScreenBounds(other));
                TestSupport.Assert(overlap.Width <= 1 || overlap.Height <= 1,
                    $"兵种系数值框相互重叠：{ScreenBounds(numerics[index])}/{ScreenBounds(other)}。");
            }
        foreach (var label in controls.OfType<Label>().Where(label => label.Text.Length > 0))
        {
            var wraps = label.Parent is ByteTextEditor;
            var required = TextRenderer.MeasureText(label.Text, label.Font, wraps ? new Size(label.ClientSize.Width, int.MaxValue) : Size.Empty,
                wraps ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TestSupport.Assert(label.ClientSize.Width >= required.Width && label.ClientSize.Height >= required.Height,
                $"兵种系文字被裁剪：{label.Text}，实际={label.ClientSize}，所需={required}，AutoEllipsis={label.AutoEllipsis}。");
            AssertInsideAncestors(label, page, "兵种系文字：" + label.Text);
            foreach (var numeric in numerics)
            {
                var overlap = Rectangle.Intersect(ScreenBounds(label), ScreenBounds(numeric));
                TestSupport.Assert(overlap.Width <= 1 || overlap.Height <= 1,
                    $"兵种系文字和数值框重叠：{label.Text}，文字={ScreenBounds(label)}，数值={ScreenBounds(numeric)}。");
            }
        }
    }

    private static void CheckEquipmentTypeLayout(BusinessEditorPanel editor)
    {
        var grid = Descendants(editor).OfType<DataGridView>().Single(control => control.Name == "EquipmentTypeEditor");
        var heights = grid.Rows.Cast<DataGridViewRow>().Select(row => row.Height).ToArray();
        TestSupport.Assert(heights.Length == 15, $"装备类型表应显示15行，实际{heights.Length}行。");
        TestSupport.Assert(heights.Max() - heights.Min() <= 1, "装备类型行高没有均匀分布：" + string.Join(',', heights));
        var horizontal = grid.Controls.OfType<HScrollBar>().Where(scroll => scroll.Visible).Select(scroll => scroll.Height).FirstOrDefault();
        var available = grid.ClientSize.Height - (grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0) - horizontal;
        var minimum = Math.Max(grid.Font.Height + 5, 20 * grid.DeviceDpi / 96);
        TestSupport.Assert(available < minimum * heights.Length ? heights.Sum() >= available : Math.Abs(heights.Sum() - available) <= 2,
            $"装备类型内容没有铺满原页框：行高合计={heights.Sum()}，可用高度={available}，行高={string.Join(',', heights)}。");
    }

    private static Rectangle ScreenBounds(Control control) => control.Parent!.RectangleToScreen(control.Bounds);

    private static void AssertInsideAncestors(Control control, Control stop, string caption)
    {
        var bounds = ScreenBounds(control);
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
        {
            var client = parent.RectangleToScreen(parent.ClientRectangle);
            client.Inflate(1, 1);
            TestSupport.Assert(client.Contains(bounds),
                $"{caption}超出祖先区域：{control.GetType().Name}/{control.Name}={bounds}，祖先{parent.GetType().Name}/{parent.Name}={client}。");
            if (parent == stop) return;
        }
        TestSupport.Assert(false, $"{caption}没有位于指定页面中。");
    }

    private static void CheckPersonScrolling(BusinessEditorPanel editor, Size window)
    {
        var scrolls = Descendants(editor).OfType<Panel>().Where(panel => panel.AutoScroll).ToArray();
        TestSupport.Assert(scrolls.All(panel => !panel.HorizontalScroll.Visible), "人物宽窗缩窄后残留水平滚动条：" +
            string.Join(';', scrolls.Where(panel => panel.HorizontalScroll.Visible).Select(panel => $"{panel.Parent?.Name}/{panel.Name}: {panel.ClientSize}, min={panel.AutoScrollMinSize}")));
        var content = Descendants(editor).Single(control => control.Name == "PersonPageContent");
        var viewport = (Panel)content.Parent!;
        if (window.Width == 1280)
        {
            TestSupport.Assert(viewport.VerticalScroll.Visible && scrolls.Count(panel => panel.VerticalScroll.Visible) == 1,
                "1280人物页应仅使用整页纵向滚动。");
            var basic = Descendants(editor).Single(control => control.Name == "PersonBasicFields");
            foreach (var label in Descendants(basic).OfType<Label>())
                TestSupport.Assert(label.Top >= 0 && label.Bottom <= label.Parent!.ClientSize.Height, "人物六行基础字段没有完整排布。");
            viewport.AutoScrollPosition = new Point(0, viewport.VerticalScroll.Maximum);
            Pump(30);
            TestSupport.Assert(viewport.AutoScrollPosition.Y < 0, "人物整页纵向滚动不能到达下方台词和列传。");
            viewport.AutoScrollPosition = Point.Empty; Pump(30);
        }
    }

    private static T Await<T>(Task<T> task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); } return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); }
    private static void WaitForPictures(Control panel)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && Descendants(panel).OfType<ResourcePreviewCard>().Any(card => !card.HasLoadedResource)) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static IEnumerable<Control> Descendants(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Pump(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); } }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static int GdiCount() => GetGuiResources(Process.GetCurrentProcess().Handle, 0);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
