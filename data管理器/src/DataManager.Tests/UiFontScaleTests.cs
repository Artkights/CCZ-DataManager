using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DataManager.App;

namespace DataManager.Tests;

internal static class UiFontScaleTests
{
    internal static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<string> { $"Source={Path.GetFullPath(source)}；按原始字节复制，未恢复历史快照。", "使用与产品相同的ApplicationBootstrap初始化。本项在真实显示器DPI上额外模拟125%/150%字体放大；不代表PerMonitor DPI切换。窗口1920×1080。" };
        foreach (var factor in new[] { 1.25f, 1.5f })
        {
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            using var fontProbe = new Control();
            using var scaledFont = new Font(fontProbe.Font.FontFamily, fontProbe.Font.SizeInPoints * factor);
            using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
            try
            {
                form.Font = scaledFont; form.Show(); form.LoadProjectForSmokeTest(root);
                SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, 1920, 1080, 0x0010 | 0x0004 | 0x0400);
                foreach (var module in new[] { "人物", "兵种系", "兵种", "宝物", "人物/套装特效" })
                {
                    var tab = form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(page => page.Text == module);
                    form.SelectModuleForSmokeTest((string)tab.Tag!); Application.DoEvents();
                    foreach (var label in Descendants(form).OfType<Label>().Where(label => label.Visible && label.Text is "版本" or "布局" or "快速定位" or "表"))
                        TestSupport.Assert(label.Width >= TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width && label.Height < label.Font.Height * 2,
                            $"顶部设置条/{factor:P0}标签折行：{label.Text} {label.Size} font={label.Font.Height} measured={TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding)}");
                    var editor = form.BusinessEditorForSmokeTest;
                    if (module == "人物") UiOptimizationReportTests.CheckUsablePersonFields(editor);
                    var fields = Descendants(editor).Where(control => control.Visible && control.Parent is not UpDownBase && control is TextBoxBase or ComboBox or NumericUpDown or CheckBox).ToArray();
                    TestSupport.Assert(fields.Length > 0 && fields.All(control => control.Width > 15 && control.Height >= control.Font.Height),
                        $"{module}/{factor:P0}有编辑控件被压缩：{string.Join(';', fields.Where(control => control.Width <= 15 || control.Height < control.Font.Height).Select(control => $"{control.GetType().Name}={control.Size}/{control.Font.Height}/{control.Text}"))}");
                    var labels = Descendants(editor).OfType<Label>().Where(label => label.Visible && label.Text is "初始HP" or "初始MP" or "暴击模式" or "特殊槽位" or "普通类型").ToArray();
                    foreach (var label in labels)
                        TestSupport.Assert(label.Width >= TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine).Width,
                            $"{module}/{factor:P0}标签被裁剪：{label.Text}");
                    if (module == "兵种系")
                        UiOptimizationReportTests.CheckJobSeriesLayout(editor);
                    using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, $"{module.Replace('/', '_')}-字体模拟{factor*100:0}%-1920x1080.png"), ImageFormat.Png);
                    results.Add($"{module}/{factor*100:0}%：{fields.Length}个编辑控件尺寸、{labels.Length}个关键标签通过；DPI={form.DeviceDpi}。");
                    TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "字体缩放产生游戏草稿。");
                }
            }
            finally { form.BusinessEditorForSmokeTest.DiscardPendingChanges(); form.Close(); Directory.Delete(root, true); }
        }
        File.WriteAllLines(Path.Combine(output, "字体缩放模拟.txt"), results);
        Console.WriteLine("UI_FONT_SCALE_TESTS_OK 125%/150% extra-font-scale; actual DPI is recorded per page");
    }
    private static IEnumerable<Control> Descendants(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
