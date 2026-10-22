using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Workbooks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DataManager.Tests;

/// <summary>Opt-in repeatable benchmark; operates on drafts and writes only its output folder.</summary>
internal static class WorkbookPerformanceTests
{
    public static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var workspace = new ProjectWorkspace();
        workspace.Open(source);
        var service = new PageWorkbookService(workspace);
        var results = new List<object>();
        foreach (var page in new[] { BusinessPageKind.Persons, BusinessPageKind.DetailedJobs, BusinessPageKind.ExtendData })
        {
            var path = Path.Combine(output, page + ".xlsx");
            // Warm the codecs, JIT and file-system cache; compare steady-state algorithms separately from project opening.
            service.Export(page, path);
            Assert(service.PreviewImport(page, path).ChangedCells == 0, "Unchanged workbook must round-trip without proposals.");
            for (var iteration = 0; iteration < 5; iteration++)
            {
                results.Add(Measure($"{page}.Export", iteration, () => service.Export(page, path), path));
                results.Add(Measure($"{page}.PreviewUnchanged", iteration,
                    () => Assert(service.PreviewImport(page, path).ChangedCells == 0, "Unchanged import changed cells."), path));
            }
        }

        var dense = Path.Combine(output, "Persons-dense.xlsx");
        service.Export(BusinessPageKind.Persons, dense);
        var changed = MakeDensePersonEdits(dense);
        Assert(changed > 0, "Dense import fixture has no edited cells.");
        for (var iteration = -1; iteration < 5; iteration++)
        {
            PageWorkbookImportPreview? preview = null;
            var measured = Measure("Persons.PreviewDense", iteration, () =>
            {
                preview = service.PreviewImport(BusinessPageKind.Persons, dense);
                Assert(preview.ChangedCells == changed, "Dense preview lost edits.");
            }, dense);
            if (iteration >= 0) results.Add(measured);
            measured = Measure("Persons.ApplyDense", iteration,
                () => Assert(service.ApplyImport(preview!) == changed, "Dense apply lost edits."), dense);
            if (iteration >= 0) results.Add(measured);
            Assert(workspace.IsDirty, "Dense import must create a draft.");
            workspace.Commands.Undo();
            Assert(!workspace.IsDirty, "Dense import undo must recover the complete draft.");
            workspace.Commands.Clear();
        }
        File.WriteAllText(Path.Combine(output, "workbook-performance.json"), JsonSerializer.Serialize(new
        {
            Source = Path.GetFullPath(source), Profile = workspace.ActiveProfile.ProfileId,
            Variant = workspace.ActiveProfile.VariantId, Layout = workspace.ActiveProfile.LayoutRevision,
            DenseChanges = changed, Runtime = Environment.Version.ToString(),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default", Samples = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"WORKBOOK_PERFORMANCE_OK dense={changed} output={output}");
    }

    private static object Measure(string operation, int iteration, Action action, string path)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew(); action(); watch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        return new { Operation = operation, Iteration = iteration, ElapsedMs = watch.Elapsed.TotalMilliseconds,
            CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            AllocatedBytes = allocatedBytes, FileBytes = new FileInfo(path).Length };
    }

    private static int MakeDensePersonEdits(string path)
    {
        using var package = SpreadsheetDocument.Open(path, true);
        var workbook = package.WorkbookPart!;
        var meta = workbook.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == "_DataManager");
        using var json = JsonDocument.Parse(string.Concat(((WorksheetPart)workbook.GetPartById(meta.Id!)).Worksheet.Descendants<Text>().Select(t => t.Text)));
        var id = json.RootElement.GetProperty("Datasets").EnumerateArray().Single(d => d.GetProperty("Key").GetString() == "persons").GetProperty("SheetId").GetUInt32();
        var sheet = workbook.Workbook.Sheets.Elements<Sheet>().Single(s => s.SheetId?.Value == id);
        var part = (WorksheetPart)workbook.GetPartById(sheet.Id!);
        var rows = part.Worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        var fields = new HashSet<string>(["武力", "智力", "统率", "敏捷", "运气"], StringComparer.Ordinal);
        var columns = rows.Single(r => r.RowIndex?.Value == 1).Elements<Cell>()
            .Where(c => fields.Contains(c.InnerText)).Select(c => Letters(c.CellReference!.Value!)).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var cell in rows.Where(r => r.RowIndex?.Value >= 3).SelectMany(r => r.Elements<Cell>()))
        {
            if (!columns.Contains(Letters(cell.CellReference!.Value!))) continue;
            var value = int.Parse(cell.InnerText, CultureInfo.InvariantCulture);
            cell.RemoveAllChildren(); cell.DataType = CellValues.Number; cell.CellValue = new CellValue(((value + 1) % 100).ToString(CultureInfo.InvariantCulture)); count++;
        }
        part.Worksheet.Save(); return count;
    }
    private static string Letters(string reference) => new(reference.TakeWhile(char.IsLetter).ToArray());
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
