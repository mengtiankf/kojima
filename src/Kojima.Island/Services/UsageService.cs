using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>AI 用量：按配置的來源逐个向 usage 子进程取数。</summary>
public sealed class UsageService
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private readonly KojimaConfig _config;
    private int _busy;

    public UsageService(AgentSupervisor agents, Dispatcher dispatcher, KojimaConfig config)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _config = config;
    }

    public IReadOnlyList<UsageSnapshot> Snapshots { get; private set; } = [];

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool Busy => Volatile.Read(ref _busy) == 1;

    public event Action? Updated;

    public async Task RefreshAsync(string period = "today")
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var sources = _config.Usage.Sources.Count > 0
                ? _config.Usage.Sources
                : ["dsh", "claude", "codex"];

            var tasks = sources.Select(source => ReadOneAsync(source, period)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var snapshots = results.Where(r => r is not null).Select(r => r!).ToList();

            _ = _dispatcher.BeginInvoke(() =>
            {
                Snapshots = snapshots;
                UpdatedAt = DateTimeOffset.Now;
                Updated?.Invoke();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("刷新用量失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<UsageSnapshot?> ReadOneAsync(string source, string period)
    {
        try
        {
            var snapshot = await _agents
                .RequestAsync<UsageSnapshot>("usage", "usage.read", new { source, period }, 30000)
                .ConfigureAwait(false);
            AppLog.Info($"usage {source}: {(snapshot is null ? "null" : snapshot.Status + " entries=" + snapshot.Entries.Count)}");
            return snapshot;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"usage {source} 失败：{ex.GetType().Name} {ex.Message}");
            return new UsageSnapshot
            {
                Pid = source,
                Name = source,
                Status = "error",
                Error = ex.Message,
                Source = "local",
            };
        }
    }
}
