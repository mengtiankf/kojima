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
        Monitor = new MonitorService(Agents, dispatcher, Config);
        Usage = new UsageService(Agents, dispatcher, Config);
        Weather = new WeatherService(dispatcher);
        Notify = new NotifyCenter(dispatcher);
        Clipboard = Config.Feature("clipboard").Enabled ? new ClipboardService() : null;
        Media = new MediaSessionService();
        Messages = new MessageCenter(dispatcher, Config);
        Agents.EventReceived += OnAgentEvent;
        Weather.Updated += OnWeather;
        Monitor.Sample += OnMonitorSample;
        _current = this;
    }

    public static AppRuntime? Current { get => _current; set => _current = value; }

    public KojimaConfig Config { get; }

    public AgentSupervisor Agents { get; }

    public MonitorService Monitor { get; }

    public UsageService Usage { get; }

    public WeatherService Weather { get; }

    public NotifyCenter Notify { get; }

    public ClipboardService? Clipboard { get; }

    public MessageCenter Messages { get; }

    /// <summary>系统媒体会话（SMTC）。</summary>
    public MediaSessionService Media { get; }

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

    private DateTime _lastCpuAlert = DateTime.MinValue;
    private DateTime _lastWeatherAlert = DateTime.MinValue;

    private void OnMonitorSample(MonitorSample sample)
    {
        if (sample.Cpu.Percent < 90 || DateTime.Now - _lastCpuAlert < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastCpuAlert = DateTime.Now;
        Notify.Push("CPU 占用过高", $"当前 {sample.Cpu.Percent:0}% ，可打开监控页查看占用进程。", "warn");
    }

    private void OnWeather(WeatherInfo info)
    {
        if (DateTime.Now - _lastWeatherAlert < TimeSpan.FromHours(3))
        {
            return;
        }

        if (info.Description.Contains('雨') || info.Description.Contains('雪'))
        {
            _lastWeatherAlert = DateTime.Now;
            Notify.Push("天气提醒", $"{info.Description}，{info.TemperatureText}，出门记得带伞。", "weather");
        }
    }

    /// <summary>启动时探测外部监控软件并写回配置（设置页展示用）。</summary>
    public async Task DetectMonitorSourcesAsync()
    {
        if (!Config.Feature("monitor").Enabled)
        {
            return;
        }

        var sources = await Monitor.DetectSourcesAsync().ConfigureAwait(false);
        if (sources.Count != Config.Monitor.DetectedSources.Count || sources.Except(Config.Monitor.DetectedSources).Any())
        {
            Config.Monitor.DetectedSources = sources.ToList();
            SaveConfig();
        }
    }

    public void StartFeatures()
    {
        if (Config.Feature("clipboard").Enabled)
        {
            Clipboard?.Start();
            Media.Start();
        }

        Messages.Start();
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
            Clipboard?.Dispose();
            Media.Dispose();
            Messages.Dispose();
            Weather.Dispose();
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
