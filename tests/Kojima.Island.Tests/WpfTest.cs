using System.Windows;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Kojima.Island;
using Kojima.Island.Theme;

namespace Kojima.Island.Tests;

internal static class WpfTest
{
    private static readonly Dispatcher UiDispatcher = StartDispatcher();

    public static void Run(Action action) => UiDispatcher.Invoke(action);

    private static Dispatcher StartDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.SetResult(application.Dispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Kojima UI tests";
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}

public abstract class IslandTestBase : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kojima-ui-tests", Guid.NewGuid().ToString("N"));

    protected void WithRuntime(Action<KojimaConfig>? arrange, Action<AppRuntime> test)
    {
        WpfTest.Run(() =>
        {
            ConfigStore.UseRoot(_root);
            var config = new KojimaConfig();
            arrange?.Invoke(config);
            ConfigStore.Save(config);
            ThemeCatalog.Reload();
            var runtime = new AppRuntime(Application.Current.Dispatcher);
            try
            {
                test(runtime);
            }
            finally
            {
                runtime.Themes.Dispose();
                runtime.Agents.DisposeAsync().AsTask().GetAwaiter().GetResult();
                AppRuntime.Current = null;
                ConfigStore.UseRoot(null);
            }
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
