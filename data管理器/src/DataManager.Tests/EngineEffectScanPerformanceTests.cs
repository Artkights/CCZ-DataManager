using System.Diagnostics;
using System.Text.Json;
using DataManager.Core.Editing;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

/// <summary>
/// Public-service regression: independent snapshots, asynchronous execution and cancellation
/// must preserve the entire evidence report. External before/after JSON baselines are also
/// produced by the isolated performance harness; this test needs no historical executable.
/// </summary>
internal static class EngineEffectScanPerformanceTests
{
    public static void Run(string root)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var service = new EngineEffectScanService();
        var stages = new List<EngineEffectScanProgress>();
        var watch = Stopwatch.StartNew();
        var baseline = service.Scan(workspace, CancellationToken.None,
            new CallbackProgress(value => stages.Add(value)));
        var expected = JsonSerializer.Serialize(baseline);
        TestSupport.Assert(stages.Select(stage => stage.CompletedStages).SequenceEqual(Enumerable.Range(0, 10)) &&
                           stages.All(stage => stage.TotalStages == 9) &&
                           stages.Zip(stages.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds),
            "扫描阶段进度不连续或时间倒退。");

        // Separate scans on separate images must not share mutable decoder/CFG state.
        var scans = new[] { service.ScanAsync(workspace), service.ScanAsync(workspace) };
        Task.WhenAll(scans).GetAwaiter().GetResult();
        foreach (var scan in scans)
            TestSupport.Assert(JsonSerializer.Serialize(scan.Result) == expected,
                "并发扫描的完整证据报告与同步扫描不一致。");

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        AssertCancelled(() => service.ScanAsync(workspace, alreadyCancelled.Token).GetAwaiter().GetResult(),
            "预先取消的异步扫描没有停止。");

        using var duringScan = new CancellationTokenSource();
        var publishedComplete = false;
        AssertCancelled(() => service.Scan(workspace, duringScan.Token, new CallbackProgress(stage =>
        {
            if (stage.CompletedStages == 3) duringScan.Cancel();
            if (stage.CompletedStages == 9) publishedComplete = true;
        })), "构建调用图时取消没有停止扫描。");
        TestSupport.Assert(!publishedComplete, "取消的扫描发布了完成事件。");

        // A cancelled context must not poison a later fresh scan.
        TestSupport.Assert(JsonSerializer.Serialize(service.Scan(workspace)) == expected,
            "取消影响了下一份快照的扫描结果。");
        Console.WriteLine($"ENGINE_EFFECT_SCAN_PERFORMANCE_TESTS_OK {Path.GetFileName(root)} {watch.ElapsedMilliseconds}ms");
    }

    private static void AssertCancelled(Action action, string message)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class CallbackProgress(Action<EngineEffectScanProgress> action) : IProgress<EngineEffectScanProgress>
    {
        public void Report(EngineEffectScanProgress value) => action(value);
    }
}
