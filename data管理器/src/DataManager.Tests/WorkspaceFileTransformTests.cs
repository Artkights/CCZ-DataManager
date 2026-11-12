using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class WorkspaceFileTransformTests
{
    public static void Run(string basePath)
    {
        CombinedTableSaveAndHistory(basePath);
        SharedBaselineConflicts(basePath);
        PatchPreservationAndSnapshotIsolation(basePath);
        ExclusiveCandidatesAndDuplicateTransforms(basePath);
        PostWriteFailureAndRetry(basePath);
        DependencyGuardAndUnregister(basePath);
        Console.WriteLine("WORKSPACE_FILE_TRANSFORM_TESTS_OK");
    }

    private static void CombinedTableSaveAndHistory(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var person = workspace.GetDocument("persons");
        var field = person.Definition.Fields.Single(item => item.Name == "武力");
        var offset = checked((int)(person.Definition.DataOffset + field.Offset));
        var path = person.FilePath;
        var before = File.ReadAllBytes(path);
        var value = (Convert.ToInt32(person.Data.Rows[0][field.Name]) + 1) % 256;
        workspace.ApplyCompositeTransaction("普通人物字段与候选联合保存", [new("persons", 0, field.Name, value)]);
        var calls = 0;
        var draft = new TransformDraft(new(Path.GetRelativePath(root, path), HashService.Sha256(before), context =>
        {
            calls++;
            TestSupport.Assert(context.BaselineBytes.AsSpan().SequenceEqual(before) && context.CandidateBytes[offset] == value,
                "转换器未收到共同基线及普通字段应用后的候选。");
            CheckActualDifferencePatches(context);
            return [.. context.CandidateBytes, 0x43, 0x43, 0x5A];
        }, "范围测试", "联合保存", candidate =>
            TestSupport.Assert(candidate.AsSpan(candidate.Length - 3).SequenceEqual(new byte[] { 0x43, 0x43, 0x5A }), "转换结果尾部复读失败。")));
        workspace.RegisterDraftParticipant(draft);
        var result = workspace.Save();
        var after = File.ReadAllBytes(path);
        TestSupport.Assert(result.Saved && calls == 1 && draft.Accepted && !workspace.IsDirty &&
            result.History is { SchemaVersion: "2.1", Files.Count: 1 } && result.History.Modules.Contains("范围测试") &&
            result.History.Files[0].WholeFileReplacement && result.History.Files[0].BeforeLength == before.Length &&
            after.Length == before.Length + 3 && after[offset] == value && Convert.ToInt32(person.GetOriginal(0, field.Name)) == value,
            "普通字段和变长候选没有进入同一历史或刷新基线：" + string.Join('；', result.Messages));
        TestSupport.Assert(workspace.UndoSaved(result.History!.OperationId).Success && File.ReadAllBytes(path).AsSpan().SequenceEqual(before),
            "联合候选历史撤回没有逐字节恢复原文件及长度。");
        TestSupport.Assert(workspace.RedoSaved(result.History.OperationId).Success && File.ReadAllBytes(path).AsSpan().SequenceEqual(after),
            "联合候选历史前进没有逐字节恢复转换结果。");
    });

    private static void SharedBaselineConflicts(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var person = workspace.GetDocument("persons");
        var path = person.FilePath;
        var before = File.ReadAllBytes(path);
        var external = (byte[])before.Clone(); external[^1] ^= 1;
        File.WriteAllBytes(path, external);
        var called = false;
        var result = workspace.Save([], [], [new(Path.GetRelativePath(root, path), HashService.Sha256(external), context =>
        { called = true; return context.CandidateBytes; }, "测试", "加载表旧基线")]);
        TestSupport.Assert(!result.Saved && !called && File.ReadAllBytes(path).AsSpan().SequenceEqual(external),
            "转换器新SHA掩盖了已加载普通表的旧基线。");
        File.WriteAllBytes(path, before);
        result = workspace.Save([], [], [new(Path.GetRelativePath(root, path), HashService.Sha256(external), context =>
        { called = true; return context.CandidateBytes; }, "测试", "转换器旧基线")]);
        TestSupport.Assert(!result.Saved && !called && File.ReadAllBytes(path).AsSpan().SequenceEqual(before) && workspace.GetHistory().Count == 0,
            "转换器与共同磁盘SHA不符时仍执行或产生历史。");
    });

    private static void PatchPreservationAndSnapshotIsolation(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var path = Path.Combine(root, "transform-test.bin");
        byte[] before = [0, 1, 2, 3, 4, 5]; File.WriteAllBytes(path, before);
        var hash = HashService.Sha256(before);
        PreparedBinaryEdit edit = new("transform-test.bin", hash,
            [new(1, [1], [9]), new(2, [2], [8]), new(4, [4], [4])], "普通补丁", "连续差异与无变化约束");
        var failed = workspace.Save([edit], [], [new("transform-test.bin", hash, context =>
        {
            CheckActualDifferencePatches(context);
            TestSupport.Assert(context.AppliedPatches.Count == 1 && context.AppliedPatches[0].Offset == 1 &&
                context.AppliedPatches[0].NewBytes.AsSpan().SequenceEqual(new byte[] { 9, 8 }), "实际差异未按连续区间合并或包含无变化字节。");
            context.AppliedPatches[0].NewBytes[0] = 1;
            return context.BaselineBytes;
        }, "测试", "恶意更改传入patch快照")]);
        TestSupport.Assert(!failed.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(before),
            "修改转换上下文绕过了原普通补丁落点检查。");
        failed = workspace.Save([edit], [], [new("transform-test.bin", hash, context =>
        {
            context.CandidateBytes[4] = 99;
            return context.CandidateBytes;
        }, "测试", "改写无变化补丁")]);
        TestSupport.Assert(!failed.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(before),
            "转换器改写原无变化patch要求后仍保存。");
        var succeeded = workspace.Save([edit], [], [new("transform-test.bin", hash, context =>
        {
            var result = context.CandidateBytes[..5];
            context.BaselineBytes[1] = 255;
            context.AppliedPatches[0].ExpectedBytes[0] = 255;
            return result;
        }, "测试", "缩短但保留原落点")]);
        TestSupport.Assert(succeeded.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 0, 9, 8, 3, 4 }) &&
            succeeded.History!.Files.Single().WholeFileReplacement, "有效缩短转换被拒绝或上下文突变影响保留基线。");
        TestSupport.Assert(workspace.UndoSaved(succeeded.History!.OperationId).Success && File.ReadAllBytes(path).AsSpan().SequenceEqual(before),
            "缩短候选不能恢复完整旧文件。");
    });

    private static void ExclusiveCandidatesAndDuplicateTransforms(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var path = Path.Combine(root, "exclusive.bin");
        byte[] before = [1, 2, 3]; File.WriteAllBytes(path, before);
        var hash = HashService.Sha256(before);
        PreparedFileTransform transform = new("exclusive.bin", hash, context => [.. context.CandidateBytes, 4], "测试", "唯一转换");
        var result = workspace.Save([], [new("exclusive.bin", hash, [1, 2, 4], "测试", "互斥完整候选")], [transform]);
        TestSupport.Assert(!result.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "完整候选与同文件转换没有互斥。");
        result = workspace.Save([], [], [transform, transform with { RelativePath = "./EXCLUSIVE.bin" }]);
        TestSupport.Assert(!result.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "相同文件的多个转换没有按规范路径拒绝。");
        result = workspace.Save([], [], [transform with { RelativePath = "../outside.bin" }]);
        TestSupport.Assert(!result.Saved && workspace.GetHistory().Count == 0, "越过项目根目录的转换未整体拒绝。");
    });

    private static void PostWriteFailureAndRetry(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var person = workspace.GetDocument("persons");
        var path = person.FilePath;
        var before = File.ReadAllBytes(path);
        var next = (Convert.ToInt32(person.Data.Rows[0]["武力"]) + 1) % 256;
        workspace.ApplyCompositeTransaction("失败后保留人物草稿", [new("persons", 0, "武力", next)]);
        var checks = 0;
        var draft = new TransformDraft(new(Path.GetRelativePath(root, path), HashService.Sha256(before), context =>
            [.. context.CandidateBytes, 1, 2], "范围测试", "模拟磁盘复读失败", _ =>
            { if (++checks == 2) throw new InvalidDataException("模拟转换结果磁盘复读失败。"); }));
        workspace.RegisterDraftParticipant(draft);
        var failure = workspace.Save();
        TestSupport.Assert(!failure.Saved && failure.History?.Status == HistoryStatus.Failed &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(before) && draft.HasPendingChanges && !draft.Accepted &&
            person.IsDirty && workspace.Commands.CanUndo && Convert.ToInt32(person.Data.Rows[0]["武力"]) == next,
            "写后转换复读失败未整体恢复文件、参与者、普通草稿与撤回栈。");
        var retry = workspace.Save();
        TestSupport.Assert(retry.Saved && draft.Accepted && !workspace.IsDirty && File.ReadAllBytes(path).Length == before.Length + 2,
            "转换失败后的保留草稿无法重试。");
    });

    private static void DependencyGuardAndUnregister(string basePath) => WithProject(basePath, (workspace, root) =>
    {
        var path = Path.Combine(root, "guard.bin");
        byte[] before = [3, 4, 5]; File.WriteAllBytes(path, before);
        var target = Path.Combine(root, "target.bin"); File.WriteAllBytes(target, [6]);
        var guard = new PreparedBinaryEdit("guard.bin", HashService.Sha256(before), [], "依赖", "空补丁守卫");
        var result = workspace.Save([guard], [], [new("target.bin", HashService.Sha256(new byte[] { 6 }),
            context => [.. context.CandidateBytes, 7], "测试", "带依赖保存")]);
        TestSupport.Assert(result.Saved && result.History!.Files.Count == 1 && result.History.Files[0].RelativePath == "target.bin" &&
            result.History.Summary.StartsWith("保存 1 个文件", StringComparison.Ordinal) &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "空补丁守卫被当成写文件或历史变更。");
        var draft = new TransformDraft(new("target.bin", HashService.Sha256(File.ReadAllBytes(target)),
            _ => throw new InvalidOperationException("已移除参与者不应参与保存"), "测试", "移除参与者"));
        workspace.RegisterDraftParticipant(draft);
        TestSupport.Assert(workspace.IsDirty, "参与者未接入dirty状态。");
        workspace.UnregisterDraftParticipant(draft);
        TestSupport.Assert(!workspace.IsDirty && workspace.Save().Saved && !draft.Accepted,
            "移除参与者后仍被查询或接受保存。");
    });

    private static void CheckActualDifferencePatches(PreparedFileTransformContext context)
    {
        var rebuilt = (byte[])context.BaselineBytes.Clone();
        long previousEnd = -1;
        foreach (var patch in context.AppliedPatches)
        {
            TestSupport.Assert(patch.Offset > previousEnd && patch.ExpectedBytes.Length == patch.NewBytes.Length &&
                patch.ExpectedBytes.Length > 0 && context.BaselineBytes.AsSpan((int)patch.Offset, patch.ExpectedBytes.Length).SequenceEqual(patch.ExpectedBytes) &&
                patch.NewBytes.Where((value, index) => value == patch.ExpectedBytes[index]).Count() == 0,
                "转换上下文的实际差异区间不是独立连续差异。");
            patch.NewBytes.CopyTo(rebuilt, checked((int)patch.Offset));
            previousEnd = patch.Offset + patch.NewBytes.Length;
        }
        TestSupport.Assert(rebuilt.AsSpan().SequenceEqual(context.CandidateBytes), "转换上下文差异无法重建普通候选。");
    }

    private static void WithProject(string basePath, Action<ProjectWorkspace, string> test)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try { var workspace = new ProjectWorkspace(); workspace.Open(root); test(workspace, root); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TransformDraft(PreparedFileTransform transform) : IWorkspaceDraftParticipant
    {
        public bool HasPendingChanges { get; private set; } = true;
        public bool Accepted { get; private set; }
        public IReadOnlyList<PreparedFileTransform> PrepareFileTransforms() => [transform];
        public void AcceptSaved() { HasPendingChanges = false; Accepted = true; }
        public Action CaptureSaveRecovery()
        {
            var pending = HasPendingChanges; var accepted = Accepted;
            return () => { HasPendingChanges = pending; Accepted = accepted; };
        }
        public void Discard() => HasPendingChanges = false;
    }
}
