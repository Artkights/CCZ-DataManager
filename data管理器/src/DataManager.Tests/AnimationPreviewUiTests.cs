using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class AnimationPreviewUiTests
{
    public static void Run(string sampleRoot)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var temporary = Path.Combine(Path.GetTempPath(), "CczAnimationUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            CheckLegacyMeffGallery(temporary);
            CheckRealWeatherAndStrategy(sampleRoot, temporary);
            CheckLateDecodesAndDisposal(temporary);
            CheckHiddenPendingPlayback(temporary);
            CheckMcallAggregation(temporary);
            CheckOversizedCurrentPlayback(temporary);
            CheckRealMcallGallery(sampleRoot, temporary);
            CheckNativePlaneEditor(temporary);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void CheckLegacyMeffGallery(string temporary)
    {
        var root = Path.Combine(temporary, "非零目录尾部Meff");
        var e5 = Path.Combine(root, "E5");
        Directory.CreateDirectory(e5);
        var path = Path.Combine(e5, "Meff.e5");
        var package = new byte[62];
        package[0] = 2; package[1] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(4), 50);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(package.AsSpan(12), 1);
        package[21] = 255; package[40] = 1; package[41] = 255;
        package[60] = 64; package[61] = 128;
        var firstOffset = E5Archive.IndexOffset + E5Archive.EntrySize + 4;
        var bytes = new byte[firstOffset + package.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset), package.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 4), package.Length);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(E5Archive.IndexOffset + 8), firstOffset);
        bytes[firstOffset - 4] = 0xAB; bytes[firstOffset - 1] = 0xCD;
        package.CopyTo(bytes, firstOffset);
        File.WriteAllBytes(path, bytes);
        var before = Sha(path);
        TestSupport.Assert(!E5Archive.TryOpen(path, out _, out var strictError) && strictError.Contains("四字节尾部", StringComparison.Ordinal),
            "Meff回归样本未覆盖普通E5严格校验拒绝的非零目录尾部");
        var project = new ProjectProfile(root, "Meff非零尾部回归", ProjectVersion.Ccz65, false,
            new Dictionary<string, string> { ["Meff.e5"] = path }, []);
        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(project).Single(item => item.Key == "Meff.e5");
        var asset = catalog.ReadAssets(project, resource).Single();
        TestSupport.Assert(StrategyAnimationResourceCodec.ReadPackageBytes(asset.Frames[0]).SequenceEqual(package),
            "Meff包公共读取路径改变了原始载荷");
        using var previews = new ImagePreviewService();
        using var thumbnail = previews.GetThumbnailAsync(project, catalog, asset, asset.Frames[0], new Size(48, 48), true)
            .GetAwaiter().GetResult();
        TestSupport.Assert(thumbnail != null, "非零目录尾部Meff没有缩略图");
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        page.LoadProject(project, [resource]);
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => page.LoadingTask.IsCompleted && gallery.RealizedCount == 1, "非零目录尾部Meff没有图库条目");
        Preview(gallery, 0);
        WaitUntil(() => GetAssetId(player) == asset.StablePhysicalId && !Field<bool>(player, "_loading"),
            "非零目录尾部Meff点击预览未完成");
        TestSupport.Assert(Frames(player).Count == 2 && Field<System.Windows.Forms.Timer>(player, "_timer").Enabled &&
            Field<System.Windows.Forms.Timer>(player, "_timer").Interval == 50,
            "Meff帧已解码但播放间隔读取仍错误经过普通E5严格目录校验：" + Field<Label>(player, "_status").Text);
        using var native = (Bitmap)typeof(ImageGridPage).GetMethod("LoadAnimationPlane", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [asset.Frames[0]])!;
        TestSupport.Assert(native.Size == new Size(1, 1), "Meff原生平面读取仍经过普通E5严格目录校验");
        host.Close();
        TestSupport.Assert(Sha(path) == before, "Meff缩略图、点击播放或原生平面读取改动了源文件");
        Console.WriteLine("非零目录尾部Meff：普通E5严格拒绝、动画缩略图/点击播放50ms/原生平面读取通过，源SHA不变。");
    }

    private static void CheckRealWeatherAndStrategy(string sampleRoot, string temporary)
    {
        var project = new ProjectProfileService().Detect(sampleRoot);
        var catalog = new ImageCatalogService();
        var allResources = catalog.BuildCatalog(project);
        var weather = allResources.Single(resource => resource.Kind == ResourceKind.WeatherSpecial);
        var strategy = allResources.First(resource => resource.Key == "Meff.e5");
        var mcall = allResources.First(resource => resource.Kind == ResourceKind.McallAnimation && resource.CanPreview);
        var paths = new[] { weather, strategy, mcall }.SelectMany(resource => resource.Paths).Distinct().ToArray();
        var before = paths.ToDictionary(path => path, Sha);
        using var previews = new ImagePreviewService();
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        page.LoadProject(project, [weather, strategy, mcall]);
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => gallery.RealizedCount > 0, "真实天气没有创建可见卡片");
        var realized = gallery.RealizedControls.ToArray();
        Select(gallery, 1);
        PumpFor(80);
        TestSupport.Assert(GetAssetId(player) == null && Frames(player).Count == 0 &&
            realized.SequenceEqual(gallery.RealizedControls), "勾选天气触发了预览或图库重建");
        Preview(gallery, 1);
        WaitUntil(() => Frames(player).Count == 4 && Field<System.Windows.Forms.Timer>(player, "_timer").Enabled,
            "点击天气没有自动解码并循环播放四帧");
        TestSupport.Assert(Frames(player).All(frame => frame.Bitmap.Size == new Size(216, 50)),
            "天气动画被当成216×200整张长条播放");
        var picture = Field<PictureBox>(player, "_picture");
        TestSupport.Assert(picture.Image?.Size == new Size(216, 50), "天气播放器没有显示单帧图片");
        CheckPreviewLayouts(page, host, gallery, player, "天气", true);
        ClickButton(player, "_toggle");
        var timer = Field<System.Windows.Forms.Timer>(player, "_timer");
        TestSupport.Assert(!timer.Enabled, $"暂停按钮未停止播放计时器；按钮可见={Field<Button>(player, "_toggle").Visible}，启用={Field<Button>(player, "_toggle").Enabled}，播放器={player.Size}，分隔={Field<SplitContainer>(page, "_previewSplit").SplitterDistance}");
        var index = Field<int>(player, "_index");
        PumpFor(230);
        TestSupport.Assert(Field<int>(player, "_index") == index, "暂停后帧号仍然变化");
        ClickButton(player, "_next");
        TestSupport.Assert(Field<int>(player, "_index") == (index + 1) % 4 && !timer.Enabled,
            "下一帧没有停播后准确前进一帧");
        Field<NumericUpDown>(player, "_interval").Value = 180;
        TestSupport.Assert(timer.Interval == 180, "播放速度没有同步计时器");
        index = Field<int>(player, "_index");
        ClickButton(player, "_toggle");
        WaitUntil(() => Field<int>(player, "_index") != index, "恢复播放后没有自动前进");
        var selectedId = GetAssetId(player);
        Selector(gallery, selectedId!).Checked = false;
        PumpFor(80);
        TestSupport.Assert(GetAssetId(player) == selectedId && Frames(player).Count == 4 && timer.Enabled && picture.Image != null,
            "取消批量勾选干扰了当前预览");
        var resourceSelector = Field<ComboBox>(page, "_resourceSelector");
        foreach (var resourceIndex in new[] { 1, 2 })
        {
            resourceSelector.SelectedIndex = resourceIndex;
            WaitUntil(() => page.LoadingTask.IsCompleted, "策略资源索引未完成");
            WaitUntil(() => gallery.RealizedCount > 0, "策略资源切换后没有可见卡片");
            var first = gallery.RealizedControls.First();
            var asset = (LogicalImageAsset)first.Tag!;
            ClickThumbnail(first);
            WaitUntil(() => GetAssetId(player) == asset.StablePhysicalId && Frames(player).Count > 0,
                "策略大/小动画没有按记录解码播放：" + asset.DisplayName);
            TestSupport.Assert(Frames(player).All(frame => frame.Bitmap.Width > 0 && frame.Bitmap.Height > 0),
                "策略动画存在无效帧");
            Console.WriteLine($"真实动画 {asset.ResourceKey}，播放帧={Frames(player).Count}，画布={Frames(player)[0].Bitmap.Size}");
        }
        host.Close();
        foreach (var path in paths)
            TestSupport.Assert(before[path] == Sha(path), "只读UI播放改动了源资源：" + path);
        Console.WriteLine("真实 Weather 四帧、暂停/下一帧/速度、Meff/Mcall 自动播放和源文件 SHA 检查通过。");
    }

    private static void CheckLateDecodesAndDisposal(string temporary)
    {
        using var catalog = new ControlledCatalog();
        using var previews = new ImmediatePreview();
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        var project = new ProjectProfile(temporary, "取消测试", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        page.LoadProject(project, catalog.Resources);
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => gallery.RealizedCount == 2, "受控动画卡片未显示");
        Select(gallery, 1);
        PumpFor(60);
        TestSupport.Assert(catalog.Calls.Count == 0, "勾选受控动画触发了解码");
        Preview(gallery, 1);
        WaitUntil(() => catalog.Calls.Count == 1, "旧动画解码没有启动");
        var first = catalog.Calls.First();
        Preview(gallery, 2);
        WaitUntil(() => catalog.Calls.Count == 2, "新动画解码没有启动");
        var second = catalog.Calls.Last(); second.Release.Set();
        WaitUntil(() => GetAssetId(player) == "Test:A:2" && Frames(player).Count == 4,
            "最新选中的动画未回填");
        first.Release.Set();
        WaitUntil(() => first.Finished.IsSet && first.Frames != null && first.Frames.All(frame => IsDisposed(frame.Bitmap)),
            "迟到的旧动画没有释放位图");
        TestSupport.Assert(GetAssetId(player) == "Test:A:2" && Frames(player).Count == 4,
            "旧解码结果覆盖了新动画");
        Preview(gallery, 2);
        Preview(gallery, 2);
        PumpFor(50);
        TestSupport.Assert(catalog.Calls.Count == 2, "重复单击/双击当前动画重新解码");
        var resources = Field<ComboBox>(page, "_resourceSelector");
        resources.SelectedIndex = 1;
        WaitUntil(() => page.LoadingTask.IsCompleted, "第二资源索引未完成");
        WaitUntil(() => gallery.RealizedCount == 1, "第二资源没有显示");
        Preview(gallery, 3);
        WaitUntil(() => catalog.Calls.Count == 3, "资源取消测试没有开始解码");
        var third = catalog.Calls.Last();
        resources.SelectedIndex = 0;
        WaitUntil(() => page.LoadingTask.IsCompleted, "第一资源索引未完成");
        third.Release.Set();
        WaitUntil(() => third.Finished.IsSet && third.Frames != null && third.Frames.All(frame => IsDisposed(frame.Bitmap)),
            "切换资源后后台动画没有被丢弃释放");
        TestSupport.Assert(GetAssetId(player) == null && Frames(player).Count == 0,
            "切换资源后的旧结果重新显示在播放器");
        WaitUntil(() => gallery.RealizedCount == 2, "关闭取消测试未重建卡片");
        Preview(gallery, 1);
        WaitUntil(() => catalog.Calls.Count == 4, "关闭取消测试没有开始解码");
        var fourth = catalog.Calls.Last();
        page.Dispose(); fourth.Release.Set();
        WaitUntil(() => fourth.Finished.IsSet && fourth.Frames != null && fourth.Frames.All(frame => IsDisposed(frame.Bitmap)),
            "控件释放后的迟到结果没有释放");
        TestSupport.Assert(player.IsDisposed, "页面释放后子播放器仍存活");
        Console.WriteLine("动画连续选择、切换资源、取消与 Dispose 的迟到结果门禁及 Bitmap 释放通过。");
    }

    private static void CheckHiddenPendingPlayback(string temporary)
    {
        using var catalog = new ControlledCatalog();
        using var previews = new ImmediatePreview();
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        var project = new ProjectProfile(temporary, "隐藏待完成播放", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        page.LoadProject(project, catalog.Resources.Take(1));
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => gallery.RealizedCount == 2, "隐藏恢复测试没有创建卡片");
        Preview(gallery, 1);
        WaitUntil(() => catalog.Calls.Count == 1, "A未开始解码");
        catalog.Calls.First().Release.Set();
        WaitUntil(() => GetAssetId(player) == "Test:A:1" && Frames(player).Count == 4, "A未完成播放");
        Preview(gallery, 2);
        WaitUntil(() => catalog.Calls.Count == 2, "B慢解码未启动");
        var cancelled = catalog.Calls.Last();
        page.SetActive(false); page.Visible = false;
        page.Visible = true; page.ActivateGallery();
        WaitUntil(() => catalog.Calls.Count == 3, "返回页面没有重新读取被取消的B焦点");
        var resumed = catalog.Calls.Last();
        resumed.Release.Set();
        WaitUntil(() => GetAssetId(player) == "Test:A:2" && Frames(player).Count == 4 &&
            Frames(player).All(frame => frame.Descriptor.ImageNumber == 2), "B恢复后仍显示A旧帧");
        cancelled.Release.Set();
        WaitUntil(() => cancelled.Finished.IsSet && cancelled.Frames != null && cancelled.Frames.All(frame => IsDisposed(frame.Bitmap)),
            "隐藏时取消的B旧结果没有释放");
        TestSupport.Assert(GetAssetId(player) == "Test:A:2" && Frames(player).All(frame => frame.Descriptor.ImageNumber == 2),
            "被取消的旧请求覆盖了恢复后的B");
        Console.WriteLine("A播放→B慢解码→隐藏取消→返回B正确恢复，以及迟到结果释放通过。");
    }

    private static void CheckMcallAggregation(string temporary)
    {
        var catalog = new McallCatalog(temporary);
        using var previews = new ImmediatePreview();
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        var project = new ProjectProfile(temporary, "大动画汇总", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        page.LoadProject(project, catalog.Resources);
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => page.LoadingTask.IsCompleted && gallery.RealizedCount == 3, "全部 Mcall 没有一次显示三个文件");
        var selector = Field<ComboBox>(page, "_resourceSelector");
        TestSupport.Assert(selector.SelectedIndex == 0 && selector.Items[0]!.ToString() == "全部大动画" &&
            gallery.RealizedControls.Select(card => ((LogicalImageAsset)card.Tag!).LogicalId).SequenceEqual([1, 2, 10]),
            "大动画默认视图或数值排序不正确");
        Preview(gallery, 2);
        TestSupport.Assert(Descendants(player).OfType<Label>().Any(label => label.Text.Contains("测试坏包", StringComparison.Ordinal)),
            "坏动画点击后没有显示明确失败原因");
        page.SetActive(false); page.Visible = false;
        page.Visible = true; page.ActivateGallery();
        TestSupport.Assert(Descendants(player).OfType<Label>().Any(label => label.Text.Contains("测试坏包", StringComparison.Ordinal)) &&
            Frames(player).Count == 0 && !Field<System.Windows.Forms.Timer>(player, "_timer").Enabled,
            "失败项返回页面后丢失诊断或显示了旧帧");
        Preview(gallery, 10);
        WaitUntil(() => GetAssetId(player) == "Mcall:10:10" && Frames(player).Count == 2, "坏包阻止了后续大动画播放");
        Preview(gallery, 1);
        WaitUntil(() => GetAssetId(player) == "Mcall:1:1" && Frames(player).Count == 2, "未播放第一个有效动画");
        var calls = catalog.DecodeCalls;
        Preview(gallery, 10);
        WaitUntil(() => GetAssetId(player) == "Mcall:10:10" && Frames(player).Count == 2, "最近动画缓存没有回填");
        TestSupport.Assert(catalog.DecodeCalls == calls, "在两个动画之间切换重新解码");
        var cards = gallery.RealizedControls.ToArray();
        gallery.SelectAll(true);
        gallery.SelectAll(false);
        TestSupport.Assert(cards.SequenceEqual(gallery.RealizedControls) && GetAssetId(player) == "Mcall:10:10" && catalog.DecodeCalls == calls,
            "全选/取消全选修改了预览、卡片或解码次数");
        var split = Field<SplitContainer>(page, "_previewSplit");
        split.SplitterDistance -= 18;
        PumpFor(60);
        TestSupport.Assert(cards.SequenceEqual(gallery.RealizedControls) && catalog.DecodeCalls == calls,
            "拖动预览分隔条重建了可保留卡片或重新解码");
        page.SetActive(false);
        TestSupport.Assert(!Field<System.Windows.Forms.Timer>(player, "_timer").Enabled, "隐藏页面没有停止播放");
        selector.SelectedIndex = 3;
        WaitUntil(() => page.LoadingTask.IsCompleted && gallery.ItemCount == 1, "单文件大动画筛选失败");
        TestSupport.Assert(((LogicalImageAsset)gallery.RealizedControls.Single().Tag!).LogicalId == 10,
            "单文件筛选没有保留真实来源编号");
        Console.WriteLine("Mcall 全部汇总、数值排序、坏项隔离、单文件筛选、点击焦点、两项播放缓存和稳定卡片通过。");
    }

    private static void CheckOversizedCurrentPlayback(string temporary)
    {
        var catalog = new McallCatalog(temporary);
        var project = new ProjectProfile(temporary, "超限当前预览", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        var type = typeof(ImageGridPage).Assembly.GetType("ImageManager.App.SelectedAnimationPreview")!;
        // A tiny injected budget exercises the same branch without allocating 128 MiB in a control regression.
        using var player = (Control)Activator.CreateInstance(type, [8L])!;
        using var host = new Form { Location = new Point(-30000, -30000), Size = new Size(800, 600), ShowInTaskbar = false };
        host.Controls.Add(player); host.Show(); Application.DoEvents();
        var bind = type.GetMethod("BindAsync")!;
        var first = catalog.ReadAssets(project, catalog.Resources.Single(resource => resource.Key == "Mcall:1")).Single();
        var second = catalog.ReadAssets(project, catalog.Resources.Single(resource => resource.Key == "Mcall:10")).Single();
        var task = (Task)bind.Invoke(player, [project, catalog, first])!;
        WaitUntil(() => task.IsCompleted, "超限当前动画没有完成读取");
        task.GetAwaiter().GetResult();
        var retired = Frames(player).Select(frame => frame.Bitmap).ToArray();
        TestSupport.Assert(Frames(player).Count == 2 && Field<System.Windows.Forms.Timer>(player, "_timer").Enabled,
            "超过缓存预算的当前动画被错误禁止播放");
        task = (Task)bind.Invoke(player, [project, catalog, second])!;
        WaitUntil(() => task.IsCompleted, "第二个超限动画未完成");
        TestSupport.Assert(retired.All(IsDisposed), "超限旧动画作为最近缓存继续驻留");
        task = (Task)bind.Invoke(player, [project, catalog, first])!;
        WaitUntil(() => task.IsCompleted, "返回超限动画未完成");
        TestSupport.Assert(catalog.DecodeCalls == 3, "超限动画被保留到了最近缓存");
        Console.WriteLine("超过预算的当前动画仍可播放，离开后释放、不进入最近缓存通过。");
    }

    private static void CheckRealMcallGallery(string sampleRoot, string temporary)
    {
        var project = new ProjectProfileService().Detect(sampleRoot);
        var catalog = new ImageCatalogService();
        var resources = catalog.BuildCatalog(project).Where(resource => resource.Kind == ResourceKind.McallAnimation).ToArray();
        using var previews = new ImagePreviewService();
        using var page = CreatePage(catalog, previews, temporary);
        using var host = Open(page);
        page.LoadProject(project, resources);
        var gallery = Field<VirtualImageGallery>(page, "_gallery");
        var player = Field<Control>(page, "_animationPreview");
        WaitUntil(() => page.LoadingTask.IsCompleted && gallery.RealizedCount > 0, "真实全部 Mcall 没有显示");
        TestSupport.Assert(gallery.ItemCount == resources.Length, "全部大动画漏掉了真实 Mcall 文件");
        var ordered = resources.OrderBy(resource =>
            int.Parse(new string(Path.GetFileNameWithoutExtension(resource.Paths[0]).Where(char.IsDigit).ToArray()))).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var resource = ordered[index];
            gallery.ScrollToItem(index);
            WaitUntil(() => gallery.RealizedControls.Any(card => ((LogicalImageAsset)card.Tag!).ResourceKey == resource.Key),
                $"真实 Mcall {resource.Key} 无法滚动到达");
            var card = gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).ResourceKey == resource.Key);
            var asset = (LogicalImageAsset)card.Tag!;
            ClickThumbnail(card);
            WaitUntil(() => GetAssetId(player) == asset.StablePhysicalId && !Field<bool>(player, "_loading"),
                $"真实 Mcall {resource.Key} 点击未完成读取");
            TestSupport.Assert(Frames(player).Count > 0, $"真实 Mcall {resource.Key} 点击后没有可播放帧");
            Console.WriteLine($"真实全部大动画逐项点击 {index + 1}/{ordered.Length} {Path.GetFileName(resource.Paths[0])}，播放帧={Frames(player).Count}");
        }
        gallery.ScrollToItem(0);
        var first = gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).ResourceKey == ordered[0].Key);
        var firstId = ((LogicalImageAsset)first.Tag!).StablePhysicalId;
        ClickThumbnail(first);
        WaitUntil(() => GetAssetId(player) == firstId && !Field<bool>(player, "_loading") && Frames(player).Count > 0,
            "真实聚合 Mcall 返回首项未开始播放");
        CheckPreviewLayouts(page, host, gallery, player, "全部大动画", false);
        gallery.ScrollToItem(gallery.ItemCount - 1);
        WaitUntil(() => gallery.RealizedControls.Any(card => ((LogicalImageAsset)card.Tag!).ResourceKey ==
            ordered.Last().Key),
            "真实 Mcall 滚动无法到达数值排序最后的文件");
        Console.WriteLine($"真实全部大动画 {gallery.ItemCount} 文件聚合、逐项点击播放、末项可达，以及天气/大动画 1280/1920 字体 100/125/150 布局通过。");
    }

    private static void CheckPreviewLayouts(ImageGridPage page, Form host, VirtualImageGallery gallery, Control player, string label, bool weather)
    {
        var originalSize = host.ClientSize;
        var originalFont = page.Font;
        var originalMaximum = host.MaximumSize;
        var originalBorder = host.FormBorderStyle;
        var originalScaleMode = host.AutoScaleMode;
        var fonts = new List<Font>();
        var split = Field<SplitContainer>(page, "_previewSplit");
        TestSupport.Assert(split.Orientation == (weather ? Orientation.Horizontal : Orientation.Vertical) && !split.Panel2Collapsed,
            label + "预览方向错误或预览区被隐藏");
        try
        {
            host.AutoScaleMode = AutoScaleMode.None;
            host.FormBorderStyle = FormBorderStyle.None;
            host.MaximumSize = new Size(4096, 2304);
            foreach (var scale in new[] { 1f, 1.25f, 1.5f })
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
                var font = new Font(originalFont.FontFamily, originalFont.Size * scale, originalFont.Style);
                fonts.Add(font);
                page.Font = font;
                host.ClientSize = size;
                host.PerformLayout();
                PumpFor(70);
                if (host.ClientSize != size)
                {
                    host.ClientSize = size;
                    PumpFor(30);
                }
                var actual = host.ClientSize;
                Console.WriteLine($"资源UI布局 {label} 请求={size.Width}x{size.Height} 实际ClientSize={actual.Width}x{actual.Height} 字体={(int)(scale * 100)}%");
                TestSupport.Assert(actual == size, $"{label} 截图宿主没有使用请求的窗口尺寸：请求{size}，实际{actual}");
                var picture = Field<PictureBox>(player, "_picture");
                var viewport = picture.Parent!;
                var image = picture.Image!;
                TestSupport.Assert(image != null && picture.Width > 0 && picture.Height > 0 && picture.Left >= 0 && picture.Top >= 0 &&
                    picture.Right <= viewport.ClientSize.Width + 1 && picture.Bottom <= viewport.ClientSize.Height + 1 &&
                    Math.Abs((long)picture.Width * image.Height - (long)picture.Height * image.Width) <= Math.Max(image.Width, image.Height),
                    $"{label} {size} 字体{scale:P0} 预览裁切或比例错误");
                var canvas = Field<Panel>(gallery, "_canvas");
                TestSupport.Assert(!gallery.HorizontalScroll.Visible,
                    $"{label} {size} 图库出现多余水平滚动条；Client={gallery.ClientSize}, Padding={gallery.Padding}, Canvas={canvas.Bounds}, Extent={gallery.AutoScrollMinSize}, Display={gallery.DisplayRectangle}");
                if (weather)
                {
                    TestSupport.Assert(split.Panel2.Width == split.Width && player.Top >= 0 && player.Height > 100,
                        "天气没有使用横向底部独立预览");
                    var id = GetAssetId(player);
                    var frameList = Frames(player);
                    var before = split.SplitterDistance;
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, before - 15);
                    PumpFor(30);
                    TestSupport.Assert(GetAssetId(player) == id && ReferenceEquals(frameList, Frames(player)),
                        "拖动天气预览高度重新解码或改变了焦点");
                    split.SplitterDistance = before;
                }
                var output = Environment.GetEnvironmentVariable("DATAMANAGER_UI_SNAPSHOTS");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    TestSupport.Assert(page.ClientSize == actual,
                        $"{label} 页面实际尺寸未跟随截图宿主：页面{page.ClientSize}，请求{actual}");
                    // A top-level off-screen Form can report a cached ClientSize larger
                    // than its native window bounds. Render the full child page so the
                    // saved image uses the exact client layout tested above.
                    using var bitmap = new Bitmap(actual.Width, actual.Height);
                    page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, actual));
                    bitmap.Save(Path.Combine(output, $"资源UI-{label}-{actual.Width}x{actual.Height}-字体{(int)(scale * 100)}.png"),
                        System.Drawing.Imaging.ImageFormat.Png);
                }
                page.Font = originalFont;
            }
        }
        finally
        {
            foreach (var child in Descendants(page).Prepend(page)) child.Font = originalFont;
            host.FormBorderStyle = originalBorder;
            host.MaximumSize = originalMaximum;
            host.ClientSize = originalSize;
            host.AutoScaleMode = originalScaleMode;
            PumpFor(40);
            foreach (var font in fonts) font.Dispose();
        }
    }

    private static void CheckNativePlaneEditor(string temporary)
    {
        var bytes = new byte[86];
        bytes[0] = 3; bytes[1] = 2; bytes[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1);
        bytes[20] = 1; bytes[23] = 8;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 3);
        bytes[40] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52), 1);
        bytes[60] = 1; bytes[80] = 1; bytes[81] = 2; bytes[82] = 3; bytes[83] = 4; bytes[84] = 5; bytes[85] = 6;
        var path = Path.Combine(temporary, "Mcall99.e5"); File.WriteAllBytes(path, bytes);
        var palette = new byte[1024]; palette[12] = 10; palette[13] = 20; palette[14] = 30;
        File.WriteAllBytes(Path.Combine(temporary, "tsb"), palette);
        var project = new ProjectProfile(temporary, "原生平面测试", ProjectVersion.Ccz65, false,
            new Dictionary<string, string> { ["Mcall:99"] = path }, []);
        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(project).Single(item => item.Kind == ResourceKind.McallAnimation);
        var asset = catalog.ReadAssets(project, resource).Single();
        using var previews = new ImagePreviewService();
        using var page = CreatePage(catalog, previews, temporary);
        page.LoadProject(project, [resource]);
        using var package = new StrategyAnimationPackageCodec().Decode(project, bytes);
        Console.WriteLine($"合成播放画布 {package.Frames[0].Size}，Alpha={package.Frames[0].GetPixel(3, 0).A}，原生={package.PlaneFrames[1].GetPixel(0, 0)}");
        TestSupport.Assert(package.Frames[0].Size == new Size(5, 2) && package.Frames[0].GetPixel(3, 0).A == 132,
            "合成测试没有产生带坐标与透明度的播放画布");
        using var edit = (Bitmap)typeof(ImageGridPage).GetMethod("LoadAnimationPlane", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [asset.Frames[0]])!;
        var native = package.PlaneFrames[package.PlaybackSequence[0]];
        TestSupport.Assert(edit.Size == new Size(2, 1) && ReferenceAtlasService.PixelHash(edit) == ReferenceAtlasService.PixelHash(native),
            "像素编辑错误读取播放画布，坐标/透明度被烘焙进原始平面");
        Console.WriteLine("Mcall 公共播放画布 5×2 / 原生编辑平面 2×1 区分通过。");
    }

    private static ImageGridPage CreatePage(IImageCatalogService catalog, IImagePreviewService previews, string temporary)
    {
        var history = new HistoryService(Path.Combine(temporary, "历史"));
        return new ImageGridPage("动画测试", catalog, new ImageMutationService(history), new ReferenceAtlasService(),
            new RsImportService(), new BatchRenameService(history), previews,
            ImagePageCapabilities.Play | ImagePageCapabilities.Export | ImagePageCapabilities.BackgroundToggle);
    }

    private static Form Open(ImageGridPage page)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000),
            ShowInTaskbar = false, Size = new Size(1100, 740) };
        form.Controls.Add(page); form.Show(); Application.DoEvents(); return form;
    }

    private static void Select(VirtualImageGallery gallery, int id)
        => gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).LogicalId == id)
            .Controls.OfType<CheckBox>().Single(check => Equals(check.Tag, "selector")).Checked = true;
    private static void Preview(VirtualImageGallery gallery, int id)
        => ClickThumbnail(gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).LogicalId == id));
    private static void ClickThumbnail(Control card)
    {
        var target = Descendants(card).OfType<PictureBox>().FirstOrDefault() ?? card;
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [EventArgs.Empty]);
    }
    private static void ClickButton(Control player, string field)
        => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Field<Button>(player, field), [EventArgs.Empty]);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static CheckBox Selector(VirtualImageGallery gallery, string physicalId)
        => gallery.RealizedControls.Single(card => ((LogicalImageAsset)card.Tag!).StablePhysicalId == physicalId)
            .Controls.OfType<CheckBox>().Single(check => Equals(check.Tag, "selector"));
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static IReadOnlyList<DecodedFrame> Frames(Control player) => Field<IReadOnlyList<DecodedFrame>>(player, "_frames");
    private static string? GetAssetId(Control player) => (string?)player.GetType().GetProperty("AssetId")!.GetValue(player);
    private static bool IsDisposed(Bitmap bitmap)
    {
        // Width calls GDI+ and races the worker's Bitmap.Dispose(). Read the
        // managed native-handle field instead; do not invoke GDI on a bitmap
        // whose lifetime is deliberately controlled by another thread.
        var field = typeof(Image).GetField("_nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(Image).GetField("nativeImage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("此运行时没有可验证的 Image 原生句柄字段。");
        var value = field.GetValue(bitmap);
        var handle = value is IntPtr address ? address : value is Pointer
            ? (IntPtr)typeof(Pointer).GetMethod("GetPointerValue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(value, null)!
            : throw new InvalidOperationException("Image 原生句柄字段的运行时表示未识别。");
        return handle == IntPtr.Zero;
    }
    private static string Sha(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    private static void WaitUntil(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message);
            Application.DoEvents(); Thread.Sleep(5);
        }
        Application.DoEvents();
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }

    private sealed class ImmediatePreview : IImagePreviewService
    {
        public Task<Bitmap?> GetThumbnailAsync(ProjectProfile project, IImageCatalogService catalog, LogicalImageAsset asset,
            FrameDescriptor descriptor, Size size, bool showBackground, CancellationToken cancellationToken = default)
            => Task.FromResult<Bitmap?>(new Bitmap(2, 1));
        public void InvalidatePaths(IEnumerable<string> paths) { }
        public void Dispose() { }
    }

    private sealed class ControlledCatalog : IImageCatalogService, IDisposable
    {
        public readonly ConcurrentQueue<DecodeCall> Calls = new();
        public IReadOnlyList<ImageResourceDescriptor> Resources { get; } = new[] { Resource("Test:A"), Resource("Test:B") };
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => Resources;
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
            => (resource.Key == "Test:A" ? new[] { 1, 2 } : new[] { 3 }).Select(id => new LogicalImageAsset(resource.Key, id,
                "测试动画" + id, ImageCategory.Dynamic, ResourceKind.WeatherSpecial,
                [new("Weather.e5", "只读合成.e5", id, 0, 1, "weather", "none", 2, 1)], [], false, false, "")).ToArray();
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        {
            var call = new DecodeCall(); Calls.Enqueue(call);
            try
            {
                if (!call.Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("受控解码未释放");
                call.Frames = Enumerable.Range(0, 4).Select(index => new DecodedFrame(asset.Frames[0] with { FrameIndex = index }, new Bitmap(2, 1))).ToArray();
                return call.Frames;
            }
            finally { call.Finished.Set(); }
        }
        private static ImageResourceDescriptor Resource(string key)
            => new(key, key, ImageCategory.Dynamic, ResourceKind.WeatherSpecial, ["只读合成.e5"], 2, true, false, false, "", "");
        public void Dispose() { foreach (var call in Calls) call.Release.Set(); }
    }
    private sealed class DecodeCall
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Finished = new();
        public IReadOnlyList<DecodedFrame>? Frames;
    }

    private sealed class McallCatalog : IImageCatalogService
    {
        public int DecodeCalls;
        public IReadOnlyList<ImageResourceDescriptor> Resources { get; }
        public McallCatalog(string root)
        {
            Resources = new[] { 10, 2, 1 }.Select(id =>
            {
                var path = Path.Combine(root, "Mcall" + id + ".e5");
                File.WriteAllBytes(path, [1, 2, 3]);
                return new ImageResourceDescriptor("Mcall:" + id, "Mcall" + id, ImageCategory.StrategyAnimation,
                    ResourceKind.McallAnimation, [path], 1, id != 2, id != 2, id == 2, id == 2 ? "测试坏包" : "有效", "");
            }).ToArray();
        }
        public IReadOnlyList<ImageResourceDescriptor> BuildCatalog(ProjectProfile project) => Resources;
        public IReadOnlyList<LogicalImageAsset> ReadAssets(ProjectProfile project, ImageResourceDescriptor resource)
        {
            var id = int.Parse(resource.Key[6..]);
            return [new LogicalImageAsset(resource.Key, id, resource.DisplayName, resource.Category, resource.Kind,
                [new FrameDescriptor(resource.Key, resource.Paths.Single(), 0, 0, 1, "mcall", "none", 2, 1)], [], false, false, "")];
        }
        public IReadOnlyList<DecodedFrame> DecodeFrames(ProjectProfile project, LogicalImageAsset asset)
        {
            Interlocked.Increment(ref DecodeCalls);
            return [new(asset.Frames[0], new Bitmap(2, 1)), new(asset.Frames[0] with { FrameIndex = 1 }, new Bitmap(2, 1))];
        }
    }
}
