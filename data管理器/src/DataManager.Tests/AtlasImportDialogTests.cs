using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using ImageManager.App;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class AtlasImportDialogTests
{
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

    private static void Invoke(object target, string name)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, null);

    private static bool IsDisposed(Bitmap bitmap)
    {
        try { _ = bitmap.Width; return false; }
        catch (ArgumentException) { return true; }
    }

    private static void WaitUntil(Func<bool> completed, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!completed())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message);
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
