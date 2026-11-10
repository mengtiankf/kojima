namespace Kojima.Core.Models;

/// <summary>剪贴板历史条目（外壳只负责显示，采集在 clipboard 子进程里）。</summary>
public sealed record ClipItem(string Text, DateTime At)
{
    public string TimeText => At.ToString("HH:mm");

    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Trim() is { Length: > 0 } s
        ? s.Length > 120 ? s[..120] + "…" : s
        : "(空白)";
}
