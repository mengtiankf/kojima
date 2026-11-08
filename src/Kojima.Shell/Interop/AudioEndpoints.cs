using System.Runtime.InteropServices;

namespace Kojima.Shell.Interop;

/// <summary>系统默认输出设备的音量 / 静音控制（Core Audio，手写 COM 接口）。</summary>
public sealed class AudioEndpoints
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid context);
        int SetMasterVolumeLevelScalar(float level, Guid context);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IAudioEndpointVolume? Open()
    {
        var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
        if (type is null)
        {
            return null;
        }

        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0)
        {
            return null;
        }

        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var instance) != 0)
        {
            return null;
        }

        return instance as IAudioEndpointVolume;
    }

    public bool IsMuted()
    {
        var volume = Open();
        return volume is not null && volume.GetMute(out var mute) == 0 && mute;
    }

    /// <summary>切换静音并返回切换后的状态。</summary>
    public bool ToggleMute()
    {
        var volume = Open();
        if (volume is null)
        {
            return false;
        }

        if (volume.GetMute(out var mute) != 0)
        {
            return false;
        }

        var next = !mute;
        volume.SetMute(next, Guid.Empty);
        return next;
    }

    public float GetVolume()
    {
        var volume = Open();
        return volume is not null && volume.GetMasterVolumeLevelScalar(out var level) == 0 ? level : 0f;
    }

    public void SetVolume(float level)
    {
        var volume = Open();
        volume?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), Guid.Empty);
    }
}
