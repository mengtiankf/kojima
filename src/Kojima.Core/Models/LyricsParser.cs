using System.Net;
using System.Text.RegularExpressions;

namespace Kojima.Core.Models;

/// <summary>LRC 歌词解析：支持 [mm:ss]、[mm:ss.ff]、[mm:ss.xxx]，一行多个时间戳。</summary>
public static partial class LyricsParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    public static LyricsResult? Parse(string source, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = new List<LyricLine>();
        foreach (var rawLine in raw.Replace("\r", "").Split('\n'))
        {
            var matches = Stamp().Matches(rawLine);
            var text = Stamp().Replace(rawLine, "").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (matches.Count == 0)
            {
                lines.Add(new LyricLine(0, WebUtility.HtmlDecode(text)));
                continue;
            }

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
                var millis = fraction.Length switch
                {
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    _ => int.Parse(fraction),
                };
                lines.Add(new LyricLine(minutes * 60_000L + seconds * 1000L + millis, WebUtility.HtmlDecode(text)));
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        if (lines.Any(line => line.TimeMs > 0))
        {
            lines = lines.Where(line => line.TimeMs > 0).OrderBy(line => line.TimeMs).ToList();
        }

        return new LyricsResult(source, raw, lines);
    }
}
