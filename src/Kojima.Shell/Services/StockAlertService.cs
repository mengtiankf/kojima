using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>自选股异动提醒：后台每 60 秒抓一次腾讯行情，单日涨跌幅越过 ±5% 时进消息中心。</summary>
public sealed class StockAlertService : IDisposable
{
    private const string UrlPrefix = "https://qt.gtimg.cn/q=";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Encoding Gbk;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly NotifyCenter _notify;
    private readonly KojimaConfig _config;
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Today);
    private bool _busy;

    static StockAlertService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public StockAlertService(NotifyCenter notify, KojimaConfig config)
    {
        _notify = notify;
        _config = config;
        _timer.Interval = TimeSpan.FromSeconds(60);
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync(alert: false);
    }

    public void Stop() => _timer.Stop();

    private async Task CheckAsync(bool alert = true)
    {
        if (_busy || !_config.Feature("stocks").Enabled)
        {
            return;
        }

        var symbols = _config.Stocks.Symbols is { Count: > 0 } list
            ? list.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (symbols.Count == 0)
        {
            return;
        }

        _busy = true;
        try
        {
            var bytes = await Http.GetByteArrayAsync(UrlPrefix + string.Join(",", symbols));
            var quotes = Parse(Gbk.GetString(bytes));
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (today != _day)
            {
                _day = today;
                _alerted.Clear();
            }

            foreach (var (code, quote) in quotes)
            {
                if (alert is false || quote.Percent is not { } percent || Math.Abs(percent) < 5)
                {
                    continue;
                }

                if (!_alerted.Add(code))
                {
                    continue;
                }

                var direction = percent > 0 ? "上涨" : "下跌";
                var price = quote.Price is { } p ? $"现价 {p:0.00}，" : "";
                _notify.Push("持仓提醒", $"{quote.Name}{direction} {Math.Abs(percent):0.00}%，{price}请留意仓位。", "stock");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("股票提醒获取行情失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
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

    public void Dispose() => _timer.Stop();

    private sealed record Quote(string Name, double? Price, double? Percent);
}
