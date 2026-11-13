using System.Runtime.InteropServices;

namespace Kojima.Agents.Native;

// 帧率实现从 BrimDeck（Apache-2.0）移植；这里补齐它依赖的少量辅助类型，
// 进程表用 Toolhelp32 读取，用于把浏览器/Electron 的 GPU 子进程帧数归到前台程序。
internal static class Why
{
    public const string Waiting = "等待第二次读数";
}

public enum SensorState { Unknown, Found, None }

public sealed record DisplayInfo(IntPtr Handle, double? Refresh);

internal static class Displays
{
    public static IReadOnlyList<DisplayInfo> Current() => [];
}

internal static class ProcessSnapshot
{
    private const uint SnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static Dictionary<int, (int Parent, string File)> Read()
    {
        var result = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? "");
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
