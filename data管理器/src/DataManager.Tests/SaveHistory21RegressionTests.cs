using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataManager.Core.Binary;
using DataManager.Core.Csv;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class SaveHistory21RegressionTests
{
    public static void Run(string basePath)
    {
        VariableLengthSaveAndHistory(basePath);
        ConflictAndPostWriteRecovery(basePath);
        AcceptSavedFailureRecovery(basePath);
        LegacyHistoryAndCsvMetadata(basePath);
    }

    private static void VariableLengthSaveAndHistory(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var person = workspace.GetDocument("persons");
            var dataPath = Path.Combine(root, "Data.e5");
            var originalData = File.ReadAllBytes(dataPath);
            var relative = Path.Combine("RS", "save-history-21.eex");
            var scriptPath = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            var original = Encoding.ASCII.GetBytes("section=1;id=9\r\n");
            var expanded = Encoding.ASCII.GetBytes("section=1;id=100000;other=unchanged\r\n");
            var shortened = Encoding.ASCII.GetBytes("id=1\r\n");
            File.WriteAllBytes(scriptPath, original);
            var originalPower = Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture);
            var nextPower = (originalPower + 1) % 256;
            workspace.ApplyCompositeTransaction("联合保存人物与剧本", [new("persons", 0, "武力", nextPower)]);
            var checks = 0;
            var first = workspace.Save([], [new(relative, HashService.Sha256(original), expanded, "文件事务测试", "变长剧本测试", bytes =>
            {
                checks++;
                if (!bytes.AsSpan().SequenceEqual(expanded)) throw new InvalidDataException("剧本候选复读不一致。");
            })]);
            TestSupport.Assert(first.Saved && first.History?.SchemaVersion == "2.1" && checks >= 2,
                "变长候选和普通表未通过统一2.1保存及写前/写后验证：" + string.Join("；", first.Messages));
            var firstHistory = first.History!;
            TestSupport.Assert(firstHistory.Files.Count == 2 && firstHistory.Modules.Contains("文件事务测试") &&
                firstHistory.Files.Single(file => file.RelativePath == relative) is { WholeFileReplacement: true } replacement &&
                replacement.BeforeLength == original.Length && replacement.FileLength == expanded.Length,
                "2.1历史未同时记录普通表和变长文件的完整前后快照。");
            var firstData = File.ReadAllBytes(dataPath);
            TestSupport.Assert(!firstData.AsSpan().SequenceEqual(originalData) && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(expanded) && !workspace.IsDirty,
                "联合保存后磁盘或草稿状态不一致。");
            workspace.ApplyCompositeTransaction("第二次联合保存", [new("persons", 0, "武力", (nextPower + 1) % 256)]);
            var second = workspace.Save([], [new(relative, HashService.Sha256(expanded), shortened, "文件事务测试", "缩短剧本测试")]);
            TestSupport.Assert(second.Saved && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(shortened),
                "缩短文件的联合保存失败：" + string.Join("；", second.Messages));
            var finalData = File.ReadAllBytes(dataPath);
            var history = workspace.History;
            var profile = workspace.ActiveProfile;
            var undoSecond = history.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, second.History!.OperationId);
            TestSupport.Assert(undoSecond.Success && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(expanded) &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(firstData), "撤回缩短剧本时没有同时恢复普通表与原文件长度：" + undoSecond.Message);
            var undoFirst = history.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, firstHistory.OperationId);
            TestSupport.Assert(undoFirst.Success && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(original) &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(originalData), "连续撤回不能逐字节恢复联合保存前状态：" + undoFirst.Message);
            TestSupport.Assert(history.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, firstHistory.OperationId).Success &&
                history.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, second.History.OperationId).Success &&
                File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(shortened) && File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(finalData),
                "连续历史前进没有恢复变长与定长文件的最终状态。");
            var outside = second.History;
            File.AppendAllText(scriptPath, "external");
            var externallyChanged = File.ReadAllBytes(scriptPath);
            var rejected = history.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, outside.OperationId);
            TestSupport.Assert(!rejected.Success && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(externallyChanged) &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(finalData), "历史外部冲突没有在任何文件写入前整体拒绝。");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ConflictAndPostWriteRecovery(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var person = workspace.GetDocument("persons");
            var dataPath = Path.Combine(root, "Data.e5");
            var originalData = File.ReadAllBytes(dataPath);
            var scriptPath = Path.Combine(root, "RS", "atomic-save.eex");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            var original = Encoding.ASCII.GetBytes("opcode=72;id=1;");
            var candidate = Encoding.ASCII.GetBytes("opcode=72;id=1000000;");
            File.WriteAllBytes(scriptPath, original);
            var value = (Convert.ToInt32(person.Data.Rows[0]["武力"], CultureInfo.InvariantCulture) + 1) % 256;
            workspace.ApplyCompositeTransaction("保留未保存人物草稿", [new("persons", 0, "武力", value)]);
            var checks = 0;
            var draft = new TestFileDraft(new("RS/atomic-save.eex", HashService.Sha256(original), candidate,
                "文件事务测试", "原生草稿", _ =>
                {
                    checks++;
                    if (checks == 2) throw new InvalidDataException("模拟写后语义复读失败。");
                }));
            workspace.RegisterDraftParticipant(draft);
            File.AppendAllText(scriptPath, "external");
            var external = File.ReadAllBytes(scriptPath);
            var conflict = workspace.Save();
            TestSupport.Assert(!conflict.Saved && draft.HasPendingChanges && !draft.Accepted && workspace.IsDirty &&
                person.IsDirty && Convert.ToInt32(person.Data.Rows[0]["武力"]) == value &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(originalData) && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(external),
                "完整文件来源SHA冲突未拒绝，或丢失普通表/原生草稿。");
            File.WriteAllBytes(scriptPath, original);
            var failure = workspace.Save();
            TestSupport.Assert(!failure.Saved && checks == 2 && failure.History?.Status == HistoryStatus.Failed &&
                draft.HasPendingChanges && !draft.Accepted && person.IsDirty && workspace.IsDirty &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(originalData) && File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(original),
                "写后失败未原子恢复变长文件、普通表、历史状态与草稿：" + string.Join("；", failure.Messages));
            var retry = workspace.Save();
            TestSupport.Assert(retry.Saved && draft.Accepted && !draft.HasPendingChanges && !workspace.IsDirty &&
                File.ReadAllBytes(scriptPath).AsSpan().SequenceEqual(candidate), "失败后的草稿不能不丢失地重试保存。");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AcceptSavedFailureRecovery(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var person = workspace.GetDocument("persons");
            var original0 = Convert.ToInt32(person.Data.Rows[0]["武力"]);
            var original1 = Convert.ToInt32(person.Data.Rows[1]["武力"]);
            var next0 = (original0 + 1) % 256; var next1 = (original1 + 1) % 256;
            workspace.ApplyCompositeTransaction("保存失败后应保留的撤回", [new("persons", 0, "武力", next0)]);
            workspace.ApplyCompositeTransaction("保存失败后应保留的重做", [new("persons", 1, "武力", next1)]);
            workspace.Commands.Undo();
            var dataPath = Path.Combine(root, "Data.e5"); var originalData = File.ReadAllBytes(dataPath);
            var scripts = new[] { Path.Combine(root, "RS", "accept-first.eex"), Path.Combine(root, "RS", "accept-second.eex") };
            Directory.CreateDirectory(Path.GetDirectoryName(scripts[0])!);
            var before = Encoding.ASCII.GetBytes("parameter=1\r\n");
            var after = Encoding.ASCII.GetBytes("parameter=1000000\r\n");
            foreach (var path in scripts) File.WriteAllBytes(path, before);
            var first = new TestFileDraft(new("RS/accept-first.eex", HashService.Sha256(before), after, "文件事务测试", "先接受的草稿"));
            var second = new TestFileDraft(new("RS/accept-second.eex", HashService.Sha256(before), after, "文件事务测试", "接受后故障的草稿")) { FailOnAccept = true };
            workspace.RegisterDraftParticipant(first); workspace.RegisterDraftParticipant(second);
            var failed = workspace.Save();
            TestSupport.Assert(!failed.Saved && failed.History?.Status == HistoryStatus.Failed &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(originalData) && scripts.All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(before)),
                "接受草稿故障未原子恢复所有定长/变长文件与失败历史：" + string.Join('；', failed.Messages));
            TestSupport.Assert(first.HasPendingChanges && second.HasPendingChanges && !first.Accepted && !second.Accepted &&
                person.IsDirty && Convert.ToInt32(person.Data.Rows[0]["武力"]) == next0 && Convert.ToInt32(person.GetOriginal(0, "武力")) == original0 &&
                workspace.Commands.CanUndo && workspace.Commands.CanRedo,
                "后一个原生草稿接受失败时，先接受的原生草稿、普通表基线或撤回/重做栈未完整恢复。");
            workspace.Commands.Redo();
            TestSupport.Assert(Convert.ToInt32(person.Data.Rows[1]["武力"]) == next1, "保存接受失败丢失既有重做命令。");
            workspace.Commands.Undo(); workspace.Commands.Undo();
            TestSupport.Assert(!person.IsDirty && Convert.ToInt32(person.Data.Rows[0]["武力"]) == original0 &&
                first.HasPendingChanges && second.HasPendingChanges, "保存接受失败后的撤回未正确恢复普通表草稿。");
            workspace.Commands.Redo();
            second.FailOnAccept = false;
            var retry = workspace.Save();
            TestSupport.Assert(retry.Saved && first.Accepted && second.Accepted && !workspace.IsDirty &&
                !workspace.Commands.CanUndo && !workspace.Commands.CanRedo && scripts.All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(after)),
                "接受故障解除后无法使用恢复的草稿重试保存：" + string.Join('；', retry.Messages));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void LegacyHistoryAndCsvMetadata(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace(); workspace.Open(root);
            var dataPath = Path.Combine(root, "Data.e5");
            var before = File.ReadAllBytes(dataPath);
            var person = workspace.GetDocument("persons");
            var value = (Convert.ToInt32(person.Data.Rows[0]["武力"]) + 1) % 256;
            workspace.ApplyCompositeTransaction("兼容旧历史", [new("persons", 0, "武力", value)]);
            var saved = workspace.Save();
            TestSupport.Assert(saved.Saved && saved.History != null, "旧历史测试基线保存失败。");
            var after = File.ReadAllBytes(dataPath);
            var priorRevision = PreviousReleaseRevision(workspace);
            TestSupport.Assert(priorRevision != workspace.ActiveProfile.LayoutRevision &&
                LayoutRevisionCompatibility.IsCompatible(root, workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId,
                    priorRevision, workspace.ActiveProfile.LayoutRevision), "不能精确识别本次前版本的布局修订。");
            var json = JsonSerializer.SerializeToNode(saved.History)!.AsObject();
            json["SchemaVersion"] = "2.0"; json["LayoutRevision"] = priorRevision;
            foreach (var file in json["Files"]!.AsArray().OfType<JsonObject>())
            { file.Remove("WholeFileReplacement"); file.Remove("BeforeLength"); }
            var manifest = Path.Combine(workspace.History.GetRoot(root), "manifest.jsonl");
            File.WriteAllText(manifest, json.ToJsonString() + Environment.NewLine, new UTF8Encoding(true));
            var loaded = workspace.History.Load(root).Single();
            TestSupport.Assert(loaded.SchemaVersion == "2.0" && loaded.Files.All(file => !file.WholeFileReplacement && file.BeforeLength == null),
                "缺少2.1新增属性的旧2.0 JSON不能无损读取。");
            var profile = workspace.ActiveProfile;
            var undo = workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, loaded.OperationId);
            TestSupport.Assert(undo.Success && File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(before), "前版本布局的旧2.0历史不能撤回：" + undo.Message);
            TestSupport.Assert(workspace.History.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, loaded.OperationId).Success &&
                File.ReadAllBytes(dataPath).AsSpan().SequenceEqual(after), "前版本布局的旧2.0历史不能前进。");
            CheckCsvMetadata(workspace, person, root, priorRevision);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckCsvMetadata(ProjectWorkspace workspace, TableDocument person, string root, string priorRevision)
    {
        var path = Path.Combine(root, "metadata.csv");
        workspace.ExportCsv(person, path);
        var exported = File.ReadAllText(path);
        var newline = exported.IndexOf('\n');
        var metadata = exported[..newline].TrimEnd('\r').Split(',');
        var body = exported[(newline + 1)..];
        foreach (var padding in new[] { ",", ",,,,", ",,   ,\t," })
        {
            File.WriteAllText(path, string.Join(',', metadata) + padding + "\r\n" + body, new UTF8Encoding(true));
            TestSupport.Assert(CsvService.ReadContext(path)?.TableKey == "persons" && workspace.ImportCsv(person, path).ChangedCells == 0,
                "Excel补齐的空白元数据列应接受且无损回导。");
        }
        var old = (string[])metadata.Clone(); old[5] = priorRevision;
        File.WriteAllText(path, string.Join(',', old) + ",,,\r\n" + body, new UTF8Encoding(true));
        TestSupport.Assert(workspace.ImportCsv(person, path).ChangedCells == 0, "本次前版本布局CSV不能安全回导。");
        foreach (var invalid in new[]
        {
            string.Join(',', metadata) + ",nonempty,,,",
            string.Join(',', metadata.Take(5)),
            string.Join(',', metadata.Take(5)) + ",",
            "#DataManagerCSV,1,," + string.Join(',', metadata.Skip(3)),
            string.Join(',', metadata.Take(5)) + ",arbitrary-layout",
            "#DataManagerCSV,999," + string.Join(',', metadata.Skip(2))
        })
        {
            File.WriteAllText(path, invalid + "\r\n" + body, new UTF8Encoding(true));
            var rejected = false;
            try { workspace.ImportCsv(person, path); }
            catch (InvalidDataException) { rejected = true; }
            TestSupport.Assert(rejected && !workspace.IsDirty, "非空附加列、缺字段或未知身份的CSV元数据未整体拒绝。");
        }
    }

    private static string PreviousReleaseRevision(ProjectWorkspace workspace)
    {
        var baseline = new ProfileRegistry().Resolve(workspace.ActiveProfile.ProfileId, workspace.ActiveProfile.VariantId);
        var geometry = string.Join("|", workspace.Definitions.Where(table => table.Key is
                "person-r-assign" or "person-s-assign" or "person-critical-special-map" or "strategy-terrain-availability")
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}"));
        var extensions = workspace.Definitions.Where(table => table.Key is "effect-archive" or "effect-messages" or "strategy-study-extended" or "forge")
            .Where(table => baseline.Tables.Any(original => original.Key == table.Key &&
                (original.RowCount != table.RowCount || original.Capability != table.Capability)))
            .OrderBy(table => table.Key).Select(table => $"{table.Key}:{table.Capability}:{table.DataOffset}:{table.RowCount}:{table.RowSize}").ToArray();
        var seed = baseline.LayoutRevision + "|" + geometry + (extensions.Length == 0 ? "" : "|" + string.Join("|", extensions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16];
    }

    private sealed class TestFileDraft(PreparedFileEdit edit) : IWorkspaceDraftParticipant
    {
        public bool HasPendingChanges { get; private set; } = true;
        public bool Accepted { get; private set; }
        public bool FailOnAccept { get; set; }
        public IReadOnlyList<PreparedFileEdit> PrepareFileEdits() => HasPendingChanges ? [edit] : [];
        public void AcceptSaved()
        {
            HasPendingChanges = false; Accepted = true;
            if (FailOnAccept) throw new InvalidDataException("模拟接受原生草稿时发生故障。");
        }
        public Action CaptureSaveRecovery()
        {
            var pending = HasPendingChanges; var accepted = Accepted;
            return () => { HasPendingChanges = pending; Accepted = accepted; };
        }
        public void Discard() => HasPendingChanges = false;
    }
}
