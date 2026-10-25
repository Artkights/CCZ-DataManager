using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ImageManager.App;

namespace ImageManager.Tests;

/// <summary>Exercises the real modeless replacement UI against the edited bitmap.</summary>
internal static class PixelColorPickingTests
{
    private const BindingFlags InstanceMethods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Color Background = Color.FromArgb(255, 27, 37, 47);
    private static readonly Color[] Sources = [Color.Red, Color.Lime, Color.Cyan, Color.Gold, Color.Magenta];
    private static readonly Color[] Targets = [Color.Navy, Color.Olive, Color.Purple, Color.Orange, Color.DeepPink];
    private static readonly Point[] SourcePoints = [new(4, 4), new(12, 24), new(20, 44), new(28, 64), new(36, 84)];
    private static readonly Point[] TargetPoints = [new(108, 14), new(116, 34), new(124, 54), new(132, 74), new(116, 918)];
    private static readonly PixelEditorSection[] Sections =
    [new("第一动作", new(0, 0, 80, 1000), 50, "Unit_mov.e5"), new("第二动作", new(100, 0, 80, 1000), 50, "Unit_atk.e5")];

    internal static void Run()
    {
        Exception? failure = null;
        using var host = new Form { ShowInTaskbar = false, Opacity = 0, Size = new(100, 100) };
        host.Shown += (_, _) =>
        {
            try
            {
                FiveRowsApplyAndUndo();
                CancelAndReadOnlyConfiguration();
                IndexedTargets();
                TransparentAndAlpha();
                PickBoundsAndOwnerLifetime();
            }
            catch (Exception error) { failure = error; }
            finally { host.Close(); }
        };
        Application.Run(host);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("PIXEL_COLOR_PICKING_TESTS_OK five-rows edited-source zoom scroll cancel readonly palette transparency alpha bounds lifetime apply undo redo");
    }

    private static void FiveRowsApplyAndUndo()
    {
        using var source = Source();
        using var editor = Show(source);
        var canvas = Named<Control>(editor, "PixelCanvas");
        var original = Pixels(canvas);
        var replacement = Open(editor);
        Require(editor.Enabled && replacement.Owner == editor, "换色窗仍禁用主底图，或没有由编辑器拥有。");

        // Reopening the command should retain the current owned window.
        Named<Button>(editor, "OpenColorReplacement").PerformClick(); Pump();
        Require(editor.OwnedForms.OfType<PixelColorReplaceDialog>().Count() == 1,
            "重复打开换色产生多个同时配置的窗口。");

        for (var index = 0; index < 5; index++)
        {
            SetEnabled(replacement, index, true);
            Set(canvas, "Zoom", index % 2 == 0 ? 4d : 1.5d);
            Pick(editor, replacement, canvas, index, false, SourcePoints[index]);
            Require(ColorAt(replacement, "ReplacementSource", index).ToArgb() == Sources[index].ToArgb(),
                $"第{index + 1}行原色未从底图坐标读取；可能取到了网格、动作标签或前一行颜色。");
            Require(Named<Label>(replacement, "ReplacementHits" + index).Text == "2", $"第{index + 1}行命中数未随原色取色刷新。");
            if (index == 4) ScrollTo(canvas, TargetPoints[index]);
            Pick(editor, replacement, canvas, index, true, TargetPoints[index]);
            Require(ColorAt(replacement, "ReplacementTarget", index).ToArgb() == Targets[index].ToArgb(),
                $"第{index + 1}行目标色取错，或滚动/缩放后坐标映射不正确。");
        }
        Require(original.SequenceEqual(Pixels(canvas)), "只配置五组取色已经修改底图。");
        Named<Button>(replacement, "ReplacementApply").PerformClick(); Pump();
        Require(!replacement.Visible && editor.OwnedForms.OfType<PixelColorReplaceDialog>().All(form => !form.Visible),
            "确认换色后配置窗口没有结束。");
        using var changed = Snapshot(canvas);
        for (var index = 0; index < 5; index++)
        {
            Require(changed.GetPixel(SourcePoints[index].X, SourcePoints[index].Y).ToArgb() == Targets[index].ToArgb() &&
                changed.GetPixel(56, 140 + index * 4).ToArgb() == Targets[index].ToArgb(),
                $"第{index + 1}组未同时替换全部匹配像素。");
            Require(changed.GetPixel(TargetPoints[index].X, TargetPoints[index].Y).ToArgb() == Targets[index].ToArgb(),
                "取色来源的目标像素被意外改写。");
        }
        Require(changed.GetPixel(90, 30).ToArgb() == Sources[0].ToArgb(), "非编辑区像素被换色。");
        var after = Pixels(canvas);
        TextButton(editor, "撤回").PerformClick(); Pump();
        Require(original.SequenceEqual(Pixels(canvas)), "一次撤回没有恢复整批五组换色。");
        TextButton(editor, "重做").PerformClick(); Pump();
        Require(after.SequenceEqual(Pixels(canvas)), "重做没有恢复同一批换色。");
        editor.Close();
    }

