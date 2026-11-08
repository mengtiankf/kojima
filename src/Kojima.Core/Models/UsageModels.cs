namespace Kojima.Core.Models;

/// <summary>一个额度条（5 小时窗口 / 每周额度 / 套餐余额……）。</summary>
public sealed class Quota
{
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public double? Minutes { get; set; }

    public double Remaining => Math.Clamp(100.0 - UsedPercent, 0.0, 100.0);
}

/// <summary>一次模型调用的 token 记录。</summary>
public sealed class TokenEntry
{
    public string Key { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Model { get; set; } = "";
    public long Input { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Output { get; set; }
    public double? Cost { get; set; }

    public long Total => Input + CacheRead + CacheWrite + Output;
}

public enum MetricKind
{
    Percent,
    Balance,
    Spend,
    Count,
    Text,
}

/// <summary>非进度条类的指标：余额、花费、订阅数量等。</summary>
public sealed class Metric
{
    public MetricKind Kind { get; set; }
    public string Label { get; set; } = "";
    public double? Percent { get; set; }
    public double? Amount { get; set; }
    public double? Total { get; set; }
    public double? Used { get; set; }
    public string? Currency { get; set; }
    public string? Unit { get; set; }
    public bool Unlimited { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
}

public sealed class ModelUsage
{
    public string Model { get; set; } = "";
    public long Tokens { get; set; }
    public double Cost { get; set; }
    public int Entries { get; set; }
}

/// <summary>一个 provider 的完整读数。</summary>
public sealed class UsageSnapshot
{
    public string Pid { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Account { get; set; }
    public string? Plan { get; set; }
    public string? Scope { get; set; }
    public List<Quota> Quotas { get; set; } = [];
    public List<Metric> Metrics { get; set; } = [];
    public List<TokenEntry> Entries { get; set; } = [];
    public string? Note { get; set; }
    public string? Source { get; set; }
    /// <summary>ok / waiting / error</summary>
    public string Status { get; set; } = "waiting";
    public string? Error { get; set; }
    public DateTimeOffset? UsageStart { get; set; }
    public bool Complete { get; set; } = true;
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;

    public bool Ok => Status == "ok";
    public bool HasQuota => Quotas.Count > 0;

    public (long Tokens, double Cost) Totals(DateTimeOffset? since)
    {
        long tokens = 0;
        double cost = 0;
        foreach (var entry in Entries)
        {
            if (since is { } start && entry.Time < start)
            {
                continue;
            }

            tokens += entry.Total;
            cost += entry.Cost ?? 0;
        }

        return (tokens, cost);
    }

    public List<ModelUsage> ByModel(DateTimeOffset? since, int limit = 4) =>
        Entries
            .Where(e => since is null || e.Time >= since)
            .GroupBy(e => string.IsNullOrEmpty(e.Model) ? "未知" : e.Model)
            .Select(g => new ModelUsage
            {
                Model = g.Key,
                Tokens = g.Sum(e => e.Total),
                Cost = g.Sum(e => e.Cost ?? 0),
                Entries = g.Count(),
            })
            .OrderByDescending(m => m.Tokens)
            .Take(limit)
            .ToList();
}

public static class UsageFormat
{
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000.0:0.##}B",
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(),
    };

    public static string Money(double amount, string currency = "USD")
    {
        var symbol = currency.ToUpperInvariant() switch
        {
            "CNY" or "RMB" => "¥",
            "USD" => "$",
            "EUR" => "€",
            _ => currency + " ",
        };
        return $"{symbol}{amount:0.##}";
    }

    public static string Span(DateTimeOffset? resetAt, double? minutes, DateTimeOffset now)
    {
        if (resetAt is { } at)
        {
            var left = at - now;
            if (left <= TimeSpan.Zero)
            {
                return "已重置";
            }

            return left.TotalDays >= 1
                ? $"{(int)left.TotalDays}d {left.Hours}h"
                : $"{(int)left.TotalHours}h {left.Minutes}m";
        }

        if (minutes is { } m)
        {
            return m >= 60 ? $"{(int)(m / 60)}h {(int)(m % 60)}m" : $"{(int)m}m";
        }

        return "";
    }
}
