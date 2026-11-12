using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.History;
using ImageManager.Core;

namespace DataManager.Tests;

internal static class ProjectLifecycleOptimizationTests
{
    private sealed record Sample(int Cycle, long PrivateBytes, long ManagedBytes, int Gdi, int User, int CachedPages);
    private sealed record RetiredPage(int Cycle, string Kind, WeakReference Reference);

    public static void Run(string repository, string output)
    {
        Directory.CreateDirectory(output);
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.ProjectLifecycleOptimization", repository);
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(owned.RootPath, "history"));
        var hashes = new Dictionary<string, string>();
        var roots = new[] { "加强版6.5未加密版", "复刻版6.6山贼" }.Select((name, index) =>
        {
            var source = Path.Combine(repository, "基底", name);
            var target = Path.Combine(owned.RootPath, "project" + index);
            CopyProject(source, target, hashes);
            return target;
        }).ToArray();
        var samples = new List<Sample>();
        var retired = new List<RetiredPage>();
        var retentionChecks = new List<object>();
        var failures = new List<string>();
        var completed = false;
        try
        {
            using var form = new MainForm(false) { Location = new Point(-32000, -32000),
                StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Size = new Size(1280, 800) };
            form.Show();
            for (var cycle = 1; cycle <= 20; cycle++)
            {
                form.LoadProjectForSmokeTest(roots[(cycle - 1) % roots.Length]);
                foreach (var page in new[] { "人物", "兵种", "策略", "__ranges", "__resources", "拓展数据" })
                {
                    form.SelectModuleForSmokeTest(page);
                    Pump(25);
                }
                form.SelectModuleForSmokeTest("兵种");
                Pump(100);
                TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "项目往返生成了意外草稿");
                var cache = (System.Collections.IDictionary)typeof(MainForm).GetField("_businessEditors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                TestSupport.Assert(cache.Count <= 7, "同项目业务页缓存无限增长");
                if (cycle < 20) CaptureRetired(cache, retired, cycle);
                samples.Add(Measure(cycle, cache.Count));
                if (cycle % 5 == 0) Console.WriteLine($"PROJECT_LIFECYCLE_CYCLE {cycle}/20 privateMiB={samples[^1].PrivateBytes / 1048576d:F1} GDI={samples[^1].Gdi} USER={samples[^1].User}");
            }
            // No collections occur during the twenty project changes. This final
            // collection checks whether disposed pages remain reachable.
            Pump(1000);
            var quiescence = Stopwatch.StartNew();
            int retainedPages;
            do
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                retainedPages = retired.Count(page => page.Reference.IsAlive);
                var resources = ImageResourceCoordinator.Shared.Statistics;
                var detail = DescribeRetained(retired);
                retentionChecks.Add(new { ElapsedMs = quiescence.ElapsedMilliseconds, RetainedPages = retainedPages,
                    Detail = detail, resources.ActiveDecodes, resources.QueuedDecodes,
                    ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount });
                Console.WriteLine($"PROJECT_RETENTION_CHECK elapsedMs={quiescence.ElapsedMilliseconds} retained={retainedPages} active={resources.ActiveDecodes} queued={resources.QueuedDecodes} {detail}");
                if (retainedPages == 0 || quiescence.ElapsedMilliseconds >= 15000) break;
                // Cancellation does not synchronously complete archive I/O. Keep
                // the first failed observation and allow a bounded UI drain;
                // this still requires every retired page to become unreachable.
                Pump(500);
            } while (true);
            samples.Add(Measure(21, samples[^1].CachedPages));
            if (retainedPages != 0 && Environment.GetEnvironmentVariable("CCZ_TEST_RETENTION_DUMP_TOOL") is { Length: > 0 } dumpTool)
            {
                var capture = new ProcessStartInfo(dumpTool) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "collect", "--process-id", Environment.ProcessId.ToString(),
                             "--type", "Heap", "--output", Path.Combine(Path.GetFullPath(output), "retained-pages.dmp") })
                    capture.ArgumentList.Add(argument);
                using var diagnostic = Process.Start(capture);
                if (diagnostic != null && !diagnostic.WaitForExit(60000)) diagnostic.Kill();
            }
            TestSupport.Assert(retainedPages == 0, $"已替换项目仍持有{retainedPages}个业务页");
            TestSupport.Assert(E5Archive.CacheStatistics.RetainedBytes <= E5Archive.MaximumCacheBytes,
                "实际主窗项目切换后E5缓存越界");
            foreach (var pair in hashes) TestSupport.Assert(Hash(pair.Key) == pair.Value, "原始基底发生变化:" + pair.Key);
            completed = true;
        }
        catch (Exception error) { failures.Add(error.ToString()); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "project-lifecycle.json"), JsonSerializer.Serialize(new
            {
                Completed = completed, ProjectChanges = 20, PageVisits = 120, Samples = samples,
                RetiredPages = retired.Count, RetainedPagesAfterCollection = retired.Count(page => page.Reference.IsAlive),
                RetentionChecks = retentionChecks,
                SourceHashes = hashes, OriginalSourcesUnchanged = hashes.All(pair => Hash(pair.Key) == pair.Value),
                Failures = failures, Notes = "Cycles are uncollected. Cycle21 is a final test-only collection; finite twenty-switch coverage does not prove an indefinite soak."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (completed) owned.Complete();
            else File.WriteAllText(Path.Combine(output, "retained-failure-workspace.txt"), owned.RootPath);
        }
        Console.WriteLine("PROJECT_LIFECYCLE_OPTIMIZATION_OK changes=20 page-visits=120 retired-pages-collected source-sha-unchanged");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CaptureRetired(System.Collections.IDictionary cache, List<RetiredPage> retired, int cycle)
    {
        foreach (System.Collections.DictionaryEntry page in cache)
            retired.Add(new(cycle, page.Key.ToString()!, new WeakReference(page.Value)));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string DescribeRetained(List<RetiredPage> retired) => string.Join("; ", retired
        .Where(page => page.Reference.IsAlive).Select(page => page.Reference.Target is BusinessEditorPanel panel
            ? $"cycle={page.Cycle}/{page.Kind} disposed={panel.IsDisposed} parent={panel.Parent?.GetType().Name ?? "null"}"
            : $"cycle={page.Cycle}/{page.Kind}"));

    private static void CopyProject(string source, string target, Dictionary<string, string> hashes)
    {
        foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(destination);
            foreach (var path in Directory.EnumerateFiles(directory).Where(path =>
                         Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(path).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)))
            {
                hashes[path] = Hash(path);
                File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
            }
        }
    }
    private static Sample Measure(int cycle, int pages)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new(cycle, process.PrivateMemorySize64, GC.GetTotalMemory(false),
            GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1), pages);
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
}
