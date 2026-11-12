using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ccz.HistoryStorage;
using DataManager.Core.History;
using DataManager.Core.Models;
using ImageManager.Core;
using DataStatus = DataManager.Core.Models.HistoryStatus;
using ImageStatus = ImageManager.Core.HistoryStatus;

namespace DataManager.Tests;

internal static class HistoryStorageV3Tests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CczHistoryV3Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var scope = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(root, "program-history"));
        try
        {
            TestBlobAndLedger(root);
            TestLegacyAndDataRecovery(root);
            TestImageRecovery(root);
            TestBackgroundBoundaries(root);
            TestLock(root);
            Console.WriteLine("HistoryStorageV3Tests: PASS (compression/dedup, append checksum/torn tail, legacy preservation/lazy payload, Data/Image undo-redo, crash rollback, external conflict, project lock)");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestBlobAndLedger(string root)
    {
        var store = new BlobStore(Path.Combine(root, "blobs"));
        var bytes = Enumerable.Range(0, 400000).Select(index => (byte)(index % 7)).ToArray();
        var first = store.Put(bytes); var second = store.Put((byte[])bytes.Clone());
        Check(first == second && Directory.GetFiles(store.Root, "*.br", SearchOption.AllDirectories).Length == 1, "blob必须按原始SHA去重");
        Check(new FileInfo(store.GetPath(first)).Length < bytes.Length / 10, "Brotli应压缩重复内容");
        Check(store.Read(first).AsSpan().SequenceEqual(bytes), "blob恢复必须逐字节相等");
        var noise = new byte[65536]; new Random(20261111).NextBytes(noise);
        var raw = store.Put(noise);
        Check(raw.Codec == "raw-v1" && new FileInfo(store.GetPath(raw)).Length == noise.Length, "压缩不缩小时必须保留raw");
        Check(store.Read(raw).AsSpan().SequenceEqual(noise) && store.Put(noise) == raw, "raw须逐字节复读并按SHA去重");
        var legacyNoise = new byte[65536]; new Random(90210).NextBytes(legacyNoise);
        var legacyBr = new BlobReference(Hash(legacyNoise), legacyNoise.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath(legacyBr))!);
        using (var output = File.Create(store.GetPath(legacyBr)))
        using (var compressed = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.Fastest)) compressed.Write(legacyNoise);
        Check(store.Put(legacyNoise) == legacyBr && store.Read(legacyBr).AsSpan().SequenceEqual(legacyNoise), "既有br-v1应兼容并复用，不因raw规则另存一份");
        File.WriteAllBytes(store.GetPath(raw), [1, 2, 3]);
        Throws(() => store.Verify(raw), "损坏raw必须拒绝恢复");
        var ledger = new HistoryLedger(Path.Combine(root, "ledger"));
        ledger.Append("Prepare", "op", new { Value = 1 });
        var segment = Directory.GetFiles(ledger.Root, "events-*.jsonl").Single(); var initial = File.ReadAllBytes(segment);
        ledger.Append("Commit", "op", new { Value = 2 });
        Check(File.ReadAllBytes(segment).AsSpan(0, initial.Length).SequenceEqual(initial), "追加不得改旧日志");
        File.AppendAllText(segment, "{\"Sequence\":3", new UTF8Encoding(false));
        Check(ledger.Read().Count == 2, "未完成尾行不能作为已提交事件");
        ledger.Append("State", "op", new { Value = 3 });
        Check(Directory.GetFiles(ledger.Root, "events-*.jsonl").Length == 2 && ledger.Read().Count == 3, "残尾必须保留并启用新段");
        File.WriteAllBytes(store.GetPath(first), [1, 2, 3, 4]);
        Throws(() => store.Verify(first), "损坏blob必须拒绝恢复");
        var corrupt = new HistoryLedger(Path.Combine(root, "corrupt-ledger"));
        corrupt.Append("Commit", "op", new { Value = 2 });
        var bad = Directory.GetFiles(corrupt.Root, "events-*.jsonl").Single();
        File.WriteAllText(bad, File.ReadAllText(bad).Replace("\"Value\":2", "\"Value\":3"), new UTF8Encoding(false));
        Throws(() => corrupt.Read(), "完整行校验失败必须拒绝");
    }

    private static void TestLegacyAndDataRecovery(string root)
    {
        var project = Path.Combine(root, "data-project"); Directory.CreateDirectory(project);
        var before = Enumerable.Repeat((byte)17, 300000).ToArray();
        var after = Enumerable.Repeat((byte)29, 310000).ToArray();
        var file = Path.Combine(project, "payload.bin"); File.WriteAllBytes(file, after);
        var history = new HistoryStore();
        var legacyEntry = Entry("legacy", before, after, DataStatus.Active);
        var legacy = ProjectHistoryPaths.GetLegacyDataManifest(project); Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, JsonSerializer.Serialize(legacyEntry) + "\n", new UTF8Encoding(true));
        var original = File.ReadAllBytes(legacy);
        var metadata = history.ListMetadata(project).Single();
        Check(metadata.Files.Single().Chunks.Single().OldBytes.Length == 0, "列表不得解码旧快照载荷");
        Check(history.LoadOperation(project, "legacy").Files[0].Chunks[0].OldBytes.AsSpan().SequenceEqual(before), "旧历史按条解码");
        Check(history.Undo(project, "ccz-6.5", "65-standard-v1", "test-layout", "legacy").Success, "旧历史应可撤回");
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "旧历史撤回字节");
        Check(File.ReadAllBytes(legacy).AsSpan().SequenceEqual(original), "撤回不能改旧清单");
        Check(history.Redo(project, "ccz-6.5", "65-standard-v1", "test-layout", "legacy").Success, "旧历史应可前进");
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(after), "旧历史前进字节");

        File.WriteAllBytes(file, before);
        var pending = Entry("pending", before, after, DataStatus.Pending); history.Append(project, pending);
        File.WriteAllBytes(file, after); history.Recover(project);
        Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "中断保存应恢复before");
        Check(history.ListMetadata(project).Single(item => item.OperationId == "pending").Status == DataStatus.Failed, "恢复应记录失败且保留历史");
        history.Recover(project);
        var next = Entry("conflict", before, after, DataStatus.Pending); history.Append(project, next);
        File.WriteAllBytes(file, [90, 91, 92]);
        Throws(() => history.Recover(project), "外部修改不能自动覆盖");
        Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 90, 91, 92 }), "必须保留外部内容");
        Check(history.ListMetadata(project).Single(item => item.OperationId == "conflict").Status == DataStatus.Conflict, "外部修改记为冲突");
        Check(File.ReadAllBytes(legacy).AsSpan().SequenceEqual(original), "所有操作保留旧清单原件");
    }

    private static HistoryEntry Entry(string id, byte[] before, byte[] after, DataStatus status) => new()
    {
        OperationId = id, SchemaVersion = "2.1", ProfileId = "ccz-6.5", VariantId = "65-standard-v1",
        LayoutRevision = "test-layout", Status = status, Summary = id,
        Files = [new FileByteChange("payload.bin", Hash(before), Hash(after), after.Length,
            [new ByteChunk(0, before, after)]) { WholeFileReplacement = true, BeforeLength = before.Length }]
    };

    private static void TestImageRecovery(string root)
    {
        var projectRoot = Path.Combine(root, "image-project"); Directory.CreateDirectory(projectRoot);
        var project = new ProjectProfile(projectRoot, "test", ProjectVersion.Ccz65, false,
            new Dictionary<string, string>(), []);
        var service = new HistoryService(Path.Combine(root, "image-history"));
        var path = Path.Combine(projectRoot, "image.e5");
        byte[] before = [10, 20, 30], after = [40, 50, 60, 70]; File.WriteAllBytes(path, before);
        var target = service.CaptureTarget(project, path, before, after, FileAttributes.Normal, FileAttributes.Normal);
        var plan = new MutationPlan("image-save", MutationKind.WholeFileReplace, "图片快照测试", [], [], false);
        File.WriteAllBytes(path, after); service.Record(project, plan, [target]);
        Check(service.Undo(project, plan.OperationId).Success && File.ReadAllBytes(path).SequenceEqual(before), "图片blob撤回");
        Check(service.Redo(project, plan.OperationId).Success && File.ReadAllBytes(path).SequenceEqual(after), "图片blob前进");
        service.SetKeepForever(project, plan.OperationId, true);
        Check(service.List(project).Single(item => item.Id == plan.OperationId).KeepForever, "永久保留标记应追加存储");
        Check(!Directory.GetFiles(Path.Combine(root, "image-history"), "*.before", SearchOption.AllDirectories).Any(), "新图片不得再永久双存原始快照");

        var pending = new HistoryRecord { Id = "interrupted-image", Summary = "中断图片", Targets = [target] };
        var ledger = new HistoryLedger(Path.Combine(service.V3Root(project), "ledger"));
        File.WriteAllBytes(path, after);
        ledger.Append("Prepare", pending.Id, new { StorageVersion = 3, Record = pending, RollbackUseAfter = false, OriginalStatus = ImageStatus.Failed });
        service.Recover(project);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "图片中断事务应整组回滚");
        ledger.Append("Prepare", "external-image", new { StorageVersion = 3,
            Record = new HistoryRecord { Id = "external-image", Targets = [target] }, RollbackUseAfter = false, OriginalStatus = ImageStatus.Failed });
        File.WriteAllBytes(path, [99]); Throws(() => service.Recover(project), "图片外部修改应冲突");
        Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 99 }), "图片外部修改原样保留");
    }

    private static void TestLock(string root)
    {
        using var first = ProjectWriteLock.Acquire(root);
        using var reentrant = ProjectWriteLock.Acquire(root);
        var rejected = Task.Run(() => { try { using var other = ProjectWriteLock.Acquire(root); return false; } catch (IOException) { return true; } }).GetAwaiter().GetResult();
        Check(rejected, "其他线程或进程不得同时写相同项目");
    }

    private static void TestBackgroundBoundaries(string root)
    {
        var project = Path.Combine(root, "frozen-project"); Directory.CreateDirectory(project);
        byte[] before = [1, 2, 3], after = [4, 5, 6, 7];
        var originalBefore = (byte[])before.Clone(); var originalAfter = (byte[])after.Clone();
        var entry = Entry("frozen-save", before, after, DataStatus.Active);
        var store = new HistoryStore();
        using (var frozen = store.FreezeWrite(project, entry))
        {
            before[0] = 99; after[0] = 98; entry.Summary = "UI later edit";
            var prepared = store.PrepareWriteAsync(frozen).GetAwaiter().GetResult();
            store.AppendPrepared(prepared);
        }
        var loaded = store.LoadOperation(project, entry.OperationId);
        Check(loaded.Summary == "frozen-save" && loaded.Files[0].Chunks[0].OldBytes.SequenceEqual(originalBefore) &&
            loaded.Files[0].Chunks[0].NewBytes.SequenceEqual(originalAfter), "后台存储必须使用冻结快照而非后续UI数组");
        var file = Path.Combine(project, "payload.bin"); File.WriteAllBytes(file, originalAfter);
        using (var prepared = store.PrepareRestoreAsync(project, entry.ProfileId, entry.VariantId, entry.LayoutRevision, entry.OperationId, false).GetAwaiter().GetResult())
        {
            File.WriteAllBytes(file, [88]);
            Check(!store.RestorePrepared(prepared).Success && File.ReadAllBytes(file).SequenceEqual(new byte[] { 88 }), "后台准备后外部修改须在提交时重核并保留");
        }
        File.WriteAllBytes(file, originalAfter);
        using (var prepared = store.PrepareRestoreAsync(project, entry.ProfileId, entry.VariantId, entry.LayoutRevision, entry.OperationId, false).GetAwaiter().GetResult())
            Check(store.RestorePrepared(prepared).Success && File.ReadAllBytes(file).SequenceEqual(originalBefore), "后台载荷准备应支持原事务恢复");
        var imageProject = new ProjectProfile(project, "frozen", ProjectVersion.Ccz65, false, new Dictionary<string, string>(), []);
        var imageHistory = new HistoryService(Path.Combine(root, "async-image-history"));
        var target = imageHistory.CaptureTarget(imageProject, file, originalBefore, originalAfter);
        File.WriteAllBytes(file, originalAfter);
        imageHistory.Record(imageProject, new MutationPlan("async-image", MutationKind.WholeFileReplace, "async", [], [], false), [target]);
        Check(imageHistory.RestoreAsync(imageProject, "async-image", false).GetAwaiter().GetResult().Success &&
            File.ReadAllBytes(file).SequenceEqual(originalBefore), "图片历史后台恢复不应依赖控件或DataTable");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message)
    { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { return; } throw new InvalidOperationException(message); }
}
