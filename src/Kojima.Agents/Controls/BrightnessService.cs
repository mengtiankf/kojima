using System.Management;
using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>屏幕亮度：WMI root\wmi 的 WmiMonitorBrightness(Methods)。</summary>
public static class BrightnessService
{
    public static bool Supported { get; private set; } = true;

    public static int Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                return Convert.ToInt32(item["CurrentBrightness"]);
            }
        }
        catch (Exception ex)
        {
            Supported = false;
            AppLog.Warn("读取亮度失败：" + ex.Message);
        }

        return 50;
    }

    public static void Set(int percent)
    {
        try
        {
            var value = Math.Clamp(percent, 0, 100);
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                item.InvokeMethod("WmiSetBrightness", [uint.MaxValue, (byte)value]);
            }
        }
        catch (Exception ex)
        {
            Supported = false;
            AppLog.Warn("设置亮度失败：" + ex.Message);
        }
    }
}
