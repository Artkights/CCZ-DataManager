using DataManager.Core.Binary;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class FixtureSnapshotTests
{
    public static void Run(string basePath)
    {
        var sourceHashes = EvidenceHashes(basePath);
        var known = TestSupport.CreateKnown65Snapshot(basePath);
        try
        {
            AssertKnownAndEmpty(known);
            ExactCopyOptOut(basePath);
            MultiStepAndMultiFileRecovery(known);
            InvalidHistoryStaysUnverified(known);
            UnknownWithoutHistoryStaysUnverified(known);
        }
        finally { Directory.Delete(known, true); }
        TestSupport.Assert(EvidenceHashes(basePath).All(pair => sourceHashes[pair.Key] == pair.Value), "快照测试修改了实际核心文件或源历史");
    }

    private static void ExactCopyOptOut(string source)
    {
        var copy = TestSupport.CreateCoreCopy(source, restoreKnown65Snapshot: false);
        try
        {
            foreach (var name in Known65FixtureSnapshot.CoreNames)
                TestSupport.Assert(HashService.Sha256(Path.Combine(copy, name)) == HashService.Sha256(Path.Combine(source, name)),
                    "显式原样副本改变了源字节：" + name);
            TestSupport.Assert(Directory.EnumerateFiles(copy).Count() == 4 && !Directory.EnumerateDirectories(copy).Any(),
                "原样四文件副本携带了源历史或其他资源");
        }
        finally { Directory.Delete(copy, true); }
    }

    private static void MultiStepAndMultiFileRecovery(string known)
    {
        WithSource(known, source =>
        {
            var first = Edit(source, ("Ekd5.exe", 100, (byte)1), ("Data.e5", 200, (byte)2));
            var second = Edit(source, ("Ekd5.exe", 200, (byte)4), ("Imsg.e5", 300, (byte)8));
            // Physical history dependencies, rather than line order or time,
            // must establish the route back to the complete known snapshot.
            new HistoryStore().Replace(source, [second, first]);
            var before = Hashes(source);
            var copy = TestSupport.CreateCoreCopy(source);
            try { AssertKnownAndEmpty(copy); }
            finally { Directory.Delete(copy, true); }
            AssertHashes(source, before, "恢复多步快照修改了源或源历史");
        });
    }

    private static void InvalidHistoryStaysUnverified(string known)
    {
        var cases = new (string Name, Action<HistoryEntry> Corrupt)[]
        {
            ("旧字节篡改", entry => entry.Files[0].Chunks[0].OldBytes[0] ^= 1),
            ("新字节篡改", entry => entry.Files[0].Chunks[0].NewBytes[0] ^= 1),
            ("前置SHA错误", entry => entry.Files[0] = entry.Files[0] with { BeforeHash = new string('0', 64) }),
            ("后置SHA错误", entry => entry.Files[0] = entry.Files[0] with { AfterHash = new string('0', 64) }),
            ("文件长度错误", entry => entry.Files[0] = entry.Files[0] with { FileLength = entry.Files[0].FileLength + 1 }),
            ("负偏移", entry => ReplaceChunks(entry, [entry.Files[0].Chunks[0] with { Offset = -1 }])),
            ("越界偏移", entry => ReplaceChunks(entry, [entry.Files[0].Chunks[0] with { Offset = entry.Files[0].FileLength }])),
            ("重叠补丁", entry => ReplaceChunks(entry, [entry.Files[0].Chunks[0], entry.Files[0].Chunks[0]])),
            ("空补丁", entry => ReplaceChunks(entry, [new ByteChunk(100, [], [])])),
            ("宽度不等", entry => ReplaceChunks(entry, [new ByteChunk(100, [1, 2], [3])])),
            ("丢失四文件快照", entry => entry.CoreFileFingerprints.Remove("Star.e5")),
            ("未改文件SHA错误", entry => entry.CoreFileFingerprints["Star.e5"] = new string('0', 64)),
            ("跨目录路径", entry => entry.Files[0] = entry.Files[0] with { RelativePath = "../Ekd5.exe" }),
            ("重复文件", entry => entry.Files.Add(entry.Files[0])),
            ("失败历史", entry => entry.Status = HistoryStatus.Failed),
            ("跨Profile历史", entry => entry.ProfileId = "ccz-6.6")
        };
        foreach (var (name, corrupt) in cases)
        {
            WithSource(known, source =>
            {
                var entry = Edit(source, ("Ekd5.exe", 100, (byte)1));
                corrupt(entry);
                new HistoryStore().Replace(source, [entry]);
                AssertRejected(source, name);
            });
        }
        WithSource(known, source =>
        {
            var entry = Edit(source, ("Ekd5.exe", 100, (byte)1));
            new HistoryStore().Replace(source, [entry]);
            File.AppendAllText(Path.Combine(source, ".data管理器历史", "manifest.jsonl"), "{malformed\n");
            AssertRejected(source, "损坏JSON行不能被宽容跳过");
        });
        WithSource(known, source =>
        {
            var entry = Edit(source, ("Ekd5.exe", 100, (byte)1));
            new HistoryStore().Replace(source, [entry]);
            var path = Path.Combine(source, "Data.e5");
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            AssertRejected(source, "其他核心文件的未记录外改");
        });
    }

    private static void UnknownWithoutHistoryStaysUnverified(string known)
        => WithSource(known, source =>
        {
            _ = Edit(source, ("Ekd5.exe", 100, (byte)1));
            AssertRejected(source, "没有历史的未知SHA");
        });

    private static void AssertRejected(string source, string reason)
    {
        var before = Hashes(source);
        var copy = TestSupport.CreateCoreCopy(source);
        try
        {
            TestSupport.Assert(!Known65FixtureSnapshot.MatchesKnown(ReadCore(copy)), "无效证据被恢复为已知快照：" + reason);
            foreach (var name in Known65FixtureSnapshot.CoreNames)
                TestSupport.Assert(HashService.Sha256(Path.Combine(copy, name)) == before[name], "证据失败仍部分回放了补丁：" + reason);
            TestSupport.Assert(!Directory.EnumerateDirectories(copy).Any(), "无效证据副本不应携带历史");
            // Snapshot proof and independent executable-consumer proof are
            // separate. Remove the latter only in this disposable negative fixture.
            LegacyDynamicLayoutTests.InvalidateAssignmentConsumer(copy);
            var workspace = new ProjectWorkspace(); workspace.Open(copy, "ccz-6.5", "65-standard-v1");
            TestSupport.Assert(!workspace.Documents.ContainsKey("person-r-assign") && !workspace.Documents.ContainsKey("person-s-assign"),
                "未知副本绕过了运行时R/S证据门禁：" + reason);
        }
        finally { Directory.Delete(copy, true); }
        string? strict = null;
        var rejected = false;
        try { strict = TestSupport.CreateKnown65Snapshot(source); }
        catch (InvalidDataException) { rejected = true; }
        finally { if (strict != null) Directory.Delete(strict, true); }
        TestSupport.Assert(rejected, "严格快照入口接受了无效证据：" + reason);
        AssertHashes(source, before, "拒绝无效证据时修改了源：" + reason);
    }

    private static void AssertKnownAndEmpty(string root)
    {
        TestSupport.Assert(Known65FixtureSnapshot.MatchesKnown(ReadCore(root)), "恢复的四核心文件不匹配完整已知6.5指纹");
        TestSupport.Assert(Directory.EnumerateFiles(root).Count() == 4 && !Directory.EnumerateDirectories(root).Any(), "已知快照副本应只有四个核心文件和空历史");
        var workspace = new ProjectWorkspace(); workspace.Open(root);
        TestSupport.Assert(workspace.ProfileMatch?.MatchKind == DataManager.Core.Models.ProfileMatchKind.Exact &&
            workspace.GetHistory().Count == 0 && workspace.GetDocument("person-r-assign").Definition.Capability == TableCapability.Editable &&
            workspace.GetDocument("person-s-assign").Definition.Capability == TableCapability.Editable,
            "已知快照没有通过现有精确匹配与R/S门禁，或夹带了源历史");
    }

    private static HistoryEntry Edit(string root, params (string Name, int Offset, byte Mask)[] edits)
    {
        var before = ReadCore(root);
        var after = before.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits) after[edit.Name][edit.Offset] ^= edit.Mask;
        var files = new List<FileByteChange>();
        foreach (var name in edits.Select(edit => edit.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var chunks = edits.Where(edit => edit.Name == name).OrderBy(edit => edit.Offset)
                .Select(edit => new ByteChunk(edit.Offset, [before[name][edit.Offset]], [after[name][edit.Offset]])).ToList();
            files.Add(new FileByteChange(name, HashService.Sha256(before[name]), HashService.Sha256(after[name]), after[name].LongLength, chunks));
            File.WriteAllBytes(Path.Combine(root, name), after[name]);
        }
        return new HistoryEntry
        {
            ProfileId = "ccz-6.5", VariantId = "65-standard-v1", Status = HistoryStatus.Active,
            CoreFileFingerprints = before.ToDictionary(pair => pair.Key, pair => HashService.Sha256(pair.Value), StringComparer.OrdinalIgnoreCase), Files = files
        };
    }

    private static void ReplaceChunks(HistoryEntry entry, IReadOnlyList<ByteChunk> chunks)
        => entry.Files[0] = entry.Files[0] with { Chunks = chunks };

    private static Dictionary<string, byte[]> ReadCore(string root)
        => Known65FixtureSnapshot.CoreNames.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Hashes(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), HashService.Sha256, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> EvidenceHashes(string root)
        => Known65FixtureSnapshot.CoreNames.Append(Path.Combine(".data管理器历史", "manifest.jsonl"))
            .ToDictionary(name => name, name => File.Exists(Path.Combine(root, name)) ? HashService.Sha256(Path.Combine(root, name)) : string.Empty,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertHashes(string root, IReadOnlyDictionary<string, string> expected, string reason)
    {
        var actual = Hashes(root);
        TestSupport.Assert(actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var hash) && hash == pair.Value), reason);
    }

    private static void WithSource(string known, Action<string> test)
    {
        var source = TestSupport.CreateCoreCopy(known, restoreKnown65Snapshot: false);
        try { test(source); }
        finally { Directory.Delete(source, true); }
    }
}
