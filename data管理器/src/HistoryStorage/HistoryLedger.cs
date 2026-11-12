using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ccz.HistoryStorage;

public sealed record LedgerEvent(long Sequence, string Previous, string Kind, string OperationId, string TransactionId, JsonElement Payload, string Checksum);
public sealed record LedgerAppendPoint(string Path, string Kind, string OperationId);

/// <summary>Checksummed append-only segments. A torn final line starts a new segment; originals remain intact.</summary>
public sealed class HistoryLedger(string root) : IHistoryLedger
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Action<LedgerAppendPoint>?> AppendObserver = new();
    /// <summary>Scoped synchronous diagnostics at the real durable-append boundary; also supports failure/recovery verification.</summary>
    public static IDisposable ObserveAppends(Action<LedgerAppendPoint> observer)
    {
        var previous = AppendObserver.Value; AppendObserver.Value = observer;
        return new ObserverScope(() => AppendObserver.Value = previous);
    }
    public string Root { get; } = Path.GetFullPath(root);
    public bool Exists => Directory.Exists(Root) && Directory.EnumerateFiles(Root, "events-*.jsonl").Any();
    public IReadOnlyList<LedgerEvent> Read()
    {
        lock (Gate) return ReadCore().Events;
    }
    public void Append(string kind, string operationId, object payload, string? transactionId = null)
    {
        lock (Gate)
        {
            var state = ReadCore();
            Directory.CreateDirectory(Root);
            var marker = Path.Combine(Root, "storage-format.json");
            if (!File.Exists(marker))
            {
                using var format = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                format.Write(JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Recovery = "CCZ history-v3", LegacyFilesAreReadOnly = true }));
                format.Flush(true);
            }
            var last = state.Events.LastOrDefault();
            var sequence = (last?.Sequence ?? 0) + 1;
            var element = JsonSerializer.SerializeToElement(payload);
            var tx = transactionId ?? operationId;
            var checksum = Checksum(sequence, last?.Checksum ?? "", kind, operationId, tx, element);
            var entry = new LedgerEvent(sequence, last?.Checksum ?? "", kind, operationId, tx, element, checksum);
            var number = state.Segment == 0 ? 1 : state.TornTail ? state.Segment + 1 : state.Segment;
            var path = Path.Combine(Root, $"events-{number:D8}.jsonl");
            StoragePaths.ValidateInside(path, Root);
            AppendObserver.Value?.Invoke(new(path, kind, operationId));
            using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
            output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush(true);
        }
    }
    private (List<LedgerEvent> Events, int Segment, bool TornTail) ReadCore()
    {
        var events = new List<LedgerEvent>(); int segment = 0; bool torn = false;
        if (!Directory.Exists(Root)) return (events, 0, false);
        var paths = Directory.EnumerateFiles(Root, "events-*.jsonl").Order(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            StoragePaths.ValidateInside(path, Root);
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path)[7..], out segment)) throw new InvalidDataException("历史日志分段名无效。");
            torn = false;
            foreach (var line in Utf8Lines.Read(path))
            {
                if (!line.Terminated) { torn = true; break; }
                LedgerEvent entry;
                try { entry = JsonSerializer.Deserialize<LedgerEvent>(line.Bytes) ?? throw new JsonException(); }
                catch (JsonException ex) { throw new InvalidDataException("历史日志完整行损坏，已停止恢复；原件保留。", ex); }
                var previous = events.LastOrDefault();
                if (entry.Sequence != (previous?.Sequence ?? 0) + 1 || entry.Previous != (previous?.Checksum ?? "") ||
                    entry.Checksum != Checksum(entry.Sequence, entry.Previous, entry.Kind, entry.OperationId, entry.TransactionId, entry.Payload))
                    throw new InvalidDataException("历史日志序号、链或校验和不一致，已停止恢复；原件保留。");
                events.Add(entry);
            }
        }
        return (events, segment, torn);
    }
    private static string Checksum(long sequence, string previous, string kind, string operation, string tx, JsonElement payload)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { sequence, previous, kind, operation, tx, payload })));
    private sealed class ObserverScope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed record Utf8Line(long Offset, byte[] Bytes, bool Terminated);
public static class Utf8Lines
{
    public static IEnumerable<Utf8Line> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[65536]; using var line = new MemoryStream(); long offset = 0; int count;
        while ((count = stream.Read(buffer)) > 0)
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') { line.WriteByte(buffer[i]); continue; }
                var bytes = line.ToArray();
                yield return new Utf8Line(offset, bytes, true);
                offset += bytes.LongLength + 1; line.SetLength(0);
            }
        if (line.Length > 0) yield return new Utf8Line(offset, line.ToArray(), false);
    }
}
