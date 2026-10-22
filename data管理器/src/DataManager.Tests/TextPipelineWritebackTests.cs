using System.Data;
using System.Text;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;

namespace DataManager.Tests;

/// <summary>Exercises actual CSV and migration pipelines on disposable core copies.</summary>
internal static class TextPipelineWritebackTests
{
    public static void Run(string basePath)
    {
        CsvInvalidTextRejectsWholeFile(basePath);
        CsvFullBiographySavesAndReopens(basePath);
        MigrationFullBiographyRoundTrips(basePath);
        MigrationHonorsTargetHiddenTail(basePath);
        Console.WriteLine("TEXT_PIPELINE_WRITEBACK_TESTS_OK");
    }

    private static string FullBiography()
    {
        var text = new string('传', 98) + "\r\nA ";
        TestSupport.Assert(EncodingService.GameEncoding.GetByteCount(text) == 200, "列传夹具不满200字节");
        return text;
    }

    private static void CsvInvalidTextRejectsWholeFile(string source)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = Open(root);
            var biography = workspace.GetDocument("person-biography");
            workspace.ApplyCompositeTransaction("CSV前已有草稿", [new("person-biography", 0, "人物列传", "既有\n草稿"),
                new("persons", 0, "武力", (Convert.ToInt32(workspace.GetDocument("persons").Data.Rows[0]["武力"]) + 1) % 100)]);
            var drafts = CaptureDrafts(workspace);
            var before = CoreBytes(root);
            var csv = Path.Combine(root, "多行文本输入.csv");
            foreach (var (value, diagnostic) in new[]
                     { (new string('汉', 101), "GBK"), ("第二行😀不可编码", "GBK"), ("前缀\0尾部", "NUL") })
            {
                File.WriteAllText(csv, "ID,人物列传\r\n0," + Quote("第一条合法\r\n多行文本") + "\r\n1," + Quote(value) + "\r\n", new UTF8Encoding(true));
                InvalidDataException? error = null;
                try { workspace.ImportCsv(biography, csv); }
                catch (InvalidDataException ex) { error = ex; }
                TestSupport.Assert(error?.Message.Contains(diagnostic, StringComparison.OrdinalIgnoreCase) == true,
                    $"CSV无效文本未明确拒绝：{diagnostic}；{error?.Message}");
                AssertDrafts(workspace, drafts, "CSV后续记录无效留下前序文本或破坏已有草稿");
                AssertFiles(root, before, "被拒绝CSV修改了磁盘文件");
                TestSupport.Assert(workspace.GetHistory().Count == 0 && workspace.IsDirty && workspace.Commands.CanUndo && !workspace.Commands.CanRedo,
                    "被拒绝CSV改变历史或草稿命令状态");
                TestSupport.Assert(workspace.Commands.Undo() == "CSV前已有草稿" && !workspace.IsDirty,
                    "被拒绝CSV登记了新命令或破坏原有命令");
                workspace.Commands.Redo(); AssertDrafts(workspace, drafts, "CSV失败后已有草稿不能重做");
            }
        }
        finally { DeleteCopy(root); }
    }

    private static void CsvFullBiographySavesAndReopens(string source)
    {
        var root = TestSupport.CreateCoreCopy(source);
        try
        {
            var workspace = Open(root); var document = workspace.GetDocument("person-biography");
            var text = FullBiography(); var csv = Path.Combine(root, "满容量列传.csv");
            File.WriteAllText(csv, "ID,人物列传\r\n0," + Quote(text) + "\r\n", new UTF8Encoding(true));
            var result = workspace.ImportCsv(document, csv);
            TestSupport.Assert(result.ChangedCells == 1 && Convert.ToString(document.Data.Rows[0]["人物列传"]) == text, "合法200字节CSV没有完整导入");
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && !workspace.IsDirty, "合法200字节CSV保存失败：" + string.Join("；", save.Messages));
            var reopened = Open(root);
            TestSupport.Assert(Convert.ToString(reopened.GetDocument("person-biography").Data.Rows[0]["人物列传"]) == text,
                "合法200字节CSV新工作区复读丢失CRLF或尾空格");
            var field = document.Definition.Fields.Single();
            var offset = checked((int)document.Definition.DataOffset + field.Offset);
            TestSupport.Assert(File.ReadAllBytes(document.FilePath).AsSpan(offset, 200).SequenceEqual(EncodingService.GameEncoding.GetBytes(text)),
                "合法200字节列传未完整写入磁盘槽位");
        }
        finally { DeleteCopy(root); }
    }

    private static void MigrationFullBiographyRoundTrips(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath); var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var sourceWorkspace = Open(source); var text = FullBiography();
            sourceWorkspace.ApplyCompositeTransaction("源列传满容量", [new("person-biography", 0, "人物列传", text)]);
            TestSupport.Assert(sourceWorkspace.Save().Saved, "迁移源200字节列传保存失败");
            var sourceBefore = CoreBytes(source); var targetBefore = CoreBytes(target);
            var service = new MigrationService(); var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            var choice = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey == "person-biography" && item.change.Id == 0 && item.change.FieldName == "人物列传")
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(choice.Count == 1 && plan.Changes[choice.Keys.Single()].Kind == MigrationChangeKind.Changed,
                "200字节列传未在迁移预览形成唯一可写字段");
            var applied = service.Apply(plan, choice);
            TestSupport.Assert(applied.Success && applied.AppliedChanges == 1, "200字节列传迁移提交失败：" + string.Join("；", applied.Messages));
            var targetWorkspace = Open(target);
            TestSupport.Assert(Convert.ToString(targetWorkspace.GetDocument("person-biography").Data.Rows[0]["人物列传"]) == text,
                "迁移200字节列传新工作区复读失真");
            var targetAfter = CoreBytes(target);
            TestSupport.Assert(targetWorkspace.UndoSaved().Success, "迁移200字节列传历史不能撤回");
            AssertFiles(target, targetBefore, "撤回列传迁移未逐字节恢复目标全部核心文件");
            TestSupport.Assert(targetWorkspace.RedoSaved().Success, "迁移200字节列传历史不能前进");
            AssertFiles(target, targetAfter, "前进列传迁移未逐字节恢复提交状态");
            TestSupport.Assert(Convert.ToString(Open(target).GetDocument("person-biography").Data.Rows[0]["人物列传"]) == text,
                "迁移历史前进后新工作区复读失真");
            AssertFiles(source, sourceBefore, "列传迁移或历史操作修改源基底");
        }
        finally { DeleteCopy(source); DeleteCopy(target); }
    }

    private static void MigrationHonorsTargetHiddenTail(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath); var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var sourceWorkspace = Open(source); var text = "迁移名称"; // Eight GBK bytes: valid in a normal twelve-byte name budget.
            sourceWorkspace.ApplyCompositeTransaction("合法源名称", [new("persons", 0, "名称", text)]);
            TestSupport.Assert(sourceWorkspace.Save().Saved, "隐藏尾部测试源名称保存失败");
            var definition = sourceWorkspace.GetDocument("persons").Definition; var field = definition.Fields.Single(field => field.Name == "名称");
            var targetPath = Path.Combine(target, definition.FileName); var bytes = File.ReadAllBytes(targetPath);
            var offset = checked((int)definition.DataOffset + field.Offset); bytes.AsSpan(offset, field.Size).Clear();
            bytes[offset] = (byte)'A'; bytes[offset + 6] = 0xDE; bytes[offset + 7] = 0xAD; File.WriteAllBytes(targetPath, bytes);
            var targetDocument = Open(target).GetDocument("persons");
            var rule = TextFieldConstraints.Get(targetDocument.Definition, field, targetDocument.OriginalFileBytes.AsSpan(checked((int)definition.DataOffset), definition.RowSize));
            TestSupport.Assert(rule.MaxPayloadBytes == 5 && !rule.Evaluate(text).CanEncode, "目标隐藏尾部未将实际容量收窄为5字节");
            var targetBefore = CoreBytes(target); var sourceBefore = CoreBytes(source);
            var service = new MigrationService(); var plan = service.Preview(source, target, "ccz-6.5", "65-standard-v1", "ccz-6.5", "65-standard-v1");
            var incompatible = plan.Changes.Select((change, index) => (change, index)).Single(item =>
                item.change.TableKey == "persons" && item.change.Id == 0 && item.change.FieldName == "名称");
            TestSupport.Assert(incompatible.change.Kind == MigrationChangeKind.Incompatible && incompatible.change.Reason?.Contains("5", StringComparison.Ordinal) == true &&
                !plan.WritableChanges.Any(change => change.TableKey == "persons" && change.Id == 0 && change.FieldName == "名称"),
                "迁移未明确标记目标实际文本容量不足");
            // A user-supplied overwrite choice cannot make an incompatible preview item writable.
            var result = service.Apply(plan, new Dictionary<int, MigrationChoice> { [incompatible.index] = MigrationChoice.Overwrite });
            TestSupport.Assert(result.Success && result.AppliedChanges == 0, "不可兼容名称被强制覆盖选择写入");
            AssertFiles(target, targetBefore, "目标隐藏非零尾部在不可兼容迁移中被改动");
            AssertFiles(source, sourceBefore, "不可兼容迁移修改了源基底");
            TestSupport.Assert(Open(target).GetHistory().Count == 0, "跳过不可兼容文本创建了虚假保存历史");
        }
        finally { DeleteCopy(source); DeleteCopy(target); }
    }

    private static ProjectWorkspace Open(string root)
    { var workspace = new ProjectWorkspace(); workspace.Open(root, "ccz-6.5", "65-standard-v1"); return workspace; }

    private static Dictionary<string, byte[]> CoreBytes(string root)
        => Directory.GetFiles(root).Where(path => new[] { "Data.e5", "Star.e5", "Imsg.e5", "Ekd5.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertFiles(string root, IReadOnlyDictionary<string, byte[]> expected, string message)
    { foreach (var (name, bytes) in expected) TestSupport.Assert(File.ReadAllBytes(Path.Combine(root, name)).SequenceEqual(bytes), message + "：" + name); }

    private static Dictionary<string, object?[][]> CaptureDrafts(ProjectWorkspace workspace)
        => workspace.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Data.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToArray());

    private static void AssertDrafts(ProjectWorkspace workspace, IReadOnlyDictionary<string, object?[][]> expected, string message)
    {
        foreach (var (key, rows) in expected)
            TestSupport.Assert(workspace.GetDocument(key).Data.Rows.Cast<DataRow>().Select((row, index) => row.ItemArray.SequenceEqual(rows[index])).All(equal => equal), message + "：" + key);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void DeleteCopy(string root)
    {
        var absolute = Path.GetFullPath(root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("DataManagerProfileSmoke_", StringComparison.Ordinal)) throw new InvalidOperationException("测试清理路径无效。");
        Directory.Delete(absolute, true);
    }
}
