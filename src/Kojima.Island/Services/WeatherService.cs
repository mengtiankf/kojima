using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>天气的调度与缓存；请求、解析和雨雪提醒都在 weather 子进程里。</summary>
public sealed class WeatherService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly AgentSupervisor _agents;
    private bool _busy;
    private bool _started;

    public WeatherService(Dispatcher dispatcher, AgentSupervisor agents)
    {
        _agents = agents;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMinutes(20),
        };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(false);
    }

    public WeatherInfo Current { get; private set; } = new();

    /// <summary>当前地区名（来自配置；空串表示自动定位）。</summary>
    public string Location { get; private set; } = "";

    public event Action<WeatherInfo>? Updated;

    /// <summary>按配置刷新：地区与间隔都从 ConfigStore 读取。</summary>
    public void ApplySettings()
    {
        var config = ConfigStore.Load();
        Location = (config.Weather.Location ?? "").Trim();
        var minutes = Math.Clamp(config.Weather.RefreshMinutes, 5, 180);
        if (Math.Abs(_timer.Interval.TotalMinutes - minutes) > 0.5)
        {
            _timer.Interval = TimeSpan.FromMinutes(minutes);
        }
    }

    public void Start()
    {
        if (_started)
        {
            ApplySettings();
            _ = RefreshAsync();
            return;
        }

        _started = true;
        ApplySettings();
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            Location = (ConfigStore.Load().Weather.Location ?? "").Trim();
            var info = await _agents.RequestAsync<WeatherInfo>("weather", "weather.read",
                new { location = Location }, 20000).ConfigureAwait(false);
            if (info is null)
            {
                AppLog.Warn("天气请求失败：子进程返回空数据");
                return;
            }

            // 未指定地区时按 IP 定位，城市名不可靠（wttr.in 常给出片区别名），统一显示「本地」
            info.City = Location.Length > 0 ? Location : "本地";

            Current = info;
            Updated?.Invoke(info);
        }
        catch (Exception ex)
        {
            AppLog.Warn("天气获取失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
