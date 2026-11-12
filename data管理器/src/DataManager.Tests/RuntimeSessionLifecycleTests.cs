using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.History;

namespace DataManager.Tests;

/// <summary>Forced collections diagnose reachability only; natural memory is measured by the 100-cycle runtime harness.</summary>
internal static class RuntimeSessionLifecycleTests
{
    private sealed record Artifact(int Trial, string Stage, string Kind, WeakReference Reference);
    private sealed record CollectionCheck(int Trial, string Stage, int Attempt, long ElapsedMs,
        int Remaining, string[] Retained, long ManagedBytesAfterForcedCollection);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.RuntimeSessionLifecycle", Path.GetFullPath(source));
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(owned.RootPath, "history"));
        var sourcePaths = DataManager.Core.Profiles.ProfileDetector.CoreFiles
            .Select(name => ProjectFileLocator.Resolve(source, name)).ToArray();
        var sourceHashes = sourcePaths.ToDictionary(path => path, HashService.Sha256, StringComparer.OrdinalIgnoreCase);
        var roots = new[] { "project-a", "project-b" }.Select(name => Path.Combine(owned.RootPath, name)).ToArray();
        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            foreach (var path in sourcePaths) File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
        }
        var checks = new List<CollectionCheck>();
        var artifacts = new List<Artifact>();
        var passed = false;
        string? failure = null;
        try
        {
            VerifyRangeUsesLoadedSnapshot(roots[0]);
            for (var trial = 1; trial <= 3; trial++)
            {
                var closed = OpenReopenClose(roots, trial, checks);
                artifacts.AddRange(closed);
                AssertReleased(closed, trial, "closed", checks);
                Console.WriteLine($"RUNTIME_SESSION_LIFECYCLE_TRIAL {trial}/3 old-documents/cache/query/closed-workspace=collected forced-GC-diagnostic-only");
            }
            TestSupport.Assert(sourceHashes.All(pair => HashService.Sha256(pair.Key) == pair.Value), "会话生命周期验收修改了原始基底");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "runtime-session-lifecycle.json"), JsonSerializer.Serialize(new
            {
                Passed = passed,
                IndependentRuns = 3,
                OpensPerRun = 2,
                Coverage = "one same-workspace reopen; two replacement-workspace reopens; real MainForm close/dispose; sync failure and async cancellation cleanup",
                GcMode = "TEST-ONLY FORCED COLLECTION DIAGNOSIS. These values are not natural runtime memory measurements. The separate RuntimePerformanceTests 100-cycle run supplies uncollected memory evidence.",
                Checks = checks,
                TrackedClosedArtifacts = artifacts.Count,
                RetainedClosedArtifacts = artifacts.Count(artifact => artifact.Reference.IsAlive),
                OriginalSourcesUnchanged = sourceHashes.All(pair => HashService.Sha256(pair.Key) == pair.Value),
                SourceHashes = sourceHashes,
                Failure = failure
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (passed) owned.Complete();
            else File.WriteAllText(Path.Combine(output, "retained-failure-workspace.txt"), owned.RootPath);
        }
        Console.WriteLine("RUNTIME_SESSION_LIFECYCLE_OK independent-runs=3 opens=6 range-source=reused forced-GC=diagnosis-only");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<Artifact> OpenReopenClose(string[] roots, int trial, List<CollectionCheck> checks)
    {
        using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000),
            ShowInTaskbar = false, Size = new(1280, 800) };
        form.Show();
        form.LoadProjectForSmokeTest(roots[0]);
        Pump(50);
        var old = ExerciseQueriesAndCapture(form, trial, "before-reopen", includeWorkspace: trial != 1);
        if (trial == 1) form.LoadProjectForSmokeTest(roots[1]);
        else ReplaceWorkspace(form, roots[1]);
        Pump(100);
        TestSupport.Assert(form.WorkspaceForSmokeTest.RootPath == roots[1] && !form.WorkspaceForSmokeTest.IsDirty,
            "重新打开项目没有恢复独立干净工作区");
        AssertReleased(old, trial, "reopened", checks);
        GC.KeepAlive(form);
        var closed = ExerciseQueriesAndCapture(form, trial, "before-close", includeWorkspace: true);
        closed.Add(new(trial, "closed", "MainForm", new(form)));
        form.Close();
        form.Dispose();
        return closed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplaceWorkspace(MainForm form, string root)
    {
        var workspace = new ProjectWorkspace();
        var report = workspace.Open(root);
        typeof(MainForm).GetField("_workspace", PrivateInstance)!.SetValue(form, workspace);
        typeof(MainForm).GetMethod("ApplyOpenedProject", PrivateInstance)!.Invoke(form, [report, root]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<Artifact> ExerciseQueriesAndCapture(MainForm form, int trial, string stage, bool includeWorkspace)
    {
        form.SelectModuleForSmokeTest("人物");
        var workspace = form.WorkspaceForSmokeTest;
        var cache = WorkspaceReadCache.For(workspace);
        var service = new RecordQueryService(workspace);
        var field = service.PhysicalField(["persons"], "兵种")
            ?? throw new InvalidOperationException("生命周期基底没有人物兵种查询属性");
        var scope = new RecordQueryScope("runtime-lifecycle-" + stage, [service.IdentityField(), field]);
        var records = cache.GetNavigationRecords(BusinessPageKind.Persons);
        var snapshot = service.BuildSnapshot(scope, records, [field.Key]);
        _ = snapshot.Filter([new(field.Key, "1")]);
        TestSupport.Assert(cache.Statistics.MaterializedColumns > 0, "生命周期验收没有建立真实属性查询缓存");

        var threw = false;
        try
        {
            service.BuildSnapshot(new("runtime-lifecycle-failure", [new("throws", "异常", _ =>
                throw new InvalidOperationException("expected query lifecycle exception")) { Dependencies = [] }]), records);
        }
        catch (InvalidOperationException error) when (error.Message == "expected query lifecycle exception") { threw = true; }
        TestSupport.Assert(threw, "查询异常清理验收没有进入属性读取");

        using var cancellation = new CancellationTokenSource();
        var canceled = false;
        try
        {
            service.BuildSnapshotAsync(new("runtime-lifecycle-cancel", [new("slow", "取消", id =>
                { Thread.Sleep(10); return [id.ToString(System.Globalization.CultureInfo.InvariantCulture)]; }) { Dependencies = [] }]), records,
                ["slow"], _ => { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); }, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { canceled = true; }
        TestSupport.Assert(canceled, "异步查询清理验收没有在分批读取后取消");

        var result = workspace.Documents.Values.Select(document =>
            new Artifact(trial, stage, "document:" + document.Definition.Key, new(document))).ToList();
        foreach (var image in workspace.Documents.Values.Select(document => document.OriginalFileBytes).Distinct(ReferenceEqualityComparer.Instance))
            result.Add(new(trial, stage, "source-byte-snapshot", new(image)));
        result.Add(new(trial, stage, "WorkspaceReadCache", new(cache)));
        result.Add(new(trial, stage, "RecordQueryService", new(service)));
        result.Add(new(trial, stage, "RecordQuerySnapshot", new(snapshot)));
        result.Add(new(trial, stage, "RecordQueryScope/delegates", new(scope)));
        if (includeWorkspace) result.Add(new(trial, stage, "ProjectWorkspace", new(workspace)));
        return result;
    }

    private static void AssertReleased(IReadOnlyList<Artifact> artifacts, int trial, string stage, List<CollectionCheck> checks)
    {
        var timer = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            Pump(50);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var retained = artifacts.Where(artifact => artifact.Reference.IsAlive).Select(artifact => artifact.Kind).ToArray();
            checks.Add(new(trial, stage, attempt, timer.ElapsedMilliseconds, retained.Length, retained, GC.GetTotalMemory(false)));
            if (retained.Length == 0) return;
            if (timer.ElapsedMilliseconds >= 10000)
                throw new InvalidOperationException($"生命周期回收诊断失败 trial={trial}, stage={stage}: {string.Join(", ", retained)}");
            Pump(200);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyRangeUsesLoadedSnapshot(string root)
    {
        using var form = new MainForm(false);
        var workspace = form.WorkspaceForSmokeTest;
        workspace.Open(root);
        var executable = Path.GetFullPath(ProjectFileLocator.Resolve(root, "Ekd5.exe"));
        var source = workspace.Documents.Values.First(document => Path.GetFullPath(document.FilePath)
            .Equals(executable, StringComparison.OrdinalIgnoreCase));
        var original = source.OriginalFileBytes;
        var changed = (byte[])original.Clone();
        changed[^1] ^= 1;
        File.WriteAllBytes(executable, changed);
        try
        {
            typeof(MainForm).GetMethod("LoadRangeWorkspace", PrivateInstance)!.Invoke(form, null);
            var session = form.RangeSessionForSmokeTest ?? throw new InvalidOperationException("范围组件重新读取了已变化的磁盘EXE，而没有复用工作区快照");
            TestSupport.Assert(session.Game.Exe.Bytes.AsSpan().SequenceEqual(original) &&
                !ReferenceEquals(session.Game.Exe.Bytes, original) &&
                session.Game.Exe.OriginalSha256 == source.OriginalFileHash && !session.MatchesDiskBaselines(),
                "范围构造修改/混用了共享EXE快照，或丢失实时磁盘冲突检测");
        }
        finally { File.WriteAllBytes(executable, original); }
    }

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(2); }
    }
}
