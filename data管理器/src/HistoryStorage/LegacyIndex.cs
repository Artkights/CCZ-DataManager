using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ccz.HistoryStorage;

public sealed record LegacyIndexEntry(long Offset, int Length, JsonElement Metadata);
public sealed record LegacyIndexDocument(long Length, long LastWriteTicks, List<LegacyIndexEntry> Entries, string? Source = null);

public static class LegacyIndex
{
    /// <summary>One-record-at-a-time metadata scan. Byte arrays are never base64-decoded for listing.</summary>
    public static IReadOnlyList<LegacyIndexEntry> Read(string source, string indexPath)
    {
        if (!File.Exists(source)) return [];
        var info = new FileInfo(source);
        try
        {
            if (File.Exists(indexPath))
            {
                var cached = JsonSerializer.Deserialize<LegacyIndexDocument>(File.ReadAllBytes(indexPath));
                if (cached?.Length == info.Length && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks &&
                    cached.Source == Path.GetFullPath(source)) return cached.Entries;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var entries = new List<LegacyIndexEntry>();
        foreach (var line in Utf8Lines.Read(source))
        {
            try
            {
                var bytes = line.Bytes.AsSpan();
                if (line.Offset == 0 && bytes.StartsWith(new byte[] { 239, 187, 191 })) bytes = bytes[3..];
                var node = JsonNode.Parse(bytes);
                if (node is not JsonObject) continue;
                StripPayloads(node);
                entries.Add(new(line.Offset, line.Bytes.Length, JsonSerializer.SerializeToElement(node)));
            }
            catch (JsonException) { /* Preserve invalid old lines without accepting them as recoverable records. */ }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            var temporary = indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                { JsonSerializer.Serialize(file, new LegacyIndexDocument(info.Length, info.LastWriteTimeUtc.Ticks, entries, Path.GetFullPath(source))); file.Flush(true); }
                File.Move(temporary, indexPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Index is a disposable acceleration only. */ }
        return entries;
    }
    public static byte[] ReadPayload(string source, LegacyIndexEntry entry)
    {
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Position = entry.Offset; var bytes = new byte[entry.Length]; file.ReadExactly(bytes);
        return entry.Offset == 0 && bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? bytes[3..] : bytes;
    }
    private static void StripPayloads(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
                if (key is "OldBytes" or "NewBytes") obj[key] = "";
                else if (obj[key] is { } child) StripPayloads(child);
        if (node is JsonArray array) foreach (var child in array) if (child != null) StripPayloads(child);
    }
}
