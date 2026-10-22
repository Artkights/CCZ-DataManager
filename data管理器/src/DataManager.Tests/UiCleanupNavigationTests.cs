using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using DataManager.App;
using DataManager.Images;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class UiCleanupNavigationTests
{
    public static void Run(string source)
    {
        TestSupport.Assert(new[] { 0, 1, 2, 3, 10 }.Select(ResourceCatalogAdapter.HalfBodyImageNumber).SequenceEqual([1, 9, 10, 11, 18]),
            "头像逻辑号到半身像物理号映射错误");
        CheckSingleFrameIsolation();
        var root = TestSupport.CreateCoreCopy(source);
        using var form = new MainForm(false)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
            ShowInTaskbar = false, Size = new Size(1280, 720)
        };
        try
        {
            WriteArchive(Path.Combine(root, "Pmapobj.e5"), [Png(Color.Red, 48, 1280), Png(Color.Blue, 48, 1280)]);
            WriteArchive(Path.Combine(root, "Fb.e5"), Enumerable.Range(0, 18).Select(index => Png(Color.FromArgb(255, index * 11, 180, 80), 120, 240)).ToArray());
            form.Show(); form.LoadProjectForSmokeTest(root); Application.DoEvents();
            CheckSinglePage(form.BusinessEditorForSmokeTest, form.WorkspaceForSmokeTest.ActiveProfile.ProfileId == "ccz-6.6");
            form.SelectModuleForSmokeTest("兵种系");
            var series = form.BusinessEditorForSmokeTest;
            TestSupport.Assert(series.DetailTabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(["兵种属性", "辅助分组"]),
                "兵种系没有合并属性与地形页");
            var terrain = Descendants(series).Single(control => control.Name == "JobSeriesTerrain");
            TestSupport.Assert(Descendants(terrain).OfType<NumericUpDown>().Count() == 60 &&
                !Descendants(terrain).OfType<DataGridView>().Any(), "地形发挥与消耗没有使用统一数值编辑器");
            var terrainEditors = Descendants(terrain).OfType<NumericUpDown>().ToArray();
            form.EntityListForSmokeTest.SelectedIndex = 20;
            TestSupport.Assert(terrainEditors.SequenceEqual(Descendants(terrain).OfType<NumericUpDown>()), "切换兵种系重建地形编辑器");
            var terrainDoc = form.WorkspaceForSmokeTest.GetDocument("job-series-terrain");
            var before = Convert.ToInt32(terrainDoc.Data.Rows[20][terrainDoc.Definition.Fields[0].Name]);
            var unrelated = terrainDoc.Data.Rows[0][terrainDoc.Definition.Fields[0].Name];
            terrainEditors[0].Value = before == 255 ? 254 : before + 1;
            TestSupport.Assert(series.Flush() && Convert.ToInt32(terrainDoc.Data.Rows[20][terrainDoc.Definition.Fields[0].Name]) != before &&
                Equals(terrainDoc.Data.Rows[0][terrainDoc.Definition.Fields[0].Name], unrelated), "合并地形编辑没有只修改当前兵种系");
            form.UndoDraftForSmokeTest();
            form.SelectModuleForSmokeTest("兵种");
            var permissions = Descendants(form.BusinessEditorForSmokeTest).OfType<TableLayoutPanel>().Single(table => table.Name == "EquipmentPermissions");
            for (var column = 0; column < 4; column++) TestSupport.Assert(permissions.GetControlFromPosition(column, 5) == null, "武器与防具之间没有完整空白行");
            foreach (var check in permissions.Controls.OfType<CheckBox>())
            {
                var column = permissions.GetColumn(check);
                TestSupport.Assert(check.Name.StartsWith(column % 2 == 0 ? "普通" : "特殊", StringComparison.Ordinal), "普通/特殊许可列顺序错误");
            }
            form.SelectModuleForSmokeTest("宝物");
            TestSupport.Assert(Descendants(form.BusinessEditorForSmokeTest).Any(control => control.Name == "EquipmentTypeEditor"), "宝物右侧没有装备类型编辑器");
            TestSupport.Assert(!Descendants(form.BusinessEditorForSmokeTest).OfType<GroupBox>().Any(group => group.Text == "类别与字段说明"), "宝物冗余说明仍占用布局");
            var images = Descendants(form).OfType<IntegratedResourcePanel>().Single();
            form.SelectModuleForSmokeTest("__resources");
            Wait(() => images.LoadingTask.IsCompleted, "首次形象目录读取超时");
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count > 0, "首次形象页没有左侧分类");
            var selected = form.EntityListForSmokeTest.Items.Cast<object>().Select((item, index) => (item, index))
                .First(pair => pair.item.ToString()!.Contains("头像", StringComparison.Ordinal)).index;
            form.EntityListForSmokeTest.SelectedIndex = selected;
            var remembered = form.EntityListForSmokeTest.SelectedItem!.ToString();
            Wait(() => images.LoadingTask.IsCompleted, "头像分类读取超时");
            for (var cycle = 0; cycle < 6; cycle++)
            {
                form.SelectModuleForSmokeTest("人物");
                form.SelectModuleForSmokeTest("__resources");
                Application.DoEvents();
                TestSupport.Assert(form.EntityListForSmokeTest.Items.Count > 0 && form.EntityListForSmokeTest.SelectedItem!.ToString() == remembered,
                    "返回缓存形象页丢失导航或分类选择");
            }
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "页面清理或导航产生意外草稿");
            Console.WriteLine("UI_CLEANUP_NAVIGATION_TESTS_OK single-tab/terrain/equipment/cached-navigation/frame-isolation");
        }
        finally
        {
            while (form.WorkspaceForSmokeTest.Commands.CanUndo) form.WorkspaceForSmokeTest.Commands.Undo();
            form.BusinessEditorForSmokeTest.DiscardPendingChanges(); form.Close();
            Directory.Delete(root, true);
        }
    }

    private static void CheckSinglePage(BusinessEditorPanel panel, bool is66)
    {
        var tabs = panel.DetailTabs;
        Application.DoEvents();
        TestSupport.Assert(tabs.TabCount == 1 && tabs.SelectedTab!.Top <= 2, "人物单页仍为重复页签标题保留空间");
        TestSupport.Assert(!Descendants(panel).OfType<GroupBox>().Any(group => group.Text is "人物与初始装备" or "人物形象指定" or "人物列传" or "暴击与撤退台词"),
            "人物冗余分组标题仍可见");
        var cards = Descendants(panel).OfType<ResourcePreviewCard>().OrderBy(card => card.PointToScreen(Point.Empty).X).ToArray();
        var expected = is66 ? new[] { "头像", "半身像", "R ", "S " } : new[] { "头像", "R ", "S " };
        TestSupport.Assert(cards.Length == expected.Length && cards.Select((card, index) => card.Text.StartsWith(expected[index], StringComparison.Ordinal) || expected[index] == "S " && card.Text == "默认S").All(matches => matches),
            "人物预览顺序或数量错误");
    }

    private static void CheckSingleFrameIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerFacePreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Face.e5");
            WriteArchive(path, [Png(Color.Orange, 12, 16)]);
            var profile = new ProjectProfile(root, "fixture", ProjectVersion.Ccz66, false, new Dictionary<string, string>(), []);
            var first = new FrameDescriptor("Face.e5", path, 1, 0, 1, "face-small", "none", 12, 16);
            var invalid = first with { SourcePath = Path.Combine(root, "missing.e5"), ImageNumber = 2, FrameIndex = 1 };
            var asset = new LogicalImageAsset("Face", 0, "八表情", ImageCategory.Portrait, ResourceKind.FaceComposite, [first, invalid], [], true, false, "fixture");
            using var frame = new FaceResourceCodec().DecodePreviewFrame(profile, asset, first);
            TestSupport.Assert(frame?.Bitmap.Size == new Size(12, 16), "静态头像预览仍解码无关表情");
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Png(Color color, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static void WriteArchive(string path, IReadOnlyList<byte[]> payloads)
    {
        var first = 0x110 + payloads.Count * 12;
        var bytes = new byte[first + payloads.Sum(payload => payload.Length)]; var offset = first;
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(0x110 + index * 12, 12);
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)payloads[index].Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)payloads[index].Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void Wait(Func<bool> completed, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(20)) { Application.DoEvents(); Thread.Sleep(5); }
        TestSupport.Assert(completed(), message); Application.DoEvents();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
