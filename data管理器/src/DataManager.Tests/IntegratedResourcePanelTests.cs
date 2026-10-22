using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Diagnostics;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Models;
using DataManager.Images;
using ImageManager.App;

namespace DataManager.Tests;

internal static class IntegratedResourcePanelTests
{
    public static void Run(string basePath)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            // A small valid R archive keeps this control test independent of
            // the original resource directory and its gallery decode cost.
            WriteRArchive(Path.Combine(root, "Pmapobj.e5"), 3);
            using (var panel = new IntegratedResourcePanel())
            {
                panel.LoadProject(root, "Animation");
                WaitLoaded(panel);
                TestSupport.Assert(Descendants(panel).OfType<Button>().Any(button => button.Text == "播放动作"),
                    "动画分类没有启用原图库的播放动作按钮");
                panel.ShowCategory("R");
                WaitLoaded(panel);
                AssertUnverified(panel, "没有引用provider的R图库");

                panel.LoadProject(root, "R", category => category == "R" ? new HashSet<int> { 1 } : null);
                WaitLoaded(panel);
                var idle = Descendants(panel).OfType<CheckBox>().Single(check => check.Text == "空闲");
                var gallery = Descendants(panel).OfType<VirtualImageGallery>().Single();
                TestSupport.Assert(gallery.ItemCount == 3, "空闲筛选关闭时必须展示全部R形象");
                idle.Checked = true;
                TestSupport.Assert(gallery.ItemCount == 2, "provider的已用编号没有传入原图库空闲筛选");
                gallery.SelectAll(true);
                TestSupport.Assert(gallery.SelectedItems.Select(asset => asset.LogicalId).OrderBy(id => id).SequenceEqual([0, 2]),
                    "空闲筛选移除了错误的R编号");
                var rPage = Descendants(panel).OfType<ImageGridPage>().Single();
                Search(panel).Text = "0";
                panel.ShowCategory("S");
                WaitLoaded(panel);
                AssertUnverified(panel, "provider未验证的S图库");
                Search(panel).Text = "S独立搜索";
                panel.ShowCategory("R");
                WaitLoaded(panel);
                TestSupport.Assert(ReferenceEquals(rPage, Descendants(panel).OfType<ImageGridPage>().Single()) &&
                    Search(panel).Text == "0" && idle.Checked,
                    "分类切换没有复用页面并保留独立搜索/筛选");
                Search(panel).Text = string.Empty;
                panel.LoadProject(root, "R", category => category == "R" ? new HashSet<int> { 0, 1 } : null);
                WaitLoaded(panel);
                TestSupport.Assert(ReferenceEquals(rPage, Descendants(panel).OfType<ImageGridPage>().Single()) && gallery.ItemCount == 1 &&
                    gallery.SelectedItems.Single().LogicalId == 2, "相同项目的引用更新重建了页面或没有更新空闲筛选");
                panel.LoadProject(root, "Animation", forceReload: true);
                panel.ClearProject();
                var cleared = Stopwatch.StartNew();
                while (cleared.ElapsedMilliseconds < 120) { Application.DoEvents(); Thread.Sleep(5); }
                TestSupport.Assert(!Descendants(panel).OfType<ImageGridPage>().Any() && rPage.IsDisposed,
                    "清理项目后旧后台结果重新挂回图库，或旧分类页仍存活");
                panel.LoadProject(root, "R");
                WaitLoaded(panel);
                TestSupport.Assert(Descendants(panel).OfType<VirtualImageGallery>().Single().ItemCount == 3,
                    "清理后的项目无法重新装载资源");
            }

            // Exercise the real MainForm callback. It must use the evidence-
            // gated workspace documents rather than the legacy image reader.
            using var form = new MainForm(openDefaultProject: false);
            form.LoadProjectForSmokeTest(root);
            TestSupport.Assert(form.WorkspaceForSmokeTest.GetDocument("person-r-assign").Definition.Capability == TableCapability.Editable,
                "6.5控件烟测需要已验证的人物R指定表");
            form.SelectModuleForSmokeTest("__resources");
            var images = Descendants(form).OfType<IntegratedResourcePanel>().Single();
            images.ShowCategory("R");
            WaitLoaded(images);
            var rGallery = Descendants(images).OfType<VirtualImageGallery>().Single();
            var count = rGallery.ItemCount;
            var used = form.WorkspaceForSmokeTest.GetDocument("person-r-assign").Data.Rows.Cast<DataRow>()
                .Select(row => Convert.ToInt32(row["R形象ID"], CultureInfo.InvariantCulture)).Where(id => id >= 0 && id < count).Distinct().Count();
            TestSupport.Assert(used > 0, "真实6.5样本未提供测试R档案中的任何已用编号");
            Descendants(images).OfType<CheckBox>().Single(check => check.Text == "空闲").Checked = true;
            TestSupport.Assert(rGallery.ItemCount == count - used, "MainForm没有把已验证6.5 R引用传入图库");

            // Use an explicit no-evidence fixture for the negative 6.6 case.
            // The real 6.6 consumer probe is allowed to enable R/S when its
            // PE signatures and section mapping are present; opening a 6.5
            // copy under a 6.6 label must not accidentally turn that into a
            // stale "always unavailable" assertion.
            LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(root);
            form.WorkspaceForSmokeTest.Open(root, "ccz-6.6", "66-standard-v1");
            TestSupport.Assert(!form.WorkspaceForSmokeTest.Documents.ContainsKey("person-r-assign"),
                "6.6缺证时不应加载继承R指定区域");
            form.SelectModuleForSmokeTest("__scan");
            form.SelectModuleForSmokeTest("__resources");
            images.ShowCategory("R");
            WaitLoaded(images);
            AssertUnverified(images, "6.6缺证R图库");
            images.ShowCategory("S");
            WaitLoaded(images);
            AssertUnverified(images, "6.6缺证S图库");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WaitLoaded(IntegratedResourcePanel panel)
    {
        var watch = Stopwatch.StartNew();
        while (!panel.LoadingTask.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("集成资源后台装载超时");
            Application.DoEvents(); Thread.Sleep(5);
        }
        panel.LoadingTask.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static TextBox Search(Control panel)
        => Descendants(panel).OfType<TextBox>().Single(box => box.PlaceholderText == "输入编号或名称筛选资源");

    private static void AssertUnverified(IntegratedResourcePanel panel, string context)
    {
        TestSupport.Assert(!Descendants(panel).OfType<CheckBox>().Any(check => check.Text == "空闲"),
            context + "仍显示不能验证的空闲筛选");
        TestSupport.Assert(Descendants(panel).OfType<Label>().Any(label => label.Text.Contains("引用未验证", StringComparison.Ordinal)),
            context + "没有说明引用证据缺失");
    }

    private static void WriteRArchive(string path, int logicalCount)
    {
        const int stripSize = 48 * 64 * 20;
        var count = logicalCount * 2;
        var first = 0x110 + count * 12;
        var bytes = new byte[first + count * stripSize];
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.AsSpan(0x110 + index * 12, 12);
            BinaryPrimitives.WriteUInt32BigEndian(entry, stripSize);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], stripSize);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)(first + index * stripSize));
        }
        File.WriteAllBytes(path, bytes);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
