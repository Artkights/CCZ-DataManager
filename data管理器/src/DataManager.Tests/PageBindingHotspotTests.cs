using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Domain;
using DataManager.Core.Models;
using DataManager.Core.Binary;

namespace DataManager.Tests;

internal static class PageBindingHotspotTests
{
    public static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.PageBindingHotspots", Path.GetFullPath(source));
        var root = owned.RootPath;
        Console.WriteLine("BINDING_OWNED_WORKSPACE " + root);
        try
        {
            var sourceFiles = Directory.EnumerateFiles(source).ToArray();
            foreach (var name in Known65FixtureSnapshot.CoreNames)
                File.Copy(sourceFiles.Single(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)), Path.Combine(root, name));
            foreach (var directory in new[] { source, Path.Combine(source, "E5") }.Where(Directory.Exists))
                foreach (var path in Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).Equals(".e5", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length < 4 * 1024 * 1024))
                {
                    var destination = Path.Combine(root, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, true);
                }
            using var form = new MainForm(false) { Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Size = new Size(1280, 800) };
            form.Show(); form.LoadProjectForSmokeTest(root); Pump();
            var samples = new List<object>();
            void Measure(string name, Action action, int count = 3)
            {
                for (var index = 0; index < count; index++)
                { Pump(); var timer = Stopwatch.StartNew(); action(); timer.Stop(); samples.Add(new { Phase = name, Sample = index, Ms = timer.Elapsed.TotalMilliseconds }); Console.WriteLine($"BINDING_PHASE {name} {index} {timer.Elapsed.TotalMilliseconds:F3}ms"); }
            }
            Measure("select-extend", () => { form.SelectModuleForSmokeTest("商店"); form.SelectModuleForSmokeTest("拓展数据"); });
            var panel = form.BusinessEditorForSmokeTest.ExtendEditor!;
            var service = new ExtendDataService(form.WorkspaceForSmokeTest);
            Measure("extend-get-status", () => _ = service.GetStatus(panel.Category));
            Measure("extend-get-options", () => _ = panel.GetEntityOptions());
            Measure("extend-raw", () => _ = service.GetRaw(panel.Category, panel.CurrentId));
            Measure("extend-diagnostics", () => _ = service.GetDiagnostics(panel.Category, panel.CurrentId));
            Measure("extend-reload-current", () => Invoke(panel, "ReloadCurrent"));
            Measure("extend-render-current", () => Invoke(panel, "RenderCurrent"));
            Measure("extend-bind-current", () => panel.BindEntity(panel.CurrentId));
            Measure("extend-query-navigation", () => Invoke(form, "RefreshQueryNavigation", true));
            Measure("select-physical", () => { form.SelectModuleForSmokeTest("商店"); form.SelectModuleForSmokeTest("人物"); form.ShowPhysicalTableForSmokeTest("persons"); });
            var document = form.WorkspaceForSmokeTest.GetDocument("persons");
            Measure("physical-configure-reference", () => Invoke(form, "ConfigureReferenceColumns", document));
            Measure("physical-configure-projected", () => Invoke(form, "ConfigureProjectedColumns", document));
            Measure("physical-query-navigation", () => Invoke(form, "RefreshQueryNavigation", false));
            Measure("physical-quick-lookup", () => Invoke(form, "PopulateQuickLookup", document));
            Measure("physical-empty-filter", () => Invoke(form, "ApplyEmptyFilter"));
            Measure("physical-widths", () => Invoke(form, "ApplyGridWidths", document.Definition.Key));
            Measure("physical-select-table-current", () => Invoke(form, "SelectTable"));
            File.WriteAllText(Path.Combine(output, "page-binding-phases.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            owned.Complete();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString());
            Console.WriteLine("BINDING_FAILED_WORKSPACE_RETAINED " + root);
            throw;
        }
    }
    private static void Invoke(object target, string name, params object?[] args)
    {
        try { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
    private static void Pump() { Application.DoEvents(); Thread.Sleep(5); Application.DoEvents(); }
}
