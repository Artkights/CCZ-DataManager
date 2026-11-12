using System.Diagnostics;
using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using DataManager.App;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Images;

namespace DataManager.Tests;

internal static class BusinessPreviewOwnerLifecycleTests
{
    public static void Run()
    {
        RunInactiveEditorInitialization();
        RunOwnerBoundary(ancestorHide: false);
        RunOwnerBoundary(ancestorHide: true);
        RunEditRestoreGuards();
        RunDisposedEditorOwnerRelease();
        ResourcePreviewCardTests.Run();
        Console.WriteLine("BUSINESS_PREVIEW_OWNER_LIFECYCLE_OK deactivate-visible/ancestor-hide tabs late-hidden/late-resumed pixels/timer/cancel binding/text/selection=retained");
    }

    private static void RunDisposedEditorOwnerRelease()
    {
        var retained = CreateDisposedEditorOwner();
        for (var attempt = 0; attempt < 5 && retained.Owner.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Application.DoEvents(); }
        TestSupport.Assert(retained.Combo.IsDisposed && retained.Observer.Triggered && !retained.Owner.IsAlive,
            "保留已 Dispose 的下拉框仍通过事件委托保留整个业务页，或 Disposed 事件未正常触发");
        GC.KeepAlive(retained.Combo);
        Console.WriteLine("BUSINESS_PREVIEW_DISPOSED_COMBO_ROOT_OK retained-disposed-combo=alive owner=collected disposed-event=delivered");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SearchableIdComboBox Combo, WeakReference Owner, DisposeObserver Observer) CreateDisposedEditorOwner()
    {
        var panel = new BusinessEditorPanel(new ProjectWorkspace());
        using var data = new DataTable();
        data.Columns.Add("ID", typeof(int)); data.Columns.Add("攻击范围", typeof(int)); data.Rows.Add(0, 5);
        var field = new TableFieldDefinition("攻击范围", 0, 1, FieldKind.UInt8);
        var document = new TableDocument
        {
            Definition = new("detailed-job-growth", "合成范围", "synthetic.bin", 0, 1, 1, 0, [field], "synthetic"),
            FilePath = string.Empty, Data = data, OriginalFileBytes = [5], OriginalFileHash = string.Empty
        };
        var range = (RangePreviewPicker)typeof(BusinessEditorPanel).GetMethod("CreateEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [document, data.Rows[0], 0, field])!;
        var tab = new TabPage("范围"); tab.Controls.Add(range); panel.DetailTabs.TabPages.Add(tab);
        var combo = range.Combo;
        var observer = new DisposeObserver();
        combo.Disposed += (_, _) => observer.Triggered = true;
        var owner = new WeakReference(panel);
        panel.Dispose();
        return (combo, owner, observer);
    }

    private sealed class DisposeObserver { public bool Triggered; }

    private static void RunInactiveEditorInitialization()
    {
        using var panel = new BusinessEditorPanel(new ProjectWorkspace());
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(480, 300) };
        form.Controls.Add(panel); form.Show(); Drain(); panel.DeactivateView();
        using var data = new DataTable();
        data.Columns.Add("ID", typeof(int)); data.Columns.Add("攻击范围", typeof(int)); data.Rows.Add(0, 5);
        var field = new TableFieldDefinition("攻击范围", 0, 1, FieldKind.UInt8);
        var document = new TableDocument
        {
            Definition = new("detailed-job-growth", "合成范围", "synthetic.bin", 0, 1, 1, 0, [field], "synthetic"),
            FilePath = string.Empty, Data = data, OriginalFileBytes = [5], OriginalFileHash = string.Empty
        };
        var range = (RangePreviewPicker)typeof(BusinessEditorPanel).GetMethod("CreateEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [document, data.Rows[0], 0, field])!;
        TestSupport.Assert(!Field<bool>(range, "_previewActive") && !Field<bool>(range.Combo, "_previewActive") &&
            Field<object?>(range, "_loader") != null && Field<int?>(range, "_requestedId") == null,
            "失活业务页构造新范围编辑器时在挂载前发起了预览");
        var calls = 0;
        range.SetPreviewLoader((id, token) => { calls++; return Task.FromResult<Bitmap?>(Bitmap(Color.Blue)); });
        var tab = new TabPage("新范围"); tab.Controls.Add(range); panel.DetailTabs.TabPages.Add(tab); Drain();
        TestSupport.Assert(calls == 0 && RangeImage(range) == null, "失活业务页挂载新范围编辑器启动了解码");
        panel.ActivateView();
        WaitUntil(() => calls == 1 && RangeImage(range) != null, "新范围编辑器没有在所属业务页激活后恢复预览");
        panel.ActivateView(); Drain();
        TestSupport.Assert(calls == 1, "新范围编辑器重复激活重复解码");
        Console.WriteLine("BUSINESS_PREVIEW_INACTIVE_BIND_OK detached/mounted=zero-requests active=one-request synthetic-base=memory");
    }

    private static void RunOwnerBoundary(bool ancestorHide)
    {
        using var tips = new ToolTip();
        using var panel = new BusinessEditorPanel(new ProjectWorkspace());
        using var parent = new Panel { Dock = DockStyle.Fill };
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1000, 640) };
        var tab = new TabPage("预览");
        var other = new TabPage("其他");
        panel.DetailTabs.TabPages.AddRange([tab, other]);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        tab.Controls.Add(flow);
        var live = Card(tips); var pendingHidden = Card(tips); var pendingResumed = Card(tips);
        var input = new TextBox { Text = "未完成文本-", Width = 120 };
        var rangeCombo = new SearchableIdComboBox();
        rangeCombo.SetOptions([new(5, "范围五")]); rangeCombo.SetBoundValue("5");
        var range = new RangePreviewPicker(rangeCombo) { Dock = DockStyle.None, Size = new Size(220, 90) };
        var popup = new SearchableIdComboBox { Width = 160 };
        popup.SetOptions([new(7, "已加载"), new(8, "隐藏迟到"), new(9, "恢复迟到")]);
        popup.SetBoundValue("7");
        flow.Controls.AddRange([live, pendingHidden, pendingResumed, range, popup, input]);
        parent.Controls.Add(panel); form.Controls.Add(parent); form.Show(); Application.DoEvents(); panel.ActivateView();

