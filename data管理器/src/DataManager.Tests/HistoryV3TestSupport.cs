using Ccz.HistoryStorage;

namespace DataManager.Tests;

internal static class HistoryV3TestSupport
{
    public static string DataLedger(string project) => Path.Combine(new DataManager.Core.History.HistoryStore().GetV3Root(project), "ledger");
    public static string LastSegment(string directory) => Directory.GetFiles(directory, "events-*.jsonl").Order(StringComparer.Ordinal).Last();
    public static IDisposable LockAtAppend(string kind, Action? beforeLock = null) => new AppendLock(kind, beforeLock);
    private sealed class AppendLock : IDisposable
    {
        private readonly IDisposable _observer;
        private FileStream? _locked;
        public AppendLock(string kind, Action? beforeLock)
            => _observer = HistoryLedger.ObserveAppends(point =>
            {
                if (point.Kind != kind || _locked != null) return;
                beforeLock?.Invoke();
                _locked = new FileStream(point.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            });
        public void Dispose() { _observer.Dispose(); _locked?.Dispose(); }
    }
}
