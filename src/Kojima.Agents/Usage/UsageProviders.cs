using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Usage;

/// <summary>
/// 用量来源调度：把 13 个来源分别映射到本机日志、本机数据库或在线额度接口。
/// 每个来源都会返回一个带说明的 snapshot，不再用 unknown 静默糊弄。
/// </summary>
public static class UsageProviders
{
    public static async Task<UsageSnapshot> ReadAsync(
        string source,
        DateOnly start,
        DateOnly end,
        UsageSettings settings,
        CancellationToken ct)
    {
        var id = (source ?? "").Trim().ToLowerInvariant();
        var info = UsageCatalog.Find(id);
        var snapshot = id switch
        {
            "claude" => await ReadClaudeAsync(start, end, ct).ConfigureAwait(false),
            "codex" => await ReadCodexAsync(start, end, ct).ConfigureAwait(false),
            "cursor" => await ReadCursorAsync(ct).ConfigureAwait(false),
            "antigravity" => Antigravity(),
            "dsh" => LocalUsage.Read("dsh", start, end),
            "kimi" => await ReadKimiAsync(start, end, ct).ConfigureAwait(false),
            "opencode" => LocalUsageExtra.ReadOpenCode(start, end),
            "zcode" => LocalUsageExtra.ReadZCode(start, end),
            "deepseek" => await ReadDeepSeekAsync(ct).ConfigureAwait(false),
            "openrouter" => await ReadOpenRouterAsync(ct).ConfigureAwait(false),
            "glm" => await ReadGlmAsync(ct).ConfigureAwait(false),
            "newapi" => await ReadNewApiAsync(settings, ct).ConfigureAwait(false),
            "sub2api" => await ReadSub2ApiAsync(settings, ct).ConfigureAwait(false),
            _ => new UsageSnapshot { Status = "unknown", Note = "未知的用量来源" },
        };

        snapshot.Pid = id;
        snapshot.Name = info?.Name ?? snapshot.Name;
        snapshot.Source ??= info?.Group ?? "本机";
        snapshot.FetchedAt = DateTimeOffset.Now;

        if (snapshot.Status is not ("error" or "unknown") &&
            snapshot.Entries.Count == 0 &&
            snapshot.Quotas.Count == 0 &&
            snapshot.Metrics.Count == 0 &&
            string.IsNullOrEmpty(snapshot.Note))
        {
            snapshot.Note = "暂无可显示的数据";
        }

        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadClaudeAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var snapshot = LocalUsage.Read("claude", start, end);
        await UsageQuotas.AddClaudeAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadCodexAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var snapshot = LocalUsage.Read("codex", start, end);
        await UsageQuotas.AddCodexAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadCursorAsync(CancellationToken ct)
    {
        var snapshot = new UsageSnapshot
        {
            Pid = "cursor",
            Name = "Cursor",
            Source = "在线",
            Scope = "额度",
            Status = "ok",
        };
        await UsageQuotas.AddCursorAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static UsageSnapshot Antigravity() => new()
    {
        Pid = "antigravity",
        Name = "Antigravity",
        Source = "本机",
        Scope = "日志",
        Status = "ok",
        Note = "Antigravity 的会话数据库使用 protobuf 记录，当前版本暂未解析",
    };

    private static async Task<UsageSnapshot> ReadKimiAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var snapshot = LocalUsageExtra.ReadKimi(start, end);
        await UsageQuotas.AddKimiAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadDeepSeekAsync(CancellationToken ct)
    {
        var snapshot = Online("deepseek", "DeepSeek");
        await UsageQuotas.AddDeepSeekAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadOpenRouterAsync(CancellationToken ct)
    {
        var snapshot = Online("openrouter", "OpenRouter");
        await UsageQuotas.AddOpenRouterAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadGlmAsync(CancellationToken ct)
    {
        var snapshot = Online("glm", "GLM");
        await UsageQuotas.AddGlmAsync(snapshot, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadNewApiAsync(UsageSettings settings, CancellationToken ct)
    {
        var snapshot = Online("newapi", "NewAPI");
        await UsageQuotas.AddNewApiAsync(snapshot, settings, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<UsageSnapshot> ReadSub2ApiAsync(UsageSettings settings, CancellationToken ct)
    {
        var snapshot = Online("sub2api", "Sub2API");
        await UsageQuotas.AddSub2ApiAsync(snapshot, settings, ct).ConfigureAwait(false);
        return snapshot;
    }

    private static UsageSnapshot Online(string id, string name) => new()
    {
        Pid = id,
        Name = name,
        Source = "在线",
        Scope = "额度",
        Status = "ok",
    };
}
