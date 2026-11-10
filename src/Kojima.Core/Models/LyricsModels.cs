namespace Kojima.Core.Models;

/// <summary>一行歌词；TimeMs 为 0 表示纯文本歌词（无时间轴）。</summary>
public sealed record LyricLine(long TimeMs, string Text);

/// <summary>歌词结果：来源、原始 LRC 与解析后的行。</summary>
public sealed record LyricsResult(string Source, string? Raw, IReadOnlyList<LyricLine> Lines)
{
    public bool HasTimeline => Lines.Any(line => line.TimeMs > 0);
}
