using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.History;

namespace DataManager.Tests;

/// <summary>The same source runs against the frozen release and the new assemblies.</summary>
internal static class OptimizationAuditTests
{
    private sealed record Geometry(string Type, string Bounds, int Dpi, string Font, string Extra);
    private sealed record MemorySample(int Cycle, long PrivateBytes, long WorkingSet, long ManagedBytes, int Gdi, int User);
    private sealed record SwitchSample(int Cycle, string Page, double Milliseconds, double SelectMs, double SettleMs);
    private sealed record Issue(string Page, string Kind, string Detail);

    public static void Run(string source, string output, int cycles = 30, bool enforce = false)
    {
        source = Path.GetFullPath(source);
        if (Directory.Exists(Path.Combine(source, "基底"))) source = Path.Combine(source, "基底", "复刻版6.6山贼");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
#if FROZEN_BASELINE
        var temporary = Path.Combine(Path.GetTempPath(), "CCZOptimizationAudit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var marker = Path.Combine(temporary, ".ccz-owned-test-workspace.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new { Owner = "DataManager.OptimizationAudit", Source = source, CreatedUtc = DateTime.UtcNow }));
#else
        var temporaryParent = Path.Combine(Path.GetTempPath(), "CCZOptimizationAudit");
        Directory.CreateDirectory(temporaryParent);
        using var owned = DataManager.Core.Binary.OwnedTemporaryWorkspace.Create(temporaryParent, "DataManager.OptimizationAudit", source);
        var temporary = owned.RootPath;
#endif
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<SwitchSample>();
        var memories = new List<MemorySample>();
        var issues = new List<Issue>();
        var fingerprints = new Dictionary<string, Dictionary<string, Geometry>>();
        var heartbeat = new ConcurrentQueue<double>();
        var actionHeartbeat = new ConcurrentQueue<double>();
        var failures = new List<string>();
        var oldParents = new Dictionary<Control, Control?>();
        var changedParents = 0;
        var layoutCallbacks = 0;
        double cold = 0, firstImage = 0;
        string? screenshot = null;
        var completed = false;
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(temporary, "test-history"));
        MainForm? form = null;
        System.Threading.Timer? pulse = null;
        var pulseActive = 1;
        var actionEpoch = 0;
        try
        {
            foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
            {
                var relative = Path.GetRelativePath(source, directory);
                var destination = relative == "." ? temporary : Path.Combine(temporary, relative);
                Directory.CreateDirectory(destination);
                foreach (var path in Directory.EnumerateFiles(directory).Where(path =>
                             Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFileName(path).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    hashes[path] = Hash(path);
                    File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
                }
            }
            form = new MainForm(false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Size = new Size(1280, 800), MaximumSize = new Size(4096, 2160)
            };
            form.Show();
            var owner = form;
            pulse = new System.Threading.Timer(_ =>
            {
                if (Volatile.Read(ref pulseActive) == 0 || owner.IsDisposed || !owner.IsHandleCreated) return;
                var epoch = Volatile.Read(ref actionEpoch);
                var posted = Stopwatch.GetTimestamp();
                try { owner.BeginInvoke((Action)(() =>
                {
                    if (Volatile.Read(ref pulseActive) == 0) return;
                    var latency = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    heartbeat.Enqueue(latency);
                    // Keep the original whole-audit statistic, but distinguish requests
                    // posted during navigation from warmup, screenshots, hashing and GC.
                    // Classify by posting time. A callback that arrives after the
                    // action interval still represents a real delayed action pulse.
                    if (epoch == 1) actionHeartbeat.Enqueue(latency);
                })); }
                catch (InvalidOperationException) { }
            }, null, 0, 50);
            var watch = Stopwatch.StartNew();
            form.LoadProjectForSmokeTest(temporary);
            Settle(form, 300);
            cold = watch.Elapsed.TotalMilliseconds;
            var first = Stopwatch.StartNew();
            while (first.ElapsedMilliseconds < 10000 && !HasLoadedPicture(form)) Pump(10);
            firstImage = watch.Elapsed.TotalMilliseconds;

            string[] scenarios = ["人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据",
                "__ranges/0", "__ranges/1", "__ranges/2", "__resources", "__formulas", "physical/persons"];
            void Select(string scenario)
            {
                if (scenario.StartsWith("physical/", StringComparison.Ordinal))
                {
                    form.SelectModuleForSmokeTest("人物");
                    form.ShowPhysicalTableForSmokeTest(scenario[9..]);
                }
                else if (scenario.StartsWith("__ranges/", StringComparison.Ordinal))
                {
                    form.SelectModuleForSmokeTest("__ranges");
                    var range = form.RangeHostForSmokeTest;
                    var tabs = Descendants(range).OfType<TabControl>().First();
                    tabs.SelectedIndex = int.Parse(scenario[9..], CultureInfo.InvariantCulture);
                }
                else form.SelectModuleForSmokeTest(scenario);
            }
            Control Surface(string scenario) => scenario.StartsWith("__ranges", StringComparison.Ordinal)
                ? form.RangeHostForSmokeTest : scenario.StartsWith("__", StringComparison.Ordinal) || scenario.StartsWith("physical", StringComparison.Ordinal)
                    ? form : form.BusinessEditorForSmokeTest;
            foreach (var scenario in scenarios)
            {
                Select(scenario);
                Settle(Surface(scenario), 350);
            }
            // Give background decode requests a bounded opportunity before comparing steady-state memory.
            Pump(1000);
            Collect(); memories.Add(MeasureMemory(0));
            heartbeat.Clear();
            Volatile.Write(ref actionEpoch, 1);
            form.Layout += (_, _) => layoutCallbacks++;
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                foreach (var scenario in scenarios)
                {
                    var timer = Stopwatch.StartNew();
                    Select(scenario);
                    var selectedMs = timer.Elapsed.TotalMilliseconds;
                    Settle(Surface(scenario), 250);
                    var elapsedMs = timer.Elapsed.TotalMilliseconds;
                    samples.Add(new(cycle, scenario, elapsedMs, selectedMs, elapsedMs - selectedMs));
                    var surface = Surface(scenario);
                    var snapshot = Snapshot(surface);
                    if (fingerprints.TryGetValue(scenario, out var previous))
                    {
                        if (!Equivalent(previous, snapshot))
                        {
                            var detail = Difference(previous, snapshot);
                            if (!issues.Any(issue => issue.Page == scenario && issue.Kind == "geometry-drift"))
                                issues.Add(new(scenario, "geometry-drift", detail));
                        }
                    }
                    else fingerprints[scenario] = snapshot;
                    if (surface is BusinessEditorPanel panel)
                    {
                        if (oldParents.TryGetValue(panel, out var parent) && !ReferenceEquals(parent, panel.Parent)) changedParents++;
                        oldParents[panel] = panel.Parent;
                    }
                    foreach (var picker in Descendants(surface).OfType<RangePreviewPicker>().Where(picker => picker.Visible))
                    {
                        var bounds = picker.PreviewBox.Bounds;
                        if (bounds.Right > picker.ClientSize.Width + 1 || bounds.Bottom > picker.ClientSize.Height + 1)
                            if (!issues.Any(issue => issue.Page == scenario && issue.Kind == "range-preview-clipped"))
                                issues.Add(new(scenario, "range-preview-clipped", $"picker={picker.ClientSize}; preview={bounds}; DPI={picker.DeviceDpi}"));
                    }
                }
                if (cycle % 5 == 0 || cycle == cycles)
                {
                    memories.Add(MeasureMemory(cycle));
                    Console.WriteLine($"OPTIMIZATION_CYCLE {cycle}/{cycles} samples={samples.Count} issues={issues.Count} privateMiB={memories[^1].PrivateBytes / 1048576d:F1}");
                }
            }
            // Stop posting action samples, then service requests that were posted by
            // the final navigation before doing any screenshots or source-file hashes.
            Volatile.Write(ref actionEpoch, 2);
            Pump(100);
            Volatile.Write(ref actionEpoch, 3);
            form.SelectModuleForSmokeTest("兵种");
            Settle(form, 300);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                screenshot = Path.Combine(output, "job-1280.png");
                bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Size = new Size(1920, 1080); Settle(form, 300);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(output, "job-1920.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            if (form.WorkspaceForSmokeTest.IsDirty) failures.Add("Navigation produced workspace drafts.");
            foreach (var pair in hashes) if (Hash(pair.Key) != pair.Value) failures.Add("Original source changed: " + pair.Key);
            Pump(300); Collect(); memories.Add(MeasureMemory(cycles + 1));
            completed = true;
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            Interlocked.Exchange(ref pulseActive, 0);
            pulse?.Dispose();
            form?.Dispose();
            var latencies = heartbeat.ToArray();
            var actionLatencies = actionHeartbeat.ToArray();
            var report = new
            {
                Completed = completed, Passed = completed && failures.Count == 0 && issues.Count == 0,
                BuildVersion = typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Source = source, ActualDpi = form?.DeviceDpi, Cycles = cycles,
                ColdInteractiveMs = cold, FirstVisibleImageMs = firstImage,
                Switches = new { Count = samples.Count, MedianMs = Percentile(samples.Select(sample => sample.Milliseconds), .5),
                    P95Ms = Percentile(samples.Select(sample => sample.Milliseconds), .95), MaximumMs = samples.Select(sample => sample.Milliseconds).DefaultIfEmpty().Max() },
                Heartbeat = new { IntervalMs = 50, DuringActions = true, Scope = "Legacy whole-audit statistic after warmup clear; includes final screenshots, hashes and GC", Samples = latencies.Length,
                    P95Ms = Percentile(latencies, .95), MaximumMs = latencies.DefaultIfEmpty().Max() },
                ActionHeartbeat = new { IntervalMs = 50, Scope = $"{cycles} navigation rounds, classified by posting interval; includes message/snapshot work and late callbacks", Samples = actionLatencies.Length,
                    P95Ms = Percentile(actionLatencies, .95), MaximumMs = actionLatencies.DefaultIfEmpty().Max() },
                ParentChangeCount = changedParents, LayoutCallbacks = layoutCallbacks,
                Memory = memories, Samples = samples, Issues = issues, Failures = failures,
                SourceHashes = hashes, OriginalSourcesUnchanged = hashes.All(pair => Hash(pair.Key) == pair.Value),
                Resources = ResourceStatistics(), Screenshot = screenshot,
                Notes = "Cache budgets are not process-memory limits. Steady-state samples after explicit test-only collection are separate from uncollected cycle samples."
            };
            File.WriteAllText(Path.Combine(output, "optimization-audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "geometry.json"), JsonSerializer.Serialize(fingerprints, new JsonSerializerOptions { WriteIndented = true }));
#if FROZEN_BASELINE
            if (completed && File.Exists(marker)) Directory.Delete(temporary, true);
            else File.WriteAllText(Path.Combine(output, "retained-failure-workspace.txt"), temporary);
#else
            if (completed && failures.Count == 0 && issues.Count == 0) owned.Complete();
            else File.WriteAllText(Path.Combine(output, "retained-failure-workspace.txt"), temporary);
#endif
        }
        Console.WriteLine($"OPTIMIZATION_AUDIT_DONE completed={completed} checks={samples.Count} layoutIssues={issues.Count} failures={failures.Count}");
        if (enforce && (!completed || failures.Count != 0 || issues.Count != 0)) throw new InvalidOperationException("Optimization audit failed; see optimization-audit.json.");
    }

    private static void Settle(Control control, int timeout)
    {
        var clock = Stopwatch.StartNew();
        string? previous = null;
        var stable = 0;
        do
        {
            Application.DoEvents();
            var current = JsonSerializer.Serialize(Snapshot(control));
            stable = current == previous ? stable + 1 : 0;
            previous = current;
            if (stable >= 2) return;
            Thread.Sleep(5);
        } while (clock.ElapsedMilliseconds < timeout);
    }
    private static Dictionary<string, Geometry> Snapshot(Control root)
    {
        var result = new Dictionary<string, Geometry>();
        void Visit(Control control, string path)
        {
            if (!control.Visible || control.IsDisposed) return;
            // Picture pixels can finish loading asynchronously; field and viewport geometry must already be stable.
            if (control is not PictureBox)
            {
                var extra = control is TableLayoutPanel table ? string.Join(',', table.GetRowHeights()) + "/" + string.Join(',', table.GetColumnWidths())
                    : control is ScrollableControl scroll ? $"{scroll.AutoScrollMinSize}/{scroll.AutoScrollPosition}/{scroll.HorizontalScroll.Visible}/{scroll.VerticalScroll.Visible}" : string.Empty;
                result[path] = new(control.GetType().Name, control.Bounds.ToString(), control.DeviceDpi,
                    $"{control.Font.Name}/{control.Font.SizeInPoints:F3}/{control.Font.Style}", extra);
            }
            for (var index = 0; index < control.Controls.Count; index++) Visit(control.Controls[index], path + "/" + index + ":" + control.Controls[index].Name);
        }
        Visit(root, root.GetType().Name); return result;
    }
    private static bool Equivalent(Dictionary<string, Geometry> left, Dictionary<string, Geometry> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);
    private static string Difference(Dictionary<string, Geometry> left, Dictionary<string, Geometry> right)
        => string.Join("; ", left.Where(pair => !right.TryGetValue(pair.Key, out var value) || pair.Value != value)
            .Take(5).Select(pair => pair.Key + ": " + JsonSerializer.Serialize(pair.Value) + " -> " +
                (right.TryGetValue(pair.Key, out var value) ? JsonSerializer.Serialize(value) : "missing")));
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static bool HasLoadedPicture(Control root) => Descendants(root).OfType<PictureBox>().Any(picture => picture.Visible && picture.Image != null);
    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static MemorySample MeasureMemory(int cycle)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new(cycle, process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(false),
            GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }
    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static object ResourceStatistics()
    {
        var core = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "ImageManager.Core");
        var coordinator = core?.GetType("ImageManager.Core.ImageResourceCoordinator");
        var shared = coordinator?.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var statistics = coordinator?.GetProperty("Statistics")?.GetValue(shared);
        var archive = core?.GetType("ImageManager.Core.E5Archive")?.GetProperty("CacheStatistics", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return new { Preview = statistics, Archive = archive };
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
}
