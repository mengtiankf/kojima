using System.Threading;
using System.Windows.Forms;
using Kojima.Core.Config;

namespace Kojima.Shell;

internal static class Program
{
    internal static readonly string InstanceSuffix =
        Environment.GetEnvironmentVariable("KOJIMA_INSTANCE") is { Length: > 0 } suffix
            ? "." + new string(suffix.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray())
            : "";

    private static readonly string MutexName = @"Local\Kojima.Shell.SingleInstance" + InstanceSuffix;

    internal static string PipeName => "kojima-shell" + InstanceSuffix;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            ShellHost.NotifyExisting();
            return;
        }

        AppLog.Init("shell");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var host = new ShellHost(args);
        host.Start();
        Application.Run(host);
    }
}
