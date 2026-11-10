using Kojima.Core.Models;
using Kojima.Shell.Services;

namespace Kojima.Shell;

/// <summary>演示数据：用于生成 README 示例图，避免暴露真实用量与桌面。</summary>
internal static class DemoData
{
    public static IReadOnlyList<UsageSnapshot> Usage()
    {
        var now = DateTimeOffset.Now;
        var claude = new UsageSnapshot
        {
            Pid = "claude",
            Name = "Claude Code",
            Plan = "Max 20x",
            Scope = "账号",
            Status = "ok",
            Source = "local",
            FetchedAt = now,
            Quotas =
            [
                new Quota { Label = "5 小时额度", UsedPercent = 34, ResetAt = now.AddHours(2).AddMinutes(13) },
                new Quota { Label = "每周额度", UsedPercent = 58, ResetAt = now.AddDays(3).AddHours(5) },
            ],
        };

        var codex = new UsageSnapshot
        {
            Pid = "codex",
            Name = "Codex",
            Plan = "Plus",
            Scope = "账号",
            Status = "ok",
            Source = "local",
            FetchedAt = now,
            Quotas =
            [
                new Quota { Label = "5 小时额度", UsedPercent = 22, ResetAt = now.AddHours(3).AddMinutes(41) },
                new Quota { Label = "每周额度", UsedPercent = 41, ResetAt = now.AddDays(4).AddHours(2) },
            ],
        };

        var deepseek = new UsageSnapshot
        {
            Pid = "dsh",
            Name = "DeepSeek Harness",
            Plan = "本地统计",
            Scope = "本机",
            Status = "ok",
            Source = "local",
            FetchedAt = now,
            Entries =
            [
                new TokenEntry { Key = "demo-1", Time = now.AddDays(-2), Model = "deepseek-v3", Input = 182_000, Output = 21_400 },
                new TokenEntry { Key = "demo-2", Time = now.AddDays(-1), Model = "deepseek-v3", Input = 96_500, Output = 12_800 },
                new TokenEntry { Key = "demo-3", Time = now.AddHours(-1), Model = "deepseek-r1", Input = 208_000, Output = 31_600 },
            ],
            Note = "本地 60 天 · 24 个会话 · 1286 次模型请求",
        };

        return [claude, codex, deepseek];
    }

    public static MonitorSample Monitor()
    {
        double[] cores = [42, 18, 63, 27, 9, 55, 33, 71, 12, 48, 26, 38, 61, 15, 44, 22];
        return new MonitorSample
        {
            Ts = DateTimeOffset.Now,
            Cpu = new CpuReading
            {
                Percent = 38,
                PerCore = cores,
                FreqMhz = 2496,
                Processes = 312,
                Threads = 5480,
            },
            Mem = new MemReading
            {
                Percent = 63,
                Used = 12_884_901_888,
                Total = 20_401_092_608,
                SwapPercent = 2,
            },
            Gpus =
            [
                new GpuReading
                {
                    Name = "NVIDIA GeForce RTX 3050 Ti",
                    Util = 24,
                    MemUsed = 3_355_443_200,
                    MemTotal = 8_589_934_592,
                    Temp = 52,
                    Power = 46,
                },
            ],
            Net = new NetReading { DownBps = 2_411_724, UpBps = 388_300 },
            Disks =
            [
                new DiskReading { Name = "C:", Mount = "C:", Used = 493_921_304_576, Total = 1_000_202_567_680 },
                new DiskReading { Name = "D:", Mount = "D:", Used = 187_904_901_120, Total = 512_110_190_592 },
            ],
            Temp = new TempReading { Cpu = 48, Gpu = 52, Power = 46, Source = "LibreHardwareMonitor" },
            Power = new PowerReading { Plugged = true },
            Processes =
            [
                new ProcReading { Name = "Visual Studio Code", Pid = 1204, Cpu = 9.1, Mem = 1_610_612_736 },
                new ProcReading { Name = "Google Chrome", Pid = 8872, Cpu = 6.3, Mem = 2_147_483_648 },
                new ProcReading { Name = "Cursor", Pid = 4410, Cpu = 3.7, Mem = 987_842_560 },
                new ProcReading { Name = "Explorer", Pid = 2200, Cpu = 2.1, Mem = 268_435_456 },
                new ProcReading { Name = "Kojima", Pid = 9088, Cpu = 0.9, Mem = 184_549_376 },
            ],
            UptimeSeconds = 9_180,
        };
    }

    public static IReadOnlyList<Kojima.Core.Models.MessageItem> Messages() =>
    [
        new("QQ", "qq", "设计组 · 阿岚", DateTime.Now.AddMinutes(-2), "toast"),
        new("微信", "wechat", "妈妈", DateTime.Now.AddMinutes(-18), "toast"),
        new("微信", "wechat", "项目群 · 6 条新消息", DateTime.Now.AddMinutes(-41), "toast"),
        new("邮件", "mail", "", DateTime.Now.AddHours(-2), "toast"),
    ];

    public static WeatherInfo Weather() => new()
    {
        Temperature = 21,
        FeelsLike = 20,
        Humidity = 62,
        WindKph = 9,
        Description = "多云",
        City = "本地",
        Glyph = "\uE75E",
        UpdatedAt = DateTimeOffset.Now,
        Days =
        [
            new WeatherDay { Date = DateOnly.FromDateTime(DateTime.Now), Max = 24, Min = 15, Description = "多云", Glyph = "\uE75E", Code = 119 },
            new WeatherDay { Date = DateOnly.FromDateTime(DateTime.Now.AddDays(1)), Max = 26, Min = 17, Description = "晴", Glyph = "\uE706", Code = 113 },
            new WeatherDay { Date = DateOnly.FromDateTime(DateTime.Now.AddDays(2)), Max = 22, Min = 14, Description = "小雨", Glyph = "\uE9C5", Code = 296 },
        ],
    };

    /// <summary>演示用的播放信息。</summary>
    public static Kojima.Shell.Services.MediaInfo Media() => new(
        "夜航星", "不才", "演示专辑", "demo", true,
        TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(224), null);
}
