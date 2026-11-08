using System.Management;
using Kojima.Core.Config;

namespace Kojima.Agents.Native;

/// <summary>外部硬件监控软件（LibreHardwareMonitor / OpenHardwareMonitor）的 WMI 数据。</summary>
internal static class HardwareSensors
{
    public const string LibreNamespace = @"root\LibreHardwareMonitor";
    public const string OpenNamespace = @"root\OpenHardwareMonitor";

    /// <summary>检测本机可用的外部监控命名空间，返回 lhm / ohm 列表。</summary>
    public static IReadOnlyList<string> Detect()
    {
        var found = new List<string>();
        if (HasSensors(LibreNamespace))
        {
            found.Add("lhm");
        }

        if (HasSensors(OpenNamespace))
        {
            found.Add("ohm");
        }

        return found;
    }

    private static bool HasSensors(string ns)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(ns, "SELECT Identifier FROM Sensor");
            using var results = searcher.Get();
            foreach (ManagementBaseObject _ in results)
            {
                return true;
            }
        }
        catch
        {
            // 命名空间不存在或权限不足
        }

        return false;
    }

    /// <summary>读取 CPU / GPU 温度与整机功耗；失败时返回 null。</summary>
    public static (double? Cpu, double? Gpu, double? Power, string? Source) Read(string preference)
    {
        var order = preference switch
        {
            "lhm" => new[] { LibreNamespace },
            "ohm" => new[] { OpenNamespace },
            _ => new[] { LibreNamespace, OpenNamespace },
        };

        foreach (var ns in order)
        {
            try
            {
                var (cpu, gpu, power) = Query(ns);
                if (cpu is not null || gpu is not null || power is not null)
                {
                    return (cpu, gpu, power, ns == LibreNamespace ? "LibreHardwareMonitor" : "OpenHardwareMonitor");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取外部传感器失败（{ns}）：{ex.Message}");
            }
        }

        return (null, null, null, null);
    }

    private static (double? Cpu, double? Gpu, double? Power) Query(string ns)
    {
        double? cpu = null;
        double? gpu = null;
        double? power = null;

        using var searcher = new ManagementObjectSearcher(ns,
            "SELECT Name, SensorType, Value FROM Sensor WHERE SensorType='Temperature' OR SensorType='Power'");
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            var name = item["Name"]?.ToString() ?? "";
            var type = item["SensorType"]?.ToString() ?? "";
            if (item["Value"] is not { } raw)
            {
                continue;
            }

            double value;
            try
            {
                value = Convert.ToDouble(raw);
            }
            catch
            {
                continue;
            }

            if (value <= 0 || value > 300)
            {
                continue;
            }

            var isCpu = name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Package", StringComparison.OrdinalIgnoreCase);
            var isGpu = name.Contains("GPU", StringComparison.OrdinalIgnoreCase);

            if (type == "Temperature")
            {
                if (isGpu && name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                {
                    gpu ??= value;
                }
                else if (isGpu)
                {
                    gpu ??= value;
                }
                else if (isCpu && name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                {
                    cpu = value;
                }
                else if (isCpu)
                {
                    cpu ??= value;
                }
            }
            else if (type == "Power" && (isCpu || isGpu))
            {
                power = (power ?? 0) + value;
            }
        }

        return (cpu, gpu, power);
    }
}
