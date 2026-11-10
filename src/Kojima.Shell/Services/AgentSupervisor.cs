using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Kojima.Core.Config;
using Kojima.Core.Ipc;

namespace Kojima.Shell.Services;

/// <summary>子进程代理：按需启动、空闲回收、请求/事件转发。</summary>
public sealed class AgentSupervisor : IAsyncDisposable
{
    private readonly Dictionary<string, AgentHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly int _shellPid = Environment.ProcessId;
    private readonly string _exePath;
    private readonly int _idleSeconds;
    private readonly Dictionary<string, int> _startFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    public AgentSupervisor(int idleSeconds = 90)
    {
        _idleSeconds = Math.Clamp(idleSeconds, 20, 3600);
        _exePath = ResolveAgentPath();
    }

    /// <summary>子进程事件（feature, eventName, data）。</summary>
    public event Action<string, string, JsonElement>? EventReceived;

    /// <summary>子进程状态变化（feature, running）。</summary>
    public event Action<string, bool>? StateChanged;

    public string AgentPath => _exePath;

    public bool IsRunning(string feature)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(feature, out var host) && host.Process is { HasExited: false };
        }
    }

    public async Task<JsonElement?> RequestAsync(
        string feature,
        string method,
        object? args = null,
        int timeoutMs = 15000,
        CancellationToken ct = default)
    {
        var host = await EnsureAsync(feature, ct).ConfigureAwait(false);
        host.LastUsed = DateTime.UtcNow;
        try
        {
            var result = await host.Client.RequestAsync(method, args, timeoutMs).ConfigureAwait(false);
            if (DateTime.UtcNow - host.StartedAt > TimeSpan.FromSeconds(30))
            {
                ClearStartFailures(feature);
            }

            return result;
        }
        catch
        {
            // 子进程可能在请求期间退出，重连一次
            await DisposeHostAsync(host).ConfigureAwait(false);
            var retry = await EnsureAsync(feature, ct).ConfigureAwait(false);
            retry.LastUsed = DateTime.UtcNow;
            return await retry.Client.RequestAsync(method, args, timeoutMs).ConfigureAwait(false);
        }
    }

    public async Task<T?> RequestAsync<T>(
        string feature,
        string method,
        object? args = null,
        int timeoutMs = 15000,
        CancellationToken ct = default)
    {
        var element = await RequestAsync(feature, method, args, timeoutMs, ct).ConfigureAwait(false);
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return element.Value.Deserialize<T>(IpcJson.Options);
    }

    /// <summary>同一功能的并发拉起请求串行化，避免同时创建多个子进程。</summary>
    private async Task<AgentHost> EnsureAsync(string feature, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_hosts.TryGetValue(feature, out var ready) && ready.Process is { HasExited: false })
            {
                return ready;
            }
        }

        var gate = _locks.GetOrAdd(feature, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_hosts.TryGetValue(feature, out var ready2) && ready2.Process is { HasExited: false })
                {
                    return ready2;
                }
            }

            return await StartAsync(feature, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AgentHost> StartAsync(string feature, CancellationToken ct)
    {
        AgentHost host;
        lock (_gate)
        {
            if (_hosts.TryGetValue(feature, out var existing) && existing.Process is { HasExited: false })
            {
                return existing;
            }

            if (_retryAfter.TryGetValue(feature, out var until) && DateTime.UtcNow < until)
            {
                var left = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                throw new InvalidOperationException($"子进程 {feature} 连续启动失败，{left} 秒后自动重试");
            }
        }

        if (!File.Exists(_exePath))
        {
            RecordStartFailure(feature, "找不到子进程可执行文件：" + _exePath);
            throw new FileNotFoundException("找不到子进程可执行文件：" + _exePath);
        }

        try
        {
            host = await StartCoreAsync(feature, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordStartFailure(feature, ex.Message);
            throw;
        }

        ClearStartFailures(feature);
        return host;
    }

    private async Task<AgentHost> StartCoreAsync(string feature, CancellationToken ct)
    {
        AgentHost host;

        var pipeName = IpcJson.PipeName(feature, _shellPid);
        var startInfo = new ProcessStartInfo(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--feature");
        startInfo.ArgumentList.Add(feature);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(_shellPid.ToString());
        startInfo.ArgumentList.Add("--idle");
        startInfo.ArgumentList.Add(_idleSeconds.ToString());

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("子进程启动失败");
        var client = await AgentPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);

        host = new AgentHost(feature, process, client);
        host.Process.EnableRaisingEvents = true;
        host.Process.Exited += (_, _) => OnHostExited(host);
        client.EventReceived += (name, data) => EventReceived?.Invoke(feature, name, data);
        client.Closed += () =>
        {
            AppLog.Warn($"子进程断开：{feature}");
            StateChanged?.Invoke(feature, false);
        };

        lock (_gate)
        {
            if (_hosts.TryGetValue(feature, out var old))
            {
                old.Client.DisposeAsync().AsTask().Wait(500);
                try
                {
                    if (!old.Process.HasExited)
                    {
                        old.Process.Kill(true);
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            _hosts[feature] = host;
        }

        AppLog.Info($"子进程已启动：{feature} pid={process.Id}");
        StateChanged?.Invoke(feature, true);
        return host;
    }

    /// <summary>关闭某个功能子进程（例如功能被禁用）。</summary>
    public async Task StopAsync(string feature)
    {
        AgentHost? host = null;
        lock (_gate)
        {
            if (_hosts.Remove(feature, out var found))
            {
                host = found;
            }
        }

        if (host is not null)
        {
            await DisposeHostAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>回收空闲或已经退出的子进程；由外壳的计时器周期性调用。</summary>
    public async Task ReapIdleAsync(TimeSpan idle)
    {
        List<AgentHost> stale = [];
        lock (_gate)
        {
            foreach (var host in _hosts.Values)
            {
                if (host.Process.HasExited)
                {
                    stale.Add(host);
                    continue;
                }

                if (host.Busy)
                {
                    continue;
                }

                if (DateTime.UtcNow - host.LastUsed > idle)
                {
                    stale.Add(host);
                }
            }

            foreach (var host in stale)
            {
                _hosts.Remove(host.Feature);
            }
        }

        foreach (var host in stale)
        {
            AppLog.Info(host.Process.HasExited
                ? $"清理已退出的子进程：{host.Feature}"
                : $"空闲回收子进程：{host.Feature}");
            await DisposeHostAsync(host).ConfigureAwait(false);
        }
    }

    private void RecordStartFailure(string feature, string reason)
    {
        int failures;
        TimeSpan delay;
        lock (_gate)
        {
            _startFailures.TryGetValue(feature, out var previous);
            failures = previous + 1;
            _startFailures[feature] = failures;
            delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));
            _retryAfter[feature] = DateTime.UtcNow + delay;
        }

        AppLog.Warn($"子进程异常（{feature}，第 {failures} 次）：{reason}；{(int)delay.TotalSeconds} 秒后允许重启");
    }

    private void ClearStartFailures(string feature)
    {
        lock (_gate)
        {
            _startFailures.Remove(feature);
            _retryAfter.Remove(feature);
        }
    }

    private void OnHostExited(AgentHost host)
    {
        if (host.Stopping)
        {
            return;
        }

        var code = -1;
        try
        {
            code = host.Process.ExitCode;
        }
        catch
        {
            // 进程对象可能已释放
        }

        AppLog.Warn($"子进程意外退出：{host.Feature} code={code}");
        lock (_gate)
        {
            if (_hosts.TryGetValue(host.Feature, out var current) && ReferenceEquals(current, host))
            {
                _hosts.Remove(host.Feature);
            }
        }

        StateChanged?.Invoke(host.Feature, false);
        RecordStartFailure(host.Feature, $"子进程退出 code={code}");
    }

    public void SetBusy(string feature, bool busy)
    {
        lock (_gate)
        {
            if (_hosts.TryGetValue(feature, out var host))
            {
                host.Busy = busy;
                host.LastUsed = DateTime.UtcNow;
            }
        }
    }

    private static async Task DisposeHostAsync(AgentHost host)
    {
        host.Stopping = true;
        try
        {
            await host.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (!host.Process.HasExited)
            {
                host.Process.Kill(true);
                host.Process.WaitForExit(2000);
            }
        }
        catch
        {
            // 忽略
        }

        host.Process.Dispose();
    }

    private static string ResolveAgentPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Kojima.Agents.exe"),
            Path.Combine(baseDir, "agents", "Kojima.Agents.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Kojima.Agents", "bin", "Debug", "net10.0-windows", "Kojima.Agents.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Kojima.Agents", "bin", "Release", "net10.0-windows", "Kojima.Agents.exe")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    public async ValueTask DisposeAsync()
    {
        List<AgentHost> hosts;
        lock (_gate)
        {
            hosts = _hosts.Values.ToList();
            _hosts.Clear();
        }

        foreach (var host in hosts)
        {
            await DisposeHostAsync(host).ConfigureAwait(false);
        }
    }

    private sealed class AgentHost(string feature, Process process, AgentPipeClient client)
    {
        public string Feature { get; } = feature;
        public Process Process { get; } = process;
        public AgentPipeClient Client { get; } = client;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool Busy { get; set; }
        public bool Stopping { get; set; }
    }
}
