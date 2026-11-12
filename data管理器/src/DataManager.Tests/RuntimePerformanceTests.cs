using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.History;

namespace DataManager.Tests;

/// <summary>The identical harness runs against the frozen release and candidate; selection uses one UI event.</summary>
internal static class RuntimePerformanceTests
{
    private sealed record Sample(int Cycle, string Scenario, double SelectMs, double ReadyMs);
    private sealed record Memory(int Cycle, long PrivateBytes, long ManagedBytes, int Gdi, int User);

    public static void Run(string source, string output, int cycles = 30)
    {
        Directory.CreateDirectory(output);
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.RuntimePerformance", Path.GetFullPath(source));
        var root = owned.RootPath;
        foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(directory).Where(file =>
                         Path.GetExtension(file).Equals(".e5", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(file).Equals("Ekd5.exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        using var history = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(root, "history"));
        using var form = new MainForm(false) { StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000),
            ShowInTaskbar = false, Size = new(1280, 800) };
        var stages = new List<(string Scenario, string Stage, double Ms)>();
        var activeScenario = "cold";
        var timingSink = typeof(MainForm).Assembly.GetType("DataManager.App.RuntimeTiming")?.GetField("Sink", BindingFlags.Static | BindingFlags.NonPublic);
        timingSink?.SetValue(null, (Action<string, double>)((stage, ms) => stages.Add((activeScenario, stage, ms))));
        form.Show();
        var coldClock = Stopwatch.StartNew();
        form.LoadProjectForSmokeTest(root);
        Ready();
        var cold = coldClock.Elapsed.TotalMilliseconds;
        var pictureClock = Stopwatch.StartNew();
        while (pictureClock.ElapsedMilliseconds < 3000 && !Descendants(form).OfType<PictureBox>().Any(p => p.Visible && p.Image != null))
        { Application.DoEvents(); Thread.Sleep(5); }
        var firstImage = coldClock.Elapsed.TotalMilliseconds;
        string[] scenarios = ["人物", "兵种系", "兵种", "宝物", "策略", "商店", "拓展数据",
            "extend/Strategies", "extend/Effects", "physical/persons", "physical/strategies",
            "__resources", "__ranges/0", "__ranges/1", "__ranges/2", "__formulas"];
        var samples = new List<Sample>();
        var memory = new List<Memory>();
        var firstEntries = new List<Sample>();
        foreach (var scenario in scenarios)
        {
            activeScenario = "first/" + scenario;
            var first = Stopwatch.StartNew(); Select(form, scenario);
            var selected = first.Elapsed.TotalMilliseconds; Ready();
            firstEntries.Add(new(0, scenario, selected, first.Elapsed.TotalMilliseconds));
        }
        Pump(150);
        memory.Add(ReadMemory(0));
        var pulses = new ConcurrentQueue<double>();
        var running = 1;
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref running) == 0 || form.IsDisposed) return;
            var posted = Stopwatch.GetTimestamp();
            try { form.BeginInvoke((Action)(() => pulses.Enqueue(Stopwatch.GetElapsedTime(posted).TotalMilliseconds))); }
            catch (InvalidOperationException) { }
        }, null, 0, 50);
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            foreach (var scenario in scenarios)
            {
                activeScenario = scenario;
                var watch = Stopwatch.StartNew();
                Select(form, scenario);
                var selected = watch.Elapsed.TotalMilliseconds;
                Ready();
                samples.Add(new(cycle, scenario, selected, watch.Elapsed.TotalMilliseconds));
                // Give late callbacks and the queue probe time without charging the next page's selection.
                Pump(5);
            }
            if (cycle % 5 == 0 || cycle == cycles)
            {
                memory.Add(ReadMemory(cycle));
                Console.WriteLine($"RUNTIME_CYCLE {cycle}/{cycles} privateMiB={memory[^1].PrivateBytes/1048576d:F1}");
            }
        }
        Volatile.Write(ref running, 0); heartbeat.Change(Timeout.Infinite, Timeout.Infinite); Pump(100);
        var navigationCache = CacheStatistics(form);
        activeScenario = "queries";
        var queries = new List<object>();
        Select(form, "人物"); Ready();
        foreach (var conditions in new IReadOnlyList<RecordQueryCondition>[]
                 { [new(RecordQueryService.IdentityKey, "曹")], [new(null, "曹")], [new(null, "__no_literal_match__")] })
        {
            var watch = Stopwatch.StartNew();
            form.QueryConditionsForSmokeTest(conditions); Ready();
            // The candidate may capture large queries in UI batches, while the frozen version is synchronous.
            while (form.QuerySummaryForSmokeTest.Contains("正在", StringComparison.Ordinal) && watch.ElapsedMilliseconds < 30000)
            { Application.DoEvents(); Thread.Sleep(1); }
            queries.Add(new { Conditions=conditions, Ms=watch.Elapsed.TotalMilliseconds, Summary=form.QuerySummaryForSmokeTest });
        }
        form.QueryForSmokeTest(string.Empty); Ready();
        if (form.WorkspaceForSmokeTest.IsDirty) throw new InvalidOperationException("Performance navigation created draft changes.");
        var report = new
        {
            Schema="ccz.runtime-performance.v2", Version=typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Source=Path.GetFullPath(source), ActualDpi=form.DeviceDpi, Window=form.Size.ToString(), Cycles=cycles,
            ColdInteractiveMs=cold, FirstVisibleImageMs=firstImage, Memory=memory,
            Heartbeat=new { IntervalMs=50, P95Ms=Percentile(pulses,.95), MaximumMs=pulses.DefaultIfEmpty().Max(), Samples=pulses.Count },
            Scenarios=samples.GroupBy(sample=>sample.Scenario).Select(group=>new { Scenario=group.Key,
                P50Ms=Percentile(group.Select(sample=>sample.ReadyMs),.5), P95Ms=Percentile(group.Select(sample=>sample.ReadyMs),.95),
                MaximumMs=group.Max(sample=>sample.ReadyMs), SelectP95Ms=Percentile(group.Select(sample=>sample.SelectMs),.95) }),
            Samples=samples, FirstEntries=firstEntries, Queries=queries,
            SourceReads=form.WorkspaceForSmokeTest.GetType().GetProperty("LastSourceReadStatistics")?.GetValue(form.WorkspaceForSmokeTest),
            NavigationOnlyCache=navigationCache, FinalQueryCache=CacheStatistics(form),
            DiagnosticStages=stages.GroupBy(value => (value.Scenario, value.Stage)).Select(group => new
            { group.Key.Scenario, group.Key.Stage, Count=group.Count(), TotalMs=group.Sum(value=>value.Ms), P95Ms=Percentile(group.Select(value=>value.Ms),.95) }),
            Notes="Identical UI event harness; no duplicate smoke handler. Copying, hashing, screenshots and forced GC excluded. Data ready includes queued UI work; asynchronous image completion measured separately. No production or test forced GC in measured loops."
        };
        File.WriteAllText(Path.Combine(output,"runtime-performance.json"), JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        owned.Complete();
        timingSink?.SetValue(null, null);
        Console.WriteLine($"RUNTIME_PERFORMANCE_OK cycles={cycles} coldMs={cold:F1} heartbeatP95={Percentile(pulses,.95):F1}");
    }

    private static object? CacheStatistics(MainForm form)
    {
        var type = typeof(RecordQueryService).Assembly.GetType("DataManager.Core.Domain.WorkspaceReadCache");
        var cache = type?.GetMethod("For")?.Invoke(null, [form.WorkspaceForSmokeTest]);
        return type?.GetProperty("Statistics")?.GetValue(cache);
    }

    private static void Select(MainForm form, string scenario)
    {
        var module = scenario.StartsWith("physical/",StringComparison.Ordinal) ? (scenario[9..] == "strategies" ? "策略" : "人物") :
            scenario.StartsWith("extend/",StringComparison.Ordinal) ? "拓展数据" :
            scenario.StartsWith("__ranges/",StringComparison.Ordinal) ? "__ranges" : scenario;
        var key=scenario.StartsWith("physical/",StringComparison.Ordinal)?scenario[9..]:"__business";
        // Choose the intended remembered view before the one real tab event. Otherwise a
        // physical-table scenario from the preceding cycle activates an intermediate view.
        var remembered=(IDictionary<string,string>)typeof(MainForm).GetField("_moduleTables",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        remembered[module]=key;
        var page=form.ModuleTabsForSmokeTest.TabPages.Cast<TabPage>().Single(tab=>Equals(tab.Tag,module));
        if (form.ModuleTabsForSmokeTest.SelectedTab != page) form.ModuleTabsForSmokeTest.SelectedTab=page;
        var table=(ComboBox)typeof(MainForm).GetField("_tableSelector",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        var choice=table.Items.Cast<object>().FirstOrDefault(item=>Convert.ToString(item.GetType().GetProperty("Key")?.GetValue(item),CultureInfo.InvariantCulture)==key);
        if (choice == null && scenario.StartsWith("physical/", StringComparison.Ordinal))
            throw new InvalidOperationException("Missing benchmark physical table: " + scenario);
        if (choice!=null && !Equals(choice,table.SelectedItem)) table.SelectedItem=choice;
        if(scenario.StartsWith("extend/",StringComparison.Ordinal) && form.BusinessEditorForSmokeTest.ExtendEditor is {} extend)
        {
            var category=scenario[7..];
            var tab=extend.DetailTabs.TabPages.Cast<TabPage>().First(page=>page.Tag?.ToString()==category);
            if(extend.DetailTabs.SelectedTab!=tab) extend.DetailTabs.SelectedTab=tab;
        }
        if(scenario.StartsWith("__ranges/",StringComparison.Ordinal))
        {
            var tabs=Descendants(form.RangeHostForSmokeTest).OfType<TabControl>().First();
            tabs.SelectedIndex=int.Parse(scenario[9..],CultureInfo.InvariantCulture);
        }
    }
    private static void Ready(){Application.DoEvents();Thread.Sleep(1);Application.DoEvents();}
    private static void Pump(int ms){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(1);}}
    private static double Percentile(IEnumerable<double> values,double p){var a=values.Order().ToArray();return a.Length==0?0:a[Math.Clamp((int)Math.Ceiling(a.Length*p)-1,0,a.Length-1)];}
    private static Memory ReadMemory(int cycle){using var p=Process.GetCurrentProcess();p.Refresh();return new(cycle,p.PrivateMemorySize64,GC.GetTotalMemory(false),GetGuiResources(p.Handle,0),GetGuiResources(p.Handle,1));}
    private static IEnumerable<Control> Descendants(Control root){foreach(Control c in root.Controls){yield return c;foreach(var d in Descendants(c))yield return d;}}
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process,int flags);
}
