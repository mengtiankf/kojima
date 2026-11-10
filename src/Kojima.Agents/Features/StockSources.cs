using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>行情源：腾讯 / 新浪 / 东方财富，统一输出 StockQuote。自动模式按顺序回退。</summary>
internal static class StockSources
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly Encoding Gbk;

    static StockSources()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public static IReadOnlyList<string> Order(string? source)
    {
        return (source ?? "auto").Trim().ToLowerInvariant() switch
        {
            "tencent" => ["tencent", "sina", "eastmoney"],
            "sina" => ["sina", "tencent", "eastmoney"],
            "eastmoney" => ["eastmoney", "tencent", "sina"],
            _ => ["tencent", "sina", "eastmoney"],
        };
    }

    public static async Task<List<StockQuote>> ReadAsync(string? source, IReadOnlyList<string> codes,
        CancellationToken ct)
    {
        var normalized = codes.Select(Normalize).Where(c => c.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        Exception? last = null;
        foreach (var provider in Order(source))
        {
            try
            {
                var quotes = provider switch
                {
                    "sina" => await ReadSinaAsync(normalized, ct).ConfigureAwait(false),
                    "eastmoney" => await ReadEastMoneyAsync(normalized, ct).ConfigureAwait(false),
                    _ => await ReadTencentAsync(normalized, ct).ConfigureAwait(false),
                };
                if (quotes.Count > 0)
                {
                    return quotes;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("行情源均不可用");
    }

    public static async Task<List<StockSuggestion>> SearchAsync(string keyword, CancellationToken ct)
    {
        var query = (keyword ?? "").Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var url = "https://smartbox.gtimg.cn/s3/?t=all&q=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = Gbk.GetString(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var start = body.IndexOf('"');
        var end = body.LastIndexOf('"');
        if (start < 0 || end <= start)
        {
            return [];
        }

        var results = new List<StockSuggestion>();
        foreach (var item in body[(start + 1)..end].Split('^', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = item.Split('~');
            if (fields.Length < 3)
            {
                continue;
            }

            var market = fields[0].Trim().ToLowerInvariant();
            var code = fields[1].Trim();
            var name = fields[2].Trim();
            if (code.Length == 0 || name.Length == 0)
            {
                continue;
            }

            var full = market switch
            {
                "sh" or "sz" or "hk" => market + code,
                "us" => "us" + code.ToUpperInvariant(),
                _ => code,
            };
            if (results.All(r => !r.Code.Equals(full, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new StockSuggestion(full, name));
            }
        }

        return results.Take(12).ToList();
    }

    /// <summary>把 600519 / 000001 / 00700 归一化为腾讯风格代码。</summary>
    public static string Normalize(string raw)
    {
        var code = (raw ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("hk") || code.StartsWith("us"))
        {
            return code;
        }

        if (code.Length == 6 && code.All(char.IsDigit))
        {
            return code[0] is '6' or '9' ? "sh" + code : "sz" + code;
        }

        if (code.Length == 5 && code.All(char.IsDigit))
        {
            return "hk" + code;
        }

        return code;
    }

    private static async Task<List<StockQuote>> ReadTencentAsync(List<string> codes, CancellationToken ct)
    {
        var url = "https://qt.gtimg.cn/q=" + string.Join(",", codes);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = Gbk.GetString(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var quotes = new List<StockQuote>();
        foreach (var line in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = line.IndexOf('"');
            var end = line.LastIndexOf('"');
            if (start <= 0 || end <= start)
            {
                continue;
            }

            var head = line[..start].Trim();
            var code = head.StartsWith("v_", StringComparison.OrdinalIgnoreCase) ? head[2..] : head;
            code = code.TrimEnd('=').Trim();
            var fields = line[(start + 1)..end].Split('~');
            if (fields.Length < 6)
            {
                continue;
            }

            var price = Num(fields[3]);
            var previous = Num(fields[4]);
            var percent = fields.Length > 32 ? Num(fields[32]) : null;
            if (percent is null && price is { } p && previous is { } prev && prev != 0)
            {
                percent = (p - prev) / prev * 100;
            }

            quotes.Add(new StockQuote(code, fields[1].Trim(), price, percent, previous));
        }

        return quotes;
    }

    private static async Task<List<StockQuote>> ReadSinaAsync(List<string> codes, CancellationToken ct)
    {
        var url = "https://hq.sinajs.cn/list=" + string.Join(",", codes);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://finance.sina.com.cn/");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = Gbk.GetString(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var quotes = new List<StockQuote>();
        foreach (var line in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var nameStart = line.IndexOf("hq_str_", StringComparison.OrdinalIgnoreCase);
            var start = line.IndexOf('"');
            var end = line.LastIndexOf('"');
            if (nameStart < 0 || start <= 0 || end <= start)
            {
                continue;
            }

            var head = line[(nameStart + 7)..start].Trim().TrimEnd('=').Trim();
            var fields = line[(start + 1)..end].Split(',');
            if (fields.Length < 4)
            {
                continue;
            }

            var price = Num(fields[3]);
            var previous = Num(fields[2]);
            double? percent = price is { } p && previous is { } prev && prev != 0 ? (p - prev) / prev * 100 : null;
            quotes.Add(new StockQuote(head, fields[0].Trim(), price, percent, previous));
        }

        return quotes;
    }

    private static async Task<List<StockQuote>> ReadEastMoneyAsync(List<string> codes, CancellationToken ct)
    {
        var secids = codes.Select(ToSecId).Where(s => s.Length > 2).ToList();
        if (secids.Count == 0)
        {
            return [];
        }

        var url = "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&fields=f12,f14,f2,f3,f18&secids=" +
                  string.Join(",", secids);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var quotes = new List<StockQuote>();
        foreach (var item in diff.EnumerateArray())
        {
            var code = Text(item, "f12");
            var name = Text(item, "f14");
            if (code.Length == 0)
            {
                continue;
            }

            var price = Number(item, "f2");
            var percent = Number(item, "f3");
            var previous = Number(item, "f18");
            quotes.Add(new StockQuote(Normalize(code), name, price, percent, previous));
        }

        return quotes;
    }

    private static string ToSecId(string code)
    {
        var value = Normalize(code);
        if (value.StartsWith("sh"))
        {
            return "1." + value[2..];
        }

        if (value.StartsWith("sz"))
        {
            return "0." + value[2..];
        }

        if (value.StartsWith("hk"))
        {
            return "116." + value[2..].PadLeft(5, '0');
        }

        return "";
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static double? Num(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
