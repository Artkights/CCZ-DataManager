using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.History;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class CreationHistoryTests
{
    public static void Run(string _)
    {
        RoundTrips();
        ExternalChangesSurvive();
        LateFileFailureRollback();
        JournalFailureRollback();
        LegacyDefaultsAndMalformedSnapshots();
        Console.WriteLine("CREATION_HISTORY_OK (create/delete undo/redo, conflicts, late file/journal rollback, legacy defaults)");
    }

    private static void RoundTrips() => WithRoot((root, store) =>
    {
        var writer = new BinaryTableWriter();
        foreach (var create in new[] { true, false })
        {
            var change = ExistenceChange(create ? "new-resource.e5" : "deleted-resource.e5", create);
            PutState(root, change, after: false);
            writer.Write(change, root, useNewBytes: true);
            AssertState(root, change, after: true, "首次创建/删除");
            var entry = Entry([change]); store.Append(root, entry);
            TestSupport.Assert(Undo(store, root, entry).Success, "创建/删除撤回失败");
            AssertState(root, change, after: false, "创建/删除撤回");
            TestSupport.Assert(Redo(store, root, entry).Success, "创建/删除重做失败");
            AssertState(root, change, after: true, "创建/删除重做");
        }
    });

    private static void ExternalChangesSurvive() => WithRoot((root, store) =>
    {
        var create = ExistenceChange("external-created.e5", create: true);
        PutState(root, create, after: true);
        var entry = Entry([create]); store.Append(root, entry);
        byte[] external = [91, 92, 93];
        File.WriteAllBytes(Path.Combine(root, create.RelativePath), external);
        TestSupport.Assert(!Undo(store, root, entry).Success &&
            File.ReadAllBytes(Path.Combine(root, create.RelativePath)).SequenceEqual(external),
            "撤回创建时删除了外部修改后的新文件");
        PutState(root, create, after: true);
        TestSupport.Assert(Undo(store, root, entry).Success, "恢复已知创建状态后无法撤回");
        File.WriteAllBytes(Path.Combine(root, create.RelativePath), external);
        TestSupport.Assert(!Redo(store, root, entry).Success &&
            File.ReadAllBytes(Path.Combine(root, create.RelativePath)).SequenceEqual(external),
            "重做创建时覆盖了外部新建的文件");

        var deletion = ExistenceChange("external-deleted.e5", create: false);
        var deletionEntry = Entry([deletion]); store.Append(root, deletionEntry);
        File.WriteAllBytes(Path.Combine(root, deletion.RelativePath), external);
        TestSupport.Assert(!Undo(store, root, deletionEntry).Success &&
            File.ReadAllBytes(Path.Combine(root, deletion.RelativePath)).SequenceEqual(external),
            "撤回删除时覆盖了外部重新创建的文件");
        File.Delete(Path.Combine(root, deletion.RelativePath));
        TestSupport.Assert(Undo(store, root, deletionEntry).Success, "恢复已知删除状态后无法撤回");
        File.WriteAllBytes(Path.Combine(root, deletion.RelativePath), external);
        TestSupport.Assert(!Redo(store, root, deletionEntry).Success &&
            File.ReadAllBytes(Path.Combine(root, deletion.RelativePath)).SequenceEqual(external),
            "重做删除时删掉了外部修改的文件");
    });

    private static void LateFileFailureRollback() => WithRoot((root, store) =>
    {
        // An open Windows handle permits the complete preflight hash read, but
        // rejects replacement. The first member has already changed existence.
        foreach (var create in new[] { true, false })
        foreach (var redo in new[] { true, false })
        {
            var stem = $"{create}-{redo}";
            var existence = ExistenceChange(stem + ".e5", create);
            var ordinary = Replacement(stem + ".locked", [2, 3], [4, 5]);
            var entry = Entry(redo ? [existence, ordinary] : [ordinary, existence]);
            entry.Status = redo ? HistoryStatus.Reverted : HistoryStatus.Active;
            foreach (var file in entry.Files) PutState(root, file, after: !redo);
            store.Append(root, entry);
            using (var locked = new FileStream(Path.Combine(root, ordinary.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = redo ? Redo(store, root, entry) : Undo(store, root, entry);
                TestSupport.Assert(!result.Success, "晚期文件占用未触发失败");
                foreach (var file in entry.Files) AssertState(root, file, after: !redo, "晚期文件失败回滚");
                TestSupport.Assert(store.Load(root).Single(item => item.OperationId == entry.OperationId).Status == entry.Status,
                    "完整回滚后历史状态没有恢复");
            }
        }
    });

    private static void JournalFailureRollback() => WithRoot((root, store) =>
    {
        foreach (var create in new[] { true, false })
        foreach (var redo in new[] { true, false })
        {
            var change = ExistenceChange($"journal-{create}-{redo}.e5", create);
            var entry = Entry([change]); entry.Status = redo ? HistoryStatus.Reverted : HistoryStatus.Active;
            PutState(root, change, after: !redo); store.Append(root, entry);
            // FileShare.Read allows Load but denies the final atomic manifest replacement.
            using (var locked = new FileStream(Path.Combine(store.GetRoot(root), "manifest.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = redo ? Redo(store, root, entry) : Undo(store, root, entry);
                TestSupport.Assert(!result.Success, "历史持久化占用未触发失败");
                AssertState(root, change, after: !redo, "历史持久化失败回滚");
            }
        }
    });

    private static void LegacyDefaultsAndMalformedSnapshots() => WithRoot((root, store) =>
    {
        var old = JsonSerializer.Deserialize<FileByteChange>(
            "{\"RelativePath\":\"legacy.e5\",\"BeforeHash\":\"A\",\"AfterHash\":\"B\",\"FileLength\":0,\"Chunks\":[]}")!;
        TestSupport.Assert(old.BeforeExists && old.AfterExists, "旧历史未默认双方文件存在");
        var invalid = ExistenceChange("invalid.e5", create: true) with
        {
            Chunks = [new ByteChunk(0, [8], [1, 2, 3])]
        };
        var failed = false;
        try { new BinaryTableWriter().Write(invalid, root, useNewBytes: true); }
        catch (InvalidDataException) { failed = true; }
        TestSupport.Assert(failed && !File.Exists(Path.Combine(root, invalid.RelativePath)), "不存在侧含非空快照仍创建了文件");
    });

    private static FileByteChange ExistenceChange(string path, bool create)
    {
        byte[] content = [1, 2, 3];
        var before = create ? [] : content;
        var after = create ? content : [];
        return Replacement(path, before, after) with { BeforeExists = !create, AfterExists = create };
    }

    private static FileByteChange Replacement(string path, byte[] before, byte[] after)
        => new(path, HashService.Sha256(before), HashService.Sha256(after), after.Length,
            [new ByteChunk(0, before, after)]) { WholeFileReplacement = true, BeforeLength = before.Length };

    private static HistoryEntry Entry(List<FileByteChange> files) => new()
    {
        SchemaVersion = "2.1", ProfileId = "creation-test", VariantId = "base", LayoutRevision = "same",
        Summary = "creation/deletion history test", Files = files
    };

    private static HistoryOperationResult Undo(HistoryStore store, string root, HistoryEntry entry)
        => store.Undo(root, entry.ProfileId, entry.VariantId, entry.LayoutRevision, entry.OperationId);

    private static HistoryOperationResult Redo(HistoryStore store, string root, HistoryEntry entry)
        => store.Redo(root, entry.ProfileId, entry.VariantId, entry.LayoutRevision, entry.OperationId);

    private static void PutState(string root, FileByteChange change, bool after)
    {
        var path = Path.Combine(root, change.RelativePath);
        if (after ? change.AfterExists : change.BeforeExists)
            File.WriteAllBytes(path, after ? change.Chunks[0].NewBytes : change.Chunks[0].OldBytes);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void AssertState(string root, FileByteChange change, bool after, string context)
    {
        var path = Path.Combine(root, change.RelativePath);
        var exists = after ? change.AfterExists : change.BeforeExists;
        TestSupport.Assert(File.Exists(path) == exists && (!exists || File.ReadAllBytes(path).SequenceEqual(
            after ? change.Chunks[0].NewBytes : change.Chunks[0].OldBytes)), context + " 未恢复存在性及完整内容");
    }

    private static void WithRoot(Action<string, HistoryStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "DataManagerCreationHistory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var storage = ProjectHistoryPaths.UseProjectStorage(root, Path.Combine(root, ".history"));
        try { action(root, new HistoryStore()); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
