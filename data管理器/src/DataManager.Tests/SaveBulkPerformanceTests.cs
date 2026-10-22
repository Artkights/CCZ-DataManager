using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.Migration;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class SaveBulkPerformanceTests
{
    public static void Run(string basePath)
    {
        TestLinearAdjacentChunks();
        TestDenseSaveAndHistory(basePath);
        RunCompactMigration(basePath);
        RunSparseMigrationJournal(basePath);
        TestIdenticalAndConflictingAliases(basePath);
        TestLateFileFailureRollsBack(basePath);
        TestMigrationCancellationBoundaries(basePath);
    }

    public static void RunCompactMigration(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var before = new byte[1024 * 1024];
            var after = new byte[before.Length];
            for (var offset = 0; offset < before.Length; offset += 4)
            {
                before[offset + 2] = after[offset + 2] = 0xA5;
                before[offset + 3] = after[offset + 3] = 0x5A;
                after[offset] = 1;
                after[offset + 1] = 2;
            }
            File.WriteAllBytes(Path.Combine(source, "Effect.e5"), after);
            File.WriteAllBytes(Path.Combine(target, "Effect.e5"), before);
            var sourceHashes = AllHashes(source);
            var targetCore = CoreHashes(target);
            var service = new MigrationService();
            var previewTimer = Stopwatch.StartNew();
            var plan = service.Preview(source, target);
            previewTimer.Stop();
            var choices = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey == "effect-archive" && item.change.Kind == MigrationChangeKind.Changed)
                .ToDictionary(item => item.index, _ => MigrationChoice.Overwrite);
            TestSupport.Assert(choices.Count == 524_288, "密集迁移预览没有覆盖全部实际物理字段");
            var applyTimer = Stopwatch.StartNew();
            var result = service.Apply(plan, choices);
            applyTimer.Stop();
            TestSupport.Assert(result.Success && result.AppliedChanges == choices.Count,
                "紧凑历史密集迁移失败：" + string.Join("；", result.Messages));
            var path = Path.Combine(target, "Effect.e5");
            TestSupport.Assert(File.ReadAllBytes(path).SequenceEqual(after) && CoreHashes(target).All(pair => targetCore[pair.Key] == pair.Value),
                "密集逻辑迁移丢失字段/保留字节或修改未选核心文件");
            var workspace = new ProjectWorkspace(); workspace.Open(target);
            var history = workspace.GetHistory().Single();
            var historyBytes = new FileInfo(Path.Combine(DataManager.Core.History.ProjectHistoryPaths.GetDataRoot(target), "manifest.jsonl")).Length;
            var journalBytes = AssertJournalChoices(result, plan, choices);
            TestSupport.Assert(history.Tables.Count == 0 && history.Files.Count == 1 && history.Files[0].Chunks.Count == 1 &&
                history.Files[0].Chunks[0].Length == before.Length && historyBytes < 4 * 1024 * 1024,
                "密集迁移未使用有完整前后 SHA 的紧凑物理快照历史");
            TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(before),
                "紧凑迁移历史不能逐字节撤回");
            TestSupport.Assert(workspace.RedoSaved().Success && File.ReadAllBytes(path).SequenceEqual(after),
                "紧凑迁移历史不能逐字节重做");
            TestSupport.Assert(AllHashes(source).OrderBy(pair => pair.Key).SequenceEqual(sourceHashes.OrderBy(pair => pair.Key)),
                "紧凑密集迁移/撤回/重做修改了源项目");
            TestSupport.Assert(journalBytes < 1024 * 1024, "密集迁移决策日志没有按字段/连续 ID 区间压缩");
            Console.WriteLine($"MIGRATION_DENSE_COMPACT fields={choices.Count} previewMs={previewTimer.Elapsed.TotalMilliseconds:F1} applyMs={applyTimer.Elapsed.TotalMilliseconds:F1} historyBytes={historyBytes} journalBytes={journalBytes}");
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    public static void RunSparseMigrationJournal(string basePath)
    {
        var source = TestSupport.CreateCoreCopy(basePath);
        var target = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var bytes = new byte[1024 * 1024];
            for (var row = 0; row < 1024; row++) { bytes[row * 1024] = 1; bytes[row * 1024 + 1] = 2; }
            File.WriteAllBytes(Path.Combine(source, "Effect.e5"), bytes);
            File.WriteAllBytes(Path.Combine(target, "Effect.e5"), new byte[bytes.Length]);
            var sourceWorkspace = new ProjectWorkspace(); sourceWorkspace.Open(source);
            var fields = sourceWorkspace.GetDocument("effect-archive").Definition.Fields;
            var firstField = fields.Single(field => field.Offset == 0).Name;
            var secondField = fields.Single(field => field.Offset == 1).Name;
            var firstIds = new HashSet<int> { 0, 1, 2, 8, 10, 11, 1023 };
            var secondIds = new HashSet<int> { 0, 2, 9, 10 };
            var service = new MigrationService();
            var plan = service.Preview(source, target);
            var choices = plan.Changes.Select((change, index) => (change, index))
                .Where(item => item.change.TableKey == "effect-archive" &&
                    (item.change.FieldName == firstField && firstIds.Contains(item.change.Id) ||
                     item.change.FieldName == secondField && secondIds.Contains(item.change.Id)))
                .ToDictionary(item => item.index, item => item.change.FieldName == secondField && item.change.Id == 2
                    ? MigrationChoice.Skip : item.change.FieldName == firstField && item.change.Id is 10 or 11
                        ? MigrationChoice.Merge : MigrationChoice.Overwrite);
            var result = service.Apply(plan, choices);
            // Numeric zero is a real value, so Merge preserves it. The journal
            // must still retain the user's Merge decision for those two IDs.
            var expectedApplied = choices.Values.Count(choice => choice == MigrationChoice.Overwrite);
            TestSupport.Assert(result.Success && result.AppliedChanges == expectedApplied,
                $"稀疏 ID/mixed-choice 迁移失败：expected={expectedApplied}, actual={result.AppliedChanges}；" + string.Join("；", result.Messages));
            var journalBytes = AssertJournalChoices(result, plan, choices);
            var migrated = File.ReadAllBytes(Path.Combine(target, "Effect.e5"));
            for (var row = 0; row < 1024; row++)
            {
                TestSupport.Assert(migrated[row * 1024] == (firstIds.Contains(row) && row is not (10 or 11) ? 1 : 0) &&
                    migrated[row * 1024 + 1] == (secondIds.Contains(row) && row != 2 ? 2 : 0) &&
                    migrated.AsSpan(row * 1024 + 2, 1022).IndexOfAnyExcept((byte)0) < 0,
                    "稀疏迁移 ID 区间合并丢失空洞/跳过选择或改写未选字段：" + row);
            }
            Console.WriteLine($"MIGRATION_SPARSE_JOURNAL selected={result.AppliedChanges} journalBytes={journalBytes}");
        }
        finally { Directory.Delete(source, true); Directory.Delete(target, true); }
    }

    private static long AssertJournalChoices(MigrationApplyResult result, MigrationPlan plan, IReadOnlyDictionary<int, MigrationChoice> choices)
    {
        var path = Path.Combine(result.BackupPath ?? throw new InvalidOperationException("成功迁移未返回备份目录"), "migration.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        TestSupport.Assert(json.RootElement.GetProperty("SchemaVersion").GetString() == "2.0", "迁移日志版本未反映区间格式");
        var expected = choices.Where(pair => pair.Value != MigrationChoice.Skip).Select(pair =>
        {
            var change = plan.Changes[pair.Key];
            return (change.TableKey, change.FieldName, Kind: change.Kind.ToString(), Choice: pair.Value.ToString(), change.Id);
        }).ToHashSet();
        var actual = new HashSet<(string TableKey, string FieldName, string Kind, string Choice, int Id)>();
        foreach (var group in json.RootElement.GetProperty("Choices").EnumerateArray())
        {
            var table = group.GetProperty("TableKey").GetString()!;
            var field = group.GetProperty("FieldName").GetString()!;
            var kind = group.GetProperty("Kind").GetString()!;
            var choice = group.GetProperty("Choice").GetString()!;
            var previousEnd = -1L;
            foreach (var range in group.GetProperty("RecordIdRanges").EnumerateArray())
            {
                TestSupport.Assert(range.GetArrayLength() == 2, "ID 区间宽度不为 2");
                var first = range[0].GetInt32();
                var last = range[1].GetInt32();
                TestSupport.Assert(first <= last && first > previousEnd, "ID 区间乱序、重复或越界");
                previousEnd = last;
                for (var id = (long)first; id <= last; id++)
                    TestSupport.Assert(actual.Add((table, field, kind, choice, checked((int)id))), "压缩日志产生重复逻辑动作");
            }
        }
        TestSupport.Assert(actual.SetEquals(expected), "压缩日志解包后与原选择的字段/ID/动作不等价");
        return new FileInfo(path).Length;
    }

    private static void TestLinearAdjacentChunks()
    {
        const int count = 524_288;
        var chunks = Enumerable.Range(0, count).Select(index => new ByteChunk(index, [0], [1])).ToArray();
        var merge = typeof(BinaryTableWriter).Assembly.GetType("DataManager.Core.Binary.ByteChunkMerger", true)!
            .GetMethod("MergeAdjacent", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<IEnumerable<ByteChunk>, IReadOnlyList<ByteChunk>>>();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var merged = merge(chunks);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestSupport.Assert(merged.Count == 1 && merged[0].Length == count && merged[0].OldBytes.All(value => value == 0) &&
            merged[0].NewBytes.All(value => value == 1), "连续字节块未合并为完整正确候选");
        TestSupport.Assert(allocated < count * 40L,
            $"连续字节合并发生重复拼接：{count} 个字节分配 {allocated}，超过线性上界");
        Console.WriteLine($"SAVE_CHUNK_MERGE chunks={count} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} allocatedBytes={allocated}");
    }

    private static void TestDenseSaveAndHistory(string basePath)
    {
        WithProject(basePath, root =>
        {
            var path = Path.Combine(root, "Effect.e5");
            var before = new byte[1024 * 1024];
            for (var at = 0; at < before.Length; at += 4) { before[at + 2] = 0xA5; before[at + 3] = 0x5A; }
            File.WriteAllBytes(path, before);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var originalCore = CoreHashes(root);
            var effects = workspace.GetDocument("effect-archive");
            var fields = effects.Definition.Fields.Where(field => field.Policy == FieldPolicy.Editable).ToArray();
            var cells = 0;
            foreach (DataRow row in effects.Data.Rows)
            foreach (var field in fields)
            {
                row[field.Name] = field.Offset % 4 == 0 ? 1 : 2;
                cells++;
            }
            TestSupport.Assert(cells == 524_288, "密集保存夹具未修改 524288 个实际物理字段");
            var watch = Stopwatch.StartNew();
            var result = workspace.Save();
            watch.Stop();
            TestSupport.Assert(result.Saved && result.ChangedFields == cells,
                "密集保存失败：" + string.Join("；", result.Messages));
            var after = File.ReadAllBytes(path);
            for (var at = 0; at < after.Length; at += 4)
                TestSupport.Assert(after[at] == 1 && after[at + 1] == 2 && after[at + 2] == 0xA5 && after[at + 3] == 0x5A,
                    "密集保存损坏已选字段或未拆保留字节：" + at);
            TestSupport.Assert(CoreHashes(root).All(pair => originalCore[pair.Key] == pair.Value),
                "扩展表密集保存修改了未选中的核心文件");
            var history = result.History ?? throw new InvalidOperationException("密集保存未返回历史");
            TestSupport.Assert(history.Tables.Count == cells && history.Files.Count == 1 &&
                history.Files[0].Chunks.Sum(chunk => chunk.Length) == cells,
                "密集保存历史缺字段或重复写入字节");
            var historyBytes = new FileInfo(Path.Combine(DataManager.Core.History.ProjectHistoryPaths.GetDataRoot(root), "manifest.jsonl")).Length;
            Console.WriteLine($"SAVE_DENSE fields={cells} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} changedBytes={result.ChangedBytes} historyBytes={historyBytes} chunks={history.Files[0].Chunks.Count}");
            var undo = workspace.UndoSaved();
            TestSupport.Assert(undo.Success && File.ReadAllBytes(path).SequenceEqual(before),
                "密集保存历史撤回未逐字节还原：" + undo.Message);
        });
    }

    private static void TestIdenticalAndConflictingAliases(string basePath)
    {
        foreach (var conflicting in new[] { false, true })
        WithProject(basePath, root =>
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var path = Path.Combine(root, "AliasIsolation.bin");
            File.WriteAllBytes(path, [0, 0, 0, 0]);
            var a = AddAlias(workspace, root, "alias-a");
            var b = AddAlias(workspace, root, "alias-b");
            a.Data.Rows[0]["值"] = 7;
            b.Data.Rows[0]["值"] = conflicting ? 8 : 7;
            SaveResult? save = null;
            Exception? error = null;
            try { save = workspace.Save(); }
            catch (InvalidDataException ex) { error = ex; }
            if (conflicting)
            {
                TestSupport.Assert(error?.Message.Contains("不同值", StringComparison.Ordinal) == true || save is { Saved: false },
                    "物理别名提交互异值时没有拒绝");
                TestSupport.Assert(File.ReadAllBytes(path).SequenceEqual(new byte[4]) && workspace.GetHistory().Count == 0,
                    "冲突别名拒绝前写入了物理文件或历史");
            }
            else
            {
                TestSupport.Assert(error == null && save is { Saved: true } && File.ReadAllBytes(path).SequenceEqual(new byte[] { 7, 0, 0, 0 }),
                    "一致的物理别名值不能原子提交");
                TestSupport.Assert(workspace.UndoSaved().Success && File.ReadAllBytes(path).SequenceEqual(new byte[4]),
                    "一致别名保存后的历史撤回失败");
            }
        });
    }

    private static void TestLateFileFailureRollsBack(string basePath)
    {
        WithProject(basePath, root =>
        {
            var effectPath = Path.Combine(root, "Effect.e5");
            File.WriteAllBytes(effectPath, new byte[1024 * 1024]);
            var workspace = new ProjectWorkspace();
            workspace.Open(root);
            var person = workspace.GetDocument("persons");
            person.Data.Rows[0]["武力"] = Convert.ToInt32(person.Data.Rows[0]["武力"]) ^ 1;
            var effects = workspace.GetDocument("effect-archive");
            effects.Data.Rows[1023][effects.Definition.Fields.Last().Name] = 1;
            var before = CoreHashes(root);
            var effectHash = HashService.Sha256(effectPath);
            using (var held = new FileStream(effectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = workspace.Save();
                TestSupport.Assert(!result.Saved, "后续物理文件禁止替换时保存仍报告成功");
            }
            TestSupport.Assert(CoreHashes(root).All(pair => before[pair.Key] == pair.Value) && HashService.Sha256(effectPath) == effectHash,
                "保存后续文件失败没有回滚先前已提交的核心文件");
            TestSupport.Assert(workspace.GetHistory().LastOrDefault()?.Status == HistoryStatus.Failed,
                "回滚成功后历史未标为失败");
            TestSupport.Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                "保存失败后留下候选临时文件");
        });
    }

    private static void TestMigrationCancellationBoundaries(string basePath)
    {
        foreach (var late in new[] { false, true })
        {
            var source = TestSupport.CreateCoreCopy(basePath);
            var target = TestSupport.CreateCoreCopy(basePath);
            try
            {
                var workspace = new ProjectWorkspace(); workspace.Open(source);
                var person = workspace.GetDocument("persons");
                var value = Convert.ToInt32(person.Data.Rows[0]["武力"]) ^ 1;
                person.Data.Rows[0]["武力"] = value;
                TestSupport.Assert(workspace.Save().Saved, "取消测试源草稿保存失败");
                File.WriteAllBytes(Path.Combine(source, "CancelResource.e5"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(target, "CancelResource.e5"), [4, 5, 6]);
                var sourceBefore = AllHashes(source);
                var targetBefore = AllHashes(target);
                var service = new MigrationService();
                var plan = service.Preview(source, target);
                using var cancellation = new CancellationTokenSource();
                var triggered = false;
                var stage = late ? "提交目标" : "校验临时项目";
                var progress = new ImmediateProgress(update =>
                {
                    if (!triggered && update.Stage == stage) { triggered = true; cancellation.Cancel(); }
                });
                var result = service.Apply(plan, includeAllWritable: true, progress: progress, cancellationToken: cancellation.Token);
                TestSupport.Assert(triggered, "取消边界未触发：" + stage);
                TestSupport.Assert(AllHashes(source).OrderBy(pair => pair.Key).SequenceEqual(sourceBefore.OrderBy(pair => pair.Key)),
                    "取消迁移改变了源项目");
                if (!late)
                    TestSupport.Assert(!result.Success && AllHashes(target).OrderBy(pair => pair.Key).SequenceEqual(targetBefore.OrderBy(pair => pair.Key)),
                        "提交前取消仍改变了目标内容或历史");
                else
                {
                    var check = new ProjectWorkspace(); check.Open(target);
                    TestSupport.Assert(result.Success && Convert.ToInt32(check.GetDocument("persons").Data.Rows[0]["武力"]) == value &&
                        File.ReadAllBytes(Path.Combine(target, "CancelResource.e5")).SequenceEqual(new byte[] { 1, 2, 3 }),
                        "替换阶段取消留下部分迁移状态");
                }
            }
            finally { Directory.Delete(source, true); Directory.Delete(target, true); }
        }
    }

    private sealed class ImmediateProgress(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    {
        public void Report(MigrationProgress value) => callback(value);
    }

    private static TableDocument AddAlias(ProjectWorkspace workspace, string root, string key)
    {
        var definition = new TableDefinition(key, key, "AliasIsolation.bin", 0, 1, 4, 0,
            [new TableFieldDefinition("值", 0, 1, FieldKind.UInt8)], "tests");
        var document = new BinaryTableReader().Read(root, definition);
        ((Dictionary<string, TableDocument>)typeof(ProjectWorkspace).GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace)!).Add(key, document);
        ((Dictionary<string, TableDefinition>)typeof(ProjectWorkspace).GetField("_definitions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace)!).Add(key, definition);
        return document;
    }

    private static Dictionary<string, string> CoreHashes(string root)
        => new[] { "Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5" }.ToDictionary(name => name,
            name => HashService.Sha256(ProjectFileLocator.Resolve(root, name)), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> AllHashes(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), HashService.Sha256);

    private static void WithProject(string basePath, Action<string> action)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
