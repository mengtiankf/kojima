using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Shell.Services;

/// <summary>音量与亮度的薄代理：设备访问在 controls 子进程，这里只做缓存与防抖。</summary>
public sealed class ControlsProxy : IDisposable
{
    private readonly AgentSupervisor _agents;
    private readonly object _gate = new();
    private Timer? _volumeTimer;
    private Timer? _brightnessTimer;

    public ControlsProxy(AgentSupervisor agents)
    {
        _agents = agents;
    }

    public double Volume { get; private set; }

    public bool Muted { get; private set; }

    public int Brightness { get; private set; } = 50;

    public bool BrightnessSupported { get; private set; } = true;

    public event Action? Changed;

    public async Task<ControlsState?> LoadAsync()
    {
        try
        {
            var state = await _agents.RequestAsync<ControlsState>("controls", "controls.state", null, 10000);
            if (state is not null)
            {
                Apply(state);
            }

            return state;
        }
        catch (Exception ex)
        {
            AppLog.Warn("读取音量/亮度失败：" + ex.Message);
            return null;
        }
    }

    public void SetVolume(double level)
    {
        Volume = Math.Clamp(level, 0, 1);
        Changed?.Invoke();
        Schedule(ref _volumeTimer, () => SendAsync("controls.volume", new { level = Volume }));
    }

    public void SetBrightness(int percent)
    {
        Brightness = Math.Clamp(percent, 0, 100);
        Changed?.Invoke();
        Schedule(ref _brightnessTimer, () => SendAsync("controls.brightness", new { percent = Brightness }));
    }

    public async Task<bool?> ToggleMuteAsync()
    {
        try
        {
            var muted = await _agents.RequestAsync<bool>("controls", "controls.mute.toggle", null, 10000);
            Muted = muted;
            Changed?.Invoke();
            return muted;
        }
        catch (Exception ex)
        {
            AppLog.Warn("切换静音失败：" + ex.Message);
            return null;
        }
    }

    private void Apply(ControlsState state)
    {
        Volume = Math.Clamp(state.Volume, 0, 1);
        Muted = state.Muted;
        Brightness = state.Brightness;
        BrightnessSupported = state.BrightnessSupported;
        Changed?.Invoke();
    }

    private void Schedule(ref Timer? timer, Func<Task> send)
    {
        lock (_gate)
        {
            timer ??= new Timer(_ => _ = send(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(80, Timeout.Infinite);
        }
    }

    private async Task SendAsync(string method, object args)
    {
        try
        {
            await _agents.RequestAsync<ControlsState>("controls", method, args, 10000);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{method} 调用失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _volumeTimer?.Dispose();
            _brightnessTimer?.Dispose();
            _volumeTimer = null;
            _brightnessTimer = null;
        }
    }
}
