using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Usage;

/// <summary>
/// 用量来源调度：把 13 个来源分别映射到本机日志、本机数据库或在线额度接口。
/// 每一列可以独立选择“用量来源”和“额度来源”（与 BrimDeck 的用量/额度分列配置对应），
/// 例如：列显示 Claude，用量读 Codex 的本机日志，额度查 Claude 的账户接口。
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

        var localFrom = toggle.Local ? Pick(toggle.UsageFrom, id, SupportsLocal) : null;
        var quotaFrom = toggle.Quota ? Pick(toggle.QuotaFrom, id, SupportsQuota) : null;

        var snapshot = localFrom is null
            ? Blank(id, info)
            : await ReadLocalAsync(localFrom, start, end, id, info).ConfigureAwait(false);

        if (quotaFrom is not null)
        {
            await QuotaGateway.ApplyAsync(quotaFrom, snapshot, settings, ct).ConfigureAwait(false);
        }

        snapshot.Pid = id;
        snapshot.Name = info?.Name ?? snapshot.Name;
        snapshot.Source = info?.Group ?? snapshot.Source ?? "本机";
        snapshot.FetchedAt = DateTimeOffset.Now;
        ModelPricing.Fill(snapshot.Entries);

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.Note))
        {
            notes.Add(snapshot.Note);
        }

        if (localFrom is not null && localFrom != id)
        {
            notes.Add($"用量来自 {Name(localFrom)}");
        }

        if (quotaFrom is not null && quotaFrom != id)
        {
            notes.Add($"额度来自 {Name(quotaFrom)}");
        }

        if (!toggle.Local && !toggle.Quota)
        {
            notes.Add("已关闭本机记录与账户额度");
        }
        else if (!toggle.Local)
        {
            notes.Add("已关闭用量记录");
        }
        else if (!toggle.Quota)
        {
            notes.Add("已关闭额度查询");
        }

        snapshot.Note = notes.Count > 0 ? string.Join(" · ", notes) : null;

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

    private static async Task<UsageSnapshot> ReadLocalAsync(
        string id,
        DateOnly start,
        DateOnly end,
        string columnId,
        UsageSourceInfo? column)
    {
        var snapshot = id switch
        {
            "claude" or "codex" or "dsh" => LocalUsage.Read(id, start, end),
            "kimi" => LocalUsageExtra.ReadKimi(start, end),
            "opencode" => LocalUsageExtra.ReadOpenCode(start, end),
            "zcode" => LocalUsageExtra.ReadZCode(start, end),
            "antigravity" => AntigravityUsage.Read(start, end),
            _ => Blank(columnId, column),
        };

        // 本机读取器会写自己的名字；这一列可能借用别的来源，统一改回列自己的标题。
        snapshot.Pid = columnId;
        snapshot.Name = column?.Name ?? snapshot.Name;
        snapshot.Source = column?.Group ?? snapshot.Source;
        return snapshot;
    }

    private static string? Pick(string? chosen, string id, Func<string, bool> supported)
    {
        var want = string.IsNullOrWhiteSpace(chosen) ? id : chosen.Trim().ToLowerInvariant();
        if (supported(want))
        {
            return want;
        }

        return supported(id) ? id : null;
    }

    private static bool SupportsLocal(string id) => UsageCatalog.Find(id)?.SupportsLocal == true;

    private static bool SupportsQuota(string id) => UsageCatalog.Find(id)?.SupportsQuota == true;

    private static string Name(string id) => UsageCatalog.Find(id)?.Name ?? id;

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
