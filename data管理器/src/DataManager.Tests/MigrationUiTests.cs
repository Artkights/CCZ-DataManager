using System.Diagnostics;
using DataManager.App;
using DataManager.Core.Migration;

namespace DataManager.Tests;

internal static class MigrationUiTests
{
    public static void Run()
    {
        TestVirtualReview();
        TestBackgroundProgress();
        TestPreviewCancellation();
        TestCommitCannotCloseEarly();
    }

    private static void TestVirtualReview()
    {
        const int count = 600892;
        var changes = Enumerable.Range(0, count).Select(index => new MigrationChange(
            "table-" + index % 20, "测试表" + index % 20, "数值", index, index, 0,
            index % 101 == 0 ? MigrationChangeKind.Incompatible : index == 40 ? MigrationChangeKind.Ignored : MigrationChangeKind.Changed, "测试说明")).ToArray();
        var plan = new MigrationPlan
        {
            SourceRoot = "source", TargetRoot = "target", SourceProfileId = "ccz-6.6", SourceVariantId = "66-standard-v1",
            TargetProfileId = "ccz-6.6", TargetVariantId = "66-standard-v1", Changes = changes
        };
        var clock = Stopwatch.StartNew();
        using var review = new MigrationReviewDialog(plan) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        review.Show();
        Application.DoEvents();
        var buildMs = clock.ElapsedMilliseconds;
        TestSupport.Assert(review.GridForSmokeTest.VirtualMode && review.GridForSmokeTest.RowCount == count,
            "迁移差异列表必须虚拟显示全部差异");
        TestSupport.Assert(review.Choices.Count == 0, "迁移默认必须全部跳过");
        TestSupport.Assert(Convert.ToInt32(review.GridForSmokeTest.Rows[count - 1].Cells["Source"].Value) == count - 1,
            "迁移虚拟表尾部字段映射错误");
        clock.Restart();
        review.FilterForSmokeTest.SelectedIndex = 1;
        var filtered = review.GridForSmokeTest.RowCount;
        review.SetAllForSmokeTest(MigrationChoice.Overwrite);
        TestSupport.Assert(filtered == (count + 19) / 20, "迁移筛选索引数量错误");
        TestSupport.Assert(review.Choices.Count == Enumerable.Range(0, count).Count(index => changes[index].TableKey == "table-0" && changes[index].Kind is not (MigrationChangeKind.Incompatible or MigrationChangeKind.Ignored)),
            "批量选择必须只修改当前筛选中的可写项");
        TestSupport.Assert(review.Choices.Keys.All(index => changes[index].TableKey == "table-0" && changes[index].Kind is not (MigrationChangeKind.Incompatible or MigrationChangeKind.Ignored)),
            "迁移批量选择包含不可映射或筛选外项目");
        review.FilterForSmokeTest.SelectedIndex = 0;
        TestSupport.Assert(review.GridForSmokeTest.Rows[20].Cells["Choice"].Value is MigrationChoice.Overwrite,
            "切回全部差异后动作丢失或写错索引");
        review.FilterForSmokeTest.SelectedIndex = 1;
        review.SetAllForSmokeTest(MigrationChoice.Skip);
        TestSupport.Assert(review.Choices.Count == 0, "批量跳过没有清除动作");
        // Editing a virtual combo must update the original plan index, not the filtered row number.
        review.GridForSmokeTest.CurrentCell = review.GridForSmokeTest.Rows[1].Cells["Choice"];
        TestSupport.Assert(review.GridForSmokeTest.BeginEdit(true), "迁移动作列无法进入编辑");
        var combo = (ComboBox)review.GridForSmokeTest.EditingControl;
        combo.SelectedValue = MigrationChoice.Merge;
        review.GridForSmokeTest.NotifyCurrentCellDirty(true);
        review.GridForSmokeTest.CommitEdit(DataGridViewDataErrorContexts.Commit);
        review.GridForSmokeTest.EndEdit();
        TestSupport.Assert(review.Choices.Count == 1 && review.Choices.GetValueOrDefault(20) == MigrationChoice.Merge,
            "虚拟下拉编辑没有写入正确的原始差异索引");
        Console.WriteLine($"MIGRATION_UI_VIRTUAL changes={count} build={buildMs}ms filter-batch-edit={clock.ElapsedMilliseconds}ms");
        TestSupport.Assert(buildMs < 15000, "六十万项差异仍存在严重的逐行创建阻塞");
        review.Close();
    }

    private static void TestBackgroundProgress()
    {
        using var owner = CreateOwner();
        using var released = new ManualResetEventSlim();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        var heartbeats = 0;
        var uiThread = Environment.CurrentManagedThreadId;
        timer.Tick += (_, _) => { if (++heartbeats >= 4) released.Set(); };
        timer.Start();
        var workerThread = MigrationProgressDialog.Run(owner, "测试后台进度", (progress, token) =>
        {
            // A burst must replace the latest status, without enqueuing 100,000 UI callbacks.
            for (var index = 0; index < 100000; index++) progress.Report(new MigrationProgress("分析数据", "测试进度", index, 100000));
            if (!released.Wait(TimeSpan.FromSeconds(8), token)) throw new TimeoutException("迁移进度阻塞了 UI 心跳");
            return Environment.CurrentManagedThreadId;
        });
        TestSupport.Assert(workerThread != uiThread && heartbeats >= 4, "迁移工作未在后台执行或进度阻塞 UI");
        Console.WriteLine($"MIGRATION_UI_BACKGROUND heartbeat={heartbeats} worker={workerThread} ui={uiThread}");
    }

    private static void TestPreviewCancellation()
    {
        using var owner = CreateOwner();
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<MigrationProgressDialog>().FirstOrDefault();
            var button = dialog == null ? null : Descendants(dialog).OfType<Button>().SingleOrDefault();
            if (button == null || !button.Enabled) return;
            timer.Stop();
            button.PerformClick();
        };
        timer.Start();
        var cancelled = false;
        try
        {
            MigrationProgressDialog.Run<int>(owner, "测试取消预览", (progress, token) =>
            {
                progress.Report(new MigrationProgress("分析数据", "测试取消"));
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(8));
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("预览取消没有传递到后台任务");
            });
        }
        catch (OperationCanceledException) { cancelled = true; }
        TestSupport.Assert(cancelled, "迁移预览取消没有保持取消语义");
    }

    private static void TestCommitCannotCloseEarly()
    {
        using var owner = CreateOwner();
        using var released = new ManualResetEventSlim();
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        var verified = false;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<MigrationProgressDialog>().FirstOrDefault();
            var button = dialog == null ? null : Descendants(dialog).OfType<Button>().SingleOrDefault();
            if (dialog == null || button == null || button.Enabled) return;
            dialog.Close();
            verified = !dialog.IsDisposed && dialog.Visible;
            timer.Stop();
            released.Set();
        };
        timer.Start();
        MigrationProgressDialog.Run(owner, "测试提交保护", (progress, token) =>
        {
            progress.Report(new MigrationProgress("提交目标", "正在原子替换"));
            if (!released.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("提交阶段取消按钮未禁用");
            TestSupport.Assert(!token.IsCancellationRequested, "提交阶段关闭窗口不应触发取消");
            return true;
        });
        TestSupport.Assert(verified, "提交阶段允许提前关闭进度窗口");
    }

    private static Form CreateOwner()
    {
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(800, 600) };
        form.Show();
        return form;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
}
