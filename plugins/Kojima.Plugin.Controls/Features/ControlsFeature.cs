using System.Text.Json;
using Kojima.Agents.Controls;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>系统音量与屏幕亮度：设备访问放在子进程，主进程只发命令。</summary>
public sealed class ControlsFeature : IAgentFeature
{
    private readonly AudioEndpoints _audio = new();

    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method)
        {
            case "controls.state":
                return Task.FromResult<object?>(State());

            case "controls.volume":
                _audio.SetVolume((float)ReadDouble(args, "level", 0));
                return Task.FromResult<object?>(State());

            case "controls.mute":
                _audio.SetMute(ReadBool(args, "muted", false));
                return Task.FromResult<object?>(State());

            case "controls.mute.toggle":
                return Task.FromResult<object?>(_audio.ToggleMute());

            case "controls.brightness":
                BrightnessService.Set((int)Math.Round(ReadDouble(args, "percent", 50)));
                return Task.FromResult<object?>(State());

            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private ControlsState State()
    {
        var brightness = BrightnessService.Supported ? BrightnessService.Get() : 0;
        return new ControlsState(
            Math.Round(_audio.GetVolume(), 3),
            _audio.IsMuted(),
            brightness,
            BrightnessService.Supported);
    }

    private static double ReadDouble(JsonElement? args, string name, double fallback)
    {
        return args is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty(name, out var value)
            && value.TryGetDouble(out var number)
            ? number
            : fallback;
    }

    private static bool ReadBool(JsonElement? args, string name, bool fallback)
    {
        return args is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }
}
