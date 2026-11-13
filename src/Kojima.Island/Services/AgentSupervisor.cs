using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Plugins;

namespace Kojima.Island.Services;

/// <summary>子进程代理：按需启动、空闲回收、请求/事件转发。</summary>
public sealed class AgentSupervisor : IAsyncDisposable
{
    private readonly Dictionary<string, AgentHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly int _shellPid = Environment.ProcessId;
    private readonly string _hostPath;
    private int _idleSeconds;
    private readonly Dictionary<string, int> _startFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    public AgentSupervisor(int idleSeconds = 90)
    {
        _idleSeconds = Math.Clamp(idleSeconds, 0, 3600);
        _hostPath = ResolveHostPath();
    }

    public void SetIdleSeconds(int seconds) => _idleSeconds = Math.Clamp(seconds, 0, 3600);

    /// <summary>子进程事件（feature, eventName, data）。</summary>
    public event Action<string, string, JsonElement>? EventReceived;

    /// <summary>子进程状态变化（feature, running）。</summary>
    public event Action<string, bool>? StateChanged;

    /// <summary>连续启动/运行失败达到阈值时通知界面（配置了“后台功能异常”提醒）。</summary>
    public event Action<string, string>? Failed;

    public string AgentPath => _hostPath;

    public bool IsRunning(string feature)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(feature, out var host) && Alive(host);
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
            if (_hosts.TryGetValue(feature, out var ready) && Alive(ready))
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
                if (_hosts.TryGetValue(feature, out var ready2) && Alive(ready2))
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
            if (_hosts.TryGetValue(feature, out var existing) && Alive(existing))
            {
                return existing;
            }

            if (_retryAfter.TryGetValue(feature, out var until) && DateTime.UtcNow < until)
            {
                var left = Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds));
                throw new InvalidOperationException($"子进程 {feature} 连续启动失败，{left} 秒后自动重试");
            }
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
        if (!File.Exists(_hostPath))
        {
            RecordStartFailure(feature, "找不到插件宿主：" + _hostPath);
            throw new FileNotFoundException("找不到插件宿主", _hostPath);
        }

        var plugin = ResolvePlugin(feature);
        var pipeName = IpcJson.PipeName(feature, _shellPid);
        var startInfo = new ProcessStartInfo(_hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = plugin.Directory,
        };
        startInfo.ArgumentList.Add("--plugin");
        startInfo.ArgumentList.Add(plugin.ManifestPath);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(_shellPid.ToString());
        startInfo.ArgumentList.Add("--idle");
        startInfo.ArgumentList.Add(_idleSeconds.ToString());

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("插件进程启动失败");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AppLog.Warn($"插件 {feature} stderr: {e.Data}");
            }
        };
        process.BeginErrorReadLine();
        var client = await AgentPipeClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);

        var host = new AgentHost(feature, process, client);
        host.Process.EnableRaisingEvents = true;
        host.Process.Exited += (_, _) => OnHostExited(host);
        client.EventReceived += (name, data) => EventReceived?.Invoke(feature, name, data);
        client.Closed += () =>
        {
            AppLog.Warn($"插件断开：{feature}");
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

        AppLog.Info($"插件已启动：{feature} pid={process.Id}");
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
                if (!Alive(host))
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
            AppLog.Info(Alive(host)
                ? $"空闲回收子进程：{host.Feature}"
                : $"清理已退出的子进程：{host.Feature}");
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
        if (failures >= 3)
        {
            Failed?.Invoke(feature, reason);
        }
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

    private static bool Alive(AgentHost host)
    {
        try
        {
            return !host.Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // 进程对象已释放：按已退出处理，避免把“没有关联进程”抛给调用方。
            return false;
        }
    }

    private async Task DisposeHostAsync(AgentHost host)
    {
        host.Stopping = true;
        lock (_gate)
        {
            if (_hosts.TryGetValue(host.Feature, out var current) && ReferenceEquals(current, host))
            {
                _hosts.Remove(host.Feature);
            }
        }

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
            if (Alive(host))
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

    private static string ResolveHostPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Kojima.PluginHost.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Kojima.PluginHost", "bin", "Debug", "net10.0-windows10.0.19041.0", "Kojima.PluginHost.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Kojima.PluginHost", "bin", "Release", "net10.0-windows10.0.19041.0", "Kojima.PluginHost.exe")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static PluginDescriptor ResolvePlugin(string feature)
    {
        var project = feature switch
        {
            "monitor" => "Kojima.Plugin.Monitor",
            "usage" => "Kojima.Plugin.Usage",
            "media" => "Kojima.Plugin.Media",
            "weather" => "Kojima.Plugin.Weather",
            "stocks" => "Kojima.Plugin.Stocks",
            "clipboard" => "Kojima.Plugin.Clipboard",
            "translate" => "Kojima.Plugin.Translate",
            "messages" => "Kojima.Plugin.Messages",
            "spectrum" => "Kojima.Plugin.Spectrum",
            "updates" => "Kojima.Plugin.Updates",
            "controls" => "Kojima.Plugin.Controls",
            _ => throw new InvalidOperationException("未知插件：" + feature),
        };

        var baseDir = AppContext.BaseDirectory;
        var repository = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "plugins", feature, "plugin.json"),
        };
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            candidates.Add(Path.Combine(
                repository,
                "plugins",
                project,
                "bin",
                configuration,
                "net10.0-windows10.0.19041.0",
                "plugin.json"));
        }

        var manifest = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"找不到 {feature} 插件；请重新安装或单独构建 {project}");
        return PluginCatalog.Load(manifest);
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