    private static void CancelAndReadOnlyConfiguration()
    {
        using var source = Source();
        using var editor = Show(source);
        var canvas = Named<Control>(editor, "PixelCanvas");
        Set(canvas, "Zoom", 3d);
        Set(canvas, "DrawingColor", Color.White);
        Mouse(canvas, MouseButtons.Left, new(48, 200));
        var edited = Pixels(canvas);
        Require(!edited.SequenceEqual(BitmapPixels(source)), "预先建立的铅笔草稿没有生效。");
        var replacement = Open(editor);
        Mouse(canvas, MouseButtons.Left, new(52, 200));
        TextButton(editor, "撤回").PerformClick();
        TextButton(editor, "重做").PerformClick();
        SendKey(editor, Keys.Control | Keys.Z);
        SendKey(editor, Keys.Control | Keys.Y);
        foreach (var save in Descendants(editor).OfType<Button>().Where(button => button.Text.Contains("立即保存", StringComparison.Ordinal)).ToArray())
            save.PerformClick();
        Pump();
        Require(edited.SequenceEqual(Pixels(canvas)) && editor.Visible && editor.ResultBitmap == null && editor.DialogResult != DialogResult.OK,
            "配置换色期间可以绘图、撤回、重做或保存。");

        var beforeFrom = ColorAt(replacement, "ReplacementSource", 1);
        Named<Button>(replacement, "ReplacementSourcePick1").PerformClick(); Pump();
        Require(!replacement.Visible, "开始从底图取色时换色窗未让出画布。");
        Mouse(canvas, MouseButtons.Right, SourcePoints[1]); Pump();
        Require(replacement.Visible && ColorAt(replacement, "ReplacementSource", 1).ToArgb() == beforeFrom.ToArgb(),
            "右键取消没有恢复换色窗或改变了原色。");

        var beforeTarget = ColorAt(replacement, "ReplacementTarget", 2);
        Named<Button>(replacement, "ReplacementTargetPick2").PerformClick(); Pump();
        SendKey(editor, Keys.Escape); Pump();
        Require(replacement.Visible && editor.Visible && ColorAt(replacement, "ReplacementTarget", 2).ToArgb() == beforeTarget.ToArgb(),
            "Esc取消单次取色关闭了整个编辑器，或改变了目标色。");

        Named<Button>(replacement, "ReplacementSourcePick0").PerformClick(); Pump();
        Mouse(canvas, MouseButtons.Left, new(90, 30)); Pump();
        Require(!replacement.Visible && edited.SequenceEqual(Pixels(canvas)), "点击动作之间的空隙被当作有效取色或写入。");
        Mouse(canvas, MouseButtons.Left, new(48, 200)); Pump();
        Require(replacement.Visible && ColorAt(replacement, "ReplacementSource", 0).ToArgb() == Color.White.ToArgb(),
            "没有从当前编辑草稿取色，或无效点击终止了等待。");
        Require(edited.SequenceEqual(Pixels(canvas)), "取色期间修改了底图。");
        Named<Button>(replacement, "ReplacementCancel").PerformClick(); Pump();
        Require(edited.SequenceEqual(Pixels(canvas)), "取消整个换色配置改变了草稿。");
        TextButton(editor, "撤回").PerformClick(); Pump();
        Require(BitmapPixels(source).SequenceEqual(Pixels(canvas)), "配置取消后旧铅笔命令丢失，或取色创建了额外撤回命令。");
        TextButton(editor, "重做").PerformClick(); Pump();
        Require(edited.SequenceEqual(Pixels(canvas)), "配置取消后旧命令不能重做。");
        // Start a second session, close it with its window close button, then draw again.
        replacement = Open(editor); replacement.Close(); Pump();
        Set(canvas, "DrawingColor", Color.Yellow);
        Mouse(canvas, MouseButtons.Left, new(52, 200));
        using var final = Snapshot(canvas);
        Require(final.GetPixel(52, 200).ToArgb() == Color.Yellow.ToArgb(), "关闭换色窗后绘图仍被锁定。");
        editor.Close();
    }

