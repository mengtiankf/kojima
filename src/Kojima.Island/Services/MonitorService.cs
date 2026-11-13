using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>系统监控数据流：需要时才拉起 monitor 子进程，面板收起后停止推送。</summary>
public sealed class MonitorService
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private readonly KojimaConfig? _config;
    private int _subscribers;
    private int _streamId;

    public MonitorService(AgentSupervisor agents, Dispatcher dispatcher, KojimaConfig? config = null)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _config = config;
    }

    /// <summary>让子进程返回它检测到的外部监控软件（lhm / ohm）。</summary>
    public async Task<IReadOnlyList<string>> DetectSourcesAsync()
    {
        try
        {
            var result = await _agents.RequestAsync("monitor", "monitor.sources", null, 8000).ConfigureAwait(false);
            if (result is { } element && element.TryGetProperty("sources", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                return list.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("检测外部监控软件失败：" + ex.Message);
        }

        return [];
    }

    /// <summary>每帧数据（已切回 UI 线程）。</summary>
    public event Action<MonitorSample>? Sample;

    public bool Streaming => _subscribers > 0;

    public MonitorSample? Last { get; private set; }

    public async Task StartAsync()
    {
        if (_streamId != 0)
        {
            return;
        }

        try
        {
            var interval = Math.Clamp(_config?.Monitor.IntervalMs ?? 1500, 400, 10000);
            var source = _config?.Monitor.Source;
            var tempSource = source is "internal" or "lhm" or "ohm" ? source : "auto";
            var result = await _agents
                .RequestAsync("monitor", "subscribe", new { intervalMs = interval, tempSource }, 10000)
                .ConfigureAwait(false);
            if (result is { } element && element.TryGetProperty("id", out var id) && id.TryGetInt32(out var parsed))
            {
                _streamId = parsed;
                _agents.SetBusy("monitor", true);
                AppLog.Info($"监控推送已开启 id={parsed}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("启动监控失败：" + ex.Message);
        }
    }

    /// <summary>数据源或采样间隔改动后重开推送；未在推送时下次订阅自然会读到新配置。</summary>
    public async Task ApplySettingsAsync()
    {
        if (_streamId == 0)
        {
            return;
        }

        await StopCoreAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_subscribers > 0)
        {
            _subscribers--;
            if (_subscribers > 0)
            {
                return;
            }
        }
        await StopCoreAsync().ConfigureAwait(false);
    }

    public async Task ForceStopAsync()
    {
        _subscribers = 0;
        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        if (_streamId == 0)
        {
            return;
        }

        var id = _streamId;
        _streamId = 0;
        _agents.SetBusy("monitor", false);
        try
        {
            await _agents.RequestAsync("monitor", "unsubscribe", new { id }, 5000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("停止监控失败：" + ex.Message);
        }
    }

    /// <summary>只取一帧（面板刚打开时先显示点东西）。</summary>
    public async Task<MonitorSample?> SampleOnceAsync()
    {
        try
        {
            var sample = await _agents.RequestAsync<MonitorSample>("monitor", "sample", null, 8000).ConfigureAwait(false);
            if (sample is not null)
            {
                Publish(sample);
            }

            return sample;
        }
        catch (Exception ex)
        {
            AppLog.Warn("单帧采样失败：" + ex.Message);
            return null;
        }
    }

    internal void OnSampleEvent(JsonElement data)
    {
        try
        {
            if (!data.TryGetProperty("sample", out var sampleElement))
            {
                return;
            }

            var sample = sampleElement.Deserialize<MonitorSample>(Kojima.Core.Ipc.IpcJson.Options);
            if (sample is not null)
            {
                Publish(sample);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("解析监控帧失败：" + ex.Message);
        }
    }

    private void Publish(MonitorSample sample)
    {
        Last = sample;
        _dispatcher.BeginInvoke(() => Sample?.Invoke(sample));
    }

    public void Seed(MonitorSample sample) => Publish(sample);

    public void AddSubscriber()
    {
        _subscribers++;
    }

    public void RemoveSubscriber()
    {
        _subscribers = Math.Max(0, _subscribers - 1);
    }
}
