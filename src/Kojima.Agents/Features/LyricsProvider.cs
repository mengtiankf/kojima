using System.Net;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>
/// 歌词源：LRCLIB（优先，带时间轴）→ 网易云音乐（搜索 + 歌词接口）。
/// 只读网络，不落盘；失败返回 null。
/// </summary>
internal static class LyricsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Dictionary<string, LyricsResult?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<LyricsResult?> FetchAsync(string title, string artist, long durationMs, CancellationToken ct)
    {
        title = (title ?? "").Trim();
        artist = (artist ?? "").Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var key = title + "|" + artist;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        LyricsResult? result = null;
        try
        {
            result = await LrclibAsync(title, artist, durationMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("LRCLIB 歌词失败：" + ex.Message);
        }

        if (result is null)
        {
            try
            {
                result = await NeteaseAsync(title, artist, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn("网易云歌词失败：" + ex.Message);
            }
        }

        lock (Cache)
        {
            if (Cache.Count > 200)
            {
                Cache.Clear();
            }

            Cache[key] = result;
        }

        return result;
    }

    private static async Task<LyricsResult?> LrclibAsync(string title, string artist, long durationMs, CancellationToken ct)
    {
        var seconds = (int)Math.Round(Math.Max(0, durationMs) / 1000.0);
        var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title)
            + "&artist_name=" + Uri.EscapeDataString(artist);
        if (seconds > 0)
        {
            url += "&duration=" + seconds;
        }

        using var get = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (get.IsSuccessStatusCode)
        {
            var body = await get.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseLrclib(body, "LRCLIB");
            if (parsed is not null)
            {
                return parsed;
            }
        }

        var search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
            + "&artist_name=" + Uri.EscapeDataString(artist);
        using var found = await Http.GetAsync(search, ct).ConfigureAwait(false);
        if (!found.IsSuccessStatusCode)
        {
            return null;
        }

        var list = await found.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(list);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        return ParseLrclib(document.RootElement[0].GetRawText(), "LRCLIB");
    }

    private static LyricsResult? ParseLrclib(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var synced = root.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        var plain = root.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
        var raw = string.IsNullOrWhiteSpace(synced) ? plain : synced;
        return LyricsParser.Parse(source, raw);
    }

    private static async Task<LyricsResult?> NeteaseAsync(string title, string artist, CancellationToken ct)
    {
        var query = Uri.EscapeDataString((artist + " " + title).Trim());
        var search = "https://music.163.com/api/search/get/web?s=" + query + "&type=1&offset=0&total=true&limit=5";
        using var request = new HttpRequestMessage(HttpMethod.Get, search);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var songs = document.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("songs", out var list) ? list : default;
        if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0)
        {
            return null;
        }

        var id = songs[0].TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
        if (id <= 0)
        {
            return null;
        }

        var lyricUrl = "https://music.163.com/api/song/lyric?id=" + id + "&lv=1&kv=1&tv=-1";
        using var lyricRequest = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
        lyricRequest.Headers.Referrer = new Uri("https://music.163.com/");
        lyricRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        using var lyricResponse = await Http.SendAsync(lyricRequest, ct).ConfigureAwait(false);
        if (!lyricResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var lyricBody = await lyricResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var lyricDoc = JsonDocument.Parse(lyricBody);
        var raw = lyricDoc.RootElement.TryGetProperty("lrc", out var lrc)
            && lrc.TryGetProperty("lyric", out var text) ? text.GetString() : null;
        return LyricsParser.Parse("网易云音乐", raw);
    }

}
