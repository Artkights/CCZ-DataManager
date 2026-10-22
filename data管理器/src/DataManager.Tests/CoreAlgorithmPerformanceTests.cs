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
using DataManager.Core.Scenarios;

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
    var synthetic = MakeDocument(3, 128, 64 * 1024);
    var structure = EexStructure.Parse(synthetic);
    var patches = structure.Commands.Where(command => command.Id == 0x72)
        .Select(command => new ScenarioTokenReplacement(command.Parameters.Single(parameter => parameter.Tag == 5).Offset, [49], Encoding.ASCII.GetBytes("*12345"))).ToArray();
    byte[] patched = [];
    Measure("patch384", () => patched = new ScenarioParameterPatchBuilder().Build(synthetic, patches), 5);
    AssertUnchanged(synthetic, patched, structure, patches);
    Assert(ScenarioEffectAssignmentService.ParseAssignments("RS/bench.eex", patched, 4).All(entry => entry.Parameters["EffectId"] == "*12345"), "补丁参数复读失败");
    Emit(new { phase, metric = "patch_result", bytes = synthetic.Length, patches = patches.Length, sha256 = HashService.Sha256(patched) });
    VerifyPatchBoundaries();

    foreach (var length in new[] { 0, 1, 4095, 4096, 4097, 8 * 1024 * 1024 })
    {
        var bytes = new byte[length]; new Random(1931 + length).NextBytes(bytes);
        var path = Path.Combine(root, "hash-test.bin"); File.WriteAllBytes(path, bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));
        Assert(HashService.Sha256(bytes) == expected && HashService.Sha256(path) == expected, "SHA文件/内存结果不同");
        if (length > 10000) Measure("sha8m", () => Assert(HashService.Sha256(path) == expected, "完整文件SHA失败"), 7);
    }
    AssertThrows(() => HashService.Sha256(Path.Combine(root, "missing.bin")), "缺失文件SHA未失败");

    var rs = Path.Combine(root, "RS"); Directory.CreateDirectory(rs);
    foreach (var path in Directory.EnumerateFiles(Path.Combine(source, "RS"), "*.eex")) File.Copy(path, Path.Combine(rs, Path.GetFileName(path)));
    var workspace = new ProjectWorkspace(); workspace.Open(root);
    var service = ScenarioEffectAssignmentService.For(workspace);
    var first = service.Scan();
    Measure("rs_scan_repeat", () => Assert(service.Scan().Count == first.Count, "重复扫描记录变化"), 5);
    Action recover = () => { };
    Measure("rs_recovery_capture", () => recover = service.CaptureSaveRecovery(), 5);
    service.AcceptSaved(); recover();
    Assert(service.HasScanResult && service.Read(false).Count == first.Count, "保存恢复未保留RS索引");
    if (phase != "before") Assert(first.Zip(service.Scan()).All(pair => ReferenceEquals(pair.First, pair.Second)), "未改动的完整SHA索引没有被复用");
    VerifyScanInvalidation();
    if (first.FirstOrDefault(entry => entry.IsWritable) is { } edit)
    {
        var target = edit.Parameters["EffectId"] == "1" ? "2" : "1";
        service.ApplyUpdate(new(edit.Key, new Dictionary<string, string> { ["EffectId"] = target }));
        var restore = service.CaptureSaveRecovery(); service.AcceptSaved(); restore();
        Assert(service.HasPendingChanges && service.Read(false).Single(entry => entry.Key == edit.Key).Parameters["EffectId"] == target,
            "保存恢复未保留RS草稿");
        var exposed = service.PrepareFileEdits(); var candidateHash = HashService.Sha256(exposed[0].CandidateBytes);
        exposed[0].CandidateBytes[0] ^= 1;
        Assert(HashService.Sha256(service.PrepareFileEdits()[0].CandidateBytes) == candidateHash,
            "公开候选数组变动污染了私有RS基线或恢复快照");
        var sourcePath = Path.Combine(root, edit.RelativePath); var baseline = File.ReadAllBytes(sourcePath);
        File.WriteAllBytes(sourcePath, baseline.Concat(new byte[] { 99 }).ToArray());
        AssertThrows(() => service.Scan(), "未保存RS草稿的来源SHA冲突没有拒绝");
        Assert(service.HasPendingChanges, "来源SHA冲突丢失RS草稿");
        File.WriteAllBytes(sourcePath, baseline);
        service.Discard(); workspace.Commands.Clear();
    }
    Emit(new { phase, metric = "rs_baseline", files = Directory.EnumerateFiles(rs, "*.eex").Count(), entries = first.Count });

    var relative = Path.Combine("RS", "algorithm-history.eex");
    var script = Path.Combine(root, relative);
    var historyBytes = MakeDocument(1, 12, 128 * 1024);
    File.WriteAllBytes(script, historyBytes);
    var tokenOffset = EexStructure.Parse(historyBytes).Commands.First(command => command.Id == 0x72).Parameters.Single(parameter => parameter.Tag == 5).Offset;
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
            _ = EexStructure.Parse(actual);
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

    void VerifyScanInvalidation()
    {
        var entry = service.Read(false).First(value => value.IsWritable && value.SubFunction == 5);
        var path = Path.Combine(root, entry.RelativePath); var original = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
        var text = EexStructure.Parse(original).Commands.Single(command => command.Ordinal == entry.CommandOrdinal).Parameters.Single(parameter => parameter.Tag == 5);
        var changed = original.ToArray(); changed[text.Offset] = changed[text.Offset] == (byte)'0' ? (byte)'1' : (byte)'0';
        File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, stamp);
        var reread = service.Scan().Single(value => value.Key == entry.Key);
        Assert(reread.SourceHash != entry.SourceHash && reread.Parameters["EffectId"] != entry.Parameters["EffectId"], "同长度/同修改时间外部变动被索引缓存漏过");
        File.WriteAllBytes(path, original); File.SetLastWriteTimeUtc(path, stamp); service.Scan();
        var added = Path.Combine(rs, "algorithm-added.eex"); File.WriteAllBytes(added, original);
        Assert(service.Scan().Count > first.Count, "扫描遗漏新增剧本");
        File.Delete(added); Assert(service.Scan().Count == first.Count, "扫描未移除已删除剧本");
        File.WriteAllText(added, "not-an-eex");
        Assert(service.Scan().Count == first.Count && service.Diagnostics.Any(value => value.Contains("algorithm-added", StringComparison.Ordinal)), "损坏剧本没有保持诊断");
        File.Delete(added);
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); AssertThrows(() => service.Scan(canceled.Token), "索引复用忽略取消"); }

        var exe = Path.Combine(root, "Ekd5.exe"); var exeOriginal = File.ReadAllBytes(exe);
        var prefix = Convert.FromHexString("0FB645F83CFE77246BC808030D"); var location = exeOriginal.AsSpan().IndexOf(prefix);
        if (location >= 0)
        {
            var beforeEngineChange = service.Scan(); var exeChanged = exeOriginal.ToArray(); exeChanged[location] ^= 1; File.WriteAllBytes(exe, exeChanged);
            var unknown = service.Scan();
            Assert(unknown.All(value => !value.LayoutVerified) && !ReferenceEquals(beforeEngineChange[0], unknown[0]), "EXE槽位证据改变后错误复用旧语义索引");
            File.WriteAllBytes(exe, exeOriginal); Assert(service.Scan().Any(value => value.LayoutVerified), "恢复EXE后未恢复引擎语义");
        }
        service.Scan();
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
static byte[] MakeDocument(int scenes, int effects, int trailer)
{
    var bodies = Enumerable.Range(0, scenes).Select(_ =>
    {
        using var body = new MemoryStream();
        for (var index = 0; index < effects; index++)
        {
            using var writer = new BinaryWriter(body, Encoding.ASCII, true);
            writer.Write((ushort)0x72); writer.Write((ushort)4); writer.Write(5); writer.Write((ushort)5);
            writer.Write(Encoding.ASCII.GetBytes("1\r\n0\r\n15\r\n2\r\n" + new string('A', 130) + "\r\n")); writer.Write((byte)0);
        }
        return body.ToArray();
    }).ToArray();
    using var stream = new MemoryStream(); using var output = new BinaryWriter(stream);
    output.Write(new byte[] { 69, 69, 88, 0, 1, 2, 3, 4, 5, 6 });
    var offset = 10 + scenes * 4;
    foreach (var body in bodies) { output.Write(offset); offset += body.Length + 4; }
    foreach (var body in bodies) { output.Write((ushort)1); output.Write(checked((ushort)body.Length)); output.Write(body); }
    output.Write(Enumerable.Repeat((byte)0xCD, trailer).ToArray()); return stream.ToArray();
}
static void AssertUnchanged(byte[] before, byte[] after, EexStructure structure, ScenarioTokenReplacement[] patches)
{
    patches = patches.OrderBy(patch => patch.Offset).ToArray();
    var allowed = new bool[before.Length];
    foreach (var patch in patches) Array.Fill(allowed, true, patch.Offset, patch.ExpectedBytes.Length);
    Array.Fill(allowed, true, 10, structure.SceneOffsets.Count * 4);
    foreach (var length in structure.Lengths) Array.Fill(allowed, true, length.Offset, 2);
    var patchIndex = 0; var delta = 0;
    for (var offset = 0; offset < before.Length; offset++)
    {
        while (patchIndex < patches.Length && patches[patchIndex].Offset + patches[patchIndex].ExpectedBytes.Length <= offset)
        { delta += patches[patchIndex].NewBytes.Length - patches[patchIndex].ExpectedBytes.Length; patchIndex++; }
        Assert(allowed[offset] || before[offset] == after[offset + delta], "基准发现未授权剧本字节改变");
    }
}
static void VerifyPatchBoundaries()
{
    var original = MakeDocument(1, 3, 512); var structure = EexStructure.Parse(original);
    var text = structure.Commands.Where(command => command.Id == 0x72).Select(command => command.Parameters.Single(parameter => parameter.Tag == 5)).ToArray();
    var insertion = text[0].Offset + "1\r\n0\r\n15\r\n".Length;
    var patches = new ScenarioTokenReplacement[]
    {
        new(text[2].Offset + "1\r\n0\r\n".Length, Encoding.ASCII.GetBytes("15"), [57]),
        new(insertion, [], Encoding.ASCII.GetBytes("*19\r\n"), true),
        new(text[0].Offset, [49], Encoding.ASCII.GetBytes("-*7"))
    };
    var candidate = new ScenarioParameterPatchBuilder().Build(original, patches);
    AssertUnchanged(original, candidate, structure, patches);
    var read = ScenarioEffectAssignmentService.ParseAssignments("RS/boundary.eex", candidate, 4);
    Assert(read[0].Parameters["EffectId"] == "-*7" && read[0].Parameters["Value"] == "*19" && read[2].Parameters["TargetId"] == "9",
        "同批插入/缩短/变长补丁的映射不正确");
    var noop = new ScenarioParameterPatchBuilder().Build(original, [new(text[0].Offset, [49], [49])]);
    Assert(!ReferenceEquals(noop, original) && noop.AsSpan().SequenceEqual(original), "无变化补丁改变基线所有权或内容");
    AssertThrows(() => new ScenarioParameterPatchBuilder().Build(original, [patches[0], patches[0]]), "重叠补丁未拒绝");
    AssertThrows(() => new ScenarioParameterPatchBuilder().Build(original, [new(0, [69], [49])]), "72指令外补丁未拒绝");
}

    }
}