    private static void IndexedTargets()
    {
        using var source = Source();
        IReadOnlyList<Color> palette = [Color.Black, Color.Red, Color.Blue, Color.White];
        using var editor = Show(source, palette);
        var canvas = Named<Control>(editor, "PixelCanvas");
        var replacement = Open(editor);
        var before = Pixels(canvas);
        Pick(editor, replacement, canvas, 0, false, SourcePoints[0]);
        Pick(editor, replacement, canvas, 0, true, SourcePoints[0]);
        Require(ColorAt(replacement, "ReplacementTarget", 0).ToArgb() == Color.Red.ToArgb(),
            "索引资源从底图取出的合法目标色发生变化。");
        Pick(editor, replacement, canvas, 0, true, TargetPoints[0]);
        Require(ColorAt(replacement, "ReplacementTarget", 0).ToArgb() == Color.Blue.ToArgb(),
            "索引资源没有把非调色板目标色映射到最近合法颜色。");
        Require(before.SequenceEqual(Pixels(canvas)), "索引目标取色修改了底图。");
        Named<Button>(replacement, "ReplacementCancel").PerformClick(); Pump();
        editor.Close();

        using var emptyPaletteEditor = Show(source, []);
        canvas = Named<Control>(emptyPaletteEditor, "PixelCanvas");
        replacement = Open(emptyPaletteEditor);
        var previousTarget = ColorAt(replacement, "ReplacementTarget", 0);
        Named<Button>(replacement, "ReplacementTargetPick0").PerformClick(); Pump();
        Mouse(canvas, MouseButtons.Left, TargetPoints[0]); Pump();
        Require(!replacement.Visible && ColorAt(replacement, "ReplacementTarget", 0).ToArgb() == previousTarget.ToArgb() &&
            !string.IsNullOrWhiteSpace(Named<Control>(emptyPaletteEditor, "ReplacementStatus").Text),
            "无可用调色板的非透明目标没有继续等待或缺少提示。");
        Mouse(canvas, MouseButtons.Right, TargetPoints[0]); Pump();
        Require(replacement.Visible, "空调色板取色失败后不能取消返回配置。");
        Named<Button>(replacement, "ReplacementCancel").PerformClick(); Pump();
        emptyPaletteEditor.Close();
    }

    private static void TransparentAndAlpha()
    {
        using var source = Source();
        var transparentA = new Point(42, 230);
        var transparentB = new Point(46, 230);
        var alphaTarget = new Point(108, 230);
        source.SetPixel(alphaTarget.X, alphaTarget.Y, Color.FromArgb(128, 198, 44, 68));
        using var editor = Show(source);
        var canvas = Named<Control>(editor, "PixelCanvas");
        Set(canvas, "Zoom", 2d);
        Set(canvas, "DrawingColor", Color.FromArgb(0, 10, 20, 30)); Mouse(canvas, MouseButtons.Left, transparentA);
        Set(canvas, "DrawingColor", Color.FromArgb(0, 80, 90, 100)); Mouse(canvas, MouseButtons.Left, transparentB);
        using var before = Snapshot(canvas);
        var alpha = before.GetPixel(alphaTarget.X, alphaTarget.Y);
        Require(alpha.A == 128, "半透明取色夹具没有保留Alpha。");
        var replacement = Open(editor);
        Pick(editor, replacement, canvas, 0, false, transparentA);
        Require(ColorAt(replacement, "ReplacementSource", 0).ToArgb() == Color.Transparent.ToArgb() &&
            Named<Label>(replacement, "ReplacementHits0").Text == "2", "透明原色未归一或不同隐藏RGB透明像素没有合并计数。");
        Pick(editor, replacement, canvas, 0, true, alphaTarget);
        Require(ColorAt(replacement, "ReplacementTarget", 0).ToArgb() == alpha.ToArgb(), "非索引目标取色改变了半透明ARGB。");
        Named<Button>(replacement, "ReplacementApply").PerformClick(); Pump();
        using (var after = Snapshot(canvas))
            Require(after.GetPixel(transparentA.X, transparentA.Y).ToArgb() == alpha.ToArgb() &&
                after.GetPixel(transparentB.X, transparentB.Y).ToArgb() == alpha.ToArgb(),
                "透明原色没有同时替换全部透明像素，或半透明结果被改成不透明。");
        TextButton(editor, "撤回").PerformClick(); Pump();
        using (var undone = Snapshot(canvas))
            Require(undone.GetPixel(transparentA.X, transparentA.Y).A == 0 && undone.GetPixel(transparentB.X, transparentB.Y).A == 0,
                "透明换色撤回未恢复透明像素。");
        editor.Close();

        using var indexedSource = Source();
        indexedSource.SetPixel(transparentA.X, transparentA.Y, Color.Transparent);
        using var indexed = Show(indexedSource, [Color.Black, Color.Red, Color.Blue]);
        canvas = Named<Control>(indexed, "PixelCanvas");
        replacement = Open(indexed);
        Pick(indexed, replacement, canvas, 0, false, SourcePoints[0]);
        Pick(indexed, replacement, canvas, 0, true, transparentA);
        Require(ColorAt(replacement, "ReplacementTarget", 0).A == 0, "索引资源不能从底图选择透明目标色。");
        Named<Button>(replacement, "ReplacementApply").PerformClick(); Pump();
        using (var result = Snapshot(canvas))
            Require(result.GetPixel(SourcePoints[0].X, SourcePoints[0].Y).A == 0 && result.GetPixel(56, 140).A == 0,
                "索引透明目标没有产生橡皮等效的透明结果。");
        indexed.Close();
    }

