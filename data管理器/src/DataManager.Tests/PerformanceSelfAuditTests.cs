using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataManager.App;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class PerformanceSelfAuditTests
{
    public static void Run(string source, string output)
    {
        var report = new Dictionary<string, object>();
        const int rows = 10000, columns = 36;
        var captures = new List<Sample>();
        long retained = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            var document = DenseDocument(rows, columns);
            Collect(); var before = GC.GetTotalMemory(false);
            captures.Add(Measure(document.CaptureOriginalValues));
            Collect(); retained = GC.GetTotalMemory(false) - before;
            TestSupport.Assert(Equals(document.GetOriginal(rows - 1, "数值35"), (rows - 1) % 251), "原始值基线遗漏末行末列");
            TestSupport.Assert(document.GetOriginal(rows, "数值0") == null && document.GetOriginal(0, "未知") == null, "缺项原始值语义改变");
            var recovery = (Action)typeof(TableDocument).GetMethod("CaptureSaveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, null)!;
            document.Data.Rows[rows - 1]["数值35"] = 254;
            TestSupport.Assert(document.IsDirty && document.EnumerateChanges().Single().RowIndex == rows - 1, "末格修改未定位");
            document.CaptureOriginalValues();
            recovery();
            TestSupport.Assert(!document.IsDirty && Equals(document.Data.Rows[rows - 1]["数值35"], (rows - 1) % 251), "基线接受失败恢复不完整");
            document.Data.Dispose();
            GC.KeepAlive(document);
        }
        report["DenseBaseline"] = new { Rows = rows, Columns = columns, Capture = Median(captures), RetainedBytes = retained };

        var workspace = new ProjectWorkspace(); workspace.Open(source);
        using (var panel = new BusinessEditorPanel(workspace))
        {
            var options = typeof(BusinessEditorPanel).GetMethod("OptionsFor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object Read(string key) => options.Invoke(panel, [key])!;
            Read("persons"); Read("items");
            var samples = new List<Sample>();
            for (var pass = 0; pass < 5; pass++)
                samples.Add(Measure(() => { for (var i = 0; i < 1000; i++) { Read("persons"); Read("items"); } }));
            report["ReferenceOptions2000"] = Median(samples);
            var people = workspace.GetDocument("persons");
            var original = people.Data.Rows[0]["名称"];
            people.Data.Rows[0]["名称"] = "缓存验证";
            var current = (IReadOnlyList<DataManager.App.IdNameOption>)Read("persons");
            TestSupport.Assert(current.First(option => option.Id == 0).Name == "缓存验证", "名称修改后引用缓存未刷新");
            people.Data.Rows[0]["名称"] = original;
            TestSupport.Assert(((IReadOnlyList<DataManager.App.IdNameOption>)Read("persons")).First(option => option.Id == 0).Name == Convert.ToString(original), "名称撤回后引用缓存未刷新");
        }
        VerifyBaselineRecovery();
        VerifyReferenceInvalidation();
        report["Source"] = Path.GetFullPath(source);
        report["Runtime"] = Environment.Version.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine("PERFORMANCE_SELF_AUDIT_TESTS_OK");
    }

    private static void VerifyBaselineRecovery()
    {
        var document = DenseDocument(3, 2);
        document.Data.Rows[1]["数值1"] = DBNull.Value;
        document.CaptureOriginalValues();
        document.Data.Rows[1]["数值1"] = 17;
        document.OriginalFileBytes = [1, 2]; document.OriginalFileHash = "before";
        document.Data.ExtendedProperties["TextDecodeWarnings"] = "warning-before";
        var recover = (Action)typeof(TableDocument).GetMethod("CaptureSaveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, null)!;
        document.Data.Rows[0]["数值0"] = 9;
        document.OriginalFileBytes = [3]; document.OriginalFileHash = "after";
        document.Data.ExtendedProperties["TextDecodeWarnings"] = "warning-after";
        document.CaptureOriginalValues();
        recover();
        TestSupport.Assert(document.IsDirty && document.GetOriginal(1, "数值1") == null &&
            Equals(document.Data.Rows[1]["数值1"], 17) && document.EnumerateChanges().Single().RowIndex == 1,
            "恢复保存前的脏草稿、空值及原始基线失败");
        TestSupport.Assert(document.OriginalFileHash == "before" && document.OriginalFileBytes.SequenceEqual(new byte[] { 1, 2 }) &&
            Equals(document.Data.ExtendedProperties["TextDecodeWarnings"], "warning-before"), "恢复保存来源或文本诊断失败");
        document.Data.Dispose();
    }

    private static void VerifyReferenceInvalidation()
    {
        TableDocument Make(string name)
        {
            var document = DenseDocument(2, 1);
            document.Data.Columns.Add("名称", typeof(string));
            document.Data.Rows[0]["名称"] = name;
            document.Data.Rows[1]["名称"] = "第二条";
            document.Data.AcceptChanges();
            return document;
        }
        var first = Make("第一来源"); var second = Make("第二来源");
        using var cache = new LookupIndexCache();
        var documents = new Dictionary<string, TableDocument> { ["items-low"] = first, ["items-high"] = second };
        IReadOnlyList<IdNameOption> Read() => cache.ReferenceOptions("items", documents);
        var initial = Read();
        TestSupport.Assert(initial.Count == 3 && initial[0].Name == "第一来源" && initial[^1].Id == 255 && ReferenceEquals(initial, Read()),
            "引用去重顺序、空槽或缓存复用改变");
        first.Data.Rows[0]["数值0"] = 25;
        TestSupport.Assert(ReferenceEquals(initial, Read()), "无关数值编辑导致引用重建");
        first.Data.Rows[0]["名称"] = "改名";
        TestSupport.Assert(Read()[0].Name == "改名", "名称变化未刷新");
        first.Data.RejectChanges();
        TestSupport.Assert(Read()[0].Name == "第一来源", "回滚名称未刷新");
        first.Data.Rows[0]["ID"] = 7;
        TestSupport.Assert(Read()[0].Id == 7 && Read().Count == 4, "ID变化未刷新");
        first.Data.RejectChanges();
        first.Data.Rows.Add(8, 0, "新增");
        TestSupport.Assert(Read().Any(option => option.Id == 8), "新增行未刷新");
        first.Data.Rows[0].Delete();
        TestSupport.Assert(Read().First(option => option.Id == 0).Name == "第二来源", "删除行未刷新或去重优先级改变");
        first.Data.RejectChanges();
        first.Data.Columns["名称"]!.ColumnName = "别称";
        TestSupport.Assert(Read()[0].Name == string.Empty, "名称列结构变化未刷新");
        first.Data.Clear();
        TestSupport.Assert(Read()[0].Name == "第二来源", "清空表未刷新");
        documents["items-low"] = Make("项目重载");
        cache.Prune(documents.Values);
        TestSupport.Assert(Read()[0].Name == "项目重载", "替换项目文档后仍引用旧快照");
        documents.Clear(); cache.Prune(documents.Values);
        TestSupport.Assert(Read().Count == 1 && Read()[0].Id == 255, "移除项目后仍残留旧引用");
        first.Data.Dispose(); second.Data.Dispose();
    }

    private static TableDocument DenseDocument(int rows, int columns)
    {
        var table = new DataTable(); table.Columns.Add("ID", typeof(int));
        var fields = Enumerable.Range(0, columns).Select(index => new TableFieldDefinition("数值" + index, index * 4, 4, FieldKind.UInt32)).ToArray();
        foreach (var field in fields) table.Columns.Add(field.Name, typeof(int));
        table.BeginLoadData();
        for (var row = 0; row < rows; row++)
        {
            var values = new object[columns + 1]; values[0] = row;
            for (var column = 1; column <= columns; column++) values[column] = row % 251;
            table.Rows.Add(values);
        }
        table.EndLoadData();
        return new TableDocument
        {
            Definition = new("performance-dense", "基线内存基准", "Data.e5", 0, rows, columns * 4, 0, fields, "验证"),
            FilePath = "unused", Data = table, OriginalFileBytes = [], OriginalFileHash = "baseline"
        };
    }

    private sealed record Sample(double Ms, long AllocatedBytes);
    private static Sample Measure(Action action)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        action(); watch.Stop(); return new(watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
    private static Sample Median(IReadOnlyList<Sample> samples)
        => new(samples.Select(s => s.Ms).Order().ElementAt(samples.Count / 2), samples.Select(s => s.AllocatedBytes).Order().ElementAt(samples.Count / 2));
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
