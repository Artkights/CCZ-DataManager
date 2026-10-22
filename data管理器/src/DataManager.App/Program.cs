using System.Windows.Forms;

namespace DataManager.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationBootstrap.Initialize();
        if (args.Length > 0 && args[0].Equals("--verify-bundle", StringComparison.OrdinalIgnoreCase))
            return BundleVerification.Run(args);
        if (args.Length > 0 && args[0].Equals("--verify-resources", StringComparison.OrdinalIgnoreCase))
            return BundleVerification.Run(args, resourcesOnly: true);
        if (args.Length > 0 && args[0].Equals("--verify-extend", StringComparison.OrdinalIgnoreCase))
            return ExtendBundleVerification.Run(args);
        Application.Run(new MainForm());
        return 0;
    }
}
