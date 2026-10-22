using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using ImageManager.App;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class JobSLabelTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int DetailedJobNameOffset = 0xD18D0;
    private const int NameSlotSize = 9;
    private const int JobCount = 80;
    private static readonly string[] Factions = ["我军", "友军", "敌军"];

    internal static void Run()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "JobSLabelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Exception? failure = null;
            using var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(100, 100) };
            host.Shown += (_, _) =>
            {
                try
                {
                    var mountain = CheckActualProject("复刻版6.6山贼", ["群英", "诸侯", "人杰", "小刀手"], temporary);
                    CheckActualProject("加强版6.5未加密版", ["群雄", "英雄", "霸王", "轻步兵"], temporary);
                    CheckMissingAndPartialNames(mountain.Project, mountain.Assets, temporary);
                    CheckJobGallery(mountain.Project, mountain.Resource, mountain.Names, temporary);
                }
                catch (Exception ex) { failure = ex; }
                finally { host.Close(); }
            };
            Application.Run(host);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            var full = Path.GetFullPath(temporary);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(full).StartsWith("JobSLabelTests_", StringComparison.Ordinal), "测试清理目录超出临时目录。");
            Directory.Delete(full, true);
        }
    }

    private static ActualProject CheckActualProject(string sampleName, string[] firstNames, string temporary)
    {
        var root = SampleRoot(sampleName);
        var profile = new ProjectProfileService().Detect(root);
        var executable = profile.TryGetResource("Ekd5.exe") ?? throw new FileNotFoundException("实际基底缺少Ekd5.exe。");
        // Read the detailed-jobs binary table independently, rather than using the display service as its own oracle.
        var expectedNames = ReadDetailedJobTable(File.ReadAllBytes(executable));
        Require(expectedNames.Count == JobCount, sampleName + "没有完整的80个兵种名称。");
        for (var id = 0; id < firstNames.Length; id++)
            Equal(firstNames[id], expectedNames[id], $"{sampleName}真实详细兵种名称#{id}");
        var names = JobSDisplayService.LoadNames(profile);
        for (var id = 0; id < JobCount; id++)
            Equal(expectedNames[id], NameOrEmpty(names, id), $"{sampleName}从详细兵种名称表读取#{id}");

        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(profile).Single(item => item.Kind == ResourceKind.SComposite);
        var original = new SResourceCodec().ReadJobAssets(profile, resource).ToList();
        Equal(240, original.Count, sampleName + "兵种S固定物理槽数量");
        var named = JobSDisplayService.ApplyNames(original, names);
        CheckLabelsAndPhysicalBindings(original, named, expectedNames, sampleName + " Core");

        var history = new HistoryService(Path.Combine(temporary, "history-" + sampleName));
        using var previews = new ImagePreviewService();
        using var page = CreatePage(catalog, history, previews);
        SetField(page, "_project", profile);
        var directlyNamed = (List<LogicalImageAsset>)Invoke(page, "NameJobSAssets", original)!;
        CheckLabelsAndPhysicalBindings(original, directlyNamed, expectedNames, sampleName + " UI命名入口");
        var load = Invoke(page, "ReadResource", profile, resource, true)!;
        var loaded = Property<IReadOnlyList<LogicalImageAsset>>(load, "Assets");
        CheckLabelsAndPhysicalBindings(original, loaded, expectedNames, sampleName + " UI真实资源读取");
        Equal(0, Property<IReadOnlyDictionary<string, string>>(load, "Failures").Count, sampleName + "资源读取失败项");

        for (var index = 0; index < original.Count; index++)
        {
            var before = original[index];
            var after = loaded[index];
            var descriptors = (FrameDescriptor[])Invoke(page, "PreviewDescriptors", after)!;
            Equal(1, descriptors.Length, sampleName + "兵种卡片代表帧数量");
            var descriptor = descriptors[0];
            Equal("mov", descriptor.Action, sampleName + "兵种卡片预览动作");
            Equal(0, descriptor.FrameIndex, sampleName + "兵种卡片预览帧");
            Equal(index + 1, descriptor.ImageNumber, sampleName + "兵种卡片预览物理号");
            Equal(profile.TryGetResource("Unit_mov.e5"), descriptor.SourcePath, sampleName + "兵种卡片预览来源");
            Require(before.Frames.Contains(descriptor), sampleName + "改名后取图描述偏离原始资产。");

            // Exercise the actual thumbnail service for all 240 physical IDs and compare with a direct frame-0 crop.
            using var thumbnail = previews.GetThumbnailAsync(profile, catalog, after, descriptor,
                new Size(48, 48), true).GetAwaiter().GetResult();
            Require(thumbnail != null, $"{sampleName}物理号{index + 1}改名后不能获取缩略图。");
            using var expected = ReadMovementZero(profile, descriptor);
            Equal(ReferenceAtlasService.PixelHash(expected), ReferenceAtlasService.PixelHash(thumbnail!),
                $"{sampleName}物理号{index + 1}改名后缩略图像素");
        }

        var generals = new SResourceCodec().ReadAssets(profile, resource);
        var unchangedGenerals = JobSDisplayService.ApplyNames(generals, names);
        Require(generals.Zip(unchangedGenerals).All(pair => ReferenceEquals(pair.First, pair.Second)),
            sampleName + "兵种命名修改了将领S资产。");
        Console.WriteLine($"[JOB_S_LABELS] {sampleName}：80名称、240标签、240真实缩略图及UI读取全部通过；" +
            string.Join(" / ", loaded.Take(12).Select(asset => asset.DisplayName)));
        return new(profile, resource, original, expectedNames);
    }

    private static void CheckLabelsAndPhysicalBindings(IReadOnlyList<LogicalImageAsset> original,
        IReadOnlyList<LogicalImageAsset> named, IReadOnlyDictionary<int, string> names, string context)
    {
        Equal(original.Count, named.Count, context + "资产数量");
        for (var index = 0; index < original.Count; index++)
        {
            var source = original[index];
            var actual = named[index];
            var physical = index + 1;
            var jobId = index / 3;
            var faction = index % 3;
            Equal(ExpectedLabel(jobId, faction, names), actual.DisplayName, context + $"物理号{physical}标签");
            Require(!actual.DisplayName.Contains("S_", StringComparison.Ordinal), context + "标签残留物理编号前缀。");
            Equal(physical, SResourceExchangeNaming.PhysicalId(actual), context + "物理图号");
            Equal(source.LogicalId, actual.LogicalId, context + "逻辑ID");
            Equal(source.StablePhysicalId, actual.StablePhysicalId, context + "稳定物理身份");
            Equal((int?)jobId, actual.JobId, context + "兵种ID元数据");
            Equal((int?)faction, actual.FactionIndex, context + "阵营元数据");
            Equal(SImageMode.Job, actual.SMode, context + "S图库类型");
            Require(actual.IsFixedSlot && actual.Frames.Count == 28 && actual.Frames.All(frame => frame.ImageNumber == physical),
                context + $"物理号{physical}不是原固定槽28帧。");
            Require(actual.EditableSections.Count == 3 && actual.EditableSections.All(section => section.ImageNumber == physical),
                context + $"物理号{physical}的写回动作条映射被改变。");
            if (ReferenceEquals(source.Frames, actual.Frames))
                Require(actual with { DisplayName = source.DisplayName } == source, context + "改名更改了标签以外的资产字段。");
            else
            {
                Require(source.Frames.SequenceEqual(actual.Frames) && source.EditableSections.SequenceEqual(actual.EditableSections),
                    context + "UI重读改变了取图或写回描述。");
                Equal(source.Status, actual.Status, context + "状态/物理详情");
                Require(source.SMapping!.Turns.SequenceEqual(actual.SMapping!.Turns), context + "物理动作绑定改变。");
            }
        }
    }

    private static void CheckMissingAndPartialNames(ProjectProfile actualProject,
        IReadOnlyList<LogicalImageAsset> assets, string temporary)
    {
        var noExecutable = actualProject with
        {
            // The real root still contains Ekd5.exe: absence from the profile must not trigger a guessed fallback.
            ResourcePaths = actualProject.ResourcePaths.Where(pair => !pair.Key.Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
        var missingPath = noExecutable.ResourcePaths.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        missingPath["Ekd5.exe"] = Path.Combine(temporary, "不存在的Ekd5.exe");
        foreach (var project in new[] { noExecutable, actualProject with { ResourcePaths = missingPath } })
        {
            var empty = JobSDisplayService.LoadNames(project);
            Equal(0, empty.Count, "缺少指定Ekd5.exe时不得借用其他名称表");
            var unnamed = JobSDisplayService.ApplyNames(assets, empty);
            CheckLabelsAndPhysicalBindings(assets, unnamed, empty, "缺文件未命名回退");
            Equal("0：未命名 我军", unnamed[0].DisplayName, "未命名标签精确格式");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var bytes = new byte[DetailedJobNameOffset + JobCount * NameSlotSize];
        void PutName(int id, string name) => gbk.GetBytes(name).CopyTo(bytes, DetailedJobNameOffset + id * NameSlotSize);
        PutName(0, "正确零"); PutName(1, "正确一"); PutName(32, "旧偏移"); PutName(79, "末兵种");
        var path = Path.Combine(temporary, "独立兵种名称夹具.exe"); File.WriteAllBytes(path, bytes);
        var fixture = actualProject with
        {
            ResourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Ekd5.exe"] = path }
        };
        var partialNames = JobSDisplayService.LoadNames(fixture);
        Equal("正确零", partialNames[0], "正确0xD18D0表头");
        Equal("旧偏移", partialNames[32], "0xD19F0属于兵种32，不得当成兵种0");
        Equal("末兵种", partialNames[79], "80项最后一项边界");
        Require(!partialNames.ContainsKey(2), "空兵种名不应借用前后名称。");
        var partiallyNamed = JobSDisplayService.ApplyNames(assets, partialNames);
        Equal("0：正确零 我军", partiallyNamed[0].DisplayName, "正确名称表头没有被旧偏移替换");
        Equal("2：未命名 我军", partiallyNamed[6].DisplayName, "空名称回退");
        Equal("79：末兵种 敌军", partiallyNamed[239].DisplayName, "末兵种敌军标签");

        File.WriteAllBytes(path, bytes[..(DetailedJobNameOffset + NameSlotSize + 4)]);
        var truncated = JobSDisplayService.LoadNames(fixture);
        Equal(1, truncated.Count, "截断表只读取完整9字节名称槽");
        Equal("1：未命名 我军", JobSDisplayService.ApplyNames(assets, truncated)[3].DisplayName, "截断名称不应被部分解码");
        Console.WriteLine("[JOB_S_LABELS] 缺文件、缺映射、空名称、旧偏移诱饵及截断槽边界通过。");
    }

    private static void CheckJobGallery(ProjectProfile project, ImageResourceDescriptor resource,
        IReadOnlyDictionary<int, string> names, string temporary)
    {
        var history = new HistoryService(Path.Combine(temporary, "gallery-history"));
        using var previews = new ImagePreviewService();
        using var page = CreatePage(new ImageCatalogService(), history, previews);
        using var form = new Form
        {
            Text = "山贼6.6 — 兵种S名称验收", ShowInTaskbar = false, Opacity = 0,
            AutoScaleMode = AutoScaleMode.None, Font = new Font("Microsoft YaHei UI", 10F),
            FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(1280, 720)
        };
        form.Controls.Add(page); form.Show(); Application.DoEvents();
        Field<ComboBox>(page, "_sMode").SelectedIndex = 1;
        page.LoadProject(project, [resource]);
        WaitUntil(() => page.LoadingTask.IsCompleted, "兵种页异步读取未完成。");
        page.LoadingTask.GetAwaiter().GetResult();
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        Equal(240, gallery.ItemCount, "真实兵种页卡片总数");
        WaitUntil(() => gallery.RealizedControls.Count > 0 && gallery.RealizedControls.All(card =>
            Descendants(card).OfType<PictureBox>().SingleOrDefault()?.Image != null), "兵种页首屏实际缩略图未完成。");
        foreach (var card in gallery.RealizedControls)
        {
            var asset = (LogicalImageAsset)card.Tag!;
            var physical = SResourceExchangeNaming.PhysicalId(asset);
            Equal(ExpectedLabel((physical - 1) / 3, (physical - 1) % 3, names),
                Descendants(card).OfType<CheckBox>().Single().Text, "首屏卡片标签");
            var descriptor = (FrameDescriptor)Descendants(card).OfType<PictureBox>().Single().Tag!;
            Equal(physical, descriptor.ImageNumber, "首屏卡片物理取图");
            Equal("mov", descriptor.Action, "首屏卡片预览动作");
            Equal(0, descriptor.FrameIndex, "首屏卡片预览帧");
        }
        var screenshotDirectory = Environment.GetEnvironmentVariable("JOB_S_LABEL_SNAPSHOTS");
        if (!string.IsNullOrWhiteSpace(screenshotDirectory))
        {
            Directory.CreateDirectory(screenshotDirectory);
            using var screenshot = new Bitmap(1280, 720, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
            var colors = new HashSet<int>();
            for (var y = 0; y < screenshot.Height; y += 16)
            for (var x = 0; x < screenshot.Width; x += 16) colors.Add(screenshot.GetPixel(x, y).ToArgb());
            Require(colors.Count > 16, "兵种页离屏截图疑似空白。");
            var output = Path.Combine(screenshotDirectory, "兵种S-山贼详细名称-1280x720.png");
            screenshot.Save(output, ImageFormat.Png);
            Console.WriteLine("[JOB_S_LABELS] 截图：" + Path.GetFullPath(output));
        }
        // Check the final fixed slot through the same virtualized card and thumbnail path.
        gallery.ScrollToItem(239);
        WaitUntil(() => gallery.RealizedControls.Any(card => card.Tag is LogicalImageAsset { PhysicalImageNumber: 240 } &&
            Descendants(card).OfType<PictureBox>().Single().Image != null), "兵种页末尾S_240未实现或未取图。");
        var last = gallery.RealizedControls.Single(card => card.Tag is LogicalImageAsset { PhysicalImageNumber: 240 });
        Equal(ExpectedLabel(79, 2, names), Descendants(last).OfType<CheckBox>().Single().Text, "虚拟兵种页最后一项标签");
        form.Close();
    }

    private static ImageGridPage CreatePage(IImageCatalogService catalog, HistoryService history, IImagePreviewService previews)
        => new("S形象", catalog, new ImageMutationService(history), new ReferenceAtlasService(),
            new RsImportService(), new BatchRenameService(history), previews, ImagePageCapabilities.CharacterS);

    private static Bitmap ReadMovementZero(ProjectProfile project, FrameDescriptor descriptor)
    {
        var payload = E5Archive.Open(descriptor.SourcePath).ReadDecoded(descriptor.ImageNumber);
        using var strip = ImageRendering.IsStandardImage(payload) ? ImageRendering.DecodeStandardImage(payload) :
            ImageRendering.DecodeIndexed(payload, 48, 48 * 11, new PaletteService().Load(project));
        return ImageRendering.Crop(strip, new Rectangle(0, 0, 48, 48));
    }

    private static Dictionary<int, string> ReadDetailedJobTable(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(936);
        var result = new Dictionary<int, string>();
        for (var id = 0; id < JobCount; id++)
        {
            var field = bytes.AsSpan(DetailedJobNameOffset + id * NameSlotSize, NameSlotSize).ToArray();
            var nul = Array.IndexOf(field, (byte)0);
            var name = encoding.GetString(field, 0, nul < 0 ? field.Length : nul).Trim();
            if (name.Length > 0) result[id] = name;
        }
        return result;
    }

    private static string ExpectedLabel(int job, int faction, IReadOnlyDictionary<int, string> names)
        => $"{job}：{(names.TryGetValue(job, out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : "未命名")} {Factions[faction]}";
    private static string NameOrEmpty(IReadOnlyDictionary<int, string> names, int id) => names.TryGetValue(id, out var name) ? name : "";
    private static string SampleRoot(string name)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "基底", name);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        return root;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static object? Invoke(object instance, string method, params object[] args)
        => instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var descendant in Descendants(control)) yield return descendant;
        }
    }
    private static void WaitUntil(Func<bool> ready, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
        Require(ready(), message);
    }
    private static void Equal<T>(T expected, T actual, string context)
        => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{context}：期望“{expected}”，实际“{actual}”。");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record ActualProject(ProjectProfile Project, ImageResourceDescriptor Resource,
        IReadOnlyList<LogicalImageAsset> Assets, IReadOnlyDictionary<int, string> Names);
}
