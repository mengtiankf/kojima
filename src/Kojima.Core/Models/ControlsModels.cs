namespace Kojima.Core.Models;

/// <summary>由 controls 子进程返回的系统音量 / 亮度状态。</summary>
public sealed record ControlsState(double Volume, bool Muted, int Brightness, bool BrightnessSupported);
