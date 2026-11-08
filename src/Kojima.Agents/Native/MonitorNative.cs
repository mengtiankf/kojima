using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace Kojima.Agents.Native;

/// <summary>Win32 探针：CPU 时间、内存、电源、电池。全部失败返回默认值，从不抛给调用方。</summary>
internal static class MonitorNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    /// <summary>系统累计 CPU 时间片（idle/kernel/user），用于做差求占用。</summary>
    public static bool TryCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        if (!GetSystemTimes(out var i, out var k, out var u))
        {
            return false;
        }

        idle = i.Value;
        kernel = k.Value;
        user = u.Value;
        return true;
    }

    public static (ulong Total, ulong Avail, ulong PageTotal, ulong PageAvail) Memory()
    {
        try
        {
            var status = new MemoryStatusEx();
            return GlobalMemoryStatusEx(status)
                ? (status.TotalPhys, status.AvailPhys, status.TotalPageFile, status.AvailPageFile)
                : (0, 0, 0, 0);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    public static (int? Percent, bool Plugged, int? SecondsLeft) Power()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return (null, false, null);
            }

            var percent = status.BatteryLifePercent == 255 ? (int?)null : status.BatteryLifePercent;
            var seconds = status.BatteryLifeTime < 0 ? (int?)null : status.BatteryLifeTime;
            return (percent, status.ACLineStatus == 1, seconds);
        }
        catch
        {
            return (null, false, null);
        }
    }

    public static double UptimeSeconds()
    {
        try
        {
            return GetTickCount64() / 1000.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>WMI：每逻辑核占用百分比，实例 Name 为 "0".."N-1"、"_Total"。</summary>
    public static double[] PerCoreCpu(out double total)
    {
        total = 0;
        var list = new List<(int Index, double Value)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                var name = item["Name"] as string ?? "";
                var value = ToDouble(item["PercentProcessorTime"]);
                if (name == "_Total")
                {
                    total = value;
                    continue;
                }

                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    list.Add((index, value));
                }
            }
        }
        catch
        {
            // WMI 不可用时返回空数组，调用方用 GetSystemTimes 的结果兜底
        }

        return list.OrderBy(p => p.Index).Select(p => Math.Clamp(p.Value, 0, 100)).ToArray();
    }

    /// <summary>WMI：CPU 当前主频（MHz）。</summary>
    public static double CpuClockMhz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MaxClockSpeed, CurrentClockSpeed FROM Win32_Processor");
            var best = 0.0;
            foreach (ManagementObject item in searcher.Get())
            {
                var current = ToDouble(item["CurrentClockSpeed"]);
                var max = ToDouble(item["MaxClockSpeed"]);
                best = Math.Max(best, current > 0 ? current : max);
            }

            return best;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>WMI：ACPI 热区温度（摄氏度）。桌面机常见 27–40℃，笔记本更准。</summary>
    public static double? ThermalCelsius()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? best = null;
            foreach (ManagementObject item in searcher.Get())
            {
                var raw = ToDouble(item["CurrentTemperature"]);
                if (raw <= 0)
                {
                    continue;
                }

                var celsius = raw / 10.0 - 273.15;
                if (celsius is > 1 and < 130)
                {
                    best = best is null ? celsius : Math.Max(best.Value, celsius);
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WMI：各盘符的读写速率（字节/秒，系统已按秒归一）。</summary>
    public static Dictionary<string, (double Read, double Write)> DiskRates()
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk");
            foreach (ManagementObject item in searcher.Get())
            {
                var name = (item["Name"] as string ?? "").TrimEnd(':');
                if (name.Length == 0 || name == "_Total")
                {
                    continue;
                }

                map[name] = (ToDouble(item["DiskReadBytesPersec"]), ToDouble(item["DiskWriteBytesPersec"]));
            }
        }
        catch
        {
            // 忽略
        }

        return map;
    }

    /// <summary>WMI：页面文件用量（字节）。返回 (已用, 总量)。</summary>
    public static (ulong Used, ulong Total) PageFile()
    {
        ulong used = 0;
        ulong total = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            foreach (ManagementObject item in searcher.Get())
            {
                var allocated = ToDouble(item["AllocatedBaseSize"]);
                var current = ToDouble(item["CurrentUsage"]);
                if (allocated > 0)
                {
                    total += (ulong)allocated * 1024UL * 1024UL;
                    used += (ulong)Math.Clamp(current, 0, allocated) * 1024UL * 1024UL;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return (used, total);
    }

    /// <summary>nvidia-smi 查询（无 NVIDIA 显卡时返回空）。</summary>
    public static List<(string Name, double Util, ulong MemUsed, ulong MemTotal, double? Temp, double? Power)> NvidiaGpus()
    {
        var list = new List<(string, double, ulong, ulong, double?, double?)>();
        try
        {
            var start = new ProcessStartInfo("nvidia-smi")
            {
                ArgumentList =
                {
                    "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw",
                    "--format=csv,noheader,nounits",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return list;
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略
                }

                return list;
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 6)
                {
                    continue;
                }

                list.Add((
                    parts[0],
                    ToDouble(parts[1]),
                    (ulong)Math.Max(0, ToDouble(parts[2])) * 1024UL * 1024UL,
                    (ulong)Math.Max(0, ToDouble(parts[3])) * 1024UL * 1024UL,
                    ToDoubleOrNull(parts[4]),
                    ToDoubleOrNull(parts[5])));
            }
        }
        catch
        {
            // 没有 nvidia-smi
        }

        return list;
    }

    public static double ToDouble(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value switch
            {
                string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
                _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return 0;
        }
    }

    public static double? ToDoubleOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
