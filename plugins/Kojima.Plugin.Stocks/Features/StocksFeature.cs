using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>股票行情与异动提醒都放在子进程：多行情源、60 秒轮询、单日 ±5% 推送 notice。</summary>
public sealed class StocksFeature : IAgentFeature
{
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);
    private AgentPipeServer? _server;
    private Timer? _timer;
    private volatile bool _started;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private int _busy;
    private bool _primed;

    public bool Busy => _started;

    public void Attach(AgentPipeServer server) => _server = server;

    public ValueTask DisposeAsync()
    {
        StopAlerts();
        return ValueTask.CompletedTask;
    }

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method.ToLowerInvariant())
        {
            case "stocks.read":
                return await ReadAsync(Codes(args)).ConfigureAwait(false);
            case "stocks.search":
                return await StockSources.SearchAsync(Text(args, "keyword"), CancellationToken.None)
                    .ConfigureAwait(false);
            case "stocks.alerts.start":
                StartAlerts();
                return new { ok = true };
            case "stocks.alerts.stop":
                StopAlerts();
                return new { ok = true };
            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private static List<string> Codes(JsonElement? args)
    {
        var codes = new List<string>();
        if (args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("codes", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var code = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(code))
                {
                    codes.Add(StockSources.Normalize(code));
                }
            }
        }

        return codes;
    }

    private static string Text(JsonElement? args, string name)
    {
        return args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static async Task<List<StockQuote>> ReadAsync(List<string> codes)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var settings = ConfigStore.Load().Stocks;
        return await StockSources.ReadAsync(settings.Source, codes, CancellationToken.None).ConfigureAwait(false);
    }

    private void StartAlerts()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer = new Timer(_ =>
        {
            var alert = _primed;
            _primed = true;
            _ = CheckAlertsAsync(alert);
        }, null, 5000, 60_000);
    }

    private void StopAlerts()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task CheckAlertsAsync(bool alert)
    {
        if (!_started || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var symbols = ConfigStore.Load().Stocks.Symbols is { Count: > 0 } list
                ? list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (symbols.Count == 0)
            {
                return;
            }

            var quotes = await ReadAsync(symbols).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (today != _day)
            {
                _day = today;
                _alerted.Clear();
            }

            if (!alert || _server is null)
            {
                return;
            }

            foreach (var quote in quotes)
            {
                if (quote.Percent is not { } percent || Math.Abs(percent) < 5 || !_alerted.Add(quote.Code))
                {
                    continue;
                }

                var direction = percent > 0 ? "上涨" : "下跌";
                var price = quote.Price is { } p ? $"现价 {p:0.00}，" : "";
                await _server.PushAsync("notice", new
                {
                    title = "持仓提醒",
                    body = $"{quote.Name}{direction} {Math.Abs(percent):0.00}%，{price}请留意仓位。",
                    kind = "stock",
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("股票提醒获取行情失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
