using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Usage;

/// <summary>
/// 用量来源调度：把 13 个来源分别映射到本机日志、本机数据库或在线额度接口。
/// 每个来源都会返回一个带说明的 snapshot，不再用 unknown 静默糊弄。
/// 每个来源可以分别关闭“本机记录”和“账户额度”（与 BrimDeck 的用量/额度分列配置对应）。
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
        var toggle = Toggle(settings, id);
        var local = toggle.Local;
        var quota = toggle.Quota;

        UsageSnapshot snapshot;
        switch (id)
        {
            case "claude":
                snapshot = local ? LocalUsage.Read("claude", start, end) : Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("claude", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "codex":
                snapshot = local ? LocalUsage.Read("codex", start, end) : Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("codex", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "kimi":
                snapshot = local ? LocalUsageExtra.ReadKimi(start, end) : Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("kimi", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "cursor":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("cursor", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "antigravity":
                snapshot = local ? AntigravityUsage.Read(start, end) : Blank(id, info);
                break;
            case "dsh":
                snapshot = local ? LocalUsage.Read("dsh", start, end) : Blank(id, info);
                break;
            case "opencode":
                snapshot = local ? LocalUsageExtra.ReadOpenCode(start, end) : Blank(id, info);
                break;
            case "zcode":
                snapshot = local ? LocalUsageExtra.ReadZCode(start, end) : Blank(id, info);
                break;
            case "deepseek":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("deepseek", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "openrouter":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("openrouter", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "glm":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("glm", snapshot, null, ct).ConfigureAwait(false);
                }
                break;
            case "newapi":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("newapi", snapshot, settings, ct).ConfigureAwait(false);
                }
                break;
            case "sub2api":
                snapshot = Blank(id, info);
                if (quota)
                {
                    await QuotaGateway.ApplyAsync("sub2api", snapshot, settings, ct).ConfigureAwait(false);
                }
                break;
            default:
                snapshot = new UsageSnapshot { Status = "unknown", Note = "未知的用量来源" };
                break;
        }

        snapshot.Pid = id;
        snapshot.Name = info?.Name ?? snapshot.Name;
        snapshot.Source ??= info?.Group ?? "本机";
        snapshot.FetchedAt = DateTimeOffset.Now;
        ModelPricing.Fill(snapshot.Entries);

        if (!local && !quota && snapshot.Note.Length == 0)
        {
            snapshot.Note = "已关闭本机记录与账户额度";
        }
        else if (!local && snapshot.Note.Length == 0)
        {
            snapshot.Note = "已关闭本机记录";
        }
        else if (!quota && snapshot.Note.Length == 0)
        {
            snapshot.Note = "已关闭账户额度查询";
        }

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

    private static UsageToggle Toggle(UsageSettings settings, string id)
    {
        if (settings.Toggles is not null && settings.Toggles.TryGetValue(id, out var toggle))
        {
            return toggle;
        }

        return new UsageToggle();
    }

    private static UsageSnapshot Blank(string id, UsageSourceInfo? info) => new()
    {
        Pid = id,
        Name = info?.Name ?? id,
        Source = info?.Group ?? "本机",
        Status = "ok",
    };
}
