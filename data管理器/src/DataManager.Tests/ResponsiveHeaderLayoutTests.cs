using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataManager.App;

namespace DataManager.Tests;

internal static class ResponsiveHeaderLayoutTests
{
    private sealed record Sample(string Context, int ActualDpi, int HeaderHeight, int SettingsRows,
        int QueryRows, int[] PairWidths, int[] InputWidths, long LayoutPasses,
        int[] ChoiceItemHeights, int[] ChoiceDropDownHeights);

    internal static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var samples = new List<Sample>();
        foreach (var factor in new[] { 1f, 1.25f, 1.5f })
        {
            var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
            using var fontProbe = new Control();
            using var font = new Font(fontProbe.Font.FontFamily, fontProbe.Font.SizeInPoints * factor);
            using var form = new MainForm(false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Font = font
            };
            try
            {
                form.Show();
                form.LoadProjectForSmokeTest(root);
                form.SelectModuleForSmokeTest("人物");
                foreach (var size in new[] { new Size(1100, 650), new Size(1280, 720), new Size(1920, 1080) })
                {
                    Resize(form, size);
                    var context = $"字体模拟{factor * 100:0}%/{size.Width}x{size.Height}/实际DPI={form.DeviceDpi}";
                    RecordQueryUiTests.CheckQueryLayout(form, context);
                    RecordQueryUiTests.CheckQueryChoiceBehavior(form, context);
                    var settingsRows = CheckSettings(form, context);
                    var queryRows = CheckQueries(form, context);
                    CheckStable(form, context);
                    var header = Named(form, "SettingsAndQuery");
                    samples.Add(new(context, form.DeviceDpi, header.Height, settingsRows, queryRows,
                        Pairs(form).Select(pair => pair.Width).ToArray(),
                        form.QueryInputsForSmokeTest.Select(input => input.Width).ToArray(),
                        form.HeaderLayoutPassesForSmokeTest,
                        form.QueryFieldsForSmokeTest.Select(field => field.ItemHeight).ToArray(),
                        form.QueryFieldsForSmokeTest.Select(field => field.DropDownHeight).ToArray()));
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(output, $"header-font{factor * 100:0}-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                }
                CheckHiddenSettings(form, factor);
                CheckWidthGrowth(form, factor);
                CheckChangedFont(form, factor);
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "顶部布局测试产生了游戏数据草稿。");
            }
            finally
            {
                form.BusinessEditorForSmokeTest.DiscardPendingChanges();
                form.Close();
                Directory.Delete(root, true);
            }
        }
        File.WriteAllText(Path.Combine(output, "responsive-header-layout.json"), JsonSerializer.Serialize(new
        {
            Source = Path.GetFullPath(source),
            Note = "使用基底原始字节的临时副本。100%/125%/150% 为字体模拟，真实 DeviceDpi 独立记录，不代表真实显示器 DPI 切换。",
            Samples = samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESPONSIVE_HEADER_LAYOUT_TESTS_OK 9 size/font cases; full-width/equal-groups/no-clipping/actions-right/hidden-placeholders/width-growth/stable-layout/non-integral-dropdown-height/native-last-item/independent-selections/runtime-font-change; actual DPI recorded separately");
    }

    private static int CheckSettings(MainForm form, string context)
    {
        var settings = Named(form, "ResponsiveSettingsRow");
        var groups = settings.Controls.Cast<Control>().Where(control => control.Visible && control.Width > 0).ToArray();
        TestSupport.Assert(groups.Length > 0, context + " 没有可见设置组。");
        foreach (var group in groups)
        {
            TestSupport.Assert(settings.ClientRectangle.Contains(group.Bounds), context + " 设置组越界：" + group.Name);
            foreach (var child in group.Controls.Cast<Control>().Where(control => control.Visible))
            {
                TestSupport.Assert(group.ClientRectangle.Contains(child.Bounds), context + " 设置控件被裁切：" + group.Name);
                if (child is Label label)
                {
                    var measured = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty,
                        TextFormatFlags.SingleLine);
                    TestSupport.Assert(label.Width >= measured.Width && label.Height >= measured.Height,
                        context + " 设置标题被裁切：" + label.Text);
                }
            }
        }
        var rows = Rows(groups);
        foreach (var row in rows)
        {
            TestSupport.Assert(row[0].Left == 0, context + " 设置行左侧存在空占位。");
            if (row.Any(control => control.Name != "SettingsHideEmpty"))
                TestSupport.Assert(Math.Abs(row[^1].Right - settings.ClientSize.Width) <= 1,
                    context + " 设置行没有铺满可用宽度。");
            CheckNoOverlap(row, context + " 设置组");
        }
        return rows.Count;
    }

    private static int CheckQueries(MainForm form, string context)
    {
        var query = Named(form, "ResponsiveQueryRows");
        var pairs = Pairs(form);
        var actions = Named(form, "RecordQueryActions");
        var rows = Rows(pairs);
        foreach (var row in rows)
        {
            TestSupport.Assert(row[0].Left == 0, context + " 条件行左侧存在空占位。");
            TestSupport.Assert(row.Max(pair => pair.Width) - row.Min(pair => pair.Width) <= 1,
                context + " 同行查询组宽度不一致。");
            var sharesActions = actions.Top == row[0].Top;
            var right = sharesActions ? actions.Left - Pixels(form, 8) : query.ClientSize.Width;
            TestSupport.Assert(Math.Abs(row[^1].Right - right) <= 1, context + " 条件行没有平分整行宽度。");
            CheckNoOverlap(sharesActions ? row.Append(actions).ToArray() : row, context + " 查询组");
        }
        TestSupport.Assert(actions.Right == query.ClientSize.Width && query.ClientRectangle.Contains(actions.Bounds),
            context + " 查询尾组没有完整靠右显示。");
        foreach (var action in actions.Controls.Cast<Control>().Where(control => control.Visible))
        {
            TestSupport.Assert(actions.ClientRectangle.Contains(action.Bounds), context + " 查询操作或匹配数量被裁切。");
            var preferred = action.GetPreferredSize(Size.Empty);
            TestSupport.Assert(action.Width >= preferred.Width && action.Height >= preferred.Height,
                context + " 查询操作或匹配数量没有保留自然宽度。");
        }
        foreach (var pair in pairs)
        {
            TestSupport.Assert(query.ClientRectangle.Contains(pair.Bounds), context + " 查询组超出区域。");
            var field = pair.Controls.OfType<ComboBox>().Single();
            var input = pair.Controls.OfType<TextBox>().Single();
            var minimumField = Math.Max(Pixels(form, 112),
                TextRenderer.MeasureText("全部属性", field.Font, Size.Empty, TextFormatFlags.SingleLine).Width
                    + SystemInformation.VerticalScrollBarWidth + Pixels(form, 8));
            var minimumInput = Math.Max(Pixels(form, 128),
                TextRenderer.MeasureText("关键词（空格分词）", input.Font, Size.Empty, TextFormatFlags.SingleLine).Width + Pixels(form, 8));
            TestSupport.Assert(field.Width >= minimumField && input.Width >= minimumInput,
                context + $" 条件内部最小宽度不足：{field.Width}/{minimumField}，{input.Width}/{minimumInput}。");
            var available = pair.ClientSize.Width - Pixels(form, 4);
            var expected = Math.Clamp((int)Math.Round(available * .4), minimumField, available - minimumInput);
            TestSupport.Assert(Math.Abs(field.Width - expected) <= 1, context + " 属性和关键词没有按 40/60 分配。");
        }
        return rows.Count + (rows.Any(row => row[0].Top == actions.Top) ? 0 : 1);
    }

    private static void CheckHiddenSettings(MainForm form, float factor)
    {
        Resize(form, new Size(1280, 720));
        var group = Named(form, "SettingsQuickLocateGroup");
        var editor = group.Controls.OfType<ComboBox>().Single();
        editor.Visible = false;
        Settle();
        TestSupport.Assert(group.Width == 0 && group.Height == 0, $"字体{factor:P0} 隐藏快速定位后仍保留占位。");
        CheckSettings(form, $"隐藏快速定位/字体{factor:P0}");
        editor.Visible = true;
        Settle();
        TestSupport.Assert(group.Width > 0 && group.Height > 0, $"字体{factor:P0} 恢复快速定位后没有重新加入布局。");
        CheckSettings(form, $"恢复快速定位/字体{factor:P0}");
    }

    private static void CheckWidthGrowth(MainForm form, float factor)
    {
        Resize(form, new Size(1920, 1080));
        var before = Pairs(form).Select(pair => pair.Bounds).ToArray();
        var inputWidths = form.QueryInputsForSmokeTest.Select(input => input.Width).ToArray();
        Resize(form, new Size(1968, 1080));
        var after = Pairs(form).Select(pair => pair.Bounds).ToArray();
        if (before.Select(bounds => bounds.Top).SequenceEqual(after.Select(bounds => bounds.Top)))
            TestSupport.Assert(form.QueryInputsForSmokeTest.Select((input, index) => input.Width > inputWidths[index]).All(value => value),
                $"字体{factor:P0} 窗口加宽后输入框没有同步变宽。");
        CheckQueries(form, $"加宽/字体{factor:P0}");
    }

    private static void CheckChangedFont(MainForm form, float factor)
    {
        var original = form.Font;
        using var changed = new Font(original.FontFamily, original.SizeInPoints * 1.15f, original.Style);
        try
        {
            form.Font = changed;
            Settle();
            RecordQueryUiTests.CheckQueryChoiceBehavior(form, $"运行中字体变化/初始{factor:P0}/实际DPI={form.DeviceDpi}");
        }
        finally
        {
            form.Font = original;
            Settle();
        }
        RecordQueryUiTests.CheckQueryChoiceBehavior(form, $"恢复字体/初始{factor:P0}/实际DPI={form.DeviceDpi}");
    }

    private static void CheckStable(MainForm form, string context)
    {
        var header = Named(form, "SettingsAndQuery");
        var before = Descendants(header).Select(control => (control.Name, control.Bounds)).ToArray();
        var passes = form.HeaderLayoutPassesForSmokeTest;
        Settle();
        var after = Descendants(header).Select(control => (control.Name, control.Bounds)).ToArray();
        TestSupport.Assert(before.SequenceEqual(after), context + " 稳定尺寸下顶部布局仍在变化。");
        TestSupport.Assert(form.HeaderLayoutPassesForSmokeTest - passes <= 3, context + " 稳定尺寸下顶部布局重复执行。");
    }

    private static List<Control[]> Rows(IEnumerable<Control> controls)
    {
        var remaining = controls.OrderBy(control => control.Top).ThenBy(control => control.Left).ToList();
        var rows = new List<Control[]>();
        while (remaining.Count > 0)
        {
            var first = remaining[0];
            var row = remaining.Where(control => control.Top < first.Bottom && control.Bottom > first.Top)
                .OrderBy(control => control.Left).ToArray();
            rows.Add(row);
            foreach (var control in row) remaining.Remove(control);
        }
        return rows;
    }

    private static void CheckNoOverlap(Control[] controls, string context)
    {
        for (var first = 0; first < controls.Length; first++)
            for (var second = first + 1; second < controls.Length; second++)
                TestSupport.Assert(!controls[first].Bounds.IntersectsWith(controls[second].Bounds),
                    context + " 重叠：" + controls[first].Name + "/" + controls[second].Name);
    }

    private static Control[] Pairs(Control form) => Enumerable.Range(1, 3).Select(index => Named(form, "RecordQueryPair" + index)).ToArray();
    private static Control Named(Control root, string name) => Descendants(root).Single(control => control.Name == name);
    private static int Pixels(Control owner, int logical) => Math.Max(1, (int)Math.Round(logical * owner.DeviceDpi / 96d));
    private static void Resize(Form form, Size size)
    {
        TestSupport.Assert(SetWindowPos(form.Handle, IntPtr.Zero, -32000, -32000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400),
            "无法设置布局验收窗口尺寸。");
        Settle();
    }
    private static void Settle() { for (var pass = 0; pass < 6; pass++) TestSupport.PumpUi(); }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
