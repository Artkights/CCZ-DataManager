namespace DataManager.Tests;

internal static class TestSupport
{
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // DoEvents is a temporary loop; unlike Application.Run it can uninstall
    // the WinForms context before the next async UI action starts.
    public static void PumpUi()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.DoEvents();
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }

    public static string RepositoryRootFromBase(string basePath)
        => Directory.GetParent(Directory.GetParent(basePath)!.FullName)!.FullName;

    /// <summary>Waits for the actual asynchronous host history operation and its UI refresh.</summary>
    public static void RestoreSavedHistory(DataManager.App.MainForm form, bool redo)
    {
        var previousContext = SynchronizationContext.Current;
        var busy = typeof(DataManager.App.MainForm).GetField("_historyIoBusy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            // A short DoEvents loop does not retain the Windows Forms context
            // installed by Application.Run, so provide it before the first await.
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (redo) form.RedoSavedForSmokeTest();
            else form.UndoSavedForSmokeTest();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while ((bool)busy.GetValue(form)!)
            {
                Application.DoEvents(); Thread.Sleep(1);
                if (timer.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("主窗异步历史恢复未完成。");
            }
            Application.DoEvents();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    /// <summary>
    /// Returns an isolated, history-free fixture. When a complete source history
    /// proves an embedded 6.5 snapshot, restore that snapshot in memory before
    /// writing the temporary copy. Set false when the caller needs exact source
    /// bytes, including deliberate unknown or modified layouts.
    /// </summary>
    public static string CreateCoreCopy(string source, bool restoreKnown65Snapshot = true)
    {
        var target = Path.Combine(Path.GetTempPath(), "DataManagerProfileSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var sourceFiles = Directory.EnumerateFiles(source).ToArray();
            IReadOnlyDictionary<string, byte[]> bytes = Known65FixtureSnapshot.CoreNames.ToDictionary(name => name,
                name => File.ReadAllBytes(sourceFiles.Single(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))),
                StringComparer.OrdinalIgnoreCase);
            if (restoreKnown65Snapshot) bytes = Known65FixtureSnapshot.Recover(source, bytes).Bytes;
            foreach (var pair in bytes) File.WriteAllBytes(Path.Combine(target, pair.Key), pair.Value);
            return target;
        }
        catch
        {
            Directory.Delete(target, true);
            throw;
        }
    }

    /// <summary>Requires an exact embedded 6.5 snapshot and leaves the source untouched.</summary>
    public static string CreateKnown65Snapshot(string source)
    {
        var target = CreateCoreCopy(source);
        var bytes = Known65FixtureSnapshot.CoreNames.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(target, name)),
            StringComparer.OrdinalIgnoreCase);
        if (Known65FixtureSnapshot.MatchesKnown(bytes)) return target;
        Directory.Delete(target, true);
        throw new InvalidDataException("测试基底没有可逐字节/SHA-256 证明的已知 6.5 快照；没有放宽运行时布局证据。");
    }
}
