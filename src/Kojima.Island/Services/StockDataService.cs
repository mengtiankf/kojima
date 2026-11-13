using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

public sealed class StockDataService : IDisposable
{
    private static readonly string[] DefaultCodes =
        ["sh000001", "sz399001", "sh600519", "sz300750", "sz002594", "hk00700"];

    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private readonly KojimaConfig _config;
    private readonly DispatcherTimer _timer;
    private int _busy;
    private bool _demo;

    public StockDataService(AgentSupervisor agents, Dispatcher dispatcher, KojimaConfig config)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _config = config;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += async (_, _) => await RefreshAsync();
        ApplySettings();
    }

    public IReadOnlyList<StockQuote> Quotes { get; private set; } = [];

    public DateTimeOffset? UpdatedAt { get; private set; }

    public string Status { get; private set; } = "尚未更新";

    public event Action? Updated;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public void ApplySettings()
    {
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_config.Stocks.RefreshSeconds, 5, 600));
    }

    public async Task RefreshAsync()
    {
        if (_demo || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var codes = _config.Stocks.Symbols is { Count: > 0 } configured
                ? configured.ToArray()
                : DefaultCodes;
            var quotes = await _agents.RequestAsync<List<StockQuote>>(
                "stocks", "stocks.read", new { codes }, 20000).ConfigureAwait(false) ?? [];
            if (_demo)
            {
                return;
            }
            if (quotes.Count == 0)
            {
                throw new InvalidOperationException("行情返回为空");
            }
            Quotes = quotes;
            UpdatedAt = DateTimeOffset.Now;
            Status = $"更新于 {UpdatedAt.Value.ToLocalTime():HH:mm}";
        }
        catch (Exception ex)
        {
            if (!_demo)
            {
                Status = "行情获取失败";
                AppLog.Warn("行情获取失败：" + ex.Message);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            _ = _dispatcher.BeginInvoke(() => Updated?.Invoke());
        }
    }

    public void Seed(IEnumerable<StockQuote> quotes)
    {
        _demo = true;
        Quotes = quotes.ToArray();
        UpdatedAt = DateTimeOffset.Now;
        Status = "演示数据";
        _ = _dispatcher.BeginInvoke(() => Updated?.Invoke());
    }

    public void Dispose() => _timer.Stop();
}
