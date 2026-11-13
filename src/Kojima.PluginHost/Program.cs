using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Kojima.Agents.Features;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Plugins;

namespace Kojima.PluginHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        IAgentFeature? feature = null;
        AgentPipeServer? server = null;
        try
        {
            var options = LaunchOptions.Parse(args);
            var plugin = PluginCatalog.Load(options.Plugin);
            var context = new PluginLoadContext(plugin.AssemblyPath);
            var assembly = context.LoadFromAssemblyPath(plugin.AssemblyPath);
            var entryType = assembly.GetType(plugin.Manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IAgentFeature).IsAssignableFrom(entryType) || entryType.IsAbstract)
            {
                throw new InvalidOperationException("插件入口未实现 IAgentFeature：" + entryType.FullName);
            }

            feature = Activator.CreateInstance(entryType) as IAgentFeature
                ?? throw new InvalidOperationException("无法创建插件入口：" + entryType.FullName);
            AppLog.Init("plugin-" + plugin.Manifest.Id);
            if (options.SelfTest)
            {
                Console.WriteLine($"OK {plugin.Manifest.Id} {plugin.Manifest.Version} {entryType.FullName}");
                return 0;
            }

            server = new AgentPipeServer(options.Pipe, async (method, args) =>
            {
                var result = await feature.HandleAsync(method, args).ConfigureAwait(false);
                return result is null ? null : JsonSerializer.SerializeToElement(result, IpcJson.Options);
            });
            feature.Attach(server);
            server.Start();
            AppLog.Info($"插件已启动：{plugin.Manifest.Id} {plugin.Manifest.Version} pipe={options.Pipe} parent={options.Parent}");

            var idle = options.IdleSeconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(options.IdleSeconds);
            while (true)
            {
                Thread.Sleep(1000);
                if (options.Parent > 0 && !IsAlive(options.Parent))
                {
                    break;
                }

                if (!feature.Busy && DateTime.UtcNow - server.LastActivity > idle)
                {
                    break;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("插件启动失败：" + ex);
            AppLog.Error("插件启动失败：" + ex);
            return 2;
        }
        finally
        {
            try
            {
                feature?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private sealed record LaunchOptions(string Plugin, string Pipe, int Parent, int IdleSeconds, bool SelfTest)
    {
        public static LaunchOptions Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "1";
                map[key] = value;
            }

            if (!map.TryGetValue("plugin", out var plugin) || string.IsNullOrWhiteSpace(plugin))
            {
                throw new ArgumentException("缺少 --plugin <plugin.json>");
            }

            var pipe = map.GetValueOrDefault("pipe", "kojima-plugin-0");
            _ = int.TryParse(map.GetValueOrDefault("parent", "0"), out var parent);
            _ = int.TryParse(map.GetValueOrDefault("idle", "90"), out var idle);
            return new LaunchOptions(plugin, pipe, parent, idle, map.ContainsKey("selftest"));
        }
    }
}

internal sealed class PluginLoadContext(string assemblyPath) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is "Kojima.Core" or "Kojima.PluginHost")
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