        var liveCalls = new List<ResourcePreviewAnimation>();
        var hiddenCalls = new List<AnimationCall>(); var resumedCalls = new List<AnimationCall>();
        var rangeCalls = new List<BitmapCall>(); var popupCalls = new List<BitmapCall>();
        live.Bind("stable-resource", "动画", _ =>
        {
            var animation = Animation(Color.Blue); liveCalls.Add(animation); return Task.FromResult(animation);
        }, true);
        pendingHidden.Bind("hidden-late", "隐藏迟到", token => AddAnimationCall(hiddenCalls, token), true);
        pendingResumed.Bind("resumed-late", "恢复迟到", token => AddAnimationCall(resumedCalls, token), true);
        range.SetPreviewLoader((id, token) => AddBitmapCall(rangeCalls, id, token));
        popup.SetPreviewLoader((id, token) => AddBitmapCall(popupCalls, id, token));
        WaitUntil(() => live.HasLoadedResource && hiddenCalls.Count == 1 && resumedCalls.Count == 1 && rangeCalls.Count == 1,
            "业务预览初始请求未启动");
        var timer = Field<System.Windows.Forms.Timer>(live, "_timer");
        Tick(timer);
        var position = Field<int>(live, "_frameIndex");
        TestSupport.Assert(position == 1 && timer.Enabled, "业务动画验收未建立活动播放进度");
        var rangeBefore = rangeCalls[0].Complete(Color.Blue);
        RequestThumbnail(popup, 7);
        var popupBefore = popupCalls.Single(call => call.Id == 7).Complete(Color.Blue);
        WaitUntil(() => RangeImage(range) != null && Thumbnails(popup).Count == 1, "业务范围/候选图像没有回填");
        rangeCombo.SetBoundValue("-"); rangeCombo.SelectionStart = 1; rangeCombo.SelectionLength = 0;
        popup.SetBoundValue("7");
        RequestThumbnail(popup, 8); RequestThumbnail(popup, 9);
        var hiddenPopup = popupCalls.Single(call => call.Id == 8);
        var resumedPopup = popupCalls.Single(call => call.Id == 9);
        Drain();
        var popupText = popup.Text;
        var popupSelectionStart = popup.SelectionStart;
        var popupSelectionLength = popup.SelectionLength;
        var beforeCount = liveCalls.Count + hiddenCalls.Count + resumedCalls.Count + rangeCalls.Count + popupCalls.Count;
        void Stop() { if (ancestorHide) parent.Visible = false; else panel.DeactivateView(); }
        void Resume() { if (ancestorHide) parent.Visible = true; else panel.ActivateView(); }
        Stop();
        TestSupport.Assert((ancestorHide ? !panel.Visible : panel.Visible) &&
            hiddenCalls[0].Token.IsCancellationRequested && resumedCalls[0].Token.IsCancellationRequested &&
            hiddenPopup.Token.IsCancellationRequested && resumedPopup.Token.IsCancellationRequested &&
            !timer.Enabled && Field<object?>(live, "_pixelLease") == null && RangeImage(range) == null && Thumbnails(popup).Count == 0,
            "业务页失活没有取消请求、停动画或释放预览租约");
        TestSupport.Assert(liveCalls[0].Frames.All(Released) && Released(rangeBefore) && Released(popupBefore),
            "业务页失活仍持有已加载的原生像素");
        Stop(); Tick(timer); range.RefreshPreview(); RequestThumbnail(popup, 7); form.Width++; form.Width--;
        Drain();
        TestSupport.Assert(beforeCount == liveCalls.Count + hiddenCalls.Count + resumedCalls.Count + rangeCalls.Count + popupCalls.Count,
            "失活但仍可见的业务预览因刷新、绘制或定时器重新请求资源");
        var lateHidden = hiddenCalls[0].Complete(Color.Red);
        var latePopupHidden = hiddenPopup.Complete(Color.Red);
        WaitUntil(() => lateHidden.Frames.All(Released) && Released(latePopupHidden), "隐藏期间到达的旧结果没有释放");

