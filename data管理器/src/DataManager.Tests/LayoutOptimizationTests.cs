using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ccz.ManagerUi;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.History;
using RangeManager.App;

namespace DataManager.Tests;

/// <summary>Checks layout geometry and view lifetime independently of the performance audit.</summary>
internal static class LayoutOptimizationTests
{
    private sealed record Check(string Scenario, bool Passed, string Detail);
    private sealed record Geometry(string Type, string Bounds, int Dpi, string Font, string Extra);
    private sealed record Settled(string Scenario, double Milliseconds, int Schedulers, long Passes);

    public static void Run(string source, string output, int cycles = 30)
    {
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        using var temporary = OwnedTemporaryWorkspace.Create(output, "DataManager.LayoutOptimizationTests", source);
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(temporary.RootPath, "history"));
        var project = Path.Combine(temporary.RootPath, "project");
        Directory.CreateDirectory(project);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<Check>(); var settled = new List<Settled>(); var screenshots = new List<string>();
        var fingerprints = new Dictionary<string, Dictionary<string, Geometry>>();
        var businessInstances = new Dictionary<string, BusinessEditorPanel>();
        var parentChanges = 0; var navigationCount = 0; var reachableInputs = 0; var dpi = 0;
        MainForm? form = null;
        Exception? failure = null;
        var ownedFonts = new List<Font>();
        try
        {
            foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
            {
                var destination = Path.Combine(project, Path.GetRelativePath(source, directory));
                Directory.CreateDirectory(destination);
                foreach (var file in Directory.EnumerateFiles(directory).Where(file =>
                    Path.GetExtension(file).Equals(".e5", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    hashes[file] = HashService.Sha256(file);
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                }
            }
            form = new MainForm(false) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, MaximumSize = new Size(4096, 2160) };
            form.Show(); form.LoadProjectForSmokeTest(project); Settle("initial"); dpi = form.DeviceDpi;
            var originalFont = form.Font;
            string[] pages = ["人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据", "range/0", "range/1", "range/2"];
            foreach (var factor in new[] { 1f, 1.25f, 1.5f })
            {
                var font = new Font(originalFont.FontFamily, originalFont.SizeInPoints * factor, originalFont.Style);
                ownedFonts.Add(font); form.Font = font;
                foreach (var size in new[] { new Size(1100, 650), new Size(1280, 800), new Size(1920, 1080) })
                {
                    SetSize(size);
                    foreach (var page in pages)
                    {
                        var scenario = $"{size.Width}x{size.Height}/font{factor:P0}/{page}";
                        CheckNow(scenario, () =>
                        {
                            Select(page); Settle(scenario);
                            CheckToolbar();
                            if (page.StartsWith("range/", StringComparison.Ordinal)) CheckRange(int.Parse(page[6..]), scenario);
                            else CheckRangePickers(form.BusinessEditorForSmokeTest);
                            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "Layout-only navigation created drafts.");
                            if (size.Width == 1100 && factor == 1.5f && page is "兵种" or "策略" or "range/0" or "range/1" or "range/2")
                                Screenshot(page.Replace('/', '-') + "-1100-font150");
                        });
                    }
                }
            }
            form.Font = originalFont; SetSize(new Size(1280, 800));
            CheckNow("maximize-restore", () =>
            {
                Select("策略"); Settle("before-maximize");
                var before = Snapshot(form.BusinessEditorForSmokeTest);
                form.WindowState = FormWindowState.Maximized; Settle("maximized");
                TestSupport.Assert(form.WindowState == FormWindowState.Maximized, "Native maximize did not occur.");
                Screenshot("strategy-maximized");
                form.WindowState = FormWindowState.Normal; SetSize(new Size(1280, 800)); Settle("restored");
                AssertSame(before, Snapshot(form.BusinessEditorForSmokeTest), "Maximize/restore changed strategy geometry.");
                Screenshot("strategy-restored");
            });
            CheckNow("pending-business-text-layout", () =>
            {
                Select("人物"); Settle("person-input-before");
                var panel = form.BusinessEditorForSmokeTest;
                var text = Descendants(panel).OfType<ByteTextEditor>().First(editor => editor.Visible && !editor.Editor.Multiline).Editor;
                var original = text.Text;
                try
                {
                    text.Text = "布局输入"; text.Focus(); text.Select(1, 2);
                    foreach (var size in new[] { new Size(1100, 650), new Size(1920, 1080), new Size(1280, 800) })
                    {
                        SetSize(size); panel.RequestViewLayout(); Settle("person-pending-" + size);
                        TestSupport.Assert(text.Text == "布局输入" && text.SelectionStart == 1 && text.SelectionLength == 2,
                            "Layout replaced pending business text or its selection.");
                    }
                }
                finally { text.Text = original; panel.DiscardPendingChanges(); }
            });
            CheckNow("pending-range-generators-layout", () =>
            {
                Select("range/1"); Settle("pierce-input-before");
                var range = form.RangeHostForSmokeTest.PierceEditor;
                SelectEditableRange(range, RangeKind.Pierce); Settle("pierce-editable-input");
                var original = range.GeneratorsForTest.Text;
                try
                {
                    range.GeneratorsForTest.Text = "未完成输入("; range.GeneratorsForTest.Focus(); range.GeneratorsForTest.Select(1, 3);
                    TestSupport.Assert(range.GeneratorsForTest.Enabled && form.RangeSessionForSmokeTest!.IsDirty &&
                        form.RangeSessionForSmokeTest.GetTextError(range.SelectedKey!.Value, 0) != null,
                        "The unfinished range input was not stored as an editable invalid draft.");
                    foreach (var size in new[] { new Size(1100, 650), new Size(1920, 1080), new Size(1280, 800) })
                    {
                        SetSize(size); Settle("range-pending-" + size);
                        TestSupport.Assert(range.GeneratorsForTest.Text == "未完成输入(" && range.GeneratorsForTest.SelectionStart == 1 && range.GeneratorsForTest.SelectionLength == 3,
                            "Layout replaced invalid range text or its selection.");
                    }
                    Select("人物"); Select("range/1"); Settle("range-pending-return");
                    TestSupport.Assert(range.GeneratorsForTest.Text == "未完成输入(", "Navigation discarded unfinished range input.");
                }
                finally
                {
                    range.GeneratorsForTest.Text = original;
                    while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
                    range.RefreshBindings();
                }
            });
            CheckNow("pending-extension-grid-layout", () =>
            {
                Select("range/2"); Settle("extension-input-before");
                var grid = form.RangeHostForSmokeTest.ExtensionEditor.GridForTest;
                try
                {
                    grid.CurrentCell = grid.Rows[0].Cells[2];
                    TestSupport.Assert(grid.BeginEdit(true) && grid.EditingControl is ComboBox, "Cannot begin native extension input.");
                    var combo = (ComboBox)grid.EditingControl!;
                    combo.DropDownStyle = ComboBoxStyle.DropDown;
                    combo.Text = "0x"; combo.Select(0, 2);
                    TestSupport.Assert(combo.Text == "0x", "The pending grid input was not established.");
                    foreach (var size in new[] { new Size(1100, 650), new Size(1920, 1080), new Size(1280, 800) })
                    {
                        SetSize(size); Settle("extension-pending-" + size);
                        TestSupport.Assert(grid.IsCurrentCellInEditMode && ReferenceEquals(combo, grid.EditingControl) && combo.Text == "0x",
                            $"Layout committed or replaced the extension grid's pending input: editing={grid.IsCurrentCellInEditMode}, same={ReferenceEquals(combo, grid.EditingControl)}, text={combo.Text}.");
                    }
                }
                finally { grid.CancelEdit(); }
            });
            SetSize(new Size(1280, 800));
            string[] returning = ["人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据", "range/0", "range/1", "range/2", "physical/persons", "__resources", "__formulas"];
            foreach (var page in returning) { Select(page); Settle("warm/" + page); }
            var parents = new Dictionary<BusinessEditorPanel, Control?>();
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                foreach (var page in returning)
                {
                    CheckNow($"cycle{cycle}/{page}/A-B-A", () =>
                    {
                        Select(page); Settle("A/" + page); var surface = Surface(page);
                        var before = Snapshot(surface);
                        if (surface is BusinessEditorPanel panel)
                        {
                            if (businessInstances.TryGetValue(page, out var initialPanel))
                                TestSupport.Assert(ReferenceEquals(panel, initialPanel), "The cached business editor instance was replaced: " + page);
                            else businessInstances[page] = panel;
                            if (parents.TryGetValue(panel, out var parent) && !ReferenceEquals(parent, panel.Parent)) parentChanges++;
                            parents[panel] = panel.Parent;
                        }
                        Select(page == "人物" ? "range/1" : "人物"); Settle("B/" + page);
                        Select(page); Settle("return/" + page);
                        AssertSame(before, Snapshot(Surface(page)), "A→B→A geometry drift: " + page);
                        var key = page;
                        if (fingerprints.TryGetValue(key, out var initial)) AssertSame(initial, before, "Geometry drift across cycles: " + page);
                        else fingerprints[key] = before;
                        TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "Read-only cycle created drafts.");
                    });
                }
                if (cycle % 5 == 0) Console.WriteLine($"LAYOUT_CYCLE {cycle}/{cycles} checks={checks.Count} failures={checks.Count(check => !check.Passed)}");
            }
            TestSupport.Assert(parentChanges == 0, "Cached business editor changed parent.");
            TestSupport.Assert(hashes.All(pair => HashService.Sha256(pair.Key) == pair.Value), "Original source files changed.");
            if (checks.Any(check => !check.Passed)) throw new InvalidOperationException("One or more layout scenarios failed.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            form?.Dispose();
            foreach (var font in ownedFonts) font.Dispose();
            var unchanged = hashes.All(pair => File.Exists(pair.Key) && HashService.Sha256(pair.Key) == pair.Value);
            var passed = failure == null && unchanged && checks.All(check => check.Passed);
            if (passed) temporary.Complete();
            temporary.Dispose();
            File.WriteAllText(Path.Combine(output, "layout-optimization.json"), JsonSerializer.Serialize(new
            {
                Passed = passed && temporary.CleanupSucceeded, ActualDpi = dpi, CrossMonitorDpiTested = false,
                DpiBoundary = "Actual host DPI is recorded; extra 125%/150% font changes do not represent monitor DPI transitions.",
                Source = source, SourceUnchanged = unchanged, SourceHashes = hashes, Cycles = cycles, NavigationCount = navigationCount,
                ParentChanges = parentChanges, ReachableRangeInputs = reachableInputs, Checks = checks, SettleSamples = settled,
                Screenshots = screenshots, Workspace = temporary.RootPath, temporary.CleanupSucceeded, temporary.RetentionReason,
                Error = failure?.ToString(),
                ExistingAuditBoundary = "The existing audit uses one 1280x800 window and first-cycle snapshots; this suite asserts settle deadlines, scheduler quiescence, narrow sizes, native maximize/restore, changed fonts, pending input, and explicit A→B→A returns."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) throw new InvalidOperationException("Layout optimization failed; see report.", failure);
        Console.WriteLine($"LAYOUT_OPTIMIZATION_TESTS_OK checks={checks.Count} cycles={cycles} DPI={dpi}");

        void CheckNow(string scenario, Action action)
        {
            Console.WriteLine("LAYOUT_CHECK_BEGIN " + scenario);
            try { action(); checks.Add(new(scenario, true, "passed")); }
            catch (Exception error) { checks.Add(new(scenario, false, error.Message)); Console.WriteLine("LAYOUT_FAILURE " + scenario + " " + error.Message); }
            Console.WriteLine("LAYOUT_CHECK_END " + scenario);
        }
        void Select(string page)
        {
            navigationCount++;
            if (page.StartsWith("range/", StringComparison.Ordinal))
            { form!.SelectModuleForSmokeTest("__ranges"); form.RangeHostForSmokeTest.Tabs.SelectedIndex = int.Parse(page[6..]); }
            else if (page.StartsWith("physical/", StringComparison.Ordinal))
            { form!.SelectModuleForSmokeTest("人物"); form.ShowPhysicalTableForSmokeTest(page[9..]); }
            else
            {
                form!.SelectModuleForSmokeTest(page);
                // The shell intentionally remembers the last physical table
                // of each module. Select the business view explicitly so a
                // previous physical/persons visit does not change this case.
                if (!page.StartsWith("__", StringComparison.Ordinal)) form.ShowPhysicalTableForSmokeTest("__business");
            }
        }
        Control Surface(string page) => page.StartsWith("range/", StringComparison.Ordinal) ? form!.RangeHostForSmokeTest :
            page.StartsWith("__", StringComparison.Ordinal) || page.StartsWith("physical/", StringComparison.Ordinal) ? form! : form!.BusinessEditorForSmokeTest;
        void SetSize(Size size)
        {
            form!.WindowState = FormWindowState.Normal;
            SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
            Settle("size/" + size);
            TestSupport.Assert(form.Size == size, "Physical window size differs: " + form.Size + " / " + size);
        }
        void Settle(string scenario)
        {
            var clock = Stopwatch.StartNew(); Dictionary<string, Geometry>? previous = null; var stable = 0; var iterations = 0;
            var latestDifference = string.Empty;
            while (clock.ElapsedMilliseconds < 10000)
            {
                Pump(15);
                iterations++;
                var snapshot = Snapshot(form!);
                var diagnostics = LayoutScheduler.GetDiagnostics(form!);
                if (previous != null && Same(previous, snapshot) && diagnostics.All(item => !item.Active || !item.Visible || !item.Pending)) stable++;
                else stable = 0;
                if (previous != null && !Same(previous, snapshot)) latestDifference = Differences(previous, snapshot);
                previous = snapshot;
                if (stable >= 3 && clock.ElapsedMilliseconds >= 75)
                {
                    var passes = diagnostics.Sum(item => item.Passes);
                    Pump(70);
                    TestSupport.Assert(Same(snapshot, Snapshot(form!)), "Geometry changed after apparent convergence: " + scenario + " " + Differences(snapshot, Snapshot(form!)));
                    var after = LayoutScheduler.GetDiagnostics(form!);
                    TestSupport.Assert(after.All(item => !item.Active || !item.Visible || !item.Pending) && after.Sum(item => item.Passes) == passes,
                        "Layout scheduler continued running after convergence: " + scenario);
                    settled.Add(new(scenario, clock.Elapsed.TotalMilliseconds, diagnostics.Count, passes)); return;
                }
            }
            throw new InvalidOperationException("Layout did not converge within 10000ms: " + scenario + $" samples={iterations} stable={stable} " +
                JsonSerializer.Serialize(LayoutScheduler.GetDiagnostics(form!).Where(item => item.Active && item.Visible && item.Pending)) + " " + latestDifference);
        }
        void CheckRange(int page, string scenario)
        {
            var host = form!.RangeHostForSmokeTest;
            if (page == 2)
            {
                var grid = host.ExtensionEditor.GridForTest;
                TestSupport.Assert(grid.Rows.Count >= 38 && grid.Columns.Count == 5, "Extension grid is incomplete.");
                grid.CurrentCell = grid.Rows[^1].Cells[2]; Pump(15);
                grid.FirstDisplayedScrollingColumnIndex = 2;
                TestSupport.Assert(grid.GetCellDisplayRectangle(2, grid.Rows.Count - 1, true).Height >= grid.Font.Height,
                    "Last extension mapping input cannot be reached.");
                grid.CurrentCell = grid.Rows[0].Cells[2]; grid.FirstDisplayedScrollingColumnIndex = 0;
                grid.FirstDisplayedScrollingRowIndex = 0; reachableInputs++; return;
            }
            var editor = page == 0 ? host.AttackEditor : host.PierceEditor;
            SelectEditableRange(editor, page == 0 ? RangeKind.Attack : RangeKind.Pierce); Settle("editable-range/" + scenario);
            var viewport = (Panel)editor.PreviewForTest.Parent!;
            var editViewport = Descendants(editor).OfType<Panel>().Single(panel => panel.Name == "RangeEditViewport");
            TestSupport.Assert(editViewport.AutoScroll && viewport.ClientSize.Height >= editViewport.Height && viewport.ClientSize.Width >= 200,
                "Range canvas/edit viewport balance is invalid.");
            foreach (var input in Descendants(editor).Where(control => control.Visible && control.Parent is not UpDownBase &&
                control is TextBoxBase or ComboBox or NumericUpDown or Button).ToArray())
            {
                for (Control? ancestor = input.Parent; ancestor != null && !ReferenceEquals(ancestor, editor.Parent); ancestor = ancestor.Parent)
                    if (ancestor is ScrollableControl { AutoScroll: true } scroll) scroll.ScrollControlIntoView(input);
                Pump(15);
                var target = input.RectangleToScreen(input.ClientRectangle);
                for (Control? ancestor = input.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    var clip = ancestor.RectangleToScreen(ancestor.ClientRectangle); clip.Inflate(1, 1);
                    TestSupport.Assert(clip.Contains(target), $"Range input is not fully scroll-reachable: {scenario}/{input.Name}/{input.Text}; input={target}; ancestor={ancestor.GetType().Name}/{clip}");
                    if (ReferenceEquals(ancestor, editor)) break;
                }
                reachableInputs++;
            }
            editViewport.AutoScrollPosition = Point.Empty; viewport.AutoScrollPosition = Point.Empty; Settle("range-scrolled/" + scenario);
        }
        void SelectEditableRange(RangeEditorPanel editor, RangeKind kind)
        {
            var bindings = form!.RangeSessionForSmokeTest!.Bindings.Where(binding => binding.Key.Kind == kind).OrderBy(binding => binding.Key.Id).ToArray();
            var index = Array.FindIndex(bindings, binding => binding.Editable && binding.Definition.Groups.Count > 0);
            TestSupport.Assert(index >= 0, "No editable finite range is available for the layout regression.");
            editor.IdListForTest.SelectedIndex = index;
            editor.GroupSelectorForTest.SelectedIndex = 0;
            TestSupport.Assert(editor.GeneratorsForTest.Enabled, "The selected range generator editor is disabled.");
        }
        void CheckToolbar()
        {
            var toolbar = form!.Controls.OfType<FlowLayoutPanel>().Single(panel => panel.AutoScroll && !panel.WrapContents);
            foreach (var button in toolbar.Controls.OfType<Button>().Where(button => button.Visible))
                TestSupport.Assert(button.Top >= toolbar.Padding.Top && button.Bottom <= toolbar.ClientRectangle.Bottom - toolbar.Padding.Bottom,
                    $"The shell toolbar clips a button vertically: {button.Text}, button={button.Bounds}, client={toolbar.ClientRectangle}.");
        }
        void Screenshot(string name)
        {
            using var bitmap = new Bitmap(form!.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            var path = Path.Combine(output, name + ".png"); bitmap.Save(path, ImageFormat.Png); screenshots.Add(path);
        }
    }

    private static void CheckRangePickers(Control root)
    {
        foreach (var picker in Descendants(root).OfType<RangePreviewPicker>().Where(control => control.Visible))
        {
            TestSupport.Assert(picker.ClientRectangle.Contains(picker.PreviewBox.Bounds) && picker.ClientRectangle.Contains(picker.Combo.Bounds),
                "A range selector clips its preview or input: " + picker.Bounds);
            var expected = (int)Math.Round(64 * picker.DeviceDpi / 96d);
            TestSupport.Assert(picker.PreviewBox.Width == expected && picker.PreviewBox.Height == expected,
                "The range selector does not use the actual DPI ratio.");
        }
    }
    private static Dictionary<string, Geometry> Snapshot(Control root)
    {
        var result = new Dictionary<string, Geometry>();
        var fonts = new Dictionary<Font, string>();
        foreach (var (path, control) in VisibleControls(root, "root").Where(item => !item.Control.IsDisposed && item.Control is not PictureBox))
        {
            var font = control.Font;
            if (!fonts.TryGetValue(font, out var fontDescription))
                fonts.Add(font, fontDescription = $"{font.Name}/{font.SizeInPoints:F3}/{font.Style}");
            var extra = control is ByteTextEditor byteText ? $"min={byteText.MinimumSize},preferred={byteText.GetPreferredSize(byteText.Size)},multiline={byteText.Editor.Multiline},native={byteText.Editor.PreferredHeight}"
                : control is TableLayoutPanel table ? string.Join(',', table.GetRowHeights()) + "/" + string.Join(',', table.GetColumnWidths()) + "/" + string.Join(',', table.RowStyles.Cast<RowStyle>().Select(style => style.Height))
                : control is ScrollableControl scroll ? $"{scroll.AutoScrollMinSize}/{scroll.AutoScrollPosition}/{scroll.HorizontalScroll.Visible}/{scroll.VerticalScroll.Visible}" : "";
            result[path] = new(control.GetType().Name, control.Bounds.ToString(), control.DeviceDpi,
                fontDescription, extra);
        }
        return result;
    }
    private static void AssertSame(Dictionary<string, Geometry> before, Dictionary<string, Geometry> after, string description)
    {
        TestSupport.Assert(Same(before, after), description + " " + Differences(before, after));
    }
    private static bool Same(Dictionary<string, Geometry> before, Dictionary<string, Geometry> after)
        => before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static string Differences(Dictionary<string, Geometry> before, Dictionary<string, Geometry> after)
        => string.Join("; ", before.Where(pair => !after.TryGetValue(pair.Key, out var value) || value != pair.Value).Take(4)
            .Select(pair => pair.Value + " -> " + (after.TryGetValue(pair.Key, out var value) ? value : "missing")));
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static IEnumerable<(string Path, Control Control)> VisibleControls(Control root, string path)
    {
        yield return (path, root);
        var index = 0;
        foreach (Control child in root.Controls)
            if (child.Visible)
            {
                var childPath = path + "/" + index++ + ":" + child.GetType().Name + ":" + child.Name;
                foreach (var item in VisibleControls(child, childPath)) yield return item;
            }
    }
    private static void Pump(int milliseconds)
    { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(3); } }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
