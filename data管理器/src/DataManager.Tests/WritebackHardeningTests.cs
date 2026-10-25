using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class WritebackHardeningTests
{
    public static void Run(string basePath)
    {
        CombinedSaveAndSingleUndo(basePath);
        FindReplaceIsAtomic(basePath);
        CandidateConflictsAndDiskVerification(basePath);
        LateHistoryFailure(basePath);
        HistoryRestoreFailure(basePath);
        ObsoleteCategoryConfigurationDoesNotBlockSave(basePath);
    }

    private static void WithProject(string source, Action<string, ProjectWorkspace> test)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try { var workspace = new ProjectWorkspace(); workspace.Open(root); test(root, workspace); }
        finally { Directory.Delete(root, true); }
    }

    private static void EditOrdinary(ProjectWorkspace workspace)
    {
        var person = workspace.GetDocument("persons");
        person.Data.Rows[0]["武力"] = (Convert.ToInt32(person.Data.Rows[0]["武力"]) + 1) % 100;
        workspace.GetDocument("person-biography").Data.Rows[0]["人物列传"] = "合并事务\n保留尾空白 ";
        var jobs = workspace.GetDocument("job-series");
        jobs.Data.Rows[0]["名称"] = "合并";
    }

    private static void CombinedSaveAndSingleUndo(string source) => WithProject(source, (root, workspace) =>
    {
        var files = new[] { "Data.e5", "Imsg.e5", "Ekd5.exe" }.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
        EditOrdinary(workspace);
        var groups = new AccessoryJobGroupService();
        var result = workspace.Save([groups.PrepareSave(workspace, [[0, 1], [2, 3]])]);
        TestSupport.Assert(result.Saved && result.History?.Files.Count == 3 && workspace.GetHistory().Count == 1,
            "普通数据与同EXE分组未形成一条三文件历史：" + string.Join("；", result.Messages));
        var reopened = new ProjectWorkspace(); reopened.Open(root);
        TestSupport.Assert(Convert.ToString(reopened.GetDocument("person-biography").Data.Rows[0]["人物列传"]) == "合并事务\n保留尾空白 ", "新工作区列传文本失真");
        TestSupport.Assert(groups.Read(reopened).Groups.Count == 2, "新工作区分组丢失");
        TestSupport.Assert(!workspace.IsDirty, "语义复读成功后仍残留普通草稿");
        TestSupport.Assert(workspace.UndoSaved().Success && files.All(file => File.ReadAllBytes(Path.Combine(root, file.Key)).SequenceEqual(file.Value)), "一次历史未完整恢复三文件");
        TestSupport.Assert(workspace.RedoSaved().Success && groups.Read(workspace).Groups.Count == 2, "合并历史重做失败");
    });

    private static void FindReplaceIsAtomic(string source) => WithProject(source, (_, workspace) =>
    {
        var names = workspace.GetDocument("persons");
        workspace.ApplyCompositeTransaction("查找测试", [new("persons", 0, "名称", "A"), new("persons", 1, "名称", "AAAAAA")]);
        var commands = workspace.Commands.CanUndo;
        var rejected = false;
        try { workspace.ApplyFindReplace(names, [0, 1], "名称", "A", "中文"); }
        catch (ArgumentException) { rejected = true; }
        TestSupport.Assert(rejected && Convert.ToString(names.Data.Rows[0]["名称"]) == "A" && Convert.ToString(names.Data.Rows[1]["名称"]) == "AAAAAA", "查找替换在后续行超限时留下部分草稿");
        TestSupport.Assert(workspace.Commands.CanUndo == commands, "失败替换改写了草稿撤回状态");
    });

    private static void CandidateConflictsAndDiskVerification(string source) => WithProject(source, (root, workspace) =>
    {
        EditOrdinary(workspace);
        var service = new AccessoryJobGroupService();
        var prepared = service.PrepareSave(workspace, [[0], [1]]);
        var patch = prepared.Patches[0];
        var conflicting = prepared with { Patches = [patch with { NewBytes = patch.NewBytes.Select(value => (byte)(value ^ 1)).ToArray() }] };
        var before = File.ReadAllBytes(Path.Combine(root, "Ekd5.exe"));
        var result = workspace.Save([prepared, conflicting]);
        TestSupport.Assert(!result.Saved && workspace.IsDirty && File.ReadAllBytes(Path.Combine(root, "Ekd5.exe")).SequenceEqual(before), "冲突补丁未整体拒绝");
        var count = 0;
        var verificationFailure = prepared with { CandidateVerifier = bytes => { prepared.CandidateVerifier!(bytes); if (++count == 2) throw new InvalidDataException("测试写后语义失败"); } };
        var allBefore = Directory.GetFiles(root).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        result = workspace.Save([verificationFailure]);
        TestSupport.Assert(!result.Saved && workspace.IsDirty && allBefore.All(file => File.ReadAllBytes(Path.Combine(root, file.Key!)).SequenceEqual(file.Value)), "磁盘复读失败未回滚或丢失草稿");
        var external = before.ToArray(); external[^1] ^= 1; File.WriteAllBytes(Path.Combine(root, "Ekd5.exe"), external);
        result = workspace.Save([prepared]);
        TestSupport.Assert(!result.Saved && workspace.IsDirty && File.ReadAllBytes(Path.Combine(root, "Ekd5.exe")).SequenceEqual(external), "过期基线覆盖外部修改");
    });

    private static void LateHistoryFailure(string source) => WithProject(source, (root, workspace) =>
    {
        EditOrdinary(workspace);
        var files = Directory.GetFiles(root).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var prepared = new AccessoryJobGroupService().PrepareSave(workspace, [[0, 1]]);
        var calls = 0; FileStream? locked = null;
        try
        {
            var edit = prepared with { CandidateVerifier = bytes =>
            {
                prepared.CandidateVerifier!(bytes);
                if (++calls == 2) locked = new FileStream(Path.Combine(workspace.History.GetRoot(root), "manifest.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read);
            } };
            var result = workspace.Save([edit]);
            TestSupport.Assert(!result.Saved && workspace.IsDirty && files.All(file => File.ReadAllBytes(Path.Combine(root, file.Key!)).SequenceEqual(file.Value)), "历史最终落盘失败没有恢复全部文件及草稿");
        }
        finally { locked?.Dispose(); }
    });

    private static void HistoryRestoreFailure(string source) => WithProject(source, (root, workspace) =>
    {
        EditOrdinary(workspace);
        TestSupport.Assert(workspace.Save().Saved, "历史失败测试前置保存失败");
        var files = Directory.GetFiles(root).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var manifest = Path.Combine(workspace.History.GetRoot(root), "manifest.jsonl");
        var history = File.ReadAllBytes(manifest);
        using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = workspace.UndoSaved();
            TestSupport.Assert(!result.Success && files.All(file => File.ReadAllBytes(Path.Combine(root, file.Key!)).SequenceEqual(file.Value)), "撤回历史落盘失败后文件未恢复操作前状态");
        }
        TestSupport.Assert(File.ReadAllBytes(manifest).SequenceEqual(history), "撤回失败改变了历史状态");
        TestSupport.Assert(workspace.UndoSaved().Success, "解除历史锁后不能再次撤回");
    });

    private static void ObsoleteCategoryConfigurationDoesNotBlockSave(string source) => WithProject(source, (root, workspace) =>
    {
        var configuration = Path.Combine(root, "System.ini");
        File.WriteAllText(configuration, "[Item]\r\nDefID=70\r\nAssID=109\r\n");
        workspace.Open(root);
        EditOrdinary(workspace);
        var person = workspace.GetDocument("persons");
        using (var locked = new FileStream(configuration, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = workspace.Save();
            TestSupport.Assert(result.Saved && !workspace.IsDirty,
                "已取消的DefID/AssID配置仍阻断按真实类型保存");
        }
    });
}
