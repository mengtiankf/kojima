using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Shell.Services;

/// <summary>系统监控数据流：需要时才拉起 monitor 子进程，面板收起后停止推送。</summary>
public sealed class MonitorService
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private int _subscribers;
    private int _streamId;

    public MonitorService(AgentSupervisor agents, Dispatcher dispatcher)
    {
        _agents = agents;
        _dispatcher = dispatcher;
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
            var result = await _agents
                .RequestAsync("monitor", "subscribe", new { intervalMs = 1500 }, 10000)
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

    public async Task StopAsync()
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

    public void AddSubscriber()
    {
        _subscribers++;
    }

    public void RemoveSubscriber()
    {
        _subscribers = Math.Max(0, _subscribers - 1);
    }
}
