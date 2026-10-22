using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DataManager.Images;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class ResourceBindingMappingTests
{
    public static void Run(string? basePath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerBinding_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paired = new[] { Png(16, 16, Color.Red), Png(32, 32, Color.Green),
                Png(16, 16, Color.Blue), Png(32, 32, Color.Yellow), Png(16, 16, Color.Purple) };
            // An unmatched final entry exists in real 6.6 variants. It must
            // stay intact and must not shift or create an incomplete icon.
            File.WriteAllBytes(Path.Combine(root, "Item.e5"), Archive(paired));
            File.WriteAllBytes(Path.Combine(root, "Mtem.e5"), Archive([
                Png(16, 16, Color.Cyan), Png(32, 32, Color.Orange),
                Png(16, 16, Color.Purple), Png(32, 32, Color.White), Png(16, 16, Color.Black)]));
            File.WriteAllBytes(Path.Combine(root, "Hitarea.e5"), Archive([Png(16, 16, Color.Orange)]));
            File.WriteAllBytes(Path.Combine(root, "Face.e5"), Archive(Enumerable.Range(1, 10)
                .Select(number => Png(48, 64, number == 9 ? Color.Green : Color.Red)).ToArray()));
            File.WriteAllBytes(Path.Combine(root, "Pmapobj.e5"), Archive([
                Png(48, 64 * 20, Color.Red), Png(48, 64 * 20, Color.Blue),
                Png(48, 64 * 20, Color.Green), Png(48, 64 * 20, Color.Yellow)]));
            foreach (var (name, width, height) in new[] { ("Unit_mov.e5", 48, 48 * 11),
                         ("Unit_atk.e5", 64, 64 * 12), ("Unit_spc.e5", 48, 48 * 5) })
                File.WriteAllBytes(Path.Combine(root, name), Archive(Enumerable.Range(1, 556)
                    .Select(number => SStrip(name, width, height, number)).ToArray()));
            var sourceHash = Hash(Path.Combine(root, "Item.e5"));
            using (var adapter = new ResourceCatalogAdapter())
            {
                var options = adapter.GetIdOptionsAsync(root, ResourceBindingKind.ItemIcon).GetAwaiter().GetResult();
                TestSupport.Assert(options.Select(option => option.Id).SequenceEqual([0, 1]),
                    "6.6宝物图标下拉必须从0开始，不能把尾部孤立条目当作图标");
                var info = adapter.ResolveBindingPreview(root, ResourceBindingKind.ItemIcon, 0);
                TestSupport.Assert(info.CanPreview && info.PhysicalImageNumbers.SequenceEqual([1, 2]), "宝物图标0没有映射第一对物理图片");
                using (var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.ItemIcon, 0, new Size(80, 80)).GetAwaiter().GetResult())
                    TestSupport.Assert(image is { Width: 32, Height: 32 } && image.GetPixel(0, 0).ToArgb() == Color.Green.ToArgb(),
                        "宝物预览没有按Data编号0读取大图");
                using (var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.ItemIcon, 1, new Size(80, 80)).GetAwaiter().GetResult())
                    TestSupport.Assert(image?.GetPixel(0, 0).ToArgb() == Color.Yellow.ToArgb(), "宝物图标1误读了第一对图片");
                foreach (var invalid in new[] { -1, 2, 255, int.MaxValue })
                {
                    using var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.ItemIcon, invalid, new Size(48, 48)).GetAwaiter().GetResult();
                    TestSupport.Assert(image == null && !adapter.ResolveBindingPreview(root, ResourceBindingKind.ItemIcon, invalid).CanPreview,
                        "未知宝物图标应该返回缺图，不能产生错位或异常");
                }
                var generic = adapter.ReadAssets(root, "Hitarea.e5");
                TestSupport.Assert(generic.Single().LogicalId == 1 && adapter.ResolveRangePreview(root, true, 0).PhysicalImageNumber == 1,
                    "宝物逻辑编号改动破坏了通用E5或范围+1映射");
                TestSupport.Assert(adapter.GetIdOptions(root, ResourceBindingKind.StrategyIcon).Select(option => option.Id).SequenceEqual([0, 1]) &&
                    adapter.ResolveBindingPreview(root, ResourceBindingKind.StrategyIcon, 0).PhysicalImageNumbers.SequenceEqual([1, 2]) &&
                    adapter.ResolveBindingPreview(root, ResourceBindingKind.StrategyIcon, 1).PhysicalImageNumbers.SequenceEqual([3, 4]),
                    "Mtem策略图标必须按0基小图/大图对编号，并忽略孤立尾条目");
                using (var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.StrategyIcon, 1, new Size(80, 80)).GetAwaiter().GetResult())
                    TestSupport.Assert(image is { Width: 32, Height: 32 } && image.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(),
                        "策略图标1没有读取Mtem物理大图4");
                TestSupport.Assert(adapter.GetIdOptions(root, ResourceBindingKind.CharacterR).Select(option => option.Id).SequenceEqual([0, 1]),
                    "R形象的0基逻辑编号不完整");
                TestSupport.Assert(adapter.GetIdOptions(root, ResourceBindingKind.Portrait).Select(option => option.Id).SequenceEqual([0, 1, 2]) &&
                    adapter.ResolveBindingPreview(root, ResourceBindingKind.Portrait, 1).PhysicalImageNumbers.SequenceEqual([9]),
                    "头像下拉必须使用Face逻辑编号，头像1应对应物理图9");
                using (var portrait = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.Portrait, 1, new Size(48, 64)).GetAwaiter().GetResult())
                    TestSupport.Assert(portrait?.GetPixel(0, 0).ToArgb() == Color.Green.ToArgb(), "头像下拉没有读取对应头像的第一帧");
                using (var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.CharacterR, 0, new Size(96, 128)).GetAwaiter().GetResult())
                    TestSupport.Assert(image is { Width: 48, Height: 64 }, "R形象下拉没有读取首帧");
                var sOptions = adapter.GetIdOptions(root, ResourceBindingKind.CharacterS);
                TestSupport.Assert(sOptions.Select(option => option.Id).SequenceEqual(Enumerable.Range(0, 253)) && sOptions[0].CanPreview,
                    "556张Unit图片的人物S下拉应为默认0加252个紧凑编号，不能直接使用图库物理图号");
                TestSupport.Assert(adapter.ResolveBindingPreview(root, ResourceBindingKind.CharacterS, 0, 6, 2).PhysicalImageNumbers.SequenceEqual([19]),
                    "S000预览必须使用兵种ID对应的图，不能被独立转数覆盖");
                CheckCharacterSFirstFrames(adapter, root);
                using (var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.CharacterS, 0, new Size(96, 96)).GetAwaiter().GetResult())
                    TestSupport.Assert(image is { Width: 48, Height: 48 } && image.GetPixel(0, 0).ToArgb() == SColor(1, "mov", 0).ToArgb(),
                        "默认S形象下拉没有读取当前兵种mov第0帧");
                CheckJobSFirstFrames(adapter, root);
                var people = adapter.ReadAssets(root, "SComposite");
                TestSupport.Assert(people.Count == 316 && people.Select(asset => asset.LogicalId).SequenceEqual(Enumerable.Range(241, 316)) &&
                    people.All(asset => !asset.IsFixedSlot && asset.Frames.Count == 28 &&
                        asset.Frames.All(frame => frame.ImageNumber == asset.LogicalId)) &&
                    people.Select(asset => asset.StablePhysicalId).Distinct().Count() == 316,
                    "将领S图库必须用241..556物理图号逐张展示，每项三动作合计28帧且物理身份唯一");
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try
                {
                    adapter.GetIdOptionsAsync(root, ResourceBindingKind.ItemIcon, cancellationToken: canceled.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("已取消的资源选项请求仍然返回结果");
                }
                catch (OperationCanceledException) { }
                using var panel = new IntegratedResourcePanel(adapter);
                panel.LoadProject(root, "Item");
                WaitForResources(panel);
                var gallery = Descendants(panel).OfType<VirtualImageGallery>().Single();
                gallery.SelectAll(true);
                TestSupport.Assert(gallery.SelectedItems.Select(asset => asset.LogicalId).SequenceEqual([0, 1]),
                    "形象设定和宝物Data页的逻辑图标编号不一致");
                panel.ShowCategory("S");
                WaitForResources(panel);
                var mode = Descendants(panel).OfType<ComboBox>().Single(combo => combo.Items.Cast<object>()
                    .Select(item => item.ToString()).SequenceEqual(["将领", "兵种"]));
                gallery = Descendants(panel).OfType<VirtualImageGallery>().Single();
                gallery.SelectAll(true);
                TestSupport.Assert(gallery.SelectedItems.All(asset => asset.SMode == SImageMode.Person && asset.LogicalId >= 241),
                    "将领S模式混入了兵种1..240编号");
                mode.SelectedIndex = 1; WaitForResources(panel); gallery.SelectAll(true);
                var jobs = gallery.SelectedItems.OrderBy(asset => asset.LogicalId).ToArray();
                TestSupport.Assert(jobs.Length == 240 && jobs.Select(asset => asset.LogicalId).SequenceEqual(Enumerable.Range(1, 240)) &&
                    jobs.All(asset => asset.SMode == SImageMode.Job) && jobs[0].Frames.All(frame => frame.ImageNumber == 1) &&
                    jobs[239].Frames.All(frame => frame.ImageNumber == 240), "兵种S模式没有完整展示1..240物理固定槽");
                TestSupport.Assert(jobs[1].StablePhysicalId != people.Single(asset => asset.LogicalId == 242).StablePhysicalId,
                    "兵种S_002与将领S_242的物理身份发生冲突");
                mode.SelectedIndex = 0; WaitForResources(panel); gallery.SelectAll(true);
                TestSupport.Assert(gallery.SelectedItems.Count == 316 && gallery.SelectedItems.All(asset => asset.SMode == SImageMode.Person),
                    "切回将领S模式后编号或数量发生变化");
            }
            TestSupport.Assert(Hash(Path.Combine(root, "Item.e5")) == sourceHash, "只读资源绑定修改了源档案");
            CheckProportionalBounds();
            CheckFixedJobSDeletion(root);
            CheckIncompletePersonS(root);
            CheckPersonSPhysicalWriteback(root);
            CheckPersonSPhysicalDeletion(root);
            CheckMisalignedSAppend(root);
            ItemIconAppendTests.Run();
            if (!string.IsNullOrWhiteSpace(basePath)) CheckDllCopy(root, basePath);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("DataManagerBinding_", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录清理范围无效。");
            Directory.Delete(root, true);
        }
    }

    private static void WaitForResources(IntegratedResourcePanel panel)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!panel.LoadingTask.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(20))
        { Application.DoEvents(); Thread.Sleep(10); }
        TestSupport.Assert(panel.LoadingTask.IsCompleted, "资源页异步加载超时");
        panel.LoadingTask.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static void CheckJobSFirstFrames(ResourceCatalogAdapter adapter, string root)
    {
        for (var job = 0; job < 80; job++)
        for (var faction = 0; faction < 3; faction++)
        {
            using var animation = adapter.DecodeJobSFirstFrameAsync(root, job, faction).GetAwaiter().GetResult();
            var physical = job * 3 + faction + 1;
            TestSupport.Assert(animation.Frames.Count == 1 && animation.Frames[0].Size == new Size(48, 48) &&
                animation.Frames[0].GetPixel(0, 0).ToArgb() == SColor(physical, "mov", 0).ToArgb(),
                $"兵种{job}阵营{faction}没有读取物理图片{physical}的mov第0帧");
            if (faction == 0)
            {
                using var compatible = adapter.DecodeJobSFirstFrame(root, job);
                TestSupport.Assert(compatible.Frames.Count == 1 && compatible.Frames[0].GetPixel(0, 0).ToArgb() ==
                    animation.Frames[0].GetPixel(0, 0).ToArgb(), "旧兵种预览接口未保留我军语义");
            }
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try
        {
            adapter.DecodeJobSFirstFrameAsync(root, 0, canceled.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("已取消的兵种首帧请求仍然返回图片");
        }
        catch (OperationCanceledException) { }
    }

    private static void CheckCharacterSFirstFrames(ResourceCatalogAdapter adapter, string root)
    {
        foreach (var (sId, physical) in new[] { (1, 241), (2, 244), (32, 334), (33, 337), (252, 556) })
        foreach (var turn in new[] { 1, 2, 3 })
        foreach (var job in new[] { 0, 59, 79 })
        {
            var info = adapter.ResolveBindingPreview(root, ResourceBindingKind.CharacterS, sId, job, turn);
            TestSupport.Assert(info.CanPreview && info.PhysicalImageNumbers.SequenceEqual([physical]),
                $"人物S{sId}没有固定映射第一转物理图{physical}，或误用了图库编号");
            using var image = adapter.DecodeBindingThumbnailAsync(root, ResourceBindingKind.CharacterS, sId,
                new Size(96, 96), job, turn).GetAwaiter().GetResult();
            TestSupport.Assert(image is { Width: 48, Height: 48 } &&
                image.GetPixel(0, 0).ToArgb() == SColor(physical, "mov", 0).ToArgb(),
                $"人物S{sId}的下拉缩略图没有固定读取第一转mov第0帧");
            using var preview = adapter.DecodeCharacterS(root, sId, job, turn);
            TestSupport.Assert(preview.Frames.Count == 1 && preview.Frames[0].Size == new Size(48, 48) &&
                preview.Frames[0].GetPixel(0, 0).ToArgb() == SColor(physical, "mov", 0).ToArgb(),
                $"人物S{sId}业务预览没有仅解码第一转mov第0帧");
        }
        TestSupport.Assert(!adapter.ResolveBindingPreview(root, ResourceBindingKind.CharacterS, 253).CanPreview,
            "人物紧凑编号超过资源末尾时不应误读图库物理253号图片");
    }

    private static void CheckPersonSPhysicalWriteback(string root)
    {
        var project = new ProjectProfileService().Detect(root) with { IsReadOnly = false };
        var descriptor = new ImageCatalogService().BuildCatalog(project).Single(item => item.Key == "SComposite");
        var asset = new SResourceCodec().ReadAssets(project, descriptor).Single(item => item.LogicalId == 242);
        var target = asset.Frames.First(frame => frame.Action == "atk" && frame.FrameIndex == 0);
        using var edited = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(edited)) graphics.Clear(Color.Gold);
        var plan = new RsImportService().PlanPixelEdit(project, asset, target, edited);
        foreach (var file in plan.Files)
        {
            var before = E5Archive.Open(file.TargetPath);
            var after = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            TestSupport.Assert(after.Entries.Count == before.Entries.Count &&
                Enumerable.Range(1, before.Entries.Count).Where(number => number != 242)
                    .All(number => before.ReadStored(number).SequenceEqual(after.ReadStored(number))),
                "修改将领S_242写到了物理242以外的图片");
            if (!Path.GetFileName(file.TargetPath).Equals("Unit_atk.e5", StringComparison.OrdinalIgnoreCase)) continue;
            using var strip = ImageRendering.DecodeStandardImage(after.ReadDecoded(242));
            TestSupport.Assert(strip.GetPixel(0, 0).ToArgb() == Color.Gold.ToArgb() &&
                strip.GetPixel(0, 64).ToArgb() == SColor(242, "atk", 1).ToArgb(),
                "将领S_242编辑未写入物理242第0帧，或覆盖了同图其他帧");
        }
        var exportRoot = Path.Combine(root, "selected-physical-export");
        var exported = new RsStripExchangeService().Export(project, asset, exportRoot);
        TestSupport.Assert(exported.Files.Count == 3 && exported.Files.All(path =>
            Path.GetRelativePath(exportRoot, path).StartsWith(Path.Combine("将领", "S_242") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)), "选中将领S_242的动作条导出没有使用独立物理编号目录");
        using var exportedMovement = new Bitmap(exported.Files.Single(path => Path.GetFileName(path) == "mov.bmp"));
        TestSupport.Assert(exportedMovement.Size == new Size(48, 48 * 11) &&
            exportedMovement.GetPixel(0, 0).ToArgb() == SColor(242, "mov", 0).ToArgb() &&
            exportedMovement.GetPixel(0, 48).ToArgb() == SColor(242, "mov", 1).ToArgb(),
            "选中S_242却导出了紧凑S编号或相邻图片的动作条");
    }

    private static void CheckPersonSPhysicalDeletion(string root)
    {
        var project = new ProjectProfileService().Detect(root) with { IsReadOnly = false };
        var descriptor = new ImageCatalogService().BuildCatalog(project).Single(item => item.Key == "SComposite");
        var asset = new SResourceCodec().ReadAssets(project, descriptor).Single(item => item.LogicalId == 242);
        var plan = new ImageMutationService(new HistoryService(Path.Combine(root, "delete-history"))).PlanDelete(project, [asset]);
        TestSupport.Assert(plan.Files.Count == 3, "将领物理删除未同时规划三个Unit档案");
        foreach (var file in plan.Files)
        {
            var before = E5Archive.Open(file.TargetPath);
            var after = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            TestSupport.Assert(after.Entries.Count == before.Entries.Count - 1 &&
                Enumerable.Range(1, 241).All(number => after.ReadStored(number).SequenceEqual(before.ReadStored(number))) &&
                Enumerable.Range(242, after.Entries.Count - 241)
                    .All(number => after.ReadStored(number).SequenceEqual(before.ReadStored(number + 1))),
                "删除将领S_242未物理移除选中图片，或改动了兵种固定槽/未选中的前序图片");
        }
    }

    private static void CheckFixedJobSDeletion(string parent)
    {
        var root = Path.Combine(parent, "job-fixed-slots"); Directory.CreateDirectory(root);
        var paths = new List<string>();
        foreach (var (name, width, height) in new[] { ("Unit_mov.e5", 48, 48 * 11),
                     ("Unit_atk.e5", 64, 64 * 12), ("Unit_spc.e5", 48, 48 * 5) })
        {
            var path = Path.Combine(root, name); paths.Add(path);
            File.WriteAllBytes(path, Archive([Enumerable.Repeat((byte)1, width * height).ToArray(),
                Enumerable.Repeat((byte)2, width * height).ToArray()]));
        }
        var project = new ProjectProfile(root, "fixed-jobs", ProjectVersion.Ccz65, false,
            paths.ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.OrdinalIgnoreCase), []);
        var descriptor = new ImageResourceDescriptor("SComposite", "S 形象", ImageCategory.CharacterS,
            ResourceKind.SComposite, paths, 2, true, true, false, "测试", "测试");
        var assets = new SResourceCodec().ReadJobAssets(project, descriptor);
        TestSupport.Assert(assets.Count == 2 && assets.All(asset => asset.IsFixedSlot), "兵种S固定槽边界没有按共同物理数量限制");
        var appendRejected = false;
        try { new RsImportService().PlanAppendCopy(project, assets[0], Array.Empty<DecodedFrame>()); }
        catch (InvalidOperationException ex) { appendRejected = ex.Message.Contains("固定槽", StringComparison.Ordinal); }
        TestSupport.Assert(appendRejected, "兵种S能够被误追加到将领形象库");
        var plan = new ImageMutationService(new HistoryService(Path.Combine(root, "history"))).PlanDelete(project, [assets[0]]);
        foreach (var file in plan.Files)
        {
            var before = E5Archive.Open(file.TargetPath);
            var after = E5Archive.Parse(file.TargetPath, file.CandidateBytes);
            TestSupport.Assert(after.Entries.Count == before.Entries.Count && after.ReadDecoded(1).All(value => value == 0) &&
                after.ReadStored(2).SequenceEqual(before.ReadStored(2)), "删除兵种S没有清空固定槽，或前移/破坏了后续编号");
        }
    }

    private static void CheckMisalignedSAppend(string parent)
    {
        var root = Path.Combine(parent, "misaligned-s"); Directory.CreateDirectory(root);
        foreach (var name in new[] { "Unit_mov.e5", "Unit_atk.e5", "Unit_spc.e5" })
            File.Copy(Path.Combine(parent, name), Path.Combine(root, name));
        var attackPath = Path.Combine(root, "Unit_atk.e5");
        var original = E5Archive.Open(attackPath);
        File.WriteAllBytes(attackPath, original.Rebuild(appendDecoded: [original.ReadDecoded(1)]));
        E5Archive.Invalidate(attackPath);
        var project = new ProjectProfileService().Detect(root) with { IsReadOnly = false };
        var descriptor = new ImageCatalogService().BuildCatalog(project).Single(item => item.Key == "SComposite");
        var asset = new SResourceCodec().ReadAssets(project, descriptor).First();
        var before = descriptor.Paths.ToDictionary(path => path, Hash);
        foreach (var plan in new Action[]
        {
            () => new RsImportService().PlanAppend(project, descriptor, root),
            () => new RsImportService().PlanAppendCopy(project, asset, Array.Empty<DecodedFrame>())
        })
        {
            var rejected = false;
            try { plan(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("数量不一致", StringComparison.Ordinal); }
            TestSupport.Assert(rejected, "Unit三档案不等长时追加将领S必须拒绝，防止新图片编号错位");
        }
        foreach (var pair in before)
            TestSupport.Assert(Hash(pair.Key) == pair.Value, "拒绝不等长Unit追加时改变了源文件");
    }

    private static void CheckIncompletePersonS(string parent)
    {
        var root = Path.Combine(parent, "incomplete-person-s");
        Directory.CreateDirectory(root);
        foreach (var (name, width, count) in new[] { ("Unit_mov.e5", 48, 11), ("Unit_atk.e5", 64, 12), ("Unit_spc.e5", 48, 5) })
            File.WriteAllBytes(Path.Combine(root, name), Archive(Enumerable.Range(1, 241)
                .Select(number => SStrip(name, width, width * count, number)).ToArray()));
        var hashes = Directory.EnumerateFiles(root).ToDictionary(path => path, Hash);
        using var adapter = new ResourceCatalogAdapter();
        var options = adapter.GetIdOptions(root, ResourceBindingKind.CharacterS);
        TestSupport.Assert(options.Select(option => option.Id).SequenceEqual([0]),
            "人物S下拉不应列出仅有第一转资源的不完整三转组");
        var info = adapter.ResolveBindingPreview(root, ResourceBindingKind.CharacterS, 1);
        TestSupport.Assert(info.CanPreview && info.PhysicalImageNumbers.SequenceEqual([241]) &&
            info.Status.Contains("不完整", StringComparison.Ordinal),
            "现有紧凑S1的三转资源不完整时应保留第一转预览并明确提示");
        using var preview = adapter.DecodeCharacterS(root, 1, 0, 3);
        TestSupport.Assert(preview.Frames.Count == 1 && preview.Mapping.Contains("不完整", StringComparison.Ordinal) &&
            preview.Frames[0].GetPixel(0, 0).ToArgb() == SColor(241, "mov", 0).ToArgb(),
            "不完整三转的既有S值没有保持第一转mov第0帧，或缺少状态提示");
        var gallery = adapter.ReadAssets(root, "SComposite");
        TestSupport.Assert(gallery.Count == 1 && gallery[0].LogicalId == 241,
            "图库不应因为人物三转组不完整而隐藏实际存在的物理S_241");
        foreach (var (path, hash) in hashes)
            TestSupport.Assert(Hash(path) == hash, "检查不完整人物S资源时改变了原文件");
    }

    private static void CheckDllCopy(string root, string basePath)
    {
        var target = Path.Combine(root, "dll65"); Directory.CreateDirectory(target);
        var originals = new Dictionary<string, string>();
        foreach (var name in new[] { "Itemicon.dll", "Mgcicon.dll" })
        {
            var source = new[] { Path.Combine(basePath, name), Path.Combine(basePath, "E5", name) }.First(File.Exists);
            originals[source] = Hash(source); File.Copy(source, Path.Combine(target, name));
        }
        using var adapter = new ResourceCatalogAdapter();
        var options = adapter.GetIdOptions(target, ResourceBindingKind.ItemIcon);
        TestSupport.Assert(options.Count > 1 && options[0].Id == 0 && options[1].Id == 1,
            "6.5 DLL宝物图标没有保留0基逻辑编号");
        var asset = adapter.ReadAssets(target, "Itemicon.dll")[0];
        TestSupport.Assert(adapter.ResolveBindingPreview(target, ResourceBindingKind.ItemIcon, 0).PhysicalImageNumbers
            .SequenceEqual(asset.Frames.Select(frame => frame.ImageNumber)), "DLL逻辑图标未映射原大小图RT_BITMAP对");
        using var image = adapter.DecodeBindingThumbnailAsync(target, ResourceBindingKind.ItemIcon, 0, new Size(128, 128)).GetAwaiter().GetResult();
        TestSupport.Assert(image != null, "6.5 DLL宝物图标0预览失败");
        foreach (var pair in originals) TestSupport.Assert(Hash(pair.Key) == pair.Value, "DLL预览改动了原始基底资源");
    }

    private static void CheckProportionalBounds()
    {
        var available = new Rectangle(10, 20, 200, 120);
        TestSupport.Assert(PreviewImageLayout.FitImageBounds(new Size(32, 32), available) == new Rectangle(50, 20, 120, 120),
            "正方形图片框没有最大等比放大或仍使用固定倍数限制");
        TestSupport.Assert(PreviewImageLayout.FitImageBounds(new Size(400, 100), available) == new Rectangle(10, 55, 200, 50),
            "宽图片框产生拉伸或裁切");
        TestSupport.Assert(PreviewImageLayout.FitImageBounds(new Size(48, 64), available) == new Rectangle(65, 20, 90, 120),
            "R形象图片框没有按原始比例完整显示");
        using var host = new Form { ClientSize = new Size(240, 180), StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        using var portrait = new Bitmap(48, 64);
        using var square = new Bitmap(32, 32);
        var picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Image = portrait };
        var viewport = PreviewImageLayout.CreateViewport(picture);
        host.Controls.Add(viewport); host.Show(); Application.DoEvents();
        TestSupport.Assert(picture.Bounds == PreviewImageLayout.FitImageBounds(portrait.Size, viewport.ClientRectangle),
            "独立预览窗口的图片框未按比例最大化");
        picture.Image = square; Application.DoEvents();
        TestSupport.Assert(picture.Bounds == PreviewImageLayout.FitImageBounds(square.Size, viewport.ClientRectangle),
            "动画或替换图片改变比例后未调整图片框");
        host.ClientSize = new Size(400, 240); Application.DoEvents();
        TestSupport.Assert(picture.Size == new Size(240, 240), "预览窗口仍限制图片放大尺寸");
        picture.Image = null; host.Close();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height); using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static Color SColor(int number, string action, int frame)
        => Color.FromArgb(255, number % 256, number / 256, (action switch { "mov" => 0, "atk" => 64, "spc" => 128, _ => 192 }) + frame + 1);

    private static byte[] SStrip(string resourceName, int width, int height, int number)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var action = Path.GetFileNameWithoutExtension(resourceName)[5..];
            for (var index = 0; index < height / width; index++)
            {
                using var brush = new SolidBrush(SColor(number, action, index));
                graphics.FillRectangle(brush, 0, index * width, width, width);
            }
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private static byte[] Archive(IReadOnlyList<byte[]> payloads)
    {
        var offset = E5Archive.IndexOffset + payloads.Count * E5Archive.EntrySize;
        var bytes = new byte[offset + payloads.Sum(payload => payload.Length)];
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = bytes.AsSpan(E5Archive.IndexOffset + index * E5Archive.EntrySize, E5Archive.EntrySize);
            BinaryPrimitives.WriteInt32BigEndian(entry, payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], payloads[index].Length);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], offset);
            payloads[index].CopyTo(bytes, offset); offset += payloads[index].Length;
        }
        return bytes;
    }
}