        Resume();
        WaitUntil(() => live.HasLoadedResource && liveCalls.Count == 2 && hiddenCalls.Count == 2 &&
            resumedCalls.Count == 2 && rangeCalls.Count == 2, "业务页恢复没有按原资源重新读取");
        hiddenCalls[1].Complete(Color.Lime); resumedCalls[1].Complete(Color.Lime);
        rangeCalls[1].Complete(Color.Lime);
        RequestThumbnail(popup, 9); popupCalls.Last(call => call.Id == 9).Complete(Color.Lime);
        WaitUntil(() => pendingHidden.HasLoadedResource && pendingResumed.HasLoadedResource &&
            RangeImage(range) != null && Thumbnails(popup).ContainsKey(9), "业务页恢复的新结果未回填");
        var lateResumed = resumedCalls[0].Complete(Color.Red);
        var latePopupResumed = resumedPopup.Complete(Color.Red);
        WaitUntil(() => lateResumed.Frames.All(Released) && Released(latePopupResumed), "恢复后到达的旧结果没有释放");
        TestSupport.Assert(timer.Enabled && Field<int>(live, "_frameIndex") == position &&
            Field<string?>(live, "_key") == "stable-resource" && input.Text == "未完成文本-" &&
            rangeCombo.Text == "-" && rangeCombo.SelectionStart == 1 && rangeCombo.SelectionLength == 0 &&
            rangeCalls[1].Id == 5 && popup.Text == popupText &&
            popup.SelectionStart == popupSelectionStart && popup.SelectionLength == popupSelectionLength &&
            RangeImage(range)!.GetPixel(0, 0).ToArgb() == Color.Lime.ToArgb() &&
            Thumbnails(popup)[9]!.GetPixel(0, 0).ToArgb() == Color.Lime.ToArgb(),
            $"恢复改变了播放位置、绑定、未完成输入/选择，或旧请求覆盖了新图像：timer={timer.Enabled}, frame={Field<int>(live, "_frameIndex")}/{position}, key={Field<string?>(live, "_key")}, input={input.Text}, range={rangeCombo.Text}, selection={rangeCombo.SelectionStart}/{rangeCombo.SelectionLength}, range-id={rangeCalls[1].Id}, popup={popup.Text}/{popupText}, popup-selection={popup.SelectionStart}:{popup.SelectionLength}/{popupSelectionStart}:{popupSelectionLength}, range-pixel={RangeImage(range)?.GetPixel(0, 0).ToArgb()}, popup-pixel={Thumbnails(popup)[9]?.GetPixel(0, 0).ToArgb()}");
        var resumedCount = liveCalls.Count + hiddenCalls.Count + resumedCalls.Count + rangeCalls.Count + popupCalls.Count;
        Resume(); Drain();
        TestSupport.Assert(resumedCount == liveCalls.Count + hiddenCalls.Count + resumedCalls.Count + rangeCalls.Count + popupCalls.Count,
            "重复激活重新读取了相同资源");

