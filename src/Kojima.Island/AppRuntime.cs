using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Kojima.Island.Services;
using Kojima.Island.Theme;

namespace Kojima.Island;

/// <summary>外壳运行时：配置、主题、子进程代理、各功能服务。</summary>
public sealed class AppRuntime
{
    private readonly Dispatcher _dispatcher;
    private static AppRuntime? _current;

    public AppRuntime(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Config = ConfigStore.Load();
        Themes = new ThemeScheduler(this, dispatcher);
        Themes.Apply();
        Agents = new AgentSupervisor(Config.Feature("monitor").IdleExitSeconds);
        Translate = new TranslateProxy(Agents, Config);
        Monitor = new MonitorService(Agents, dispatcher, Config);
        Usage = new UsageService(Agents, dispatcher, Config);
        Weather = new WeatherService(dispatcher, Agents);
        Notify = new NotifyCenter(dispatcher);
        Updates = new UpdateCoordinator(Notify, Agents);
        StockAlerts = new StockAlertService(Agents);
        Clipboard = new ClipboardProxy(Agents, dispatcher);
        Media = new MediaProxy(Agents, dispatcher);
        Spectrum = new SpectrumProxy(Agents);
        Messages = new MessageProxy(Agents, dispatcher, Config);
        Controls = new ControlsProxy(Agents);
        Agents.EventReceived += OnAgentEvent;
        Agents.Failed += (feature, reason) => Notify.Push("后台功能异常", $"子进程 {feature} 连续失败：{reason}", "warn");
        _current = this;
    }

    public static AppRuntime? Current { get => _current; set => _current = value; }

    public KojimaConfig Config { get; }

    public ThemeScheduler Themes { get; }

    public AgentSupervisor Agents { get; }

    public TranslateProxy Translate { get; }

    public MonitorService Monitor { get; }

    public UsageService Usage { get; }

    public WeatherService Weather { get; }

    public NotifyCenter Notify { get; }

    public UpdateCoordinator Updates { get; }

    public StockAlertService StockAlerts { get; }

    public ClipboardProxy Clipboard { get; }

    public MessageProxy Messages { get; }

    public ControlsProxy Controls { get; }

    /// <summary>系统媒体会话（SMTC）。</summary>
    /// <summary>灵岛窗口（由 App 启动时挂上），用于保存设置后热刷新。</summary>
    public Kojima.Island.Island.IslandWindow? Island { get; set; }

    public MediaProxy Media { get; }

    /// <summary>系统输出频谱（WASAPI 环回）。</summary>
    public SpectrumProxy Spectrum { get; }

    public event Action? ConfigChanged;

    public event Action<string>? ThemeChanged;

    public void ApplyTheme(string id)
    {
        Config.Appearance.Mode = "manual";
        Config.Island.Theme = id;
        Themes.Apply();
        SaveConfig();
    }

    internal void RaiseThemeChanged(string id) => ThemeChanged?.Invoke(id);

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
            switch (key)
            {
                case "monitor":
                    await Monitor.StopAsync().ConfigureAwait(false);
                    break;
                case "clipboard":
                    Clipboard.Stop();
                    break;
                case "messages":
                    Messages.Stop();
                    break;
                case "media":
                    Media.Stop();
                    break;
                case "spectrum":
                    Spectrum.Stop();
                    break;
                case "stocks":
                    StockAlerts.Stop();
                    break;
            }

            await Agents.StopAsync(key).ConfigureAwait(false);
        }
        else
        {
            switch (key)
            {
                case "monitor":
                    await Monitor.StartAsync().ConfigureAwait(false);
                    break;
                case "stocks":
                    StockAlerts.Start();
                    break;
                case "clipboard":
                    Clipboard.Start();
                    break;
                case "messages":
                    Messages.Start();
                    break;
                case "media":
                    Media.Start();
                    break;
                case "spectrum":
                    Spectrum.Start();
                    break;
                case "usage":
                    _ = Usage.RefreshAsync();
                    break;
                case "weather":
                    _ = Weather.RefreshAsync();
                    break;
                case "controls":
                    _ = Controls.LoadAsync();
                    break;
            }
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
            Clipboard.Start();
        }

        if (Config.Feature("media").Enabled)
        {
            Media.Start();
            Spectrum.Start();
        }

        if (Config.Feature("messages").Enabled)
        {
            Messages.Start();
        }

        if (Config.Feature("stocks").Enabled)
        {
            StockAlerts.Start();
        }
    }

    internal void OnAgentEvent(string feature, string name, JsonElement data)
    {
        if (name == "notice")
        {
            try
            {
                var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "提醒" : "提醒";
                var body = data.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var kind = data.TryGetProperty("kind", out var k) ? k.GetString() ?? "info" : "info";
                Notify.Push(title, body, kind);
            }
            catch (Exception ex)
            {
                AppLog.Warn("处理子进程提醒失败：" + ex.Message);
            }

            return;
        }

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
            Themes.Dispose();
            Clipboard?.Dispose();
            Media.Dispose();
            Spectrum.Dispose();
            Messages.Dispose();
            Controls.Dispose();
            StockAlerts.Dispose();
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
