namespace Kojima.Core.Models;

public sealed class CpuReading
{
    public double Percent { get; set; }
    public double[] PerCore { get; set; } = [];
    public double FreqMhz { get; set; }
    public int Processes { get; set; }
    public int Threads { get; set; }
}

public sealed class MemReading
{
    public double Percent { get; set; }
    public ulong Used { get; set; }
    public ulong Total { get; set; }
    public double SwapPercent { get; set; }
    public ulong SwapUsed { get; set; }
    public ulong SwapTotal { get; set; }
}

public sealed class GpuReading
{
    public string Name { get; set; } = "";
    public double Util { get; set; }
    public ulong MemUsed { get; set; }
    public ulong MemTotal { get; set; }
    public double? Temp { get; set; }
    public double? Power { get; set; }
}

public sealed class NetReading
{
    public double DownBps { get; set; }
    public double UpBps { get; set; }
    public ulong TotalDown { get; set; }
    public ulong TotalUp { get; set; }
}

public sealed class DiskReading
{
    public string Name { get; set; } = "";
    public string Mount { get; set; } = "";
    public ulong Used { get; set; }
    public ulong Total { get; set; }
    public double ReadBps { get; set; }
    public double WriteBps { get; set; }
}

public sealed class TempReading
{
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    /// <summary>整机/CPU 功耗（瓦），外部监控软件提供时才有。</summary>
    public double? Power { get; set; }
    public string? Source { get; set; }
}

public sealed class PowerReading
{
    public int? BatteryPercent { get; set; }
    public bool Plugged { get; set; }
    public int? SecondsLeft { get; set; }
    public double? DrainW { get; set; }
    /// <summary>电池健康度（满充容量 / 设计容量），桌面机为 null。</summary>
    public double? HealthPercent { get; set; }
}

public sealed class ProcReading
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public double Cpu { get; set; }
    public ulong Mem { get; set; }
}

/// <summary>一帧监控数据。</summary>
public sealed class MonitorSample
{
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.Now;
    public CpuReading Cpu { get; set; } = new();
    public MemReading Mem { get; set; } = new();
    public List<GpuReading> Gpus { get; set; } = [];
    public NetReading Net { get; set; } = new();
    public List<DiskReading> Disks { get; set; } = [];
    public TempReading Temp { get; set; } = new();
    public PowerReading Power { get; set; } = new();
    public List<ProcReading> Processes { get; set; } = [];
    /// <summary>前台程序提交的帧率；没有权限或没有读数时为 null。</summary>
    public FrameReading? Frames { get; set; }
    public double UptimeSeconds { get; set; }
}

public sealed class FrameReading
{
    public double PerSecond { get; set; }
    public string Program { get; set; } = "";
    public double? Refresh { get; set; }
}
