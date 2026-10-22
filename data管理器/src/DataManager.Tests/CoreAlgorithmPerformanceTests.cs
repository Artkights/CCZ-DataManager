using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.History;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

internal static class CoreAlgorithmPerformanceTests
{
    public static void Run(string basePath, string phase = "after")
    {
var source = Path.GetFullPath(basePath);

var root = Path.Combine(Path.GetTempPath(), "DataManagerAlgorithmBench_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var name in ProfileDetector.CoreFiles) File.Copy(ProjectFileLocator.Resolve(source, name), Path.Combine(root, name));

    foreach (var length in new[] { 0, 1, 4095, 4096, 4097, 8 * 1024 * 1024 })
    {
        var bytes = new byte[length]; new Random(1931 + length).NextBytes(bytes);
        var path = Path.Combine(root, "hash-test.bin"); File.WriteAllBytes(path, bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));
        Assert(HashService.Sha256(bytes) == expected && HashService.Sha256(path) == expected, "SHA文件/内存结果不同");
        if (length > 10000) Measure("sha8m", () => Assert(HashService.Sha256(path) == expected, "完整文件SHA失败"), 7);
    }
    AssertThrows(() => HashService.Sha256(Path.Combine(root, "missing.bin")), "缺失文件SHA未失败");

    var workspace = new ProjectWorkspace(); workspace.Open(root);
    var relative = "algorithm-history.bin";
    var script = Path.Combine(root, relative);
    var historyBytes = Enumerable.Repeat((byte)49, 128 * 1024).ToArray();
    File.WriteAllBytes(script, historyBytes);
    var tokenOffset = 128;
    var beforeSave = File.ReadAllBytes(script);
    var historyRoot = workspace.History.GetRoot(root);
    var saveTimes = new List<double>(); var saveAllocations = new List<long>();
    HistoryEntry? finalHistory = null;
    for (var iteration = 0; iteration < 6; iteration++)
    {
        var before = File.ReadAllBytes(script); var candidate = before.ToArray(); candidate[tokenOffset] = iteration % 2 == 0 ? (byte)'2' : (byte)'1';
        var candidateHash = HashService.Sha256(candidate); var verified = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        var result = workspace.Save([], [new(relative, HashService.Sha256(before), candidate, "RS基准", "同长度参数修改", actual =>
        {
            verified++;
            Assert(HashService.Sha256(actual) == candidateHash, "候选SHA验证失败");
        })]);
        watch.Stop(); saveTimes.Add(watch.Elapsed.TotalMilliseconds); saveAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert(result.Saved && result.History?.SchemaVersion == "2.1" && verified >= 2, "同长度RS保存失败：" + string.Join("；", result.Messages));
        finalHistory = result.History;
        if (phase != "before") Assert(!result.History!.Files.Single().WholeFileReplacement && result.History.Files.Single().Chunks.Sum(chunk => chunk.Length) == 1,
            "同长度RS未保留为稀疏2.1历史");
    }
    Emit(new { phase, metric = "save_same_length", median_ms = Median(saveTimes), allocated_bytes = Median(saveAllocations.Select(value => (double)value).ToList()),
        manifest_bytes = new FileInfo(Path.Combine(historyRoot, "manifest.jsonl")).Length, file_bytes = historyBytes.Length, saves = 6 });
    var profile = workspace.ActiveProfile;
    var finalBytes = File.ReadAllBytes(script);
    Assert(workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, finalHistory!.OperationId).Success, "2.1同长度撤回失败");
    Assert(File.ReadAllBytes(script)[tokenOffset] == (byte)'2', "2.1同长度撤回字节不匹配");
    Assert(workspace.History.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, finalHistory.OperationId).Success && File.ReadAllBytes(script).AsSpan().SequenceEqual(finalBytes), "2.1同长度前进失败");
    var expanded = finalBytes.Concat(new byte[] { 90, 91, 92 }).ToArray();
    var lengthChange = workspace.Save([], [new(relative, HashService.Sha256(finalBytes), expanded, "RS基准", "变长仍保留完整快照")]);
    Assert(lengthChange.Saved && lengthChange.History!.Files.Single().WholeFileReplacement, "变长历史未保留完整快照");
    Assert(workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, lengthChange.History!.OperationId).Success &&
        File.ReadAllBytes(script).AsSpan().SequenceEqual(finalBytes), "变长历史撤回失败");
    Assert(workspace.History.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, lengthChange.History.OperationId).Success &&
        File.ReadAllBytes(script).AsSpan().SequenceEqual(expanded), "变长历史前进失败");
    File.AppendAllText(script, "external"); var external = File.ReadAllBytes(script);
    Assert(!workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, lengthChange.History.OperationId).Success &&
        File.ReadAllBytes(script).AsSpan().SequenceEqual(external), "2.1历史没有拒绝外部冲突");
    VerifyDenseHistory();
    VerifyLegacyHistory();
    VerifyHistoryStoreStreaming();
    Console.WriteLine("CORE_ALGORITHM_PERFORMANCE_REGRESSION_OK");

    void VerifyHistoryStoreStreaming()
    {
        var historyProject = Path.Combine(root, "history-streaming"); Directory.CreateDirectory(historyProject);
        var store = new HistoryStore(); var payload = Enumerable.Repeat((byte)0xAB, 64 * 1024).ToArray();
        var entries = Enumerable.Range(0, 40).Select(index => new HistoryEntry
        {
            SchemaVersion = index % 2 == 0 ? "2.0" : "2.1", Summary = "旧历史 " + index,
            Files = [new("old.bin", "before", "after", payload.Length, [new(0, payload, payload)])]
        }).ToArray();
        store.Replace(historyProject, entries);
        var manifest = Path.Combine(store.GetRoot(historyProject), "manifest.jsonl");
        var originalLines = File.ReadAllLines(manifest);
        var appended = new HistoryEntry { Summary = "追加目标" };
        Measure("history_append_large_manifest", () => store.Append(historyProject, new HistoryEntry { Summary = "新记录" }), 5);
        store.Append(historyProject, appended);
        Measure("history_update_large_manifest", () => { appended.Summary = Guid.NewGuid().ToString("N"); store.Update(historyProject, appended); }, 5);
        Assert(store.Load(historyProject).Count == entries.Length + 7 &&
            File.ReadLines(manifest).Take(entries.Length).SequenceEqual(originalLines), "新历史写入改变了旧2.0/2.1记录或记录数量");
        var missing = new HistoryEntry { Summary = "Update目标不存在" }; store.Update(historyProject, missing);
        Assert(store.Load(historyProject).Last().OperationId == missing.OperationId, "Update缺失目标没有追加记录");
        var beforeLock = HashService.Sha256(manifest);
        using (var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            AssertThrows(() => store.Append(historyProject, new HistoryEntry()), "锁定manifest时Append未失败");
            AssertThrows(() => store.Update(historyProject, missing), "锁定manifest时Update未失败");
            Assert(HashService.Sha256(manifest) == beforeLock, "历史写入失败后原manifest被改变");
        }
        Assert(!Directory.EnumerateFiles(store.GetRoot(historyProject), "*.tmp").Any(), "历史写入失败后遗留临时文件");
        File.AppendAllText(manifest, "{broken-json\r\n");
        var loadedBefore = store.Load(historyProject).Count; store.Append(historyProject, new HistoryEntry());
        Assert(store.Load(historyProject).Count == loadedBefore + 1, "损坏历史行阻塞了新记录写入");
        if (phase != "before") Assert(File.ReadLines(manifest).Contains("{broken-json"), "流式写入删除了旧的损坏记录");
        var invalidTarget = new HistoryEntry { Summary = "同ID但Files字段损坏的旧行" };
        var invalidLine = JsonSerializer.Serialize(new { invalidTarget.OperationId, Files = "not-an-array" });
        File.AppendAllText(manifest, invalidLine + Environment.NewLine);
        store.Update(historyProject, invalidTarget);
        Assert(store.Load(historyProject).Last().OperationId == invalidTarget.OperationId &&
            store.Load(historyProject).Count(value => value.OperationId == invalidTarget.OperationId) == 1,
            "同ID的无效记录没有按Load/Update原规则在末尾追加有效记录");
        if (phase != "before") Assert(File.ReadLines(manifest).Contains(invalidLine), "Update误替换了同ID但无法反序列化的损坏记录");
        var emptyProject = Path.Combine(root, "history-empty");
        store.Update(emptyProject, missing);
        Assert(store.Load(emptyProject).Single().OperationId == missing.OperationId, "空历史Update未创建记录");
        Emit(new { phase, metric = "history_large_manifest", bytes = new FileInfo(manifest).Length, old_entries = entries.Length });
    }

    void VerifyDenseHistory()
    {
        var original = new byte[32768]; new Random(234).NextBytes(original); var candidate = original.ToArray();
        for (var index = 0; index < candidate.Length; index += 2) candidate[index] ^= 1;
        var relative = "dense-candidate.bin"; var path = Path.Combine(root, relative); File.WriteAllBytes(path, original);
        var saved = workspace.Save([], [new(relative, HashService.Sha256(original), candidate, "密集补丁", "等长散布修改")]);
        Assert(saved.Saved && saved.History!.Files.Single().WholeFileReplacement, "密集散布修改生成了膨胀的稀疏JSON历史");
        Assert(workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, saved.History!.OperationId).Success &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "密集历史撤回失败");
        Assert(workspace.History.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, saved.History.OperationId).Success &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(candidate), "密集历史前进失败");
    }
    void VerifyLegacyHistory()
    {
        var path = Path.Combine(root, "legacy.bin"); var original = new byte[] { 1, 2, 3 }; var candidate = new byte[] { 1, 7, 3 };
        File.WriteAllBytes(path, candidate);
        var entry = new HistoryEntry { SchemaVersion = "2.0", ProfileId = profile.ProfileId, VariantId = profile.VariantId, LayoutRevision = profile.LayoutRevision,
            Files = [new("legacy.bin", HashService.Sha256(original), HashService.Sha256(candidate), 3, [new(1, [2], [7])])] };
        workspace.History.Append(root, entry);
        Assert(workspace.History.Undo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, entry.OperationId).Success && File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "2.0历史撤回失败");
        Assert(workspace.History.Redo(root, profile.ProfileId, profile.VariantId, profile.LayoutRevision, entry.OperationId).Success && File.ReadAllBytes(path).AsSpan().SequenceEqual(candidate), "2.0历史前进失败");
    }
}
finally
{
    var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "DataManagerAlgorithmBench_";
    if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("基准临时路径不在预期目录");
    Directory.Delete(root, true);
}

void Measure(string metric, Action action, int iterations)
{
    action();
    var times = new List<double>(); var allocations = new List<double>();
    for (var index = 0; index < iterations; index++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); action(); watch.Stop();
        times.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
    Emit(new { phase, metric, median_ms = Median(times), allocated_bytes = Median(allocations), iterations });
}
static double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void AssertThrows(Action action, string message) { try { action(); } catch { return; } throw new InvalidOperationException(message); }

    }
}
