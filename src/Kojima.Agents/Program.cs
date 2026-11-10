using System.Diagnostics;
using System.Text.Json;
using Kojima.Agents.Features;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Models;
using Kojima.Core.Themes;

namespace Kojima.Agents;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var options = LaunchOptions.Parse(argv);
        if (options.SelfTest)
        {
            if (options.SelfTestTarget.Equals("usage", StringComparison.OrdinalIgnoreCase))
            {
                return RunUsageSelfTest();
            }

            if (options.SelfTestTarget.Equals("themes", StringComparison.OrdinalIgnoreCase))
            {
                return RunThemeSelfTest();
            }

            return RunSelfTest();
        }

        if (!FeatureIds.TryParse(options.Feature, out var featureId))
        {
            Console.Error.WriteLine($"未知功能：{options.Feature}");
            return 2;
        }

        AppLog.Init("agent-" + FeatureIds.Key(featureId));

        IAgentFeature feature = FeatureIds.Key(featureId) switch
        {
            "monitor" => new MonitorFeature(),
            "usage" => new UsageFeature(),
            "weather" => new WeatherFeature(),
            "stocks" => new StocksFeature(),
            "translate" => new TranslateFeature(),
            "clipboard" => new ClipboardFeature(),
            "media" => new MediaFeature(),
            _ => new NoopFeature(),
        };

        var server = new AgentPipeServer(options.Pipe, async (method, args) =>
        {
            var result = await feature.HandleAsync(method, args).ConfigureAwait(false);
            return result is null ? null : JsonSerializer.SerializeToElement(result, IpcJson.Options);
        });

        feature.Attach(server);
        server.Start();
        AppLog.Info($"已启动：feature={FeatureIds.Key(featureId)} pipe={options.Pipe} parent={options.Parent}");

        var idle = TimeSpan.FromSeconds(Math.Max(15, options.IdleSeconds));
        while (true)
        {
            Thread.Sleep(1000);

            if (options.Parent > 0 && !IsAlive(options.Parent))
            {
                AppLog.Info("主进程已退出，子进程结束");
                break;
            }

            if (!feature.Busy && DateTime.UtcNow - server.LastActivity > idle)
            {
                AppLog.Info($"空闲超过 {idle.TotalSeconds:0} 秒，子进程退出");
                break;
            }
        }

        try
        {
            feature.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 退出阶段忽略
        }

        return 0;
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

    /// <summary>无 UI 自检：采两帧监控数据，打印关键字段。</summary>
    private static int RunThemeSelfTest()
    {
        ThemeCatalog.Reload();
        var failures = 0;
        Console.WriteLine("id                             dark  text2/card  text3/card  text/surface");
        foreach (var palette in ThemeCatalog.All.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var surface = palette.Surface with { A = 255 };
            var card = new Rgba(palette.Text.R, palette.Text.G, palette.Text.B,
                (byte)(palette.Dark ? 0x3C : 0x22)).Over(surface);
            var c2 = Rgba.ContrastRatio(palette.Text2, card);
            var c3 = Rgba.ContrastRatio(palette.Text3, card);
            var c1 = Rgba.ContrastRatio(palette.Text, surface);
            var ok = c2 >= 4.5 && c3 >= 3.0;
            if (!ok)
            {
                failures++;
            }

            Console.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{palette.Id,-30} {palette.Dark,-5} {c2,10:0.00} {c3,11:0.00} {c1,12:0.00} {(ok ? "" : "  <-- 不达标")}"));
        }

        Console.WriteLine(failures == 0 ? "OK 主题对比度全部达标" : $"FAIL {failures} 套主题不达标");
        return failures == 0 ? 0 : 1;
    }

    private static int RunSelfTest()
    {
        try
        {
            var feature = new MonitorFeature();
            var first = feature.HandleAsync("sample", null).GetAwaiter().GetResult() as MonitorSample;
            Thread.Sleep(1400);
            var second = feature.HandleAsync("sample", null).GetAwaiter().GetResult() as MonitorSample;
            if (second is null)
            {
                Console.Error.WriteLine("sample 返回空");
                return 1;
            }

            Console.WriteLine($"cores={second.Cpu.PerCore.Length} cpu={second.Cpu.Percent:0.0}% " +
                              $"freq={second.Cpu.FreqMhz:0}MHz procs={second.Cpu.Processes} threads={second.Cpu.Threads}");
            Console.WriteLine($"mem={second.Mem.Percent:0.0}% {second.Mem.Used / 1048576}MB/{second.Mem.Total / 1048576}MB " +
                              $"swap={second.Mem.SwapPercent:0.0}%");
            foreach (var gpu in second.Gpus)
            {
                Console.WriteLine($"gpu {gpu.Name} util={gpu.Util:0}% vram={gpu.MemUsed / 1048576}/{gpu.MemTotal / 1048576}MB " +
                                  $"temp={gpu.Temp?.ToString("0") ?? "-"} power={gpu.Power?.ToString("0") ?? "-"}");
            }

            Console.WriteLine($"net down={second.Net.DownBps / 1024:0}KB/s up={second.Net.UpBps / 1024:0}KB/s");
            foreach (var disk in second.Disks)
            {
                Console.WriteLine($"disk {disk.Name} {disk.Used / 1073741824}/{disk.Total / 1073741824}GB " +
                                  $"r={disk.ReadBps / 1024:0}KB/s w={disk.WriteBps / 1024:0}KB/s");
            }

            Console.WriteLine($"temp cpu={second.Temp.Cpu?.ToString("0.0") ?? "-"} gpu={second.Temp.Gpu?.ToString("0") ?? "-"} src={second.Temp.Source ?? "-"}");
            Console.WriteLine($"power battery={second.Power.BatteryPercent?.ToString() ?? "-"} plugged={second.Power.Plugged} " +
                              $"left={second.Power.SecondsLeft?.ToString() ?? "-"} drain={second.Power.DrainW?.ToString("0.0") ?? "-"}");
            Console.WriteLine($"uptime={TimeSpan.FromSeconds(second.UptimeSeconds):hh\\:mm\\:ss}");
            foreach (var proc in second.Processes.Take(5))
            {
                Console.WriteLine($"proc {proc.Name} pid={proc.Pid} cpu={proc.Cpu:0.0}% mem={proc.Mem / 1048576}MB");
            }

            var changed = first is not null && Math.Abs(first.Cpu.Percent - second.Cpu.Percent) > 0.001;
            Console.WriteLine(changed ? "OK 采样正常" : "WARN 两帧 CPU 相同");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("自检失败：" + ex);
            return 1;
        }
    }

    /// <summary>无 UI 自检：打印三个本机用量来源的统计。</summary>
    private static int RunUsageSelfTest()
    {
        foreach (var source in new[] { "dsh", "claude", "codex" })
        {
            var sw = Stopwatch.StartNew();
            var snapshot = Usage.LocalUsage.Read(source, DateOnly.FromDateTime(DateTime.Now), DateOnly.FromDateTime(DateTime.Now));
            sw.Stop();
            var today = snapshot.Totals(new DateTimeOffset(DateTime.Today));
            var all = snapshot.Totals(null);
            Console.WriteLine($"{source}: status={snapshot.Status} entries={snapshot.Entries.Count} " +
                              $"today={today.Tokens} all={all.Tokens} cost={all.Cost:0.00} " +
                              $"elapsed={sw.ElapsedMilliseconds}ms note={snapshot.Note}");
            if (snapshot.Status == "error")
            {
                Console.WriteLine("  error: " + snapshot.Error);
            }
        }

        return 0;
    }

    private sealed record LaunchOptions(string Feature, string Pipe, int Parent, int IdleSeconds, bool SelfTest, string SelfTestTarget)
    {
        public static LaunchOptions Parse(string[] argv)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < argv.Length; i++)
            {
                var key = argv[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? argv[++i]
                    : "1";
                map[key[2..]] = value;
            }

            var feature = map.GetValueOrDefault("feature", "monitor");
            var selfTestTarget = map.GetValueOrDefault("selftest", "");
            var pipe = map.GetValueOrDefault("pipe", $"kojima-{feature}-0");
            _ = int.TryParse(map.GetValueOrDefault("parent", "0"), out var parent);
            _ = int.TryParse(map.GetValueOrDefault("idle", "90"), out var idle);
            return new LaunchOptions(feature, pipe, parent, idle, map.ContainsKey("selftest"), selfTestTarget);
        }
    }
}
