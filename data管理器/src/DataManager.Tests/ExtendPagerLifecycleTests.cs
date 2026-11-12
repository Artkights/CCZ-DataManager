using System.Diagnostics;
using System.Runtime.CompilerServices;
using DataManager.App;
using DataManager.Core.Domain;

namespace DataManager.Tests;

internal static class ExtendPagerLifecycleTests
{
    public static void Run()
    {
        RunSelectionAndValidation();
        RunRetainedDisposedPager(rebuildFirst: false);
        RunRetainedDisposedPager(rebuildFirst: true);
        Console.WriteLine("EXTEND_PAGER_LIFECYCLE_OK selection/entity/partial-input=retained whole-owner/rebuilt-pager=collected disposed-event=delivered fixture=memory");
    }

    private static void RunSelectionAndValidation()
    {
        var workspace = ExtendHiddenReturnTests.CreateWorkspace(out var effects, out _);
        effects.Data.Rows[0]["槽10特效值"] = 27;
        effects.Data.Rows[1]["槽10特效值"] = 31;
        using var panel = new BusinessEditorPanel(workspace);
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1000, 700) };
        form.Controls.Add(panel); form.Show(); panel.Bind(BusinessPageKind.ExtendData, 0); Drain();
        var editor = panel.ExtendEditor!;
        var pager = Find<ComboBox>(editor, "ExtendPage");
        var value = Find<TextBox>(editor, "ExtendEffectValue0");
        var previous = Find<Button>(editor, "ExtendPreviousPage");
        var next = Find<Button>(editor, "ExtendNextPage");
        TestSupport.Assert(pager.Items.Count == 26 && pager.SelectedIndex == 0 && value.Text == "7" &&
            !previous.Enabled && next.Enabled, "真实拓展分页器初始绑定或按钮状态错误");
        pager.SelectedIndex = 1; Drain();
        TestSupport.Assert(value.Text == "27" && previous.Enabled && next.Enabled &&
            ReferenceEquals(pager, Find<ComboBox>(editor, "ExtendPage")) &&
            ReferenceEquals(value, Find<TextBox>(editor, "ExtendEffectValue0")), "切页改变了控件身份或没有绑定第10槽");
        TestSupport.Assert(editor.BindEntity(1), "正常切换实体失败"); Drain();
        TestSupport.Assert(editor.CurrentId == 1 && pager.SelectedIndex == 1 && value.Text == "31" &&
            ReferenceEquals(pager, Find<ComboBox>(editor, "ExtendPage")), "实体切换没有保留当前分页器或重新绑定字段");
        value.Text = "-"; value.SelectionStart = 1; value.SelectionLength = 0;
        pager.SelectedIndex = 2; Drain();
        TestSupport.Assert(pager.SelectedIndex == 1 && value.Text == "-" && value.SelectionStart == 1 &&
            value.SelectionLength == 0 && editor.HasPendingChanges &&
            Convert.ToInt32(effects.Data.Rows[1]["槽10特效值"]) == 31 && !editor.BindEntity(0) && editor.CurrentId == 1,
            "切页或实体切换绕过了未完成输入校验、丢失输入/光标或修改原字段");
        editor.DiscardPendingChanges(); previous.PerformClick(); Drain();
        TestSupport.Assert(pager.SelectedIndex == 0 && value.Text == "8" && !previous.Enabled && next.Enabled,
            "修复输入后上一页按钮没有恢复正常选择");
        next.PerformClick(); Drain();
        TestSupport.Assert(pager.SelectedIndex == 1 && value.Text == "31", "下一页按钮没有保留原选择行为");
        Console.WriteLine("EXTEND_PAGER_BEHAVIOR_OK real-selection/buttons/entity/rejected-partial-input controls/caret=retained");
    }

    private static void RunRetainedDisposedPager(bool rebuildFirst)
    {
        var retained = CreateRetainedDisposedPager(rebuildFirst);
        for (var attempt = 0; attempt < 8 && (retained.Owner.IsAlive || retained.Editor.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Drain(); }
        TestSupport.Assert(retained.Pager.IsDisposed && retained.Observer.Count == 1 &&
            !retained.Owner.IsAlive && !retained.Editor.IsAlive,
            $"保留已释放拓展分页器仍持有所属页，或Disposed通知异常：rebuild={rebuildFirst}, notifications={retained.Observer.Count}, owner={retained.Owner.IsAlive}, editor={retained.Editor.IsAlive}");
        GC.KeepAlive(retained.Pager);
        Console.WriteLine($"EXTEND_PAGER_DISPOSED_ROOT_OK mode={(rebuildFirst ? "rebuild" : "whole-owner")} retained-disposed-pager=alive owner/editor=collected disposed-event=once");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetainedPager CreateRetainedDisposedPager(bool rebuildFirst)
    {
        var workspace = ExtendHiddenReturnTests.CreateWorkspace(out _, out _);
        var panel = new BusinessEditorPanel(workspace);
        panel.Bind(BusinessPageKind.ExtendData, 0);
        var editor = panel.ExtendEditor!;
        var pager = Find<ComboBox>(editor, "ExtendPage");
        var observer = new DisposeObserver();
        // Deliberately capture the owner: clearing SelectedIndexChanged alone
        // must not leave a Disposed handler as a second route to the page.
        pager.Disposed += (_, _) => { observer.Count++; GC.KeepAlive(panel); };
        var ownerReference = new WeakReference(panel);
        var editorReference = new WeakReference(editor);
        if (rebuildFirst)
        {
            editor.ResetProject();
            var replacement = Find<ComboBox>(editor, "ExtendPage");
            TestSupport.Assert(!panel.IsDisposed && !editor.IsDisposed && pager.IsDisposed && observer.Count == 1 &&
                !ReferenceEquals(pager, replacement) && replacement.SelectedIndex == 0,
                "真实重建没有单独释放旧分页器或没有交付正常Disposed通知");
            replacement.SelectedIndex = 1;
            TestSupport.Assert(editor.BindEntity(1) && replacement.SelectedIndex == 1,
                "旧分页器释放后新分页器不能正常切页/切换实体");
        }
        panel.Dispose();
        return new(pager, ownerReference, editorReference, observer);
    }

    private sealed record RetainedPager(ComboBox Pager, WeakReference Owner, WeakReference Editor, DisposeObserver Observer);
    private sealed class DisposeObserver { public int Count; }
    private static T Find<T>(Control root, string name) where T : Control
        => Descendants(root).OfType<T>().Single(control => control.Name == name);
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void Drain()
    { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 40) { Application.DoEvents(); Thread.Sleep(1); } }
}
