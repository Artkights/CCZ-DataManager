using DataManager.Core.History;

namespace DataManager.Core.Scanner;

/// <summary>
/// Serializes engine writes between data-manager instances of the same project.
/// This lock is deliberately local to this application; external tools are still
/// detected by the SHA baselines taken before and inside the binary transaction.
/// </summary>
public sealed class EngineEffectWriteLock : IDisposable
{
    private FileStream? _stream;

    private EngineEffectWriteLock(FileStream stream, string lockPath)
    {
        _stream = stream;
        LockPath = lockPath;
    }

    public string LockPath { get; }

    public static EngineEffectWriteLock Acquire(string projectRoot, int attempts = 20, int delayMilliseconds = 150)
    {
        var directory = Path.Combine(ProjectHistoryPaths.GetProjectStorageRoot(projectRoot), "引擎写入");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "write.lock");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 256, leaveOpen: true))
                    writer.Write($"{Environment.ProcessId} {DateTime.Now:O}");
                stream.Flush();
                return new EngineEffectWriteLock(stream, path);
            }
            catch (IOException) when (attempt + 1 < attempts)
            {
                Thread.Sleep(delayMilliseconds);
            }
            catch (IOException)
            {
                break;
            }
        }
        throw new IOException("另一个引擎写入正在进行，请稍后重试。");
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream == null) return;
        stream.Dispose();
        try { File.Delete(LockPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
