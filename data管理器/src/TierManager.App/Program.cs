using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace TierManager.App;

internal static class EncodingBootstrap
{
    [ModuleInitializer]
    internal static void Init() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static bool Test(string sourceRoot)
    {
        string temp = Path.Combine(Path.GetTempPath(), "tier-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Console.WriteLine("TEMP=" + temp);
        try
        {
            foreach (string name in new[] { "Ekd5.exe", "Data.e5", "Star.e5" })
            {
                string from = Path.Combine(sourceRoot, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(temp, name));
            }
            var profile = TierScanner.Load(temp);
            Console.WriteLine($"SCAN {profile.Summary}");
            Console.WriteLine($"canWrite={profile.CanWrite} reason={profile.ReadOnlyReason}");
            if (!profile.CanWrite) return false;

            var rows = profile.Entries.Select(Clone).ToList();
            rows[0].Label = "中";
            if (rows.Count > 1)
            {
                rows[1].Threshold = (byte)Math.Min(255, rows[1].Threshold!.Value + 2);
                rows[1].Label = "R";
            }
            byte last = rows[^1].Threshold ?? 0;
            if (last < 250 && rows.Count < 12)
                rows.Add(new TierEntry { Threshold = (byte)(last + 5), Label = "N" });
            var tooLong = rows.Select(Clone).ToList();
            tooLong[0].Label = "中文";
            var rejected = TierWriter.Apply(profile, tooLong);
            Console.WriteLine($"REJECT {rejected.Success} {rejected.Message}");
            if (rejected.Success) return false;
            var first = TierWriter.Apply(profile, rows);
            Console.WriteLine($"WRITE1 {first.Success} {first.Message}");
            if (!first.Success) return false;

            var after = TierScanner.Load(temp);
            Console.WriteLine($"READ1 {after.Summary}");
            if (!after.CanWrite || after.Labels[0] != "中" || after.Labels.Count != rows.Count) return false;

            var rows2 = after.Entries.Select(Clone).ToList();
            rows2[0].Label = "文";
            if (rows2.Count > 2) rows2.RemoveAt(rows2.Count - 1);
            var second = TierWriter.Apply(after, rows2);
            Console.WriteLine($"WRITE2 {second.Success} {second.Message}");
            if (!second.Success) return false;

            var final = TierScanner.Load(temp);
            Console.WriteLine($"READ2 {final.Summary}");
            return final.CanWrite && final.Labels[0] == "文" && final.Labels.Count == rows2.Count;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("TRANSFORM_KEEP") != "1")
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }

    private static TierEntry Clone(TierEntry entry) => new()
    {
        Ordinal = entry.Ordinal,
        Threshold = entry.Threshold,
        Label = entry.Label,
        OriginalReturn = entry.OriginalReturn
    };

    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;
        public override void Write(char value) { first.Write(value); second.Write(value); }
        public override void Write(string? value) { first.Write(value); second.Write(value); }
        public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
        public override void Flush() { first.Flush(); second.Flush(); }
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception == null) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "tier-ui-crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {exception}{Environment.NewLine}");
        }
        catch
        {
        }
        try
        {
            MessageBox.Show(exception.ToString(), "档位管理器错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (!AttachConsole(-1)) AllocConsole();
            var console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            try
            {
                var logStream = new FileStream(Path.Combine(Path.GetTempPath(), "tier-cli.log"),
                    FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                var log = new StreamWriter(logStream) { AutoFlush = true };
                Console.SetOut(new TeeWriter(console, log));
            }
            catch
            {
                Console.SetOut(console);
            }
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        if (args.Length >= 2 && args[0] == "--scan")
        {
            try
            {
                var profile = TierScanner.Load(args[1]);
                Console.WriteLine($"base={profile.RootPath}");
                Console.WriteLine($"kind={profile.Kind} tierBase={profile.TierBase} tiers={profile.TierCount}");
                Console.WriteLine($"func=0x{profile.TierFuncVa:X8} displayPtr=0x{profile.DisplayPointerVa:X8} table=0x{profile.DisplayTableVa:X8}");
                Console.WriteLine($"thresholds={string.Join(",", profile.Thresholds)}");
                Console.WriteLine($"labels={string.Join("|", profile.Labels)}");
                Console.WriteLine($"callSites={profile.CallSites.Count} cave=0x{profile.CaveVa:X8}/{profile.CaveLength}");
                Console.WriteLine($"canWrite={profile.CanWrite} reason={profile.ReadOnlyReason}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex.Message);
                return 1;
            }
        }

        if (args.Length >= 2 && args[0] == "--test")
        {
            try
            {
                return Test(args[1]) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        string? initialRoot = args.Length >= 1 && !args[0].StartsWith("--", StringComparison.Ordinal) &&
                              Directory.Exists(args[0])
            ? args[0]
            : null;
        Application.Run(new MainForm(initialRoot));
        return 0;
    }
}
