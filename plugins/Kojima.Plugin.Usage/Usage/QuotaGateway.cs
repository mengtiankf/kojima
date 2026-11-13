using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Usage;

/// <summary>
/// 在线额度接口的缓存与退避层：短时间内的重复刷新直接读缓存，
/// 429 或连续失败时退避 5 分钟并继续展示上一次的有效数据。
/// </summary>
internal static class QuotaGateway
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Block = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed class Saved
    {
        public DateTimeOffset SavedAt { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
        public string? Plan { get; set; }
        public List<Quota> Quotas { get; set; } = [];
        public List<Metric> Metrics { get; set; } = [];
    }

    public static async Task ApplyAsync(
        string source,
        UsageSnapshot snapshot,
        UsageSettings? settings,
        CancellationToken ct)
    {
        var saved = Read(source);
        var now = DateTimeOffset.Now;

        if (saved is not null && saved.BlockedUntil > now)
        {
            Apply(snapshot, saved);
            Append(snapshot, $"额度接口限流，{Math.Max(1, (int)Math.Ceiling((saved.BlockedUntil - now).TotalMinutes))} 分钟后重试（显示缓存）");
            return;
        }

        if (saved is not null && now - saved.SavedAt < Fresh)
        {
            Apply(snapshot, saved);
            return;
        }

        var beforeQuotas = snapshot.Quotas.Count;
        var beforeMetrics = snapshot.Metrics.Count;
        await RequestAsync(source, snapshot, settings, ct).ConfigureAwait(false);
        var added = snapshot.Quotas.Count > beforeQuotas || snapshot.Metrics.Count > beforeMetrics;

        if (added)
        {
            Save(source, snapshot, beforeQuotas, beforeMetrics, blocked: false);
            return;
        }

        var stale = saved ?? Read(source);
        if (stale is not null)
        {
            Apply(snapshot, stale);
            var minutes = Math.Max(1, (int)(now - stale.SavedAt).TotalMinutes);
            Append(snapshot, $"额度接口暂不可用，显示 {minutes} 分钟前的缓存");
            if (IsRateLimited(snapshot))
            {
                stale.BlockedUntil = now + Block;
                Write(source, stale);
            }
            return;
        }

        if (IsRateLimited(snapshot))
        {
            Write(source, new Saved { SavedAt = now, BlockedUntil = now + Block });
        }
    }

    private static Task RequestAsync(string source, UsageSnapshot snapshot, UsageSettings? settings, CancellationToken ct) =>
        source switch
        {
            "claude" => UsageQuotas.AddClaudeAsync(snapshot, ct),
            "codex" => UsageQuotas.AddCodexAsync(snapshot, ct),
            "cursor" => UsageQuotas.AddCursorAsync(snapshot, ct),
            "kimi" => UsageQuotas.AddKimiAsync(snapshot, ct),
            "deepseek" => UsageQuotas.AddDeepSeekAsync(snapshot, ct),
            "openrouter" => UsageQuotas.AddOpenRouterAsync(snapshot, ct),
            "glm" => UsageQuotas.AddGlmAsync(snapshot, ct),
            "newapi" => UsageQuotas.AddNewApiAsync(snapshot, settings ?? new UsageSettings(), ct),
            "sub2api" => UsageQuotas.AddSub2ApiAsync(snapshot, settings ?? new UsageSettings(), ct),
            _ => Task.CompletedTask,
        };

    private static string PathFor(string source) =>
        Path.Combine(ConfigStore.RootDirectory, "cache", $"quota-{source}.json");

    private static Saved? Read(string source)
    {
        try
        {
            var path = PathFor(source);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"额度缓存读取失败：{source}：{ex.Message}");
            return null;
        }
    }

    private static void Write(string source, Saved saved)
    {
        try
        {
            var path = PathFor(source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(saved, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"额度缓存写入失败：{source}：{ex.Message}");
        }
    }

    private static void Save(string source, UsageSnapshot snapshot, int beforeQuotas, int beforeMetrics, bool blocked)
    {
        var saved = new Saved
        {
            SavedAt = DateTimeOffset.Now,
            Plan = snapshot.Plan,
            Quotas = snapshot.Quotas.Skip(beforeQuotas).ToList(),
            Metrics = snapshot.Metrics.Skip(beforeMetrics).ToList(),
        };
        if (blocked) saved.BlockedUntil = DateTimeOffset.Now + Block;
        Write(source, saved);
    }

    private static void Apply(UsageSnapshot snapshot, Saved saved)
    {
        if (snapshot.Plan is null && saved.Plan is not null) snapshot.Plan = saved.Plan;
        foreach (var quota in saved.Quotas) snapshot.Quotas.Add(quota);
        foreach (var metric in saved.Metrics) snapshot.Metrics.Add(metric);
    }

    private static void Append(UsageSnapshot snapshot, string note) =>
        snapshot.Note = string.IsNullOrWhiteSpace(snapshot.Note) ? note : snapshot.Note + "；" + note;

    private static bool IsRateLimited(UsageSnapshot snapshot) =>
        snapshot.Note?.Contains("429", StringComparison.Ordinal) == true;
}
