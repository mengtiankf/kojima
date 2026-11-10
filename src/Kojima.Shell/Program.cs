using System.Threading;
using System.Windows.Forms;
using Kojima.Core.Config;

namespace Kojima.Shell;

internal static class Program
{
    private const string MutexName = @"Local\Kojima.Shell.SingleInstance";

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
