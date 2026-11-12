using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using DataManager.App;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

/// <summary>Exercises numeric portrait/R/S pickers and navigation on an isolated real project.</summary>
internal static class PersonImageIntegrationTests
{
    internal static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var root = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        using var adapter = new ResourceCatalogAdapter();
        var layouts = new List<object>();
        try
        {
            foreach (var path in adapter.Scan(source).Resources
                .Where(resource => resource.Key is "Face" or "Tou" or "Pmapobj.e5" or "SComposite")
                .SelectMany(resource => resource.Paths).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(source, path);
                TestSupport.Assert(!relative.StartsWith("..", StringComparison.Ordinal), "资源路径超出测试基底。");
                var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination, true);
            }
            var hashes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            foreach (var factor in new[] { 1f, 1.25f, 1.5f })
            {
                using var defaultControl = new Control();
                using var font = new Font(defaultControl.Font.FontFamily, defaultControl.Font.SizeInPoints * factor);
                using var form = new MainForm(false)
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                    ShowInTaskbar = false, MaximumSize = new Size(4096, 2160), Font = font
                };
                form.Show(); form.LoadProjectForSmokeTest(root); form.SelectModuleForSmokeTest("人物");
                var panel = form.BusinessEditorForSmokeTest;
                foreach (var size in new[] { new Size(1100, 650), new Size(1280, 720), new Size(1920, 1080) })
                {
                    form.Size = size; Pump();
                    layouts.Add(new { WindowWidth = form.Width, WindowHeight = form.Height, ClientWidth = form.ClientSize.Width,
                        ClientHeight = form.ClientSize.Height, form.DeviceDpi, FontPoints = form.Font.SizeInPoints, FontFactor = factor });
                    using (var diagnosticImage = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(diagnosticImage, new Rectangle(Point.Empty, form.Size));
                        diagnosticImage.Save(Path.Combine(output, $"人物-{size.Width}x{size.Height}-字号{factor * 100:0}.png"), ImageFormat.Png);
                    }
                    var columns = Descendants(panel).OfType<TableLayoutPanel>().Single(control => control.Name == "PersonPreviewColumns");
                    var cards = Descendants(columns).OfType<ResourcePreviewCard>().ToArray();
                    TestSupport.Assert(columns.ColumnCount == 3 && cards.Length == 3 &&
                        !Descendants(panel).Any(control => control.Text.StartsWith("半身像", StringComparison.Ordinal)),
                        "人物形象未统一为头像/R/S三列。");
                    var widths = columns.GetColumnWidths();
                    TestSupport.Assert(widths.Max() - widths.Min() <= 2, "人物图片三列没有等宽。");
                    foreach (var button in Descendants(columns).OfType<Button>())
                    {
                        TestSupport.Assert(button.Visible && button.ClientSize.Width >= button.GetPreferredSize(Size.Empty).Width - 2 &&
                            button.ClientSize.Height >= button.GetPreferredSize(Size.Empty).Height - 2,
                            $"定位按钮文字被裁切：{button.Text}/{button.Size}/{button.GetPreferredSize(Size.Empty)}。");
                        AssertInside(button, panel);
                        var header = button.Parent!;
                        var combo = Descendants(header).OfType<SearchableIdComboBox>().Single();
                        TestSupport.Assert(combo.IdOnly && combo.DrawMode == DrawMode.OwnerDrawVariable && combo.Width >= 50,
                            "人物下拉框没有保留纯编号与缩略图，或编号输入空间过小。");
                        TestSupport.Assert(!Screen(combo).IntersectsWith(Screen(button)), "编号框和定位按钮重叠。");
                        AssertInside(combo, panel);
                    }
                    Wait(() => cards.All(card => card.HasLoadedResource), "人物初始图片未完成加载。");
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(output, $"人物-{size.Width}x{size.Height}-字号{factor * 100:0}.png"), ImageFormat.Png);
                }
                if (factor == 1f)
                {
                    CheckPickers(panel, Field<ResourceCatalogAdapter>(form, "_resourceAdapter"), root);
                    CheckLocation(form, "Face", "LocatePersonFaceImage", "persons", "头像");
                    CheckLocation(form, "R", "LocatePersonRImage", "person-r-assign", "R形象ID");
                }
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty && !panel.HasPendingChanges,
                    "浏览图片、输入恢复或定位产生了未保存修改。");
                if (factor == 1f) CheckProjectSwitch(form, root, source);
                form.Dispose();
            }
            TestSupport.Assert(hashes.All(pair => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)),
                "图片浏览或定位改变了游戏文件。");
            File.WriteAllText(Path.Combine(output, "人物布局元数据.json"), System.Text.Json.JsonSerializer.Serialize(layouts,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PERSON_IMAGE_INTEGRATION_OK three-columns dropdown-thumbnails typed-id missing-id cache-refresh Face/R-location DPI-font-layout");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckPickers(BusinessEditorPanel panel, ResourceCatalogAdapter adapter, string root)
    {
        foreach (var (category, kind, prefix) in new[]
        {
            ("Face", ResourceBindingKind.Portrait, "头像 "),
            ("R", ResourceBindingKind.CharacterR, "R "),
            ("S", ResourceBindingKind.CharacterS, "S ")
        })
        {
            var button = Descendants(panel).OfType<Button>().Single(item => item.Name == "LocatePerson" + category + "Image");
            var combo = Descendants(button.Parent!).OfType<SearchableIdComboBox>().Single();
            if (!combo.Enabled) continue;
            Wait(() => combo.Options.Count > 1, category + "下拉选项未加载。");
            var original = combo.Text;
            var valid = combo.Options.First(item => item.Id.ToString() != original).Id;
            var loader = Field<Func<int, CancellationToken, Task<Bitmap?>>>(combo, "_previewLoader");
            using (var thumb = loader(valid, CancellationToken.None).GetAwaiter().GetResult())
                TestSupport.Assert(thumb != null && thumb.Width <= 48 && thumb.Height <= 48, category + "缺少按比例下拉缩略图。");
            var card = Descendants(panel).OfType<ResourcePreviewCard>().Single(item => item.Text.StartsWith(prefix, StringComparison.Ordinal));
            combo.Text = valid.ToString();
            Wait(() => card.Text == prefix + valid && card.HasLoadedResource, category + "键入编号没有即时预览。");
            var preview = Descendants(card).OfType<PictureBox>().Single();
            var last = preview.Image;
            foreach (var invalid in new[] { "", "-1", "2147483647", "检索" })
            {
                combo.Text = invalid; Pump();
                TestSupport.Assert(ReferenceEquals(preview.Image, last), category + "无效中间输入覆盖了最后有效预览。");
            }
            // A valid UInt16/portrait-byte ID with no image retains its literal number.
            var missing = kind == ResourceBindingKind.Portrait ? 255 : 65534;
            if (!combo.Options.Any(item => item.Id == missing))
            {
                combo.Text = missing.ToString();
                Wait(() => card.Text == prefix + missing && (card.HasLoadedResource ||
                    Field<Label>(card, "_state").Text == "预览不可用"), category + "缺图编号预览未完成。");
                TestSupport.Assert(combo.Text == missing.ToString() && preview.Image == null, category + "缺图时替换了原始编号或保留旧图。");
            }
            combo.Text = original;
            TestSupport.Assert(panel.Flush(), category + "恢复原编号后无法验证。");
            adapter.Invalidate(root); panel.RefreshResourcePickers();
            var reloaded = Field<Func<int, CancellationToken, Task<Bitmap?>>>(combo, "_previewLoader");
            TestSupport.Assert(!ReferenceEquals(loader, reloaded), category + "资源更新没有重置下拉图像缓存上下文。");
        }
    }

    private static void CheckLocation(MainForm form, string category, string buttonName, string table, string field)
    {
        form.SelectModuleForSmokeTest("人物"); Pump();
        var panel = form.BusinessEditorForSmokeTest;
        var document = form.WorkspaceForSmokeTest.GetDocument(table);
        var row = document.Data.Columns.Contains("ID")
            ? document.Data.Rows.Cast<System.Data.DataRow>().First(item => Convert.ToInt32(item["ID"]) == panel.CurrentId)
            : document.Data.Rows[panel.CurrentId - document.Definition.BeginId];
        var expected = Convert.ToInt32(row[field]);
        var button = Descendants(panel).OfType<Button>().Single(item => item.Name == buttonName);
        button.PerformClick();
        var images = Descendants(form).OfType<IntegratedResourcePanel>().Single();
        Wait(() => form.StatusForSmokeTest.Contains("当前草稿定位至", StringComparison.Ordinal) ||
            form.StatusForSmokeTest.Contains("定位失败", StringComparison.Ordinal) || form.StatusForSmokeTest.StartsWith("未找到", StringComparison.Ordinal),
            category + "按钮未完成定位。");
        TestSupport.Assert(!form.StatusForSmokeTest.Contains("失败", StringComparison.Ordinal) &&
            form.StatusForSmokeTest.Contains("当前草稿定位至", StringComparison.Ordinal), form.StatusForSmokeTest);
        var page = Field<ImageGridPage>(images, "_page");
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        TestSupport.Assert(gallery.SelectedItems.Single().LogicalId == expected &&
            gallery.FocusedAssetId == gallery.SelectedItems.Single().StablePhysicalId,
            category + "定位没有选中并高亮当前草稿对应编号。");
        // A missing target reports failure without selecting an unrelated ID.
        var locate = images.LocateLogicalImageAsync(category, int.MaxValue);
        Wait(() => locate.IsCompleted, category + "缺图定位未返回。");
        TestSupport.Assert(!locate.GetAwaiter().GetResult(), category + "缺图编号被定位成其他图片。");
        var zero = images.LocateLogicalImageAsync(category, 0);
        Wait(() => zero.IsCompleted, category + "编号0定位未返回。");
        TestSupport.Assert(zero.GetAwaiter().GetResult() && gallery.SelectedItems.Single().LogicalId == 0,
            category + "编号0发生物理图号偏移。");
        form.SelectModuleForSmokeTest("人物"); Pump();
    }

    private static void CheckProjectSwitch(MainForm form, string originalRoot, string source)
    {
        var nextRoot = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            form.SelectModuleForSmokeTest("__resources");
            var images = Descendants(form).OfType<IntegratedResourcePanel>().Single();
            Wait(() => images.LoadingTask.IsCompleted, "项目切换前图库未加载。");
            images.LoadProject(originalRoot, "Face", forceReload: true);
            var stale = images.LocateLogicalImageAsync("Face", 0);
            form.LoadProjectForSmokeTest(nextRoot);
            Wait(() => stale.IsCompleted, "旧项目定位请求在项目切换后未结束。");
            TestSupport.Assert(!stale.GetAwaiter().GetResult(), "旧项目定位结果覆盖了新项目。");
            TestSupport.Assert(form.WorkspaceForSmokeTest.RootPath == nextRoot && !form.WorkspaceForSmokeTest.IsDirty,
                "项目切换后工作区身份或草稿状态错误。");
        }
        finally { Directory.Delete(nextRoot, true); }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static Rectangle Screen(Control control) => control.Parent!.RectangleToScreen(control.Bounds);
    private static void AssertInside(Control control, Control stop)
    {
        var bounds = Screen(control);
        for (var ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            var parentBounds = ancestor.RectangleToScreen(ancestor.ClientRectangle); parentBounds.Inflate(1, 1);
            TestSupport.Assert(parentBounds.Contains(bounds), $"{control.Name}/{control.Text}超出{ancestor.GetType().Name}/{ancestor.Name}：{bounds}/{parentBounds}。");
            if (ancestor == stop) return;
        }
    }
    private static void Pump() { Application.DoEvents(); Thread.Sleep(10); Application.DoEvents(); }
    private static void Wait(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(40)) Pump();
        TestSupport.Assert(condition(), message);
    }
}
