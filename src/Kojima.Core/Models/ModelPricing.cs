namespace Kojima.Core.Models;

/// <summary>
/// 模型价格表（美元 / 百万 token）。日志里没有成本时用来估算，
/// 口径参考 OpenRouter 公开价格：输入、输出、缓存读、缓存写。
/// 匹配不到的模型返回 null，界面就不显示金额，避免编造。
/// </summary>
public static class ModelPricing
{
    private readonly record struct Rate(double Input, double Output, double CacheRead, double CacheWrite);

    private static readonly (string Match, Rate Rate)[] Table =
    [
        ("claude-opus-4", new Rate(15, 75, 1.5, 18.75)),
        ("claude-sonnet-4", new Rate(3, 15, 0.3, 3.75)),
        ("claude-3-7-sonnet", new Rate(3, 15, 0.3, 3.75)),
        ("claude-3-5-sonnet", new Rate(3, 15, 0.3, 3.75)),
        ("claude-haiku-4", new Rate(1, 5, 0.1, 1.25)),
        ("claude-3-5-haiku", new Rate(0.8, 4, 0.08, 1)),
        ("gpt-5-codex", new Rate(1.25, 10, 0.125, 1.25)),
        ("gpt-5-mini", new Rate(0.25, 2, 0.025, 0.25)),
        ("gpt-5", new Rate(1.25, 10, 0.125, 1.25)),
        ("gpt-4.1", new Rate(2, 8, 0.5, 2)),
        ("o4-mini", new Rate(1.1, 4.4, 0.275, 1.1)),
        ("o3", new Rate(2, 8, 0.5, 2)),
        ("deepseek-reasoner", new Rate(0.55, 2.19, 0.14, 0.55)),
        ("deepseek", new Rate(0.27, 1.1, 0.07, 0.27)),
        ("kimi-k2", new Rate(0.6, 2.5, 0.15, 0.6)),
        ("kimi", new Rate(0.6, 2.5, 0.15, 0.6)),
        ("glm-4.6", new Rate(0.6, 2.2, 0.11, 0.6)),
        ("glm-4.5", new Rate(0.6, 2.2, 0.11, 0.6)),
        ("gemini-2.5-pro", new Rate(1.25, 10, 0.31, 1.25)),
        ("gemini-2.5-flash", new Rate(0.3, 2.5, 0.075, 0.3)),
        ("qwen3-coder", new Rate(0.3, 1.2, 0.03, 0.3)),
        ("grok-4", new Rate(3, 15, 0.75, 3)),
    ];

    /// <summary>按输入/输出/缓存用量估算一次调用的美元成本；未知模型返回 null。</summary>
    public static double? Estimate(string? model, long input, long cacheRead, long cacheWrite, long output)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var key = model.Trim().ToLowerInvariant();
        foreach (var (match, rate) in Table)
        {
            if (!key.Contains(match, StringComparison.Ordinal))
            {
                continue;
            }

            var cost = input / 1_000_000.0 * rate.Input
                + output / 1_000_000.0 * rate.Output
                + cacheRead / 1_000_000.0 * rate.CacheRead
                + cacheWrite / 1_000_000.0 * rate.CacheWrite;
            return cost > 0 ? cost : 0;
        }

        return null;
    }

    /// <summary>给没有 Cost 的条目补上估算值；已有成本（例如 OpenCode 自带 cost）不覆盖。</summary>
    public static void Fill(IEnumerable<TokenEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Cost is not null)
            {
                continue;
            }

            entry.Cost = Estimate(entry.Model, entry.Input, entry.CacheRead, entry.CacheWrite, entry.Output);
        }
    }
}
