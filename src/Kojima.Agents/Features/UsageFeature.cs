using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Agents.Usage;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>AI 用量：按来源读取本机日志，返回额度 / Token / 成本快照。</summary>
public sealed class UsageFeature : IAgentFeature
{
    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
    }

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method)
        {
            case "usage.read":
            {
                var source = ReadString(args, "source") ?? "dsh";
                var period = ReadString(args, "period") ?? "today";
                var snapshot = await Task.Run(() => ReadSnapshot(source, period)).ConfigureAwait(false);
                return snapshot;
            }
            case "usage.sources":
                return new[] { "dsh", "claude", "codex" };
            default:
                return null;
        }
    }

    private static UsageSnapshot ReadSnapshot(string source, string period)
    {
        var now = DateTime.Now;
        var start = period switch
        {
            "week" => DateOnly.FromDateTime(now.AddDays(-(int)now.DayOfWeek + 1)),
            "month" => new DateOnly(now.Year, now.Month, 1),
            _ => DateOnly.FromDateTime(now),
        };

        try
        {
            var snapshot = LocalUsage.Read(source, start, DateOnly.FromDateTime(now));
            snapshot.FetchedAt = now;
            return snapshot;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 {source} 用量失败：{ex.Message}");
            return new UsageSnapshot
            {
                Pid = source,
                Name = source,
                Status = "error",
                Error = ex.Message,
                FetchedAt = now,
            };
        }
    }

    private static string? ReadString(JsonElement? args, string name)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
