using System.Runtime.InteropServices;
using System.Windows;

namespace Kojima.Shell.Interop;

/// <summary>显示器的 DIP 工作区，供岛体定位与拖拽吸附使用。</summary>
internal sealed record DisplayInfo(string DeviceName, Rect Work, bool Primary)
{
    private const uint MONITORINFOF_PRIMARY = 1;

    public static IReadOnlyList<DisplayInfo> All()
    {
        var list = new List<DisplayInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpi = 96u;
            try
            {
                if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                {
                    dpi = dpiX;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            var scale = 96.0 / dpi;
            list.Add(new DisplayInfo(
                info.szDevice ?? "",
                new Rect(
                    info.rcWork.Left * scale,
                    info.rcWork.Top * scale,
                    info.rcWork.Width * scale,
                    info.rcWork.Height * scale),
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public static DisplayInfo? Find(string? deviceName)
    {
        var all = All();
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = all.FirstOrDefault(d =>
                string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return all.FirstOrDefault(d => d.Primary) ?? all.FirstOrDefault();
    }

    public static DisplayInfo? At(double x, double y)
    {
        return All().FirstOrDefault(d =>
                   x >= d.Work.Left && x < d.Work.Right && y >= d.Work.Top && y < d.Work.Bottom)
               ?? Find(null);
    }
}
