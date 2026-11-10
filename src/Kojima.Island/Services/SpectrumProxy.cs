using System.Text.Json;
using Kojima.Core.Ipc;

namespace Kojima.Island.Services;

/// <summary>频谱外壳代理：WASAPI 采集在 spectrum 子进程，这里只保留最近一组频段能量。</summary>
public sealed class SpectrumProxy : IDisposable
{
    public const int Bands = 28;

    private readonly AgentSupervisor _agents;
    private double[] _levels = new double[Bands];
    private bool _running;

    public SpectrumProxy(AgentSupervisor agents)
    {
        _agents = agents;
        _agents.EventReceived += OnEvent;
    }

    /// <summary>0..1 的频段能量，低频在前。</summary>
    public double[] Levels => Volatile.Read(ref _levels);

    public bool Running => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _ = StartAsync();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        Volatile.Write(ref _levels, new double[Bands]);
        _ = _agents.RequestAsync<object>("spectrum", "spectrum.stop", null, 4000);
    }

    public void Dispose()
    {
        Stop();
        _agents.EventReceived -= OnEvent;
    }

    private async Task StartAsync()
    {
        try
        {
            var levels = await _agents.RequestAsync<double[]>("spectrum", "spectrum.start", null, 15000).ConfigureAwait(false);
            if (levels is { Length: > 0 })
            {
                Volatile.Write(ref _levels, levels);
            }
        }
        catch (Exception ex)
        {
            _running = false;
            Core.Config.AppLog.Warn("启动频谱子进程失败：" + ex.Message);
        }
    }

    private void OnEvent(string feature, string name, JsonElement data)
    {
        if (feature != "spectrum" || name != "spectrum")
        {
            return;
        }

        try
        {
            var levels = data.Deserialize<double[]>(IpcJson.Options);
            if (levels is { Length: > 0 })
            {
                Volatile.Write(ref _levels, levels);
            }
        }
        catch
        {
            // 忽略一帧坏数据
        }
    }
}
