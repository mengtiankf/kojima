using System.Net;
using System.Text;
using System.Text.Json;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

internal static partial class LyricsProvider
{
    private static async Task<LyricsResult?> QqAsync(string title, string artist, long durationMs, CancellationToken ct)
    {
        var search = await GetJsonAsync(
            "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?format=json&p=1&n=20&w=" +
            Uri.EscapeDataString(title + " " + artist), "https://y.qq.com/", ct);
        if (search is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? mid = null;
        foreach (var candidate in list.EnumerateArray())
        {
            var candidateMid = String(candidate, "songmid");
            if (candidateMid.Length is < 8 or > 32 ||
                !Matches(title, String(candidate, "songname"), durationMs, Number(candidate, "interval")))
            {
                continue;
            }
            mid = candidateMid;
            break;
        }
        if (mid is null)
        {
            return null;
        }

        var payload = JsonSerializer.Serialize(new
        {
            comm = new { ct = 24, cv = 0 },
            lyric = new
            {
                module = "music.musichallSong.PlayLyricInfo",
                method = "GetPlayLyricInfo",
                param = new { songMID = mid },
            },
        });
        var response = await GetJsonAsync(
            "https://u.y.qq.com/cgi-bin/musicu.fcg?data=" + Uri.EscapeDataString(payload), "https://y.qq.com/", ct);
        if (response is { ValueKind: JsonValueKind.Object } result &&
            result.TryGetProperty("lyric", out var lyric) &&
            lyric.TryGetProperty("data", out var content))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(String(content, "lyric")));
                var parsed = LyricsParser.Parse("QQ 音乐", WebUtility.HtmlDecode(raw));
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (FormatException)
            {
            }
        }

        var legacy = await GetJsonAsync(
            "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=" + mid + "&format=json&nobase64=1",
            "https://y.qq.com/", ct);
        return legacy is { ValueKind: JsonValueKind.Object } old
            ? LyricsParser.Parse("QQ 音乐", WebUtility.HtmlDecode(String(old, "lyric")))
            : null;
    }

    private static async Task<LyricsResult?> KugouAsync(string title, string artist, long durationMs, CancellationToken ct)
    {
        var keyword = Uri.EscapeDataString(artist + " - " + title);
        var search = await GetJsonAsync(
            $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={keyword}&duration={durationMs}&hash=",
            null, ct);
        if (search is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            var id = String(candidate, "id");
            var key = String(candidate, "accesskey");
            if (id.Length == 0 || key.Length == 0 ||
                !Matches(title, String(candidate, "song"), durationMs, Number(candidate, "duration") / 1000))
            {
                continue;
            }

            var reply = await GetJsonAsync(
                $"https://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={key}&fmt=lrc&charset=utf8",
                null, ct);
            if (reply is not { ValueKind: JsonValueKind.Object } value)
            {
                continue;
            }
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(String(value, "content")));
                var parsed = LyricsParser.Parse("酷狗音乐", raw);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (FormatException)
            {
            }
        }
        return null;
    }
}
