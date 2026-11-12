using System.Security.Cryptography;
using System.Text;

namespace Ccz.HistoryStorage;

/// <summary>A reentrant OS mutex shared by data and image transactions, independent of release/history paths.</summary>
public static class ProjectWriteLock
{
    public static IDisposable Acquire(string projectRoot)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var name = "CCZ.HistoryV3." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Local\\" : "") + name);
        bool held;
        try { held = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) { mutex.Dispose(); throw new IOException("此项目正在由另一个管理器写入，请等待该事务完成。"); }
        return new Lease(mutex);
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose() { var value = Interlocked.Exchange(ref _mutex, null); if (value == null) return; value.ReleaseMutex(); value.Dispose(); }
    }
}
