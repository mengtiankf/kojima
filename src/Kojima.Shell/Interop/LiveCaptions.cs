using System.Runtime.Versioning;

namespace Kojima.Shell.Interop;

/// <summary>
/// 唤起 Windows 11 的实时字幕（Live Captions）。系统没有公开的启动 API，
/// 与用户手动操作一致，转发 Win+Ctrl+L；旧系统回退到辅助功能设置页。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LiveCaptions
{
    private const int MinimumBuild = 22621;

    public static bool Supported => Environment.OSVersion.Version.Build >= MinimumBuild;

    /// <summary>切换实时字幕；返回 false 表示当前系统不支持，调用方应回退到设置页。</summary>
    public static bool Toggle()
    {
        if (!Supported)
        {
            return false;
        }

        try
        {
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_L, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_L, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("唤起实时字幕失败：" + ex.Message);
            return false;
        }
    }
}
