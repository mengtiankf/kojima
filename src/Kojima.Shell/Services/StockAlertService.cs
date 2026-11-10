using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>自选股异动提醒的瘦客户端：规则和行情轮询在 stocks 子进程里，这里只负责开关。</summary>
public sealed class StockAlertService : IDisposable
{
    private readonly AgentSupervisor _agents;

    public StockAlertService(AgentSupervisor agents) => _agents = agents;

    public void Start() => _ = SendAsync("stocks.alerts.start");

    public void Stop() => _ = SendAsync("stocks.alerts.stop");

    private async Task SendAsync(string method)
    {
        try
        {
            await _agents.RequestAsync<object>("stocks", method, null, 10000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"股票提醒子进程调用失败（{method}）：{ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