        panel.DetailTabs.SelectedTab = other; Drain();
        TestSupport.Assert(!timer.Enabled && RangeImage(range) == null && Thumbnails(popup).Count == 0,
            "业务子页切走后预览仍活动");
        panel.DetailTabs.SelectedTab = tab;
        WaitUntil(() => live.HasLoadedResource && liveCalls.Count == 3 && rangeCalls.Count == 3,
            "返回业务子页没有恢复预览");
        rangeCalls[2].Complete(Color.Lime);
        foreach (var call in hiddenCalls.Concat(resumedCalls).Where(call => !call.Completed)) call.Complete(Color.Lime);
        Drain();
        panel.Dispose();
        TestSupport.Assert(Field<List<Control>>(panel, "_previewAncestors").Count == 0 && !timer.Enabled &&
            Field<object?>(live, "_loader") == null && Field<object?>(popup, "_previewLoader") == null &&
            Field<object?>(range, "_loader") == null, "业务页释放后仍保留祖先订阅或预览loader");
        Console.WriteLine($"BUSINESS_PREVIEW_BOUNDARY_OK mode={(ancestorHide ? "ancestor-hide" : "inactive-visible")} animation-resume={position} partial-range='-' last-valid=5 late-results=disposed tabs=restored");
    }

    private static void RunEditRestoreGuards()
    {
        using var combo = new SearchableIdComboBox();
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false };
        combo.SetOptions([new(7, "已加载")]); form.Controls.Add(combo); form.Show(); Drain();
        combo.SetBoundValue("7"); Drain();
        combo.SetPreviewActive(false);
        combo.Text = "7：已加载";
        combo.SetPreviewActive(true);
        typeof(SearchableIdComboBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(combo, [new KeyEventArgs(Keys.Left)]);
        combo.Text = "用户新输入-"; combo.SelectionStart = 2; combo.SelectionLength = 1;
        Drain();
        TestSupport.Assert(combo.Text == "用户新输入-" && combo.SelectionStart == 2 && combo.SelectionLength == 1,
            "恢复回调覆盖了用户在重新显示后的新输入或选择");
        combo.SetBoundValue("7"); Drain();
        combo.SetPreviewActive(false);
        combo.SetBoundValue("新的主动绑定-");
        combo.SetPreviewActive(true); Drain();
        TestSupport.Assert(combo.Text == "新的主动绑定-" && combo.SelectionStart == combo.Text.Length && combo.SelectionLength == 0,
            "恢复回调覆盖了失活期间的主动绑定");
        combo.SetBoundValue("7"); Drain();
        combo.SetPreviewActive(false); combo.SetPreviewActive(true);
        combo.Text = "直接新输入-"; combo.SelectionStart = 2; combo.SelectionLength = 1;
        Drain();
        TestSupport.Assert(combo.Text == "直接新输入-" && combo.SelectionStart == 2 && combo.SelectionLength == 1,
            $"恢复回调覆盖了恢复后直接Text赋值：text={combo.Text},selection={combo.SelectionStart}:{combo.SelectionLength}");
        Console.WriteLine("BUSINESS_PREVIEW_EDIT_RESTORE_GUARDS_OK newer-user-edit/newer-binding/direct-text=retained");
    }

    private static ResourcePreviewCard Card(ToolTip tips) => new(tips)
        { Dock = DockStyle.None, Size = new Size(180, 150) };
    private static Task<ResourcePreviewAnimation> AddAnimationCall(List<AnimationCall> calls, CancellationToken token)
    { var call = new AnimationCall(token); calls.Add(call); return call.Task; }
    private static Task<Bitmap?> AddBitmapCall(List<BitmapCall> calls, int id, CancellationToken token)
    { var call = new BitmapCall(id, token); calls.Add(call); return call.Task; }
    private static ResourcePreviewAnimation Animation(Color color)
    { var bitmap = Bitmap(color); return new([bitmap, new Bitmap(bitmap)], 2000, "controlled"); }
    private static Bitmap Bitmap(Color color)
    { var bitmap = new Bitmap(4, 4); using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color); return bitmap; }
    private static void RequestThumbnail(SearchableIdComboBox combo, int id)
        => typeof(SearchableIdComboBox).GetMethod("RequestThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(combo, [id]);
    private static void Tick(System.Windows.Forms.Timer timer)
        => typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timer, [EventArgs.Empty]);
    private static Bitmap? RangeImage(RangePreviewPicker range) => Field<Bitmap?>(range.PreviewBox, "_image");
    private static Dictionary<int, Bitmap?> Thumbnails(SearchableIdComboBox combo) => Field<Dictionary<int, Bitmap?>>(combo, "_thumbnails");
    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static bool Released(Bitmap bitmap)
    {
        var field = typeof(Image).GetField("_nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(Image).GetField("nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var value = field.GetValue(bitmap);
        var handle = value is IntPtr address ? address :
            (IntPtr)typeof(Pointer).GetMethod("GetPointerValue", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
        return handle == IntPtr.Zero;
    }
    private static void WaitUntil(Func<bool> predicate, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(1); }
        TestSupport.Assert(predicate(), failure);
    }
    private static void Drain()
    { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 60) { Application.DoEvents(); Thread.Sleep(1); } }

    private sealed class AnimationCall(CancellationToken token)
    {
        private readonly TaskCompletionSource<ResourcePreviewAnimation> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => token;
        public Task<ResourcePreviewAnimation> Task => _completion.Task;
        public bool Completed => _completion.Task.IsCompleted;
        public ResourcePreviewAnimation Complete(Color color)
        { var animation = Animation(color); _completion.SetResult(animation); return animation; }
    }
    private sealed class BitmapCall(int id, CancellationToken token)
    {
        private readonly TaskCompletionSource<Bitmap?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => id;
        public CancellationToken Token => token;
        public Task<Bitmap?> Task => _completion.Task;
        public Bitmap Complete(Color color)
        { var bitmap = Bitmap(color); _completion.SetResult(bitmap); return bitmap; }
    }
}
