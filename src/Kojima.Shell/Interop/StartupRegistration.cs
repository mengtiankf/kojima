using System.IO;
using Microsoft.Win32;

namespace Kojima.Shell.Interop;

/// <summary>开机启动：写入 HKCU 的 Run 键，不需要管理员权限。</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kojima";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                   value.Contains("Kojima", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) ||
                    !Path.GetFileName(exe).Equals("Kojima.Shell.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // 开发环境（dotnet run / 测试宿主）不写启动项，避免把调试路径固化。
                    return;
                }

                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Core.Config.AppLog.Warn("更新开机启动失败：" + ex.Message);
        }
    }
}
