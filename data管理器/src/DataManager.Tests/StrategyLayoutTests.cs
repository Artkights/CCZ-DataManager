using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Ccz.ManagerUi;
using DataManager.App;

namespace DataManager.Tests;

/// <summary>Cold strategy navigation and native editor geometry on real fixture copies.</summary>
internal static class StrategyLayoutTests
{
    internal static void Run(string source, string output, float? focusedFont = null)
    {
        Directory.CreateDirectory(output);
        var results = new List<string> { $"Source={Path.GetFullPath(source)}; fresh copies preserve original fixture bytes." };
        foreach (var factor in focusedFont.HasValue ? new[] { focusedFont.Value } : new[] { 1f, 1.25f, 1.5f })
        {
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            using var defaultFont = new Control();
            using var font = new Font(defaultFont.Font.FontFamily, defaultFont.Font.SizeInPoints * factor);
            using var form = new MainForm(false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, MaximumSize = new Size(4096, 2160), Font = font
            };
            try
            {
                form.Show();
                form.LoadProjectForSmokeTest(root);
                SetSize(form, new Size(1280, 800));
                // The first user navigation after loading goes directly to
                // strategy; no visit to its other tabs is allowed to repair it.
                form.SelectModuleForSmokeTest("策略");
                Settle(form);
                Check(form, "cold");
                Save(form, output, $"cold-font{factor * 100:0}");
                if (factor == 1f) SaveEffectIndex(form, output, "effect-index-1280x800-font100");
                var cold = Capture(form.BusinessEditorForSmokeTest);
                var cycles = focusedFont.HasValue ? 0 : 30;
                for (var cycle = 0; cycle < cycles; cycle++)
                {
                    form.SelectModuleForSmokeTest("人物"); Settle(form);
                    form.SelectModuleForSmokeTest("策略"); Settle(form);
                    Check(form, $"module-return-{cycle}");
                    SameGeometry(cold, Capture(form.BusinessEditorForSmokeTest), "module return");
                    var tabs = form.BusinessEditorForSmokeTest.DetailTabs;
                    tabs.SelectedIndex = 1; Settle(form);
                    tabs.SelectedIndex = 0; Settle(form);
                    Check(form, $"tab-return-{cycle}");
                    SameGeometry(cold, Capture(form.BusinessEditorForSmokeTest), "detail tab return");
                }
                form.SelectModuleForSmokeTest("兵种"); Settle(form);
                var jobTables = Descendants(form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab!)
                    .OfType<TableLayoutPanel>().Where(table => table.ColumnCount == 4 && table.Controls.OfType<Label>().Any(label => label.Text == "HP成长")).ToArray();
                TestSupport.Assert(jobTables.Length == 1, "Job FieldsGridBlock regression fixture is missing.");
                CheckGrid(jobTables[0], "job-regression");
                form.SelectModuleForSmokeTest("策略"); Settle(form);
                SameGeometry(cold, Capture(form.BusinessEditorForSmokeTest), "job return");
                foreach (var size in new[] { new Size(1100, 650), new Size(1920, 1080), new Size(1280, 800) })
                {
                    SetSize(form, size); Settle(form);
                    Check(form, $"{size.Width}x{size.Height}");
                    Save(form, output, $"{size.Width}x{size.Height}-font{factor * 100:0}");
                    if (factor == 1f && size.Width == 1920) SaveEffectIndex(form, output, "effect-index-1920x1080-font100");
                }
                SameGeometry(cold, Capture(form.BusinessEditorForSmokeTest), "resize return");
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty && !form.BusinessEditorForSmokeTest.HasPendingChanges,
                    "Strategy layout/navigation created a draft or pending input.");
                CheckUnsubmittedName(form);
                var diagnostics = LayoutScheduler.GetDiagnostics(form);
                results.Add($"Profile={form.WorkspaceForSmokeTest.ActiveProfile.ProfileId}/{form.WorkspaceForSmokeTest.ActiveProfile.VariantId}; DPI={form.DeviceDpi}; font={factor:P0}; cold/{cycles} returns/resize/maximize geometry within 2px; job grid regression; unsubmitted name retained; schedulers={diagnostics.Count}; clean=true.");
            }
            catch (Exception error)
            {
                Save(form, output, $"failure-font{factor * 100:0}");
                File.WriteAllLines(Path.Combine(output, $"failure-font{factor * 100:0}.txt"),
                    new[] { error.ToString() }.Concat(Descendants(form.BusinessEditorForSmokeTest)
                        .Select(control => $"{control.GetType().Name}/{control.Name}/{control.Text}: bounds={control.Bounds}; minimum={control.MinimumSize}; dock={control.Dock}; font={control.Font.SizeInPoints}"))
                    .Concat(LayoutScheduler.GetDiagnostics(form).Select(item => item.ToString())));
                throw;
            }
            finally { form.Dispose(); Directory.Delete(root, true); }
        }
        File.WriteAllLines(Path.Combine(output, "strategy-layout-results.txt"), results);
        Console.WriteLine("STRATEGY_LAYOUT_TESTS_OK " + output);
    }

    private static void Check(MainForm form, string stage)
    {
        var editor = form.BusinessEditorForSmokeTest;
        var page = editor.DetailTabs.SelectedTab!;
        TestSupport.Assert(page.Text == "策略详情", stage + ": strategy detail is not selected.");
        var sections = Descendants(page).Where(control => control.Name is
            "StrategyRangeFields" or "StrategyConfigurationFields" or "StrategyTypeAndExtensionFields" or "StrategyEffectClassField").ToArray();
        TestSupport.Assert(sections.Length >= 3, stage + ": strategy sections missing.");
        foreach (var section in sections)
        {
            var table = Descendants(section).OfType<TableLayoutPanel>().Single(control => control.ColumnCount == 4);
            CheckGrid(table, stage);
        }
        var labels = Descendants(page).OfType<Label>().ToArray();
        foreach (var name in new[] { "大动画", "小动画", "类型索引", "命中上限", "伤害系数", "战场AI策略", "练武场AI策略", "效果索引" })
            TestSupport.Assert(labels.Any(label => label.Text == name), stage + ": missing label " + name);
        var extensionChecks = Descendants(page).OfType<CheckBox>()
            .Where(check => check.Name.StartsWith("Flag_strategy-extension_", StringComparison.Ordinal)).ToArray();
        if (form.WorkspaceForSmokeTest.ActiveProfile.ProfileId == "ccz-6.6")
            TestSupport.Assert(extensionChecks.Select(check => check.Text)
                    .SequenceEqual(new[] { "策略模仿", "辅助穿透" }) && extensionChecks.All(check => check.Checked),
                stage + ": 6.6 strategy extension flags are not two checked bit0/bit1 boxes.");
        else
            TestSupport.Assert(extensionChecks.Length == 0, stage + ": 6.4/6.5 strategy page exposed extension flags.");
        if (form.WorkspaceForSmokeTest.Documents.ContainsKey("strategy-effect-class"))
        {
            var section = sections.Single(control => control.Name == "StrategyEffectClassField");
            TestSupport.Assert(Descendants(section).OfType<ComboBox>().Count() == 1 &&
                Descendants(section).OfType<Label>().Count(label => label.Text == "效果索引") == 1,
                stage + ": effect class should have one label and dropdown.");
        }
        else
            TestSupport.Assert(Descendants(sections.Single(control => control.Name == "StrategyEffectClassField"))
                .OfType<ComboBox>().Single().Enabled == false, stage + ": unavailable effect class should retain a disabled dropdown.");
        foreach (var picker in Descendants(page).OfType<RangePreviewPicker>())
            TestSupport.Assert(picker.PreviewBox.Width == picker.PreviewBox.Height && picker.PreviewBox.Width >= 64 * picker.DeviceDpi / 96,
                stage + ": range preview was clipped or stretched.");
        TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, stage + ": geometry wrote game bytes.");
    }

    private static void CheckGrid(TableLayoutPanel table, string stage)
    {
        foreach (var label in table.Controls.OfType<Label>())
        {
                var position = table.GetPositionFromControl(label);
                var input = table.GetControlFromPosition(position.Column + 1, position.Row)!;
                var primary = Primary(input);
                var labelBounds = Bounds(label);
                var inputBounds = Bounds(primary);
                var labelCenter = labelBounds.Top + label.Padding.Top +
                    (labelBounds.Height - label.Padding.Vertical) / 2d;
                TestSupport.Assert(Math.Abs(labelCenter - (inputBounds.Top + inputBounds.Height / 2d)) <= 2,
                    $"{stage}: {label.Text} is not vertically aligned: label={labelBounds}/{label.Padding}; input={inputBounds}.");
                TestSupport.Assert(input.Width > 15 && input.Height >= input.Font.Height,
                    stage + ": editor collapsed for " + label.Text);
                var cell = table.GetCellPosition(input);
                var widths = table.GetColumnWidths(); var heights = table.GetRowHeights();
                var cellBounds = new Rectangle(widths.Take(cell.Column).Sum(), heights.Take(cell.Row).Sum(),
                    widths.Skip(cell.Column).Take(table.GetColumnSpan(input)).Sum(), heights[cell.Row]);
                cellBounds.Inflate(1, 1);
                TestSupport.Assert(cellBounds.Contains(input.Bounds),
                    $"{stage}: {label.Text} exceeds its cell: {input.Bounds}/{cellBounds}.");
        }
        for (var row = 0; row < table.RowCount; row++)
        {
            var first = table.GetControlFromPosition(1, row);
            var second = table.GetControlFromPosition(3, row);
            if (first == null || second == null || ReferenceEquals(first, second)) continue;
            var left = Bounds(Primary(first)); var right = Bounds(Primary(second));
            TestSupport.Assert(Math.Abs(left.Top + left.Height / 2d - right.Top - right.Height / 2d) <= 2,
                $"{stage}: paired primary editors are not aligned: {left}/{right}.");
        }
    }

    private static void CheckUnsubmittedName(MainForm form)
    {
        var panel = form.BusinessEditorForSmokeTest;
        var nameLabel = Descendants(panel.DetailTabs.SelectedTab!).OfType<Label>().Single(label => label.Text == "名称");
        var table = (TableLayoutPanel)nameLabel.Parent!;
        var name = (ByteTextEditor)table.GetControlFromPosition(table.GetColumn(nameLabel) + 1, table.GetRow(nameLabel))!;
        name.Editor.Text = "布局输入"; name.Editor.Select(1, 2);
        TestSupport.Assert(panel.HasPendingChanges && !form.WorkspaceForSmokeTest.IsDirty, "Name test did not create an unsubmitted input.");
        // Exercise layout and lifecycle directly. Navigation intentionally
        // flushes valid text, so it cannot prove unsubmitted input retention.
        panel.DeactivateView(); panel.ActivateView(); Settle(form);
        form.WindowState = FormWindowState.Maximized; Settle(form);
        TestSupport.Assert(form.WindowState == FormWindowState.Maximized, "Native maximize did not occur.");
        Check(form, "maximized-pending-name");
        form.WindowState = FormWindowState.Normal; SetSize(form, new Size(1280, 800)); Settle(form);
        Check(form, "restored-pending-name");
        TestSupport.Assert(name.Editor.Text == "布局输入" && name.Editor.SelectionStart == 1 && name.Editor.SelectionLength == 2 &&
            panel.HasPendingChanges && !form.WorkspaceForSmokeTest.IsDirty, "Layout/lifecycle replaced or submitted the unfinished name input.");
    }

    private static Control Primary(Control input) => input is ByteTextEditor bytes ? bytes.Editor : input;
    private static Rectangle Bounds(Control control) => control.Parent!.RectangleToScreen(control.Bounds);
    private static Dictionary<string, Rectangle> Capture(BusinessEditorPanel editor)
    {
        var page = editor.DetailTabs.SelectedTab!;
        return Descendants(page).OfType<Label>().Where(label => label.Parent is TableLayoutPanel table && table.ColumnCount == 4)
            .ToDictionary(label => label.Text, label => Bounds(Primary(((TableLayoutPanel)label.Parent!).GetControlFromPosition(
                ((TableLayoutPanel)label.Parent).GetColumn(label) + 1, ((TableLayoutPanel)label.Parent).GetRow(label))!)));
    }
    private static void SameGeometry(Dictionary<string, Rectangle> expected, Dictionary<string, Rectangle> actual, string stage)
    {
        TestSupport.Assert(expected.Keys.Order().SequenceEqual(actual.Keys.Order()), stage + ": fields changed.");
        foreach (var (name, before) in expected)
        {
            var after = actual[name];
            TestSupport.Assert(Math.Abs(before.X - after.X) <= 2 && Math.Abs(before.Y - after.Y) <= 2 &&
                Math.Abs(before.Width - after.Width) <= 2 && Math.Abs(before.Height - after.Height) <= 2,
                $"{stage}: {name} changed from {before} to {after}.");
        }
    }
    private static void Settle(MainForm form)
    {
        Pump(220);
        var before = LayoutScheduler.GetDiagnostics(form).Sum(item => item.Passes);
        Pump(100);
        var after = LayoutScheduler.GetDiagnostics(form).Sum(item => item.Passes);
        TestSupport.Assert(before == after && LayoutScheduler.GetDiagnostics(form).All(item => !item.Active || !item.Visible || !item.Pending),
            $"Strategy layout did not converge: {before}->{after}.");
    }
    private static void SetSize(MainForm form, Size size)
    {
        SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
        TestSupport.Assert(form.Size == size, $"Window is {form.Size}, expected {size}.");
    }
    private static void Save(MainForm form, string output, string name)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
    }
    private static void SaveEffectIndex(MainForm form, string output, string name)
    {
        var page = form.BusinessEditorForSmokeTest.DetailTabs.SelectedTab!;
        var content = Descendants(page).Single(control => control.Name == "StrategyPageContent");
        var viewport = (Panel)content.Parent!;
        var position = viewport.AutoScrollPosition;
        var effect = Descendants(page).Single(control => control.Name == "StrategyEffectClassField");
        viewport.ScrollControlIntoView(effect); Settle(form);
        var visible = viewport.RectangleToScreen(viewport.ClientRectangle);
        foreach (var section in Descendants(page).Where(control => control.Name is "StrategyTypeAndExtensionFields" or "StrategyEffectClassField"))
            TestSupport.Assert(visible.Contains(Bounds(section)), "Type/effect index is not visible in its screenshot: " + Bounds(section));
        Save(form, output, name);
        viewport.AutoScrollPosition = new Point(-position.X, -position.Y); Settle(form);
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
