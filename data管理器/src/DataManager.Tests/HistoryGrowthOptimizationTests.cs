using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.History;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class HistoryGrowthOptimizationTests
{
    public static void Run(string sourceManifest, string output)
    {
        Directory.CreateDirectory(output);
        using var owned = OwnedTemporaryWorkspace.Create(Path.GetTempPath(), "DataManager.HistoryGrowthOptimization", Path.GetDirectoryName(sourceManifest)!);
        using var scope = ProjectHistoryPaths.UseBaseDirectory(Path.Combine(owned.RootPath, "history"));
        var project = Path.Combine(owned.RootPath, "project"); Directory.CreateDirectory(project);
        var originalHash = HashService.Sha256(sourceManifest);
        var store = new HistoryStore();
        var samples = new List<object>();
        var oldBlobs = new Dictionary<string, string>();
        var oldSegments = new Dictionary<string, byte[]>();
        long legacyPayloadBytes = 0;
        var count = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var line in File.ReadLines(sourceManifest))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                legacyPayloadBytes += Encoding.UTF8.GetByteCount(line);
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line)!;
                var elapsed = Stopwatch.StartNew(); store.Append(project, entry); elapsed.Stop(); count++;
                var storage = ProjectHistoryPaths.GetProjectStorageRoot(project);
                var files = Directory.GetFiles(storage, "*", SearchOption.AllDirectories);
                foreach (var pair in oldBlobs) TestSupport.Assert(HashService.Sha256(pair.Key) == pair.Value, "追加新历史重写了已有blob");
                foreach (var pair in oldSegments)
                    TestSupport.Assert(File.ReadAllBytes(pair.Key).AsSpan().StartsWith(pair.Value), "追加新历史重写了ledger既有前缀");
                foreach (var file in files.Where(path => path.Contains(Path.DirectorySeparatorChar + "blobs" + Path.DirectorySeparatorChar)))
                    oldBlobs.TryAdd(file, HashService.Sha256(file));
                foreach (var file in files.Where(path => Path.GetFileName(path).StartsWith("events-", StringComparison.Ordinal)))
                    oldSegments[file] = File.ReadAllBytes(file);
                samples.Add(new { Records = count, LegacyPayloadUtf8Bytes = legacyPayloadBytes,
                    V3Bytes = files.Sum(path => new FileInfo(path).Length), BlobFiles = oldBlobs.Count, AppendMs = elapsed.Elapsed.TotalMilliseconds });
            }
            watch.Stop();
            var listTimer = Stopwatch.StartNew(); var metadata = store.ListMetadata(project); listTimer.Stop();
            TestSupport.Assert(metadata.Count == count && HashService.Sha256(sourceManifest) == originalHash, "历史条数或旧原件指纹不一致");
            File.WriteAllText(Path.Combine(output, "history-growth.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Source = sourceManifest, SourceSha256 = originalHash, OriginalUnchanged = true,
                SourceBytes = new FileInfo(sourceManifest).Length, Records = count, ConversionMs = watch.Elapsed.TotalMilliseconds,
                MetadataListMs = listTimer.Elapsed.TotalMilliseconds, Samples = samples,
                Notes = "Legacy curve counts original UTF-8 line payloads without line terminators; SourceBytes is the complete unchanged source manifest. V3 counts all actual project-storage files. This is an isolated representation comparison, not a migration or deletion of user history. Every prior blob hash and ledger prefix is checked after each append."
            }, new JsonSerializerOptions { WriteIndented = true }));
            owned.Complete();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error + Environment.NewLine + owned.RootPath); throw;
        }
        finally { TestSupport.Assert(HashService.Sha256(sourceManifest) == originalHash, "真实旧历史被修改"); }
        Console.WriteLine($"HISTORY_GROWTH_OPTIMIZATION_OK records={count} old-blobs-unchanged ledger-prefix-unchanged source-sha-unchanged");
    }
}
