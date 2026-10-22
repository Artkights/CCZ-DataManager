using System.Diagnostics;
using System.Reflection;
using DataManager.App;
using DataManager.Images;

namespace DataManager.Tests;

internal static class ResourcePreviewCardTests
{
    public static void Run()
    {
        using var tips = new ToolTip();
        using var form = new Form { Size = new Size(600, 400), Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        using var card = new ResourcePreviewCard(tips);
        form.Controls.Add(card); form.Show(); Application.DoEvents();
        var calls = 0;
        var first = Animation(Color.Red);
        card.Bind("A", "相同资源", _ => { calls++; return Task.FromResult(first); }, true);
        WaitUntil(() => Field<ResourcePreviewAnimation?>(card, "_animation") != null, "关联预览未完成首次解码");
        var timer = Field<System.Windows.Forms.Timer>(card, "_timer");
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(timer, [EventArgs.Empty]);
        var position = Field<int>(card, "_frameIndex");
        card.Bind("A", "另一实体使用相同资源", _ => { calls++; return Task.FromResult(Animation(Color.Blue)); }, true);
        TestSupport.Assert(calls == 1 && ReferenceEquals(first, Field<ResourcePreviewAnimation?>(card, "_animation")) && Field<int>(card, "_frameIndex") == position,
            "同资源重绑重新解码或重置播放进度");
        card.Visible = false;
        TestSupport.Assert(!timer.Enabled, "隐藏的关联预览仍运行播放定时器");
        card.Visible = true;
        TestSupport.Assert(timer.Enabled && Field<int>(card, "_frameIndex") == position, "重新显示预览没有从原进度恢复");

        var stale = new TaskCompletionSource<ResourcePreviewAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<ResourcePreviewAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
        card.Bind("B", "旧请求", _ => stale.Task, true);
        card.Bind("C", "当前请求", _ => current.Task, true);
        var staleAnimation = Animation(Color.Yellow);
        stale.SetResult(staleAnimation);
        WaitUntil(() => IsDisposed(staleAnimation.Frames[0]), "迟到的旧资源解码没有释放位图");
        var currentAnimation = Animation(Color.Green);
        current.SetResult(currentAnimation);
        WaitUntil(() => ReferenceEquals(currentAnimation, Field<ResourcePreviewAnimation?>(card, "_animation")), "当前资源请求未回填");
        TestSupport.Assert(IsDisposed(first.Frames[0]), "替换关联资源后未释放原位图");

        var late = new TaskCompletionSource<ResourcePreviewAnimation>(TaskCreationOptions.RunContinuationsAsynchronously);
        card.Bind("D", "关闭后到达", _ => late.Task, true);
        card.Dispose();
        var lateAnimation = Animation(Color.Purple);
        late.SetResult(lateAnimation);
        WaitUntil(() => IsDisposed(lateAnimation.Frames[0]), "关闭关联预览后迟到的解码没有释放");

        using var queued = new ResourcePreviewCard(tips);
        form.Controls.Add(queued); queued.BringToFront(); Application.DoEvents();
        var queuedAnimation = Animation(Color.Orange);
        queued.Bind("queued", "队列中的结果", _ => Task.FromResult(queuedAnimation), true);
        queued.Dispose();
        Application.DoEvents();
        TestSupport.Assert(IsDisposed(queuedAnimation.Frames[0]), "已排队但尚未显示的解码在关闭时泄漏");
        Console.WriteLine("RESOURCE_PREVIEW_CARD_TESTS_OK");
    }

    private static ResourcePreviewAnimation Animation(Color color)
    {
        var bitmap = new Bitmap(4, 4);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        return new ResourcePreviewAnimation([bitmap, new Bitmap(bitmap)], 1000, "test");
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
        TestSupport.Assert(condition(), failure);
    }
}
