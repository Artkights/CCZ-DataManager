using System.Data;
using System.Reflection;
using System.Text;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class DraftOperationAtomicityTests
{
    public static void Run(string basePath)
    {
        RejectInvalidFinalDraft(basePath);
        LateBundleFailureRestoresEveryDraft(basePath);
        IrrelevantConfigurationSurvivesLateLock(basePath);
        FailedUndoRedoKeepsCommand(basePath);
        Console.WriteLine("DRAFT_OPERATION_ATOMICITY_TESTS_OK");
    }

    private static void WithProject(string source, Action<string, ProjectWorkspace> test)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            File.WriteAllText(Path.Combine(root, "System.ini"), "[System]\r\nDefID=70\r\nAssID=109\r\n");
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var jobs = workspace.GetDocument("job-series");
            workspace.ApplyCompositeTransaction("保留原有草稿", [new("job-series", 0, "名称", "A"),
                new("persons", 0, "武力", (Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]) + 1) % 100)]);
            TestSupport.Assert(jobs.IsDirty && workspace.Commands.CanUndo, "原有草稿夹具未建立");
            test(root, workspace);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void RejectInvalidFinalDraft(string source) => WithProject(source, (root, workspace) =>
    {
        var jobs = workspace.GetDocument("job-series");
        var csv = WriteCsv(root, "jobs.csv", "ID,名称\r\n0,B\r\n");
        var persons = WriteCsv(root, "persons.csv", "ID,武力\r\n0,77\r\n");
        var operations = new Action[]
        {
            () => workspace.ImportCsv(jobs, csv),
            () => workspace.ImportCsvBundle(new Dictionary<string, string> { ["persons"] = persons, ["job-series"] = csv }),
            () => workspace.ApplyBatchMultiple(jobs, [0, 1], ["名称"], "统一赋值", "B"),
            () => workspace.ApplyFindReplace(jobs, [0], "名称", "A", "B"),
            () => workspace.ApplyCompositeTransaction("应拒绝", [new("persons", 0, "武力", 77), new("job-series", 0, "名称", "B")])
        };
        var before = Capture(workspace); var undo = CommandCount(workspace, "_undo"); var redo = CommandCount(workspace, "_redo");
        var files = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
        foreach (var operation in operations)
        {
            DataColumnChangeEventHandler rejectEdit = (_, args) =>
            {
                if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "B")
                    throw new InvalidDataException("模拟草稿操作在变更后失败");
            };
            jobs.Data.ColumnChanged += rejectEdit;
            try { Reject(operation); } finally { jobs.Data.ColumnChanged -= rejectEdit; }
            AssertEqual(workspace, before, "草稿变更失败时留下部分草稿或清掉原有草稿");
            TestSupport.Assert(workspace.IsDirty && CommandCount(workspace, "_undo") == undo && CommandCount(workspace, "_redo") == redo,
                "失败草稿操作增删了命令");
        }
        // The grid has already assigned B when RecordCellEdit is entered.
        // Inject the failure inside its protected operation, after that assignment.
        jobs.Data.Rows[0]["名称"] = "B";
        DataColumnChangeEventHandler rejectBoundEdit = (_, args) =>
        {
            if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "B")
                throw new InvalidDataException("模拟已绑定单元格登记失败");
        };
        jobs.Data.ColumnChanged += rejectBoundEdit;
        try { Reject(() => workspace.RecordCellEdit(jobs, 0, "名称", "A", "B")); }
        finally { jobs.Data.ColumnChanged -= rejectBoundEdit; }
        AssertEqual(workspace, before, "单元格登记失败未恢复编辑前草稿");
        TestSupport.Assert(CommandCount(workspace, "_undo") == undo && CommandCount(workspace, "_redo") == redo,
            "失败单元格登记增删了命令");
        TestSupport.Assert(files.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "草稿入口意外修改磁盘文件");
        TestSupport.Assert(workspace.Commands.Undo() == "保留原有草稿" && !workspace.IsDirty, "原有草稿命令在失败操作后不可撤回");
    });

    private static void LateBundleFailureRestoresEveryDraft(string source) => WithProject(source, (root, workspace) =>
    {
        var jobs = workspace.GetDocument("job-series");
        var csv = WriteCsv(root, "jobs.csv", "ID,名称\r\n0,B\r\n");
        var persons = WriteCsv(root, "persons.csv", "ID,武力\r\n0,77\r\n");
        var invalid = WriteCsv(root, "invalid.csv", "ID,不存在的字段\r\n0,X\r\n");
        var before = Capture(workspace); var undo = CommandCount(workspace, "_undo"); FileStream? locked = null;
        DataColumnChangeEventHandler lockAfterFirst = (_, args) =>
        {
            if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "B" && locked == null)
                locked = new FileStream(Path.Combine(root, "System.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        jobs.Data.ColumnChanged += lockAfterFirst;
        try
        {
            Reject(() => workspace.ImportCsvBundle(new Dictionary<string, string>
                { ["job-series"] = csv, ["persons"] = persons, ["person-biography"] = invalid }));
            TestSupport.Assert(locked != null, "未在前序表已修改后制造INI锁定");
            AssertEqual(workspace, before, "多表CSV晚失败未恢复全部表与派生值");
            TestSupport.Assert(CommandCount(workspace, "_undo") == undo && !workspace.Commands.CanRedo, "多表CSV晚失败产生了草稿命令");
        }
        finally { jobs.Data.ColumnChanged -= lockAfterFirst; locked?.Dispose(); }
    });

    private static void IrrelevantConfigurationSurvivesLateLock(string source) => WithProject(source, (root, workspace) =>
    {
        var jobs = workspace.GetDocument("job-series"); FileStream? locked = null;
        var before = Capture(workspace); var undo = CommandCount(workspace, "_undo");
        DataColumnChangeEventHandler lockAfterFirst = (_, args) =>
        {
            if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "B" && locked == null)
                locked = new FileStream(Path.Combine(root, "System.ini"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        jobs.Data.ColumnChanged += lockAfterFirst;
        try
        {
            workspace.ApplyBatchMultiple(jobs, [0, 1], ["名称"], "统一赋值", "B");
            TestSupport.Assert(locked != null && Convert.ToString(jobs.Data.Rows[1]["名称"]) == "B" && CommandCount(workspace, "_undo") == undo + 1,
                "无关INI锁定仍影响按真实类型派生或未登记完整批量命令");
        }
        finally { jobs.Data.ColumnChanged -= lockAfterFirst; locked?.Dispose(); }
        workspace.Commands.Undo(); AssertEqual(workspace, before, "批量操作撤回未恢复原有草稿");
    });

    private static void FailedUndoRedoKeepsCommand(string source) => WithProject(source, (root, workspace) =>
    {
        var jobs = workspace.GetDocument("job-series");
        workspace.ApplyBatch(jobs, [0, 1], "名称", "统一赋值", "B");
        var after = Capture(workspace); var undo = CommandCount(workspace, "_undo");
        DataColumnChangeEventHandler rejectUndo = (_, args) =>
        {
            if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "A")
                throw new InvalidDataException("模拟撤回刷新失败");
        };
        jobs.Data.ColumnChanged += rejectUndo;
        try { Reject(() => workspace.Commands.Undo()); } finally { jobs.Data.ColumnChanged -= rejectUndo; }
        AssertEqual(workspace, after, "撤回失败产生了部分草稿");
        TestSupport.Assert(CommandCount(workspace, "_undo") == undo && !workspace.Commands.CanRedo, "撤回失败丢失了命令");
        workspace.Commands.Undo(); var before = Capture(workspace); var redo = CommandCount(workspace, "_redo");
        DataColumnChangeEventHandler rejectRedo = (_, args) =>
        {
            if (args.Column?.ColumnName == "名称" && Convert.ToString(args.Row["名称"]) == "B")
                throw new InvalidDataException("模拟重做刷新失败");
        };
        jobs.Data.ColumnChanged += rejectRedo;
        try { Reject(() => workspace.Commands.Redo()); } finally { jobs.Data.ColumnChanged -= rejectRedo; }
        AssertEqual(workspace, before, "前进失败产生了部分草稿");
        TestSupport.Assert(CommandCount(workspace, "_redo") == redo, "前进失败丢失了命令");
        workspace.Commands.Redo(); AssertEqual(workspace, after, "解除失败注入后不能重做原命令");
    });

    private static Dictionary<string, object?[][]> Capture(ProjectWorkspace workspace)
        => workspace.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Data.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray());

    private static void AssertEqual(ProjectWorkspace workspace, IReadOnlyDictionary<string, object?[][]> expected, string message)
    {
        foreach (var (key, rows) in expected)
        {
            var actual = workspace.GetDocument(key).Data;
            TestSupport.Assert(actual.Rows.Count == rows.Length && actual.Rows.Cast<DataRow>().Select((row, index) => row.ItemArray.SequenceEqual(rows[index])).All(equal => equal),
                message + "：" + key);
        }
    }

    private static string WriteCsv(string root, string name, string content)
    { var path = Path.Combine(root, name); File.WriteAllText(path, content, new UTF8Encoding(true)); return path; }

    private static int CommandCount(ProjectWorkspace workspace, string name)
    {
        var stack = typeof(EditCommandManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace.Commands)!;
        return (int)stack.GetType().GetProperty("Count")!.GetValue(stack)!;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or AggregateException) { return; }
        throw new InvalidOperationException("预期失败的草稿操作返回了成功。");
    }
}
