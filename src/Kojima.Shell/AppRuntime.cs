using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Kojima.Shell.Services;
using Kojima.Shell.Theme;

namespace Kojima.Shell;

/// <summary>外壳运行时：配置、主题、子进程代理、各功能服务。</summary>
public sealed class AppRuntime
{
    private readonly Dispatcher _dispatcher;
    private static AppRuntime? _current;

    public AppRuntime(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Config = ConfigStore.Load();
        ThemeRuntime.Apply(Config.Island.Theme);
        Agents = new AgentSupervisor(Config.Feature("monitor").IdleExitSeconds);
        Monitor = new MonitorService(Agents, dispatcher);
        Usage = new UsageService(Agents, dispatcher, Config);
        Weather = new WeatherService(dispatcher);
        Agents.EventReceived += OnAgentEvent;
        _current = this;
    }

    public static AppRuntime? Current { get => _current; set => _current = value; }

    public KojimaConfig Config { get; }

    public AgentSupervisor Agents { get; }

    public MonitorService Monitor { get; }

    public UsageService Usage { get; }

    public WeatherService Weather { get; }

    public event Action? ConfigChanged;

    public event Action<string>? ThemeChanged;

    public void ApplyTheme(string id)
    {
        Config.Island.Theme = id;
        ThemeRuntime.Apply(id);
        ThemeChanged?.Invoke(id);
        SaveConfig();
    }

    public void SaveConfig()
    {
        try
        {
            ConfigStore.Save(Config);
            ConfigChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("保存配置失败：" + ex.Message);
        }
    }

    public bool FeatureEnabled(string key) => Config.Feature(key).Enabled;

    /// <summary>功能开关：关闭时立即回收对应子进程。</summary>
    public async Task SetFeatureAsync(string key, bool enabled)
    {
        Config.Feature(key).Enabled = enabled;
        SaveConfig();
        if (!enabled)
        {
            await Agents.StopAsync(key).ConfigureAwait(false);
        }
        else if (key == "monitor")
        {
            await Monitor.StartAsync().ConfigureAwait(false);
        }
    }

    internal void OnAgentEvent(string feature, string name, JsonElement data)
    {
        if (feature == "monitor" && name == "sample")
        {
            Monitor.OnSampleEvent(data);
        }
    }

    /// <summary>退出：停采样、回收所有子进程、落盘配置。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Monitor.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("停止监控失败：" + ex.Message);
        }

        try
        {
            await Agents.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("回收子进程失败：" + ex.Message);
        }

        SaveConfig();
    }

    public void Dispose() => DisposeAsync().AsTask().Wait(3000);
}