    private static Bitmap Source()
    {
        var source = new Bitmap(180, 1000);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(Background);
        for (var index = 0; index < 5; index++)
        {
            source.SetPixel(SourcePoints[index].X, SourcePoints[index].Y, Sources[index]);
            source.SetPixel(56, 140 + index * 4, Sources[index]);
            source.SetPixel(TargetPoints[index].X, TargetPoints[index].Y, Targets[index]);
        }
        source.SetPixel(90, 30, Sources[0]); // Visible canvas gutter is not an editable section.
        return source;
    }

    private static void PickBoundsAndOwnerLifetime()
    {
        using var source = Source();
        using (var editor = Show(source))
        {
            var canvas = Named<Control>(editor, "PixelCanvas");
            Set(canvas, "Zoom", 4d);
            var replacement = Open(editor);
            var before = Pixels(canvas);
            var previous = ColorAt(replacement, "ReplacementSource", 0);
            Named<Button>(replacement, "ReplacementSourcePick0").PerformClick(); Pump();
            foreach (var point in new[] { new Point(-1, 0), new Point(0, -1), new Point(canvas.Width - 1, 0), new Point(0, canvas.Height - 1) })
            {
                MouseAtClient(canvas, MouseButtons.Left, point);
                Require(!replacement.Visible && Get<bool>(canvas, "ReplacementPickActive") &&
                    ColorAt(replacement, "ReplacementSource", 0).ToArgb() == previous.ToArgb() && before.SequenceEqual(Pixels(canvas)),
                    $"负坐标或画布额外边框 {point} 被当作有效底图像素。");
            }
            Mouse(canvas, MouseButtons.Right, SourcePoints[0]);
            Require(replacement.Visible, "无效边界点击后不能取消取色。");
            editor.Close();
            Require(replacement.IsDisposed, "关闭编辑器没有释放换色子窗。");
        }
        foreach (var disposeDirectly in new[] { false, true })
        {
            using var editor = Show(source);
            var canvas = Named<Control>(editor, "PixelCanvas");
            var replacement = Open(editor);
            Named<Button>(replacement, "ReplacementTargetPick4").PerformClick(); Pump();
            Require(!replacement.Visible && Get<bool>(canvas, "ReplacementPickActive"), "关闭生命周期夹具未处于隐藏取色状态。");
            if (disposeDirectly) editor.Dispose(); else editor.Close();
            Pump();
            Require(editor.IsDisposed && replacement.IsDisposed &&
                !Application.OpenForms.Cast<Form>().Contains(replacement),
                "待取色时关闭或释放编辑器遗留隐藏子窗，或回调访问了已释放的窗口。");
        }
    }

