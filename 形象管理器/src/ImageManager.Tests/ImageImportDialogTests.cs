using System.Collections;
using System.Drawing;
using System.Reflection;
using ImageManager.App;
using ImageManager.Core;

namespace ImageManager.Tests;

internal static class ImageImportDialogTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ImageImportDialog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Exception? failure = null;
            using var host = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(100, 100) };
            host.Shown += (_, _) =>
            {
                try
                {
                    Console.WriteLine("[IMAGE_IMPORT_UI] 映射与冲突"); MappingAndConflicts(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 多格式发现与自动转换说明"); MultiFormatDiscoveryAndRules(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] Face0逐表情文件分配"); FaceExpressionSlots(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 候选生命周期与逐帧"); LatestPreparationAndPreview(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 写入格式切换、限制与候选失效"); OutputFormatSelection(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 裁切范围与源图变化"); CropAndSourceChanges(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 追加顺序与可选小图"); AppendOrderAndPairSlots(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 150%/200%模拟缩放布局"); ScaledLayout(root);
                    Console.WriteLine("[IMAGE_IMPORT_UI] 关闭后取消"); CancellationAfterClose(root);
                }
                catch (Exception ex) { failure = ex; }
                finally { host.Close(); }
            };
            Application.Run(host);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            var full = Path.GetFullPath(root);
            Require(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(full).StartsWith("ImageImportDialog_", StringComparison.Ordinal), "测试清理路径超出临时目录。");
            Directory.Delete(full, true);
        }
    }

    private static void MappingAndConflicts(string root)
    {
        var folder = Path.Combine(root, "同名冲突"); Directory.CreateDirectory(folder);
        foreach (var name in new[] { "front.bmp", "front.png", "back.JPEG", "任意名字.jpg", "back.gif", "front.webp" }) File.WriteAllBytes(Path.Combine(folder, name), [0]);
        var resource = Resource(root, ResourceKind.RComposite);
        var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [Target(resource, 0)], service);
        Show(dialog);
        Call(dialog, "ScanDirectory", folder);
        var slots = Slots(dialog);
        Require(Value<string>(slots[0], "SourcePath").Length == 0 && Value<string>(slots[0], "Status").Contains("多个候选"),
            "同一动作的BMP/PNG冲突被静默选定。");
        Require(Value<string>(slots[1], "SourcePath").EndsWith("back.JPEG", StringComparison.Ordinal), "目录未识别JPEG动作条。");
        Require(Field<ListBox>(dialog, "_pending").Items.Cast<string>().All(path => !path.EndsWith(".gif") && !path.EndsWith(".webp")),
            "R/S目录把不支持的静态格式加入了动作条待分配列表。");
        using var picker = (OpenFileDialog)Call(dialog, "FileDialog", true)!;
        Require(picker.Filter.Split('|')[1] == "*.bmp;*.png;*.jpg;*.jpeg", "R/S选择器没有保持四种动作条格式限制。");
        Await((Task)Call(dialog, "PrepareAsync")!);
        Require(service.Pending.Count == 0, "未解决的冲突进入了可提交准备流程。");
        var mapping = Field<DataGridView>(dialog, "_mapping");
        mapping.ClearSelection(); mapping.Rows[0].Selected = true; mapping.CurrentCell = mapping.Rows[0].Cells[0];
        var pending = Field<ListBox>(dialog, "_pending");
        pending.ClearSelected(); pending.SelectedIndex = pending.Items.IndexOf(Path.Combine(folder, "任意名字.jpg"));
        Call(dialog, "AssignCurrent");
        Require(Value<string>(slots[0], "SourcePath").EndsWith("任意名字.jpg", StringComparison.Ordinal), "任意文件名没有显式分配给动作槽。");
        Require(!Field<Button>(dialog, "_confirm").Enabled && dialog.ResultPlan == null, "仅完成映射就可以确认写入。");
        dialog.Close();
    }

    private static void LatestPreparationAndPreview(string root)
    {
        var resource = Resource(root, ResourceKind.SComposite);
        var target = Target(resource, 242);
        var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [target], service);
        Show(dialog);
        var slot = Slots(dialog)[0];
        Call(dialog, "Assign", slot, Path.Combine(root, "a.png"));
        Call(dialog, "PopulateMapping");
        var firstTask = (Task)Call(dialog, "PrepareAsync")!;
        Require(service.Pending.Count == 1 && service.Pending[0].Request.Assignments.Count(item => item.Included) == 1,
            "替换缺省动作没有保留为未提供，或映射丢失。");
        var first = Prepared(root, "first", "a.png");
        first.Rows[0].After!.SetPixel(5, 5, Color.FromArgb(128, 240, 40, 80));
        service.Pending[0].Completion.SetResult(first); Await(firstTask);
        Require(ReferenceEquals(dialog.PreparedResult, first) && Field<Button>(dialog, "_confirm").Enabled, "合法候选没有开放确认。");
        Require(Field<PictureBox>(dialog, "_after").Image is Bitmap exactPreview &&
            exactPreview.GetPixel(5, 5).ToArgb() == Color.FromArgb(128, 240, 40, 80).ToArgb(), "整条候选预览改变了半透明像素的RGB或Alpha。");
        var mode = Field<ComboBox>(dialog, "_previewMode");
        mode.SelectedIndex = 1;
        var frame = Field<NumericUpDown>(dialog, "_frame"); frame.Value = 10;
        var preview = Field<PictureBox>(dialog, "_after");
        Require(preview.Image?.Size == new Size(48, 48) && ((Bitmap)preview.Image).GetPixel(0, 0).R == 110,
            $"逐帧预览没有按mov规格从第0帧切分到第10帧：mode={mode.SelectedIndex} frame={frame.Value} size={preview.Image?.Size} red={(preview.Image is Bitmap bmp ? bmp.GetPixel(0, 0).R : -1)}。");
        Snapshot(dialog, "统一导入-S逐帧-1280x720");

        Call(dialog, "Assign", slot, Path.Combine(root, "old.png")); Call(dialog, "PopulateMapping");
        Require(!first.CanCommit && !Field<Button>(dialog, "_confirm").Enabled && dialog.PreparedResult == null,
            "换源没有释放旧候选或关闭确认。");
        var oldTask = (Task)Call(dialog, "PrepareAsync")!;
        Call(dialog, "Assign", slot, Path.Combine(root, "latest.png")); Call(dialog, "PopulateMapping");
        var latestTask = (Task)Call(dialog, "PrepareAsync")!;
        Require(service.Pending[1].Token.IsCancellationRequested, "换源没有取消旧准备任务。");
        var latest = Prepared(root, "latest", "latest.png");
        service.Pending[2].Completion.SetResult(latest); Await(latestTask);
        var stale = Prepared(root, "stale", "old.png");
        service.Pending[1].Completion.SetResult(stale); Await(oldTask);
        Require(ReferenceEquals(dialog.PreparedResult, latest) && !stale.CanCommit && Field<Button>(dialog, "_confirm").Enabled,
            "旧异步结果覆盖最新候选，或过期候选图片没有释放。");

        Call(dialog, "Assign", slot, Path.Combine(root, "bad.png")); Call(dialog, "PopulateMapping");
        var errorTask = (Task)Call(dialog, "PrepareAsync")!;
        var error = new PreparedImageImport(null, [new ImageImportPreviewRow("S_242", "mov", Path.Combine(root, "bad.png"),
            new Size(49, 528), Size.Empty, "RAW", "", "", null, null, "宽度不符")]);
        service.Pending[3].Completion.SetResult(error); Await(errorTask);
        Require(!Field<Button>(dialog, "_confirm").Enabled && Field<TextBox>(dialog, "_details").Text.Contains("宽度不符"), "错误行可确认或错误原因不可见。");
        Call(dialog, "ExcludeCurrentResult");
        Require(!Value<bool>(slot, "Included") && dialog.PreparedResult == null, "排除错误来源没有更新映射并失效旧准备。");

        Call(dialog, "Assign", slot, Path.Combine(root, "final.png")); Call(dialog, "PopulateMapping");
        var finalTask = (Task)Call(dialog, "PrepareAsync")!;
        var final = Prepared(root, "final", "final.png");
        service.Pending[4].Completion.SetResult(final); Await(finalTask);
        var expectedPlan = final.Plan;
        Call(dialog, "Confirm");
        Require(dialog.DialogResult == DialogResult.OK && ReferenceEquals(dialog.ResultPlan, expectedPlan), "确认没有交出最新候选事务。");
        dialog.Dispose();
        Require(!final.CanCommit && ReferenceEquals(dialog.ResultPlan, expectedPlan) && expectedPlan!.Files[0].CandidateBytes[0] == 1,
            "窗口释放没有释放预览，或不应失效的候选字节失效。");
    }

    private static void MultiFormatDiscoveryAndRules(string root)
    {
        var folder = Path.Combine(root, "任意格式素材"); Directory.CreateDirectory(folder);
        var names = new[] { "任意.bmp", "任意.PNG", "任意.jpg", "任意.JPEG", "任意.gif", "任意.tif", "任意.TIFF", "任意.ico", "任意.WEBP" };
        foreach (var name in names) File.WriteAllBytes(Path.Combine(folder, name), [0]);
        foreach (var name in new[] { "动画.e5", "动画.bin", "说明.txt" }) File.WriteAllBytes(Path.Combine(folder, name), [0]);
        var resource = Resource(root, ResourceKind.E5Indexed);
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Append, preparation: new ControlledPreparation());
        Show(dialog); Call(dialog, "ScanDirectory", folder);
        var pending = Field<ListBox>(dialog, "_pending").Items.Cast<string>().Select(Path.GetFileName).ToArray();
        Require(names.All(name => pending.Contains(name, StringComparer.Ordinal)) && pending.Length == names.Length,
            "目录发现没有接受全部静态图片扩展名，或将动画包当成了静态图片。");
        using var fileDialog = (OpenFileDialog)Call(dialog, "FileDialog", true)!;
        var patterns = fileDialog.Filter.Split('|')[1];
        Require(new[] { "bmp", "png", "jpg", "jpeg", "gif", "tif", "tiff", "ico", "webp" }
            .All(extension => patterns.Contains("*." + extension, StringComparison.Ordinal)), "统一导入文件选择器没有包含常见图片格式。");
        Require(!patterns.Contains("*.e5", StringComparison.Ordinal) && !patterns.Contains("*.bin", StringComparison.Ordinal),
            "静态素材过滤器混入动画包格式。");
        using var legacyPicker = (OpenFileDialog)typeof(ImageGridPage).GetMethod("ImageOpenDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [true])!;
        Require(legacyPicker.Filter == fileDialog.Filter, "专用静态图片入口与统一导入选择器的格式支持不一致。");
        using var animationPicker = (OpenFileDialog)typeof(ImageGridPage).GetMethod("AnimationPackageOpenDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [true])!;
        Require(animationPicker.Filter.Split('|')[1] == "*.e5;*.bin", "动画包入口被替换成普通静态图片过滤器。");
        var rule = (string)Call(dialog, "Rules")!;
        Require(rule.Contains("任意尺寸") && rule.Contains("目标MOD") && rule.Contains("存储编码") && !rule.Contains("保留来源"),
            "普通图片规则仍宣称保留来源尺寸/编码，或没有说明目标MOD自动转换。");
        dialog.Close();

        var history = new HistoryService(Path.Combine(root, "入口审计历史"));
        using var previews = new ImagePreviewService();
        using var page = new ImageGridPage("入口审计", new ImageCatalogService(), new ImageMutationService(history),
            new ReferenceAtlasService(), new RsImportService(), new BatchRenameService(history), previews,
            ImagePageCapabilities.Add | ImagePageCapabilities.Delete | ImagePageCapabilities.Replace | ImagePageCapabilities.BatchRename | ImagePageCapabilities.PixelEdit);
        typeof(ImageGridPage).GetField("_project", Private)!.SetValue(page, Project(root));
        var buttons = Field<Dictionary<string, Button>>(page, "_buttons");
        var hasPair = buttons.TryGetValue("map-pair", out var pairButton);
        Require(buttons.ContainsKey("replace") && hasPair, "地图普通替换或成对导入入口丢失。");
        ConfigureUiResource(page, Resource(root, ResourceKind.MapPair));
        Require(pairButton!.Visible, "地图资源没有显示独立的地图与地形导入按钮。");
        Require(!buttons["add"].Enabled, "固定地图槽错误开放了未经验证的新增入口。");
        ConfigureUiResource(page, Resource(root, ResourceKind.WeatherSpecial));
        Require(buttons["add"].Enabled && buttons["replace"].Enabled && !buttons["delete"].Enabled && !buttons["rename"].Enabled,
            "天气模板追加/替换没有开放，或固定编号删除/重命名约束丢失。");
        ConfigureUiResource(page, Resource(root, ResourceKind.PmapSpecial));
        Require(buttons["add"].Enabled && buttons["replace"].Enabled && !buttons["delete"].Enabled && !buttons["rename"].Enabled && !buttons["pixel"].Enabled,
            "Pmap模板追加/替换没有开放，或删除/重命名/像素编辑边界丢失。");
        ConfigureUiResource(page, resource);
        Require(!pairButton.Visible, "普通静态资源错误显示了地图与地形专用导入按钮。");
    }

    private static void OutputFormatSelection(string root)
    {
        var resource = Resource(root, ResourceKind.SComposite);
        var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [Target(resource, 242)], service);
        Show(dialog);
        var format = Field<ComboBox>(dialog, "_outputFormat");
        Require(format.Enabled && format.Items.Count == 3 && format.SelectedIndex == 0 && format.Text.Contains("BMP"),
            "可转换资源没有默认BMP或格式选项不完整。");
        Call(dialog, "Assign", Slots(dialog)[0], Path.Combine(root, "format.png"));
        var initialTask = (Task)Call(dialog, "PrepareAsync")!;
        Require(service.Pending[0].Request.OutputFormat == ImageImportOutputFormat.Bmp, "默认BMP没有传入实际导入请求。");
        var initial = Prepared(root, "format-bmp", "format.png");
        initial.Rows[0].After!.SetPixel(0, 0, Color.FromArgb(255, 247, 0, 255));
        service.Pending[0].Completion.SetResult(initial); Await(initialTask);
        Require(Field<PictureBox>(dialog, "_after").Image is Bitmap transparent && transparent.GetPixel(0, 0).A == 0 &&
            initial.Rows[0].After!.GetPixel(0, 0).A == 255, "BMP候选没有按图库透明键展示，或展示过程修改了实际候选。");
        Field<ComboBox>(dialog, "_previewMode").SelectedIndex = 1;
        Field<NumericUpDown>(dialog, "_frame").Value = 10;
        Require(!Field<TextBox>(dialog, "_folder").Visible, "转换预览仍显示素材目录工具。");
        format.SelectedIndex = 1;
        Require(!initial.CanCommit && !Field<Button>(dialog, "_confirm").Enabled &&
            service.Pending[1].Request.OutputFormat == ImageImportOutputFormat.Png, "切换PNG没有废弃旧候选并自动重新转换。");
        var oldTask = Field<Task>(dialog, "_formatPreparationTask");
        format.SelectedIndex = 2;
        var latestTask = Field<Task>(dialog, "_formatPreparationTask");
        Require(service.Pending[1].Token.IsCancellationRequested && service.Pending[2].Request.OutputFormat == ImageImportOutputFormat.Preserve,
            "快速切换保留原格式没有取消旧任务或没有传递真实选择。");
        var latest = Prepared(root, "format-preserve", "format.png");
        service.Pending[2].Completion.SetResult(latest); Await(latestTask);
        var stale = Prepared(root, "format-stale", "format.png");
        service.Pending[1].Completion.SetResult(stale); Await(oldTask);
        Require(ReferenceEquals(dialog.PreparedResult, latest) && !stale.CanCommit && Field<Button>(dialog, "_confirm").Enabled,
            "过期格式转换覆盖新候选或错误开放确认。");
        Require(Field<NumericUpDown>(dialog, "_frame").Value == 10, "切换格式丢失逐帧预览位置。");
        Snapshot(dialog, "写入格式-预览-1280x720");
        dialog.Size = new Size(1920, 1080); Application.DoEvents();
        Snapshot(dialog, "写入格式-预览-1920x1080");
        dialog.Close();

        foreach (var kind in new[] { ResourceKind.DllBitmap, ResourceKind.WeatherSpecial, ResourceKind.PmapSpecial, ResourceKind.MapPair })
        {
            using var locked = new ImageImportDialog(Project(root), Resource(root, kind), ImageImportOperation.Append, preparation: new ControlledPreparation());
            var selector = Field<ComboBox>(locked, "_outputFormat");
            Require(!selector.Enabled && selector.Items.Count == 1 && selector.Text == "保留原格式" &&
                Field<Label>(locked, "_formatInfo").Text.Length > 0, $"{kind}固定结构未锁定格式或缺少原因。");
        }

        foreach (var hasCompanion in new[] { false, true })
        {
            var rawResource = Resource(root, hasCompanion ? ResourceKind.FaceComposite : ResourceKind.E5Indexed);
            var rawService = new ControlledPreparation();
            using var rawDialog = new ImageImportDialog(Project(root), rawResource, ImageImportOperation.Append, preparation: rawService);
            Show(rawDialog);
            Call(rawDialog, "Assign", Slots(rawDialog)[0], Path.Combine(root, "raw-append.png"));
            var rawTask = (Task)Call(rawDialog, "PrepareAsync")!;
            var rows = new List<ImageImportPreviewRow>
            {
                new("新增", "image", Path.Combine(root, "raw-append.png"), new Size(64, 80), new Size(64, 80),
                    "无（新增）", "RAW8", "保留原结构", null, new Bitmap(64, 80))
            };
            if (hasCompanion) rows.Add(new("Tou", "Tou", Path.Combine(root, "raw-append.png"), new Size(64, 80), new Size(64, 80),
                "DIB32", "DIB32", "保留DIB", null, new Bitmap(64, 80)));
            var plan = new MutationPlan("raw-append", MutationKind.Append, "test", [new MutationFile(Path.Combine(root, "raw.e5"), [1], "test")], [], false);
            rawService.Pending[0].Completion.SetResult(new PreparedImageImport(plan, rows)); Await(rawTask);
            var selector = Field<ComboBox>(rawDialog, "_outputFormat");
            Require(!selector.Enabled && selector.Items.Count == 1 && selector.Text == "保留原格式",
                "新增RAW模板或RAW头像与Tou配套行没有正确锁定格式。");
            rawDialog.Close();
        }
    }

    private static void ConfigureUiResource(ImageGridPage page, ImageResourceDescriptor resource)
    {
        var selector = Field<ComboBox>(page, "_resourceSelector");
        var itemType = typeof(ImageGridPage).GetNestedType("ResourceItem", BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [resource, false], null)!;
        var initializing = typeof(ImageGridPage).GetField("_initializing", Private)!;
        initializing.SetValue(page, true);
        try { selector.Items.Clear(); selector.Items.Add(item); selector.SelectedIndex = 0; }
        finally { initializing.SetValue(page, false); }
        Call(page, "ConfigureWriteButtons", resource);
        Call(page, "UpdateWriteConstraints");
    }

    private static void FaceExpressionSlots(string root)
    {
        var resource = Resource(root, ResourceKind.FaceComposite);
        var target = Target(resource, 0) with { DisplayName = "Face000", IsFixedSlot = true };
        var folder = Path.Combine(root, "Face0八表情"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "000.png"); File.WriteAllBytes(path, [0]);
        var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [target], service);
        Show(dialog);
        var slots = Slots(dialog);
        Require(slots.Length == 8 && slots.Select(slot => Value<string>(slot, "Role")).SequenceEqual(Enumerable.Range(0, 8).Select(index => $"expression:{index}")),
            "Face0没有列出八个独立表情文件槽。");
        Require(slots.Count(slot => Value<bool>(slot, "Included")) == 1 && Value<bool>(slots[0], "Included"),
            "Face0默认导入范围不正确，空素材表情可能误入准备。");
        var mapping = Field<DataGridView>(dialog, "_mapping");
        Require(Enumerable.Range(0, 8).All(index => Equals(mapping.Rows[index].Cells["role"].Value, $"表情{index}（中心裁切）")),
            "Face0表情标签没有明确表情编号及中心裁切规则。");
        Snapshot(dialog, "静态自适应-Face0八表情文件映射-1280x720");
        Call(dialog, "ScanDirectory", folder);
        Require(slots.All(slot => Value<string>(slot, "SourcePath").Length == 0), "Face0目录识别将同一默认文件静默分配给多个表情。");
        Call(dialog, "Assign", slots[7], path); Call(dialog, "PopulateMapping");
        Require(Value<bool>(slots[7], "Included"), "给非默认Face表情选图后没有自动勾选该槽。");
        var prepare = (Task)Call(dialog, "PrepareAsync")!;
        Require(service.Pending.Count == 1 && service.Pending[0].Request.Assignments.Where(item => item.Included)
                .Select(item => item.Role).SequenceEqual(new[] { "expression:7" }),
            "Face0准备没有只包含实际指定的表情。");
        service.Pending[0].Completion.SetResult(new PreparedImageImport(null, [])); Await(prepare);
        dialog.Close();

        using var normal = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [Target(resource, 1)], new ControlledPreparation());
        Require(Slots(normal).Length == 1 && Value<string>(Slots(normal)[0], "Role") == "image", "普通Face头像错误展开成了八表情组。");
    }

    private static void CropAndSourceChanges(string root)
    {
        var sourcePath = Path.Combine(root, "彩色头像.png");
        var crop = new Rectangle(16, 8, 64, 64);
        SaveSource(sourcePath, Color.LightGreen);
        var resource = Resource(root, ResourceKind.FaceComposite);
        var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [Target(resource, 0)], service);
        Show(dialog);
        Call(dialog, "Assign", Slots(dialog)[0], sourcePath); Call(dialog, "PopulateMapping");
        var firstTask = (Task)Call(dialog, "PrepareAsync")!;
        var first = CroppedPreparation(root, sourcePath, crop, Color.Gold);
        service.Pending[0].Completion.SetResult(first); Await(firstTask);
        var sourcePreview = Field<PictureBox>(dialog, "_sourcePreview");
        var afterPreview = Field<PictureBox>(dialog, "_after");
        Require(sourcePreview.Image is Bitmap source && source.Size == new Size(96, 80) &&
            source.GetPixel(crop.X, crop.Y).R == 255 && source.GetPixel(crop.X, crop.Y).G == 0 &&
            source.GetPixel(crop.X + crop.Width - 1, crop.Y + crop.Height - 1).R == 255 &&
            source.GetPixel(40, 30).ToArgb() == Color.LightGreen.ToArgb(),
            "源图裁切红框或原始画布显示不符合准备结果。");
        Require(afterPreview.Image is Bitmap candidate && candidate.Size == crop.Size && candidate.GetPixel(0, 0).ToArgb() == Color.Gold.ToArgb(),
            "裁切预览误改了实际候选图片，或候选输出尺寸不符。");
        Require(Field<TextBox>(dialog, "_details").Text.Contains("X=16，Y=8，宽=64，高=64") &&
            Field<TextBox>(dialog, "_details").Text.Contains("PNG / 32位"), "裁切坐标或源图格式不可见。");
        Snapshot(dialog, "统一导入-裁切预览-1280x720");

        SaveSource(sourcePath, Color.LightCoral);
        Call(dialog, "UpdatePreview");
        Require(sourcePreview.Image == null && !Field<Button>(dialog, "_confirm").Enabled &&
            Field<TextBox>(dialog, "_details").Text.Contains("源文件已变化"), "源图被改写后仍显示旧缓存或允许确认。");
        Call(dialog, "Confirm");
        Require(dialog.ResultPlan == null && dialog.DialogResult != DialogResult.OK, "源图变化后仍能确认旧候选。");

        var refreshedTask = (Task)Call(dialog, "PrepareAsync")!;
        var refreshed = CroppedPreparation(root, sourcePath, crop, Color.Orange);
        service.Pending[1].Completion.SetResult(refreshed); Await(refreshedTask);
        Require(!first.CanCommit && Field<Button>(dialog, "_confirm").Enabled &&
            sourcePreview.Image is Bitmap updated && updated.GetPixel(40, 30).ToArgb() == Color.LightCoral.ToArgb(),
            "重新准备没有丢弃旧缓存并恢复新源图确认。");
        dialog.Close();
    }

    private static PreparedImageImport CroppedPreparation(string root, string sourcePath, Rectangle crop, Color candidateColor)
    {
        var before = new Bitmap(crop.Width, crop.Height);
        using (var graphics = Graphics.FromImage(before)) graphics.Clear(Color.CornflowerBlue);
        var after = new Bitmap(crop.Width, crop.Height);
        using (var graphics = Graphics.FromImage(after)) graphics.Clear(candidateColor);
        var plan = new MutationPlan("crop", MutationKind.Replace, "crop",
            [new MutationFile(Path.Combine(root, "fake.face.e5"), [1], "test")], [], false)
        {
            SourceSnapshots = new Dictionary<string, MutationSourceSnapshot>(StringComparer.OrdinalIgnoreCase)
            { [Path.GetFullPath(sourcePath)] = MutationSourceSnapshot.Capture(sourcePath) }
        };
        var row = new ImageImportPreviewRow("头像 #0", "large", sourcePath, new Size(96, 80), crop.Size,
            "RAW", "RAW", "按目标64×64画布居中裁切，红框表示源图裁切范围", before, after);
        typeof(ImageImportPreviewRow).GetProperty(nameof(ImageImportPreviewRow.SourceCrop))!.SetValue(row, crop);
        typeof(ImageImportPreviewRow).GetProperty(nameof(ImageImportPreviewRow.SourceFormat))!.SetValue(row, "PNG / 32位");
        return new PreparedImageImport(plan, [row]);
    }

    private static void SaveSource(string path, Color color)
    {
        using var source = new Bitmap(96, 80, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(color); graphics.FillRectangle(brush, 16, 8, 64, 64);
            using var accent = new SolidBrush(Color.DarkSlateBlue); graphics.FillEllipse(accent, 25, 40, 28, 24);
        }
        source.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void AppendOrderAndPairSlots(string root)
    {
        var resource = Resource(root, ResourceKind.E5Indexed);
        using (var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Append, preparation: new ControlledPreparation()))
        {
            Show(dialog);
            Call(dialog, "AddPending", (object)new[] { Path.Combine(root, "z.png"), Path.Combine(root, "a.png") });
            Call(dialog, "AssignInOrder");
            var slots = Slots(dialog);
            Require(slots.Length == 2 && Value<string>(slots[0], "SourcePath").EndsWith("z.png") && Value<string>(slots[1], "SourcePath").EndsWith("a.png"),
                "明确按序分配没有使用可见列表顺序，或普通追加没有创建独立组。");
            var grid = Field<DataGridView>(dialog, "_mapping"); grid.CurrentCell = grid.Rows[1].Cells[0];
            Call(dialog, "MoveGroup", -1);
            Require(Value<int>(slots[1], "Group") == 0 && Value<int>(slots[0], "Group") == 1, "追加组上移没有改变候选编号顺序。");
            dialog.Close();
        }
        var paired = resource with { Category = ImageCategory.ItemIcon };
        using (var dialog = new ImageImportDialog(Project(root), paired, ImageImportOperation.Append, preparation: new ControlledPreparation()))
        {
            Show(dialog);
            var slots = Slots(dialog);
            Require(slots.Length == 2 && Value<string>(slots[0], "Role") == "large" && Value<bool>(slots[0], "Included") &&
                Value<string>(slots[1], "Role") == "small" && !Value<bool>(slots[1], "Included"), "配对图标默认大图及可选小图槽错误。");
            Snapshot(dialog, "统一导入-大小图映射-1280x720");
            dialog.Close();
        }
    }

    private static void CancellationAfterClose(string root)
    {
        var resource = Resource(root, ResourceKind.E5Indexed); var service = new ControlledPreparation();
        using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Append, preparation: service);
        Show(dialog);
        Call(dialog, "Assign", Slots(dialog)[0], Path.Combine(root, "cancel.png"));
        var task = (Task)Call(dialog, "PrepareAsync")!;
        dialog.Close();
        Require(service.Pending[0].Token.IsCancellationRequested && dialog.ResultPlan == null, "取消窗口没有取消后台任务或留下可提交结果。");
        var prepared = Prepared(root, "cancelled", "cancel.png");
        service.Pending[0].Completion.SetResult(prepared); Await(task);
        Require(!prepared.CanCommit && dialog.PreparedResult == null, "窗口关闭后完成的候选没有被释放。");
    }

    private static void ScaledLayout(string root)
    {
        var path = Path.Combine(root, "缩放预览.png"); SaveSource(path, Color.LightGreen);
        foreach (var scale in new[] { 1.25F, 1.5F, 2F })
        {
            var service = new ControlledPreparation(); var resource = Resource(root, ResourceKind.FaceComposite);
            using var dialog = new ImageImportDialog(Project(root), resource, ImageImportOperation.Replace, [Target(resource, 0)], service);
            Show(dialog);
            dialog.Scale(new SizeF(scale, scale));
            // Simulate larger UI metrics inside the same physical workspace;
            // do not change the desktop's native DPI or display settings.
            dialog.MinimumSize = new Size(780, 560); dialog.Size = new Size(1280, 720);
            dialog.PerformLayout(); Application.DoEvents();
            Snapshot(dialog, $"统一导入-映射-模拟缩放{scale * 100:0}%-1280x720");
            AssertControlsFit(dialog, scale);
            Require(Field<DataGridView>(dialog, "_mapping").Height >= 56 && Field<SplitContainer>(dialog, "_mappingSplit").Panel2Collapsed,
                $"{scale:P0}模拟缩放后的映射表不可用，或空的待分配列表没有收起。");
            Call(dialog, "Assign", Slots(dialog)[0], path); Call(dialog, "PopulateMapping");
            var task = (Task)Call(dialog, "PrepareAsync")!;
            service.Pending[0].Completion.SetResult(CroppedPreparation(root, path, new Rectangle(16, 8, 64, 64), Color.Gold));
            Await(task);
            AssertControlsFit(dialog, scale);
            var resultGrid = Field<DataGridView>(dialog, "_results");
            AssertReachable(dialog, resultGrid, new Size(160, 80), $"{scale:P0} 转换结果表");
            var selector = Field<ComboBox>(dialog, "_outputFormat");
            AssertReachable(dialog, selector, new Size(100, 20), $"{scale:P0} 写入格式");
            Snapshot(dialog, $"统一导入-结果表-模拟缩放{scale * 100:0}%-1280x720");
            foreach (var name in new[] { "_before", "_sourcePreview", "_after" })
            {
                var picture = Field<PictureBox>(dialog, name);
                Require(picture.Image != null && picture.Width >= 96 && picture.Height >= 96,
                    $"{scale:P0}模拟缩放后的{name}预览不可见：{picture.Bounds}，parents=" + string.Join(" | ", Parents(picture).Select(parent => $"{parent.GetType().Name}:{parent.Bounds}")));
                AssertReachable(dialog, picture, new Size(96, 96), $"{scale:P0} {name}");
            }
            Snapshot(dialog, $"统一导入-预览-模拟缩放{scale * 100:0}%-1280x720");
            dialog.Close();
        }
    }

    private static void AssertControlsFit(Form form, float scale)
    {
        foreach (var button in Descendants(form).OfType<Button>().Where(button => button.Visible))
        {
            Require(button.Width > 0 && button.Height > 0, $"{scale:P0}模拟缩放后的按钮尺寸无效：{button.Text}");
            var scroll = Parents(button).OfType<ScrollableControl>().FirstOrDefault(parent => parent.AutoScroll);
            scroll?.ScrollControlIntoView(button);
            AssertInsideWindow(form, button, $"{scale:P0} {button.Text}");
            AssertReachable(form, button, button.ClientSize, $"{scale:P0} {button.Text}");
            if (button.Parent is FlowLayoutPanel parent && !parent.AutoScroll)
                Require(button.Right <= parent.ClientSize.Width + 2 && button.Bottom <= parent.ClientSize.Height + 2,
                    $"{scale:P0}模拟缩放后的按钮超出按钮栏：{button.Text}");
        }
        foreach (var scroll in Descendants(form).OfType<ScrollableControl>().Where(control => control.AutoScroll))
            scroll.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
    }

    private static void AssertInsideWindow(Form form, Control control, string label)
    {
        var rectangle = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        Require(rectangle.Left >= 0 && rectangle.Top >= 0 && rectangle.Right <= form.ClientSize.Width && rectangle.Bottom <= form.ClientSize.Height,
            $"{label}超出模拟缩放窗口：{rectangle} / {form.ClientSize}；parents=" + string.Join(" | ", Parents(control).Select(parent => $"{parent.GetType().Name}:{parent.Bounds} minimum={parent.MinimumSize} dock={parent.Dock}")));
    }

    private static IEnumerable<Control> Parents(Control control)
    { for (var parent = control.Parent; parent != null; parent = parent.Parent) yield return parent; }

    private static void AssertReachable(Form form, Control control, Size minimumVisible, string label)
    {
        Parents(control).OfType<ScrollableControl>().FirstOrDefault(parent => parent.AutoScroll)?.ScrollControlIntoView(control);
        Application.DoEvents();
        var visible = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        foreach (var parent in Parents(control))
            visible.Intersect(form.RectangleToClient(parent.RectangleToScreen(parent.ClientRectangle)));
        Require(visible.Width >= minimumVisible.Width && visible.Height >= minimumVisible.Height,
            $"{label}无法滚动到可辨认的内容区域：可见{visible.Size}，要求{minimumVisible}。");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Show(Form form)
    {
        form.Show(); form.Size = new Size(1280, 720); Application.DoEvents();
        var prepare = Field<Button>(form, "_prepare"); var confirm = Field<Button>(form, "_confirm");
        foreach (var button in new[] { prepare, confirm })
        {
            var rectangle = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            Require(rectangle.Width > 0 && rectangle.Height > 0 && rectangle.Top >= 0 && rectangle.Bottom <= form.ClientSize.Height,
                "1280×720下准备或确认按钮超出窗口。");
        }
    }
    private static PreparedImageImport Prepared(string root, string id, string file)
    {
        var image = new Bitmap(48, 528);
        using (var graphics = Graphics.FromImage(image))
            for (var frame = 0; frame < 11; frame++)
            { using var brush = new SolidBrush(Color.FromArgb(100 + frame, 20, 30)); graphics.FillRectangle(brush, 0, frame * 48, 48, 48); }
        var plan = new MutationPlan(id, MutationKind.Replace, id, [new MutationFile(Path.Combine(root, "fake.e5"), [1], "test")], [], false);
        return new PreparedImageImport(plan, [new ImageImportPreviewRow("将领/S_242 → Unit #242", "mov", Path.Combine(root, file),
            image.Size, image.Size, "PNG", "BMP32", "候选编码预览", new Bitmap(image), image)]);
    }
    private static ProjectProfile Project(string root) => new(root, "UI夹具", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
    private static ImageResourceDescriptor Resource(string root, ResourceKind kind) => new("fixture.e5", "UI测试资源", ImageCategory.Scene,
        kind, [Path.Combine(root, "fixture.e5")], 0, true, true, false, "", "");
    private static LogicalImageAsset Target(ImageResourceDescriptor resource, int physical) => new(resource.Key, physical,
        resource.Kind == ResourceKind.SComposite ? $"S_{physical:000}" : $"R{physical:000}", resource.Category, resource.Kind,
        [new FrameDescriptor(resource.Key, resource.Paths[0], physical, 0, 1, "mov", "none", 48, 48)], [], false, false, "");
    private static object[] Slots(object dialog) => ((IEnumerable)typeof(ImageImportDialog).GetField("_slots", Private)!.GetValue(dialog)!).Cast<object>().ToArray();
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static T Value<T>(object instance, string name) => (T)instance.GetType().GetProperty(name, Private)!.GetValue(instance)!;
    private static object? Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    private static void Await(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
        Require(task.IsCompleted, "UI准备任务未在15秒内完成。"); task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static void Snapshot(Form form, string name)
    {
        var directory = Environment.GetEnvironmentVariable("IMAGE_IMPORT_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(directory, name + ".png"));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ControlledPreparation : IImageImportPreparationService
    {
        internal List<Pending> Pending { get; } = [];
        public Task<PreparedImageImport> PrepareAsync(ImageImportRequest request, CancellationToken cancellationToken = default)
        {
            var item = new Pending(request, cancellationToken, new TaskCompletionSource<PreparedImageImport>(TaskCreationOptions.RunContinuationsAsynchronously));
            Pending.Add(item); return item.Completion.Task;
        }
    }
    private sealed record Pending(ImageImportRequest Request, CancellationToken Token, TaskCompletionSource<PreparedImageImport> Completion);
}
