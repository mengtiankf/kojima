using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Ipc;

namespace Kojima.Agents.Features;

/// <summary>股票行情与异动提醒都放在子进程：行情 GBK 解码、60 秒轮询、单日 ±5% 推送 notice。</summary>
public sealed class StocksFeature : IAgentFeature
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static readonly Encoding Gbk;

    static StocksFeature()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

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
                    codes.Add(code);
                }
            }
        }

        return codes;
    }

    private static async Task<string> ReadAsync(List<string> codes)
    {
        if (codes.Count == 0)
        {
            return "";
        }

        var url = "https://qt.gtimg.cn/q=" + string.Join(",", codes);
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return Gbk.GetString(bytes);
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

            var text = await ReadAsync(symbols).ConfigureAwait(false);
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

            foreach (var (code, quote) in Parse(text))
            {
                if (quote.Percent is not { } percent || Math.Abs(percent) < 5 || !_alerted.Add(code))
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

    /// <summary>解析 v_xxx="1~名称~代码~现价~昨收~…~涨跌幅~…"。</summary>
    private static Dictionary<string, Quote> Parse(string text)
    {
        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = line.IndexOf('"');
            var end = line.LastIndexOf('"');
            if (start <= 0 || end <= start)
            {
                continue;
            }

            var code = line[..start].Trim();
            if (code.StartsWith("v_", StringComparison.OrdinalIgnoreCase))
            {
                code = code[2..];
            }

            if (code.EndsWith('='))
            {
                code = code[..^1].Trim();
            }

            var fields = line[(start + 1)..end].Split('~');
            if (fields.Length < 6)
            {
                continue;
            }

            var price = Num(fields[3]);
            var previous = Num(fields[4]);
            var percent = fields.Length > 32 ? Num(fields[32]) : null;
            if (percent is null && price is { } current && previous is { } close && close != 0)
            {
                percent = (current - close) / close * 100;
            }

            quotes[code] = new Quote(fields[1].Trim(), price, percent);
        }

        return quotes;
    }

    private static double? Num(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed record Quote(string Name, double? Price, double? Percent);
}
