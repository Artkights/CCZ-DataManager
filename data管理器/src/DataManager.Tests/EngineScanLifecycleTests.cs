using System.Diagnostics;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineScanLifecycleTests
{
    public static void Run(string repositoryRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var lines = new List<string>();
        var names = new[] { "洗隋", "加强版6.5未加密版" };
        var sources = names.Select(name => Path.Combine(repositoryRoot, "基底", name)).ToArray();
        var hashes = sources.ToDictionary(source => source, source => new[] { "Ekd5.exe", "Data.e5", "Star.e5", "Imsg.e5" }
            .ToDictionary(file => file, file => HashService.Sha256(Path.Combine(source, file))));
        var copies = sources.Select(source => TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false)).ToArray();
        try
        {
            using var form = new MainForm(openDefaultProject: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false
            };
            Control.CheckForIllegalCrossThreadCalls = true;
            Exception? exerciseFailure = null;
            form.Shown += async (_, _) =>
            {
                try { await ExerciseAsync(); }
                catch (Exception ex) { exerciseFailure = ex; Record("LIFECYCLE_EXECUTION_FAILURE " + ex); }
                finally { form.CancelEngineForLifecycleTest(); form.Dispose(); Application.ExitThread(); }
            };
            Application.Run(form);
            if (exerciseFailure != null) throw new InvalidOperationException("生命周期UI场景失败。", exerciseFailure);

            async Task ExerciseAsync()
            {
                form.LoadProjectForSmokeTest(copies[0]);
                form.SelectModuleForSmokeTest("__engine");
                await WaitForAsync(() => form.EngineReportForLifecycleTest != null && !form.EngineScanPendingForSmokeTest, "首次后台扫描");
                var baseline = form.EngineReportForLifecycleTest!;
                EngineEffectAssignmentCatalog? catalog = null;
                foreach (var page in new[] { 1, 2, 0, 1, 2, 0, 1 })
                {
                    form.EngineSubTabsForSmokeTest.SelectedIndex = page;
                    await PumpAsync();
                    Assert(ReferenceEquals(form.EngineReportForLifecycleTest, baseline) && !form.EngineScanPendingForSmokeTest,
                        "相同SHA切页重新扫描或替换了报告对象。");
                    if (page == 0) continue;
                    if (catalog == null) catalog = form.EngineCatalogForLifecycleTest;
                    else Assert(ReferenceEquals(catalog, form.EngineCatalogForLifecycleTest), "相同分配指纹切页重建了目录。");
                }
                Record("LIFECYCLE_OK sameSHA_subpage_report_and_catalog_reuse");

                var cancelledSuccess = new TaskCompletionSource<EngineEffectScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.HoldEngineWorkerForLifecycleTest(cancelledSuccess.Task);
                var generation = form.EngineGenerationForLifecycleTest;
                form.CancelEngineForLifecycleTest();
                form.RefreshEngineForLifecycleTest();
                Assert(form.EngineGenerationForLifecycleTest > generation && !ReferenceEquals(form.EngineTaskForLifecycleTest, cancelledSuccess.Task),
                    "取消后立即重入仍等待旧worker。");
                await WaitForAsync(() => form.EngineReportForLifecycleTest != null && !form.EngineScanPendingForSmokeTest, "取消后重扫");
                var restarted = form.EngineReportForLifecycleTest;
                cancelledSuccess.SetResult(baseline);
                await PumpAsync(12);
                Assert(ReferenceEquals(form.EngineReportForLifecycleTest, restarted), "同项目旧worker成功结果覆盖了重扫报告。");
                Record("LIFECYCLE_OK cancel_immediate_restart_and_late_success_ignored");

                var cancelledFailure = new TaskCompletionSource<EngineEffectScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.HoldEngineWorkerForLifecycleTest(cancelledFailure.Task);
                form.CancelEngineForLifecycleTest();
                form.RefreshEngineForLifecycleTest();
                Assert(!ReferenceEquals(form.EngineTaskForLifecycleTest, cancelledFailure.Task), "取消后未创建新worker。");
                await WaitForAsync(() => form.EngineReportForLifecycleTest != null && !form.EngineScanPendingForSmokeTest, "同项目取消后新扫描");
                var afterRestart = form.EngineReportForLifecycleTest;
                cancelledFailure.SetException(new InvalidDataException("只用于测试的旧worker失败"));
                await PumpAsync(12);
                Assert(ReferenceEquals(form.EngineReportForLifecycleTest, afterRestart), "同root/fingerprint旧异常清空了新报告。");
                Record("LIFECYCLE_OK cancelled_sameRoot_sameSHA_late_failure_ignored");

                var oldProject = new TaskCompletionSource<EngineEffectScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.HoldEngineWorkerForLifecycleTest(oldProject.Task);
                form.LoadProjectForSmokeTest(copies[1]);
                form.SelectModuleForSmokeTest("__engine");
                await WaitForAsync(() => form.EngineReportForLifecycleTest is { } report && SameRoot(report.ExePath, copies[1]) &&
                    !form.EngineScanPendingForSmokeTest, "切项目后台扫描");
                var newProject = form.EngineReportForLifecycleTest;
                oldProject.SetResult(baseline);
                await PumpAsync(12);
                Assert(ReferenceEquals(form.EngineReportForLifecycleTest, newProject) &&
                    SameRoot(form.WorkspaceForSmokeTest.RootPath, copies[1], isDirectory: true), "旧项目完成结果覆盖了新项目。");
                Record("LIFECYCLE_OK project_switch_late_success_ignored");

                var oldProjectFailure = new TaskCompletionSource<EngineEffectScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.HoldEngineWorkerForLifecycleTest(oldProjectFailure.Task);
                form.LoadProjectForSmokeTest(copies[0]);
                form.SelectModuleForSmokeTest("__engine");
                await WaitForAsync(() => form.EngineReportForLifecycleTest is { } report && SameRoot(report.ExePath, copies[0]) &&
                    !form.EngineScanPendingForSmokeTest, "切回项目后台扫描");
                var backToFirst = form.EngineReportForLifecycleTest;
                oldProjectFailure.SetException(new InvalidDataException("只用于测试的旧项目失败"));
                await PumpAsync(12);
                Assert(ReferenceEquals(form.EngineReportForLifecycleTest, backToFirst), "旧项目异常清空了新项目报告。");
                Record("LIFECYCLE_OK project_switch_late_failure_ignored");

                var layout = EngineEffectAssignmentService.ProbeJobLayout(File.ReadAllBytes(Path.Combine(copies[0], "Ekd5.exe")));
                await CheckReportContentInvalidationAsync("Ekd5.exe", layout.GetJobValueOffset(0, 0), isExe: true);
                await CheckReportContentInvalidationAsync("Imsg.e5", new FileInfo(Path.Combine(copies[0], "Imsg.e5")).Length - 1, isExe: false);
                foreach (var key in new[] { "items-low", "items-high" })
                {
                    form.EngineSubTabsForSmokeTest.SelectedIndex = 1;
                    form.RefreshEngineForLifecycleTest(); await PumpAsync();
                    var beforeReport = form.EngineReportForLifecycleTest;
                    var beforeCatalog = form.EngineCatalogForLifecycleTest!;
                    var document = form.WorkspaceForSmokeTest.Documents[key];
                    var original = document.Data.Rows[0]["装备特效值"];
                    var value = (Convert.ToInt32(original) + 73) % 256;
                    var id = document.Definition.BeginId;
                    document.Data.Rows[0]["装备特效值"] = value;
                    form.RefreshEngineForLifecycleTest(); await PumpAsync();
                    Assert(ReferenceEquals(beforeReport, form.EngineReportForLifecycleTest) &&
                        !ReferenceEquals(beforeCatalog, form.EngineCatalogForLifecycleTest) &&
                        form.EngineCatalogForLifecycleTest!.Items.Single(item => item.Id == id).Value == value,
                        key + "内存编辑没有使分配目录失效，或错误触发引擎扫描。");
                    document.Data.Rows[0]["装备特效值"] = original;
                    form.RefreshEngineForLifecycleTest(); await PumpAsync();
                    Assert(!form.WorkspaceForSmokeTest.IsDirty, "恢复内存字段后仍存在草稿。");
                    Record("LIFECYCLE_OK " + key + "_memory_edit_catalog_invalidation");

                    beforeCatalog = form.EngineCatalogForLifecycleTest!;
                    var path = document.FilePath;
                    var raw = File.ReadAllBytes(path);
                    var stamp = File.GetLastWriteTimeUtc(path);
                    var changed = (byte[])raw.Clone();
                    var offset = checked((int)document.Definition.DataOffset + 0x16);
                    changed[offset] ^= 0x35;
                    try
                    {
                        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
                        form.RefreshEngineForLifecycleTest(); await PumpAsync();
                        Assert(ReferenceEquals(beforeReport, form.EngineReportForLifecycleTest) &&
                            !ReferenceEquals(beforeCatalog, form.EngineCatalogForLifecycleTest) &&
                            form.EngineCatalogForLifecycleTest!.Items.Single(item => item.Id == id).Value == changed[offset],
                            key + "同mtime外部编辑没有重读分配数据。");
                    }
                    finally { File.WriteAllBytes(path, raw); File.SetLastWriteTimeUtc(path, stamp); }
                    form.RefreshEngineForLifecycleTest(); await PumpAsync();
                    Assert(form.EngineCatalogForLifecycleTest!.Items.Single(item => item.Id == id).Value == Convert.ToInt32(original),
                        key + "恢复外部文件后仍显示旧缓存。");
                    Record("LIFECYCLE_OK " + key + "_sameMtime_external_edit_catalog_invalidation");
                }
                form.CancelEngineForLifecycleTest();
                await PumpAsync(15);
                Assert(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "UI测试丢失STA消息上下文。");
                Record("ENGINE_SCAN_LIFECYCLE_TESTS_OK");

                async Task CheckReportContentInvalidationAsync(string fileName, long offset, bool isExe)
                {
                    var previous = form.EngineReportForLifecycleTest!;
                    var previousWorker = form.EngineTaskForLifecycleTest;
                    var path = Path.Combine(copies[0], fileName);
                    var raw = File.ReadAllBytes(path);
                    var stamp = File.GetLastWriteTimeUtc(path);
                    var changed = (byte[])raw.Clone(); changed[checked((int)offset)] ^= 1;
                    try
                    {
                        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
                        var expectedHash = HashService.Sha256(path);
                        form.RefreshEngineForLifecycleTest();
                        await WaitForAsync(() => form.EngineReportForLifecycleTest is { } report && !form.EngineScanPendingForSmokeTest &&
                            (isExe ? report.ExeSha256 : report.ImsgSha256) == expectedHash, fileName + "内容变化重扫");
                        Assert(!ReferenceEquals(previous, form.EngineReportForLifecycleTest) && File.GetLastWriteTimeUtc(path) == stamp,
                            fileName + "同mtime内容变化错误复用报告。");
                        if (!isExe)
                        {
                            var current = form.EngineReportForLifecycleTest!;
                            Assert(ReferenceEquals(previousWorker, form.EngineTaskForLifecycleTest) &&
                                ReferenceEquals(previous.FunctionRoles, current.FunctionRoles) &&
                                ReferenceEquals(previous.IndirectCalls, current.IndirectCalls) &&
                                ReferenceEquals(previous.Wrappers, current.Wrappers),
                                "仅Imsg变化仍重建结构分析或启动了新扫描worker。");
                        }
                    }
                    finally { File.WriteAllBytes(path, raw); File.SetLastWriteTimeUtc(path, stamp); }
                    form.RefreshEngineForLifecycleTest();
                    await WaitForAsync(() => ReferenceEquals(previous, form.EngineReportForLifecycleTest) && !form.EngineScanPendingForSmokeTest,
                        fileName + "恢复原SHA复用");
                    Record("LIFECYCLE_OK " + fileName + "_sameMtime_content_invalidation_and_restoredSHA_reuse");
                }
            }
        }
        catch (Exception ex)
        {
            Record("LIFECYCLE_FAILURE " + ex);
            throw;
        }
        finally
        {
            foreach (var copy in copies) Directory.Delete(copy, true);
            foreach (var source in sources)
                foreach (var pair in hashes[source])
                    Assert(HashService.Sha256(Path.Combine(source, pair.Key)) == pair.Value, "生命周期测试改动了真实基底。");
            File.WriteAllLines(Path.Combine(outputDirectory, "扫描生命周期结果.txt"), lines);
        }

        void Record(string message) { lines.Add(message); Console.WriteLine(message); }
    }

    private static bool SameRoot(string path, string expected, bool isDirectory = false)
        => Path.GetFullPath(isDirectory ? path : Path.GetDirectoryName(path)!).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForAsync(Func<bool> predicate, string stage)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await PumpAsync(2);
            if (predicate()) return;
        } while (watch.Elapsed < TimeSpan.FromSeconds(60));
        throw new TimeoutException(stage + "未在60秒内完成。");
    }

    private static async Task PumpAsync(int rounds = 6)
    {
        Assert(SynchronizationContext.Current is WindowsFormsSynchronizationContext,
            "UI测试缺少WinForms消息上下文。");
        await Task.Delay(rounds * 8);
        Assert(SynchronizationContext.Current is WindowsFormsSynchronizationContext,
            "UI测试等待后丢失WinForms消息上下文。");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