    private static PixelEditorDialog Show(Bitmap source, IReadOnlyList<Color>? palette = null)
    {
        var editor = new PixelEditorDialog(source, "取色专项", Sections, palette)
        { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
        editor.Show(); Pump();
        editor.ClientSize = new(1180, 720); Pump();
        return editor;
    }

    private static PixelColorReplaceDialog Open(PixelEditorDialog editor)
    {
        Named<Button>(editor, "OpenColorReplacement").PerformClick(); Pump();
        var dialog = editor.OwnedForms.OfType<PixelColorReplaceDialog>().Single(form => form.Visible);
        dialog.ShowInTaskbar = false; dialog.Opacity = 0;
        return dialog;
    }

    private static void Pick(PixelEditorDialog editor, PixelColorReplaceDialog replacement, Control canvas, int row, bool target, Point point)
    {
        Named<Button>(replacement, (target ? "ReplacementTargetPick" : "ReplacementSourcePick") + row).PerformClick(); Pump();
        Require(!replacement.Visible && editor.Enabled, "底图取色未隐藏配置窗口或主窗仍被禁用。");
        Mouse(canvas, MouseButtons.Left, point); Pump();
        Require(replacement.Visible && !replacement.IsDisposed, "完成取色后没有返回同一配置窗口。");
    }

    private static void SetEnabled(PixelColorReplaceDialog dialog, int row, bool value)
    {
        var swatch = Named<Button>(dialog, "ReplacementSource" + row);
        var table = (TableLayoutPanel)swatch.Parent!;
        var rowIndex = table.GetPositionFromControl(swatch).Row;
        table.Controls.OfType<CheckBox>().Single(check => table.GetPositionFromControl(check).Row == rowIndex).Checked = value;
        Pump();
    }

    private static void ScrollTo(Control canvas, Point pixel)
    {
        var scroll = (ScrollableControl)canvas.Parent!;
        var zoom = Get<double>(canvas, "Zoom");
        scroll.AutoScrollPosition = new(30, Math.Max(0, (int)(pixel.Y * zoom) - scroll.ClientSize.Height / 2)); Pump();
        Require(scroll.AutoScrollPosition.Y < 0, "滚动坐标用例未实际滚动画布。");
        var local = CanvasPoint(canvas, pixel);
        var viewport = scroll.PointToClient(canvas.PointToScreen(local));
        Require(scroll.ClientRectangle.Contains(viewport), "滚动后待取像素仍在可视区外。");
    }

    private static void Mouse(Control canvas, MouseButtons button, Point pixel)
    {
        MouseAtClient(canvas, button, CanvasPoint(canvas, pixel));
    }

    private static void MouseAtClient(Control canvas, MouseButtons button, Point point)
    {
        Invoke(canvas, "OnMouseDown", new MouseEventArgs(button, 1, point.X, point.Y, 0));
        Invoke(canvas, "OnMouseUp", new MouseEventArgs(button, 1, point.X, point.Y, 0));
        Pump();
    }

    private static Point CanvasPoint(Control canvas, Point pixel)
    {
        var zoom = Get<double>(canvas, "Zoom");
        return new((int)Math.Floor((pixel.X + .5) * zoom), (int)Math.Floor((pixel.Y + .5) * zoom));
    }

    private static void SendKey(Control target, Keys key)
    {
        var message = Message.Create(target.Handle, 0x0100, (IntPtr)(int)(key & Keys.KeyCode), IntPtr.Zero);
        var arguments = new object[] { message, key };
        typeof(PixelEditorDialog).GetMethod("ProcessCmdKey", InstanceMethods)!.Invoke(target, arguments);
    }

    private static Color ColorAt(Control dialog, string prefix, int index)
    {
        var swatch = Named<Button>(dialog, prefix + index);
        return swatch.Tag is Color exact ? exact : swatch.BackColor;
    }
    private static Bitmap Snapshot(Control canvas) => (Bitmap)Invoke(canvas, "Snapshot")!;
    private static int[] Pixels(Control canvas) { using var bitmap = Snapshot(canvas); return BitmapPixels(bitmap); }
    private static int[] BitmapPixels(Bitmap bitmap)
    {
        var result = new int[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++) result[y * bitmap.Width + x] = bitmap.GetPixel(x, y).ToArgb();
        return result;
    }

    private static T Named<T>(Control root, string name) where T : Control => Descendants(root).OfType<T>().Single(control => control.Name == name);
    private static Button TextButton(Control root, string text) => Descendants(root).OfType<Button>().Single(button => button.Text == text);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name, InstanceMethods)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, InstanceMethods)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, InstanceMethods)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Pump() { Application.DoEvents(); Application.DoEvents(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
