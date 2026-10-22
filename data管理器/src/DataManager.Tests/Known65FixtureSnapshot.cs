using System.Text;
using System.Text.Json;
using DataManager.Core.Binary;
using DataManager.Core.Models;
using DataManager.Core.Profiles;

namespace DataManager.Tests;

/// <summary>
/// Test fixtures may discard history only after replaying its byte evidence back
/// to an embedded, complete 6.5 fingerprint. This class never writes a file.
/// </summary>
internal static class Known65FixtureSnapshot
{
    internal static readonly string[] CoreNames = ["Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5"];
    private static readonly Lazy<IReadOnlyList<ProfileFingerprint>> Known = new(() => new ProfileRegistry()
        .GetProfile("ccz-6.5").Variants.SelectMany(variant => variant.Fingerprints).ToArray());

    internal sealed record Recovery(IReadOnlyDictionary<string, byte[]> Bytes, bool IsKnown, string Evidence);

    public static Recovery Recover(string source, IReadOnlyDictionary<string, byte[]> original)
    {
        if (MatchesKnown(original)) return new(original, true, "四个核心文件已匹配嵌入的完整 6.5 SHA-256 指纹。");
        var path = DataManager.Core.History.ProjectHistoryPaths.GetDataManifestForRead(source);
        if (!File.Exists(path)) return new(original, false, "源项目没有可回放的保存历史。");
        List<HistoryEntry> entries;
        try
        {
            // Unlike the UI's tolerant history loader, a fixture must never
            // silently ignore malformed evidence while claiming a clean base.
            entries = File.ReadAllLines(path, new UTF8Encoding(false, true))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<HistoryEntry>(line)
                    ?? throw new InvalidDataException("历史行为空对象。"))
                .Where(entry => entry.SchemaVersion == "2.0" && entry.ProfileId == "ccz-6.5" &&
                    entry.VariantId == "65-standard-v1" && entry.Status is HistoryStatus.Active or HistoryStatus.Reverted)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or DecoderFallbackException or FormatException)
        {
            return new(original, false, "历史证据无法严格解析：" + ex.Message);
        }

        var queue = new Queue<IReadOnlyDictionary<string, byte[]>>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { Identity(original) };
        queue.Enqueue(original);
        while (queue.TryDequeue(out var current))
        {
            foreach (var entry in entries)
            {
                var prior = Reverse(current, entry);
                if (prior == null || !visited.Add(Identity(prior))) continue;
                if (MatchesKnown(prior))
                    return new(prior, true, "保存历史的所有目标字节、前后 SHA-256 和四文件快照均通过校验，已回溯到嵌入的完整 6.5 指纹。");
                queue.Enqueue(prior);
            }
        }
        return new(original, false, "没有从当前四文件快照通往已知 6.5 指纹的完整有效历史链；保持源字节。");
    }

    public static bool MatchesKnown(IReadOnlyDictionary<string, byte[]> bytes)
        => Known.Value.Any(fingerprint => CoreNames.All(name => bytes.TryGetValue(name, out var file) &&
            fingerprint.FileLengths.TryGetValue(name, out var length) && length == file.LongLength &&
            fingerprint.Sha256.TryGetValue(name, out var hash) && IsSha256(hash) &&
            hash.Equals(HashService.Sha256(file), StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyDictionary<string, byte[]>? Reverse(IReadOnlyDictionary<string, byte[]> current, HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OperationId) || entry.Files == null || entry.Files.Count is < 1 or > 4 ||
            entry.CoreFileFingerprints == null || entry.CoreFileFingerprints.Count != CoreNames.Length) return null;
        var beforeHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.CoreFileFingerprints)
            if (!CoreNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || !IsSha256(pair.Value) ||
                !beforeHashes.TryAdd(pair.Key, pair.Value)) return null;
        var expectedAfter = new Dictionary<string, string>(beforeHashes, StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in entry.Files)
        {
            // A four-file fixture does not borrow evidence from external files,
            // resource transactions, subdirectories or path aliases.
            if (file == null || !CoreNames.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase) ||
                !files.Add(file.RelativePath) || !IsSha256(file.BeforeHash) || !IsSha256(file.AfterHash) ||
                !file.BeforeHash.Equals(beforeHashes[file.RelativePath], StringComparison.OrdinalIgnoreCase)) return null;
            expectedAfter[file.RelativePath] = file.AfterHash;
        }
        if (CoreNames.Any(name => !current.TryGetValue(name, out var file) ||
            !expectedAfter[name].Equals(HashService.Sha256(file), StringComparison.OrdinalIgnoreCase))) return null;

        var candidate = new Dictionary<string, byte[]>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var file in entry.Files)
        {
            var bytes = current[file.RelativePath];
            if (file.FileLength != bytes.LongLength || file.Chunks == null || file.Chunks.Count == 0 ||
                file.Chunks.Any(chunk => chunk == null)) return null;
            var prior = bytes.ToArray();
            long previousEnd = -1;
            foreach (var chunk in file.Chunks.OrderBy(chunk => chunk.Offset))
            {
                if (chunk.OldBytes == null || chunk.NewBytes == null || chunk.OldBytes.Length == 0 ||
                    chunk.OldBytes.Length != chunk.NewBytes.Length || chunk.Offset < 0 || chunk.Offset < previousEnd ||
                    chunk.Offset > prior.LongLength - chunk.NewBytes.Length ||
                    !bytes.AsSpan((int)chunk.Offset, chunk.NewBytes.Length).SequenceEqual(chunk.NewBytes)) return null;
                chunk.OldBytes.CopyTo(prior, (int)chunk.Offset);
                previousEnd = chunk.Offset + chunk.NewBytes.Length;
            }
            if (!HashService.Sha256(prior).Equals(file.BeforeHash, StringComparison.OrdinalIgnoreCase)) return null;
            candidate[file.RelativePath] = prior;
        }
        if (CoreNames.Any(name => !beforeHashes[name].Equals(HashService.Sha256(candidate[name]), StringComparison.OrdinalIgnoreCase))) return null;
        return candidate;
    }

    private static string Identity(IReadOnlyDictionary<string, byte[]> bytes)
        => string.Join('|', CoreNames.Select(name => HashService.Sha256(bytes[name])));

    private static bool IsSha256(string? hash)
        => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}
