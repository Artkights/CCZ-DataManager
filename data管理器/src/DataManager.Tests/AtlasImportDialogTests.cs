using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class AtlasImportDialogTests
{
    public static void RunAcceptance(string repository, string? imageFilter = null)
    {
        var output = Path.Combine(repository, "data管理器", "验证", "参考图灵活回导-20261026");
        Directory.CreateDirectory(output);
        var project = new ProjectProfileService().Detect(Path.Combine(repository, "基底", "加强版6.5未加密版"));
        var catalog = new ImageCatalogService();
        var resource = catalog.BuildCatalog(project).Single(item => item.Category == ImageCategory.CharacterS);
        var asset = catalog.ReadAssets(project, resource).Single(item => item.LogicalId == 242);
        var frames = catalog.DecodeFrames(project, asset);
        var sourceBytes = frames.Select(frame => frame.Descriptor.SourcePath).Distinct()
            .ToDictionary(path => path, File.ReadAllBytes);
        var reports = new List<string>();
        try
        {
            var context = ReferenceAtlasContext.Create(asset, frames);
            using var host = new Form { Location = new Point(-30000, -30000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Size = new Size(200, 150) };
            Exception? failure = null;
            host.Shown += (_, _) => host.BeginInvoke((Action)(() =>
            {
                try
                {
                    foreach (var (file, name) in new[]
                    {
                        ("暗紫背景-2048.png", "暗紫回图"),
                        ("白底边框-663.png", "白底边框回图")
                    })
                    {
                        if (imageFilter != null && !name.Contains(imageFilter, StringComparison.Ordinal)) continue;
                        var path = Path.Combine(repository, "形象管理器", "src", "ImageManager.Tests", "Fixtures", "Atlas", file);
                        TestSupport.Assert(File.Exists(path), "真实回图不存在：" + path);
                        CheckRealImageEditing(context, path, output, name, reports);
                    }
                }
                catch (Exception error) { failure = error; }
                finally { host.Close(); }
            }));
            Application.Run(host);
            if (failure != null) throw new InvalidOperationException("真实回图 UI 验收失败。", failure);
            TestSupport.Assert(sourceBytes.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), "预览或格位编辑修改了游戏资源。");
            File.WriteAllText(Path.Combine(output, "UI交互验收.md"),
                "# 参考图灵活回导 UI 交互验收\n\n" + string.Join("\n", reports.Select(line => "- " + line)) +
                "\n- 三个游戏资源文件字节保持一致；测试只生成预览与布局测试清单。\n");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckRealImageEditing(ReferenceAtlasContext context, string path, string output, string name, List<string> reports)
    {
        using (var direct = new ReferenceAtlasService().Normalize(path, context, new AiAtlasNormalizationOptions()))
            File.WriteAllText(Path.Combine(output, name + "-Direct-initial.txt"),
                $"SourceSize={direct.SourceSize}\nROI={direct.SourceMapping?.EffectiveBounds}\nBackground={direct.BackgroundColor}\nBlocking={direct.HasBlockingIssues}\n" +
                string.Join("\n", direct.Diagnostics) + "\n" + string.Join("\n", direct.FrameDiagnostics.Where(item => item.Severity == AtlasIssueSeverity.Error).Select(item => item.FrameKey + " " + item.Message)));
        using var dialog = new AiAtlasImportDialog(new ReferenceAtlasService(), path, context)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000),
            ShowInTaskbar = false, ClientSize = new Size(1320, 860)
        };
        dialog.Show();
        WaitForResult(dialog);
        var initial = Field<AiAtlasNormalizationResult>(dialog, "_result");
        var initialOptions = Field<AiAtlasNormalizationOptions>(dialog, "_options");
        var targetHeader = Descendants(dialog).OfType<Label>().Single(label => label.Text.StartsWith("锁定目标：", StringComparison.Ordinal));
        var tooltip = Field<ToolTip>(dialog, "_targetToolTip").GetToolTip(targetHeader) ?? string.Empty;
        TestSupport.Assert(context.Frames.All(frame => tooltip.Contains(frame.Descriptor.SourcePath, StringComparison.Ordinal)), "锁定目标没有提示实际三文件路径。");
        File.WriteAllText(Path.Combine(output, name + "-UI-initial.txt"),
            $"Path={path}\nSHA256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}\n" +
            $"SourceSize={initial.SourceSize}\nROI={initial.SourceMapping?.EffectiveBounds}\nBackground={initial.BackgroundColor}\n" +
            $"Options.SourceMapping=null:{initialOptions.SourceMapping == null}\nSamples={initialOptions.BackgroundSamples.Count}\nTolerance={Field<NumericUpDown>(dialog, "_tolerance").Value}\n" +
            string.Join("\n", initial.Diagnostics) + "\n" + string.Join("\n", initial.FrameDiagnostics.Select(item => $"{item.FrameKey}: {item.Severity} {item.SourceBounds}: {item.Message}")));
        SaveDialog(dialog, Path.Combine(output, name + "-裁剪格位-1320.png"));
        TestSupport.Assert(initial.Frames.Count == 28 && !initial.HasBlockingIssues && initial.SourceMapping?.FrameRectangles.Count == 28,
            name + " 初次自动识别没有形成完整28帧：" + string.Join("；", initial.FrameDiagnostics.Where(item => item.Severity == AtlasIssueSeverity.Error)
                .Select(item => item.FrameKey + " " + item.Message)));
        dialog.ClientSize = new Size(980, 680);
        Application.DoEvents();
        var editor = Field<object>(dialog, "_sourceEditor");
        var coordinates = Field<NumericUpDown[]>(editor, "_coordinates");
        foreach (Control control in coordinates[0].Parent!.Controls)
            TestSupport.Assert(control.Bounds.Right <= control.Parent!.ClientSize.Width + 1 && control.Bounds.Bottom <= control.Parent.ClientSize.Height + 1,
                name + " 在980宽窗口中源矩形工具被截断：" + control.Text);
        var canvas = Field<Control>(editor, "_canvas");
        TestSupport.Assert(canvas.Height >= 180 && canvas.Width >= 500 && !Field<DataGridView>(dialog, "_diagnostics").Visible,
            name + " 的源图默认编辑区没有展开或画布过小。");
        SaveDialog(dialog, Path.Combine(output, name + "-裁剪格位-980.png"));
        var tabs = Descendants(dialog).OfType<TabControl>().Single(control => control.TabPages[0].Text == "裁剪与格位");
        tabs.SelectedIndex = 1;
        Application.DoEvents();
        TestSupport.Assert(Field<DataGridView>(dialog, "_diagnostics").Visible, "切换到像素页没有恢复逐帧诊断。");
        SaveDialog(dialog, Path.Combine(output, name + "-逐帧对比-980.png"));
        var pixelTabs = Descendants(dialog).OfType<TabControl>().Single(control => control.TabPages[0].Text == "逐帧对比与调整");
        pixelTabs.SelectedIndex = 1;
        Application.DoEvents();
        SaveDialog(dialog, Path.Combine(output, name + "-像素蒙版-980.png"));
        tabs.SelectedIndex = 0;
        Field<CheckBox>(dialog, "_expandSource").Checked = false;
        TestSupport.Assert(Field<DataGridView>(dialog, "_diagnostics").Visible, "取消放大编辑区没有恢复逐帧诊断。");
        Field<CheckBox>(dialog, "_expandSource").Checked = true;
        dialog.ClientSize = new Size(1320, 860);

        var options = Field<AiAtlasNormalizationOptions>(dialog, "_options");
        var scope = Field<ComboBox>(editor, "_scope");
        var originalMapping = EditorMapping(editor);
        options.BackgroundSamples.Add(new(originalMapping.EffectiveBounds.Left + 2, originalMapping.EffectiveBounds.Top + 2));
        options.BackgroundSamples.Add(new(originalMapping.EffectiveBounds.Right - 2, originalMapping.EffectiveBounds.Bottom - 2));
        scope.SelectedIndex = 0;
        var roi = originalMapping.EffectiveBounds;
        ApplyEditorRectangle(editor, new Rectangle(roi.X + 1, roi.Y + 1, roi.Width - 2, roi.Height - 2));
        TestSupport.Assert(options.BackgroundSamples.Count == 0 && options.SourceMapping!.FrameRectangles.Count == 0 && options.SourceMapping.ConfirmedFrames.Count == 0,
            "数字ROI变化没有清空旧格位与背景取色点。");
        WaitForResult(dialog);
        TestSupport.Assert(Field<AiAtlasNormalizationResult>(dialog, "_result").SourceMapping!.EffectiveBounds == new Rectangle(roi.X + 1, roi.Y + 1, roi.Width - 2, roi.Height - 2),
            "数字ROI修改没有传入归一化。");
        Field<Button>(editor, "_undoButton").PerformClick();
        WaitForResult(dialog);
        TestSupport.Assert(EditorMapping(editor).EffectiveBounds == roi, "撤回没有恢复有效画布。");

        var selected = context.Frames.Single(frame => frame.Descriptor.Action == "atk" && frame.Descriptor.FrameIndex == 8).Descriptor;
        var selectedKey = AiAtlasNormalizer.FrameKey(selected);
        Invoke(editor, "SelectFrame", selectedKey);
        scope.SelectedIndex = 3;
        var keys = (IReadOnlyList<string>)InvokeResult(editor, "SelectedKeys")!;
        TestSupport.Assert(keys.Count == 4, "当前攻击语义行没有包含四帧。");
        var before = EditorMapping(editor);
        var bounds = (Rectangle)InvokeResult(editor, "SelectionBounds")!;
        ApplyEditorRectangle(editor, new Rectangle(bounds.X + 1, bounds.Y, bounds.Width, bounds.Height));
        WaitForResult(dialog);
        var moved = EditorMapping(editor);
        TestSupport.Assert(keys.All(key => moved.FrameRectangles[key].X == before.FrameRectangles[key].X + 1 && moved.ConfirmedFrames.Contains(key)),
            "语义行数字平移没有一起变换四个源框并确认。");
        TestSupport.Assert((Field<DataGridView>(dialog, "_diagnostics").CurrentRow!.Tag as AiAtlasFrameDiagnostic)!.FrameKey == selectedKey,
            "重新处理后当前帧被错误重置。");
        Field<Button>(editor, "_undoButton").PerformClick();
        WaitForResult(dialog);
        TestSupport.Assert(keys.All(key => EditorMapping(editor).FrameRectangles[key] == before.FrameRectangles[key]), "格位撤回没有恢复语义行。");
        Field<Button>(editor, "_redoButton").PerformClick();
        WaitForResult(dialog);
        TestSupport.Assert(keys.All(key => EditorMapping(editor).FrameRectangles[key] == moved.FrameRectangles[key]), "格位重做没有恢复语义行。");
        Field<Button>(editor, "_undoButton").PerformClick();
        WaitForResult(dialog);
        scope.SelectedIndex = 4;
        bounds = EditorMapping(editor).FrameRectangles[selectedKey];
        var view = ((float Scale, PointF Offset))InvokeResult(canvas, "View")!;
        var center = new Point((int)Math.Round(view.Offset.X + (bounds.Left + bounds.Width / 2d) * view.Scale),
            (int)Math.Round(view.Offset.Y + (bounds.Top + bounds.Height / 2d) * view.Scale));
        Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0));
        Invoke(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, center.X + 6, center.Y, 0));
        Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, center.X + 6, center.Y, 0));
        WaitForResult(dialog);
        TestSupport.Assert(EditorMapping(editor).FrameRectangles[selectedKey].X > bounds.X &&
            EditorMapping(editor).FrameRectangles[selectedKey].Size == bounds.Size, "拖动源框没有按原图坐标平移或意外改变大小。");
        Field<Button>(editor, "_undoButton").PerformClick();
        WaitForResult(dialog);
        ApplyEditorRectangle(editor, new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height));
        WaitForResult(dialog);
        TestSupport.Assert(EditorMapping(editor).FrameRectangles[selectedKey].Width == bounds.Width - 1 &&
            EditorMapping(editor).ConfirmedFrames.Contains(selectedKey) && !EditorMapping(editor).RepairAspectRatio,
            "单帧非方形源框或默认等比模式没有保持。");
        Field<Button>(editor, "_undoButton").PerformClick();
        WaitForResult(dialog);
        scope.SelectedIndex = 1;
        Invoke(editor, "ConfirmSelection");
        WaitForResult(dialog);
        TestSupport.Assert(EditorMapping(editor).ConfirmedFrames.Count == 28, "全部格位确认遗漏帧。");

        var manifest = AtlasManifest.Create(context.Asset, new ReferenceAtlasLayoutPlanner().Plan(context.Asset, context.Frames), 1)
            with { SourcePhysicalImageNumber = 337, SourceResourceKeys = ["来源337-不作为写入目标.e5"] };
        var manifestPath = Path.Combine(output, "来源337-布局测试.atlas.json");
        manifest.Save(manifestPath);
        Invoke(dialog, "AssociateManifestFile", manifestPath);
        WaitForResult(dialog);
        var result = Field<AiAtlasNormalizationResult>(dialog, "_result");
        TestSupport.Assert(ReferenceEquals(Field<ReferenceAtlasContext>(dialog, "_context"), context) &&
            result.Frames.All(frame => frame.Descriptor.ImageNumber == context.PhysicalImageNumber) &&
            result.SourceMapping!.FrameRectangles.Keys.All(key => context.Frames.Any(frame => AiAtlasNormalizer.FrameKey(frame.Descriptor) == key)),
            "关联来源337清单改变了当前242目标。");
        TestSupport.Assert(options.GeometryMode == FrameAlignmentMode.None, "源矩形编辑启用了旧人物占位缩放。");
        Field<CheckBox>(dialog, "_reviewed").Checked = true;
        if (result.HasBlockingIssues)
        {
            scope.SelectedIndex = 1;
            Invoke(editor, "ConfirmSelection");
            WaitForResult(dialog);
            Field<CheckBox>(dialog, "_reviewed").Checked = true;
        }
        SaveDialog(dialog, Path.Combine(output, name + "-当前形象锁定-1320.png"));
        TestSupport.Assert(Field<Button>(dialog, "_confirm").Enabled, "检查警告并确认映射后仍不能生成写入候选。");
        Field<Button>(dialog, "_confirm").PerformClick();
        TestSupport.Assert(dialog.ConfirmedFrames.Count == 28 && dialog.ConfirmedFrames.All(frame => frame.Descriptor.ImageNumber == context.PhysicalImageNumber),
            "确认操作没有保持当前物理形象的28帧。");
        foreach (var frame in dialog.ConfirmedFrames) frame.Dispose();
        reports.Add($"{name}：28帧自动识别；1320×860与980×680控件布局；数字ROI清映射/取样；语义行/单帧变换与拖框；撤回重做；28帧确认；来源337清单保持目标242。通过。");
        dialog.Close();
    }

    private static void ApplyEditorRectangle(object editor, Rectangle rectangle)
    {
        var coordinates = Field<NumericUpDown[]>(editor, "_coordinates");
        var values = new[] { rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height };
        for (var index = 0; index < 4; index++) coordinates[index].Value = values[index];
        Invoke(editor, "ApplyCoordinates");
    }

    private static AtlasSourceMapping EditorMapping(object editor)
        => (AtlasSourceMapping)editor.GetType().GetProperty("Mapping")!.GetValue(editor)!;

    private static void WaitForResult(AiAtlasImportDialog dialog)
        => WaitUntil(() => !Field<bool>(dialog, "_processing") && Field<long>(dialog, "_successfulRevision") == Field<long>(dialog, "_normalizationRevision"),
            "真实回图后台处理没有完成。", 60);

    private static void SaveDialog(Form dialog, string path)
    {
        dialog.PerformLayout();
        Application.DoEvents();
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }

    public static void Run()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var host = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000),
            Size = new Size(300, 200), ShowInTaskbar = false
        };
        Exception? failure = null;
        host.Shown += (_, _) => host.BeginInvoke((Action)(() =>
        {
            try { RunOnMessageLoop(); }
            catch (Exception error) { failure = error; }
            finally { host.Close(); }
        }));
        Application.Run(host);
        if (failure != null) throw new InvalidOperationException("AI 参考图窗口回归失败。", failure);
    }

    private static void RunOnMessageLoop()
    {
        var frames = CreateFrames();
        try
        {
            var asset = new LogicalImageAsset("SComposite", 242, "S_242", ImageCategory.CharacterS,
                ResourceKind.SComposite, frames.Select(frame => frame.Descriptor).ToArray(), [], false, false, "");
            var context = ReferenceAtlasContext.Create(asset, frames, 2);
            CheckSinglePhysicalScope(context, frames);
            CheckRestartDiscardsLateResult(context);
            CheckCloseCancelsWorker(context);
            TestSupport.Assert(frames.All(frame => frame.Bitmap.Width > 0), "对话框释放了调用者拥有的参考帧");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void CheckSinglePhysicalScope(ReferenceAtlasContext context, IReadOnlyList<DecodedFrame> allFrames)
    {
        using var service = new ControlledAtlasService(autoRelease: true);
        using var dialog = Open(service, context, allFrames);
        WaitUntil(() => service.Calls.Count == 1 && Field<Button>(dialog, "_confirm").Enabled, "默认归一化未完成");
        var first = service.Calls.Single();
        TestSupport.Assert(first.Kernel == AtlasResamplingKernel.NearestNeighbor && first.Geometry == FrameAlignmentMode.None,
            "AI 导回仍默认模糊卷积或按原始轮廓缩放");
        TestSupport.Assert(first.TargetTurn == 1 && first.Turns.SequenceEqual(new[] { 1 }) && first.FrameCount == 28 &&
            context.PhysicalImageNumber == 242, "物理S_242导回没有保持单图28帧范围");
        var legacy = Field<CheckBox>(dialog, "_legacyThreeTurns");
        TestSupport.Assert(legacy.Parent == null && !legacy.Checked, "独立物理S不应显示跨物理三转组合开关");
        Field<Button>(dialog, "_confirm").PerformClick();
        TestSupport.Assert(dialog.DialogResult == DialogResult.OK && dialog.ConfirmedFrames.Count == 28 &&
                           dialog.ConfirmedFrames.All(frame => frame.Descriptor.Turn == 1 && frame.Descriptor.ImageNumber == 242),
            "确认写回没有保持物理S_242的真实帧描述符");
        foreach (var frame in dialog.ConfirmedFrames) frame.Dispose();
    }

    private static void CheckRestartDiscardsLateResult(ReferenceAtlasContext context)
    {
        using var service = new ControlledAtlasService(autoRelease: false, ignoreCancellation: true);
        using var dialog = Open(service, context);
        WaitUntil(() => service.Calls.Count == 1, "首次处理没有启动");
        var first = service.Calls.Single();
        Invoke(dialog, "Rerun");
        WaitUntil(() => service.Calls.Count == 2, "重新处理没有启动新请求");
        var second = service.Calls.Last();
        TestSupport.Assert(first.Token.IsCancellationRequested, "重新处理没有取消旧任务");
        TestSupport.Assert(!Field<Button>(dialog, "_confirm").Enabled, "后台处理期间仍可写回过期结果");
        second.Release.Set();
        try { WaitUntil(() => Field<Button>(dialog, "_confirm").Enabled, "新任务结果没有回填"); }
        catch
        {
            Console.WriteLine($"诊断：完成 {second.Finished.IsSet}，有结果 {second.Result != null}，取消 {second.Token.IsCancellationRequested}，" +
                $"UI结果 {Field<object?>(dialog, "_result") != null}，处理中 {Field<bool>(dialog, "_processing")}，" +
                $"修订 {Field<long>(dialog, "_normalizationRevision")}，成功修订 {Field<long>(dialog, "_successfulRevision")}，" +
                $"状态 {Field<Label>(dialog, "_status").Text}");
            throw;
        }
        var accepted = Field<AiAtlasNormalizationResult>(dialog, "_result");
        TestSupport.Assert(ReferenceEquals(accepted, second.Result), "写回候选不是最新处理结果");
        first.Release.Set();
        WaitUntil(() => first.Finished.IsSet, "旧任务未退出");
        WaitUntil(() => IsDisposed(first.Result!.SourcePreview), "迟到的旧任务结果未释放");
        TestSupport.Assert(ReferenceEquals(Field<AiAtlasNormalizationResult>(dialog, "_result"), accepted),
            "旧任务迟到回填并覆盖了新任务结果");
        dialog.Close();
    }

    private static void CheckCloseCancelsWorker(ReferenceAtlasContext context)
    {
        using var service = new ControlledAtlasService(autoRelease: false);
        var dialog = Open(service, context);
        WaitUntil(() => service.Calls.Count == 1, "关闭取消测试的后台任务没有启动");
        var call = service.Calls.Single();
        dialog.Close();
        dialog.Dispose();
        WaitUntil(() => call.Finished.IsSet, "关闭对话框没有停止后台任务");
        TestSupport.Assert(call.Token.IsCancellationRequested && call.Cancelled,
            "关闭窗口没有通过 CancellationToken 取消参考图归一化");
        TestSupport.Assert(dialog.ConfirmedFrames.Count == 0, "关闭窗口意外产生可写回帧");
    }

    private static AiAtlasImportDialog Open(ControlledAtlasService service, ReferenceAtlasContext context,
        IReadOnlyList<DecodedFrame>? legacy = null)
    {
        var dialog = new AiAtlasImportDialog(service, "测试参考图.png", context, legacy)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), ShowInTaskbar = false
        };
        dialog.Show();
        return dialog;
    }

    private static List<DecodedFrame> CreateFrames()
    {
        var frames = new List<DecodedFrame>();
        foreach (var action in new[] { (Name: "mov", Size: 48, Count: 11), (Name: "atk", Size: 64, Count: 12), (Name: "spc", Size: 48, Count: 5) })
        for (var index = 0; index < action.Count; index++)
        {
            var bitmap = new Bitmap(action.Size, action.Size);
            bitmap.SetPixel(3, 3, Color.Red);
            frames.Add(new(new("Unit_" + action.Name + ".e5", "只读样本.e5", 242, index, 1,
                action.Name, "前", bitmap.Width, bitmap.Height), bitmap));
        }
        return frames;
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object?[] arguments)
        => InvokeResult(target, name, arguments);

    private static object? InvokeResult(object target, string name, params object?[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, arguments);

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.Width; return false; }
        catch (ArgumentException) { return true; }
    }

    private static void WaitUntil(Func<bool> completed, string message, int seconds = 8)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new InvalidOperationException(message);
            Application.DoEvents(); Thread.Sleep(5);
        }
        Application.DoEvents();
    }

    private sealed class ControlledAtlasService(bool autoRelease, bool ignoreCancellation = false) : IReferenceAtlasService, IDisposable
    {
        internal readonly ConcurrentQueue<Call> Calls = new();
        public ReferenceAtlasExportResult Export(string directory, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames, Color key)
            => throw new NotSupportedException();
        public ReferenceAtlasExportResult Export(string directory, ReferenceAtlasContext context, Color key, ReferenceAtlasExportOptions? options = null)
            => throw new NotSupportedException();
        public AiAtlasNormalizationResult Normalize(string path, LogicalImageAsset asset, IReadOnlyList<DecodedFrame> frames, AiAtlasNormalizationOptions options)
            => throw new NotSupportedException("对话框必须使用包含当前转数的上下文接口。");
        public AiAtlasNormalizationResult Normalize(string path, ReferenceAtlasContext context, AiAtlasNormalizationOptions options,
            CancellationToken cancellationToken = default)
        {
            var call = new Call(context.TargetTurn, context.Frames.Count, context.Frames.Select(frame => frame.Descriptor.Turn).Distinct().Order().ToArray(),
                options.Kernel, options.GeometryMode, cancellationToken);
            Calls.Enqueue(call);
            try
            {
                if (!autoRelease)
                {
                    if (ignoreCancellation) call.Release.Wait(TimeSpan.FromSeconds(10));
                    else call.Release.Wait(cancellationToken);
                }
                if (!ignoreCancellation) cancellationToken.ThrowIfCancellationRequested();
                TestSupport.Assert(context.Frames.All(frame => frame.Bitmap.Width > 0), "后台任务引用的参考帧被 UI 提前释放");
                var outputFrames = context.Frames.Select(frame => new DecodedFrame(frame.Descriptor, new Bitmap(frame.Bitmap))).ToArray();
                call.Result = new()
                {
                    SourcePath = path, SourceSize = new Size(348, 348), TargetCanvasSize = 348,
                    BackgroundKind = AtlasBackgroundKind.KeyColor, BackgroundColor = Color.Magenta,
                    Frames = outputFrames, FrameDiagnostics = [], Diagnostics = [], SourcePreview = new Bitmap(4, 4),
                    MaskPreview = new Bitmap(4, 4), NormalizedAtlas = new Bitmap(4, 4)
                };
                return call.Result;
            }
            catch (OperationCanceledException) { call.Cancelled = true; throw; }
            finally { call.Finished.Set(); }
        }
        public void Dispose()
        {
            foreach (var call in Calls) call.Release.Set();
            WaitUntil(() => Calls.All(call => call.Finished.IsSet), "测试后台任务未清理");
            foreach (var call in Calls) { call.Release.Dispose(); call.Finished.Dispose(); }
        }
    }

    private sealed class Call(int targetTurn, int frameCount, int[] turns, AtlasResamplingKernel kernel,
        FrameAlignmentMode geometry, CancellationToken token)
    {
        internal int TargetTurn { get; } = targetTurn;
        internal int FrameCount { get; } = frameCount;
        internal int[] Turns { get; } = turns;
        internal AtlasResamplingKernel Kernel { get; } = kernel;
        internal FrameAlignmentMode Geometry { get; } = geometry;
        internal CancellationToken Token { get; } = token;
        internal ManualResetEventSlim Release { get; } = new(false);
        internal ManualResetEventSlim Finished { get; } = new(false);
        internal bool Cancelled { get; set; }
        internal AiAtlasNormalizationResult? Result { get; set; }
    }
}
