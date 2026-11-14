using System.Net;
using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

internal static partial class LyricsProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Dictionary<string, LyricsResult?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<LyricsResult?> FetchAsync(
        string title,
        string artist,
        long durationMs,
        string appId,
        CancellationToken ct)
    {
        title = (title ?? "").Trim();
        artist = (artist ?? "").Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var settings = ConfigStore.Load().Lyrics;
        if (!settings.Enabled)
        {
            return null;
        }

        var sources = settings.Sources is { Count: > 0 }
            ? settings.Sources
            : ["lrclib", "netease", "qq", "kugou"];
        var signature = settings.EnhancedLocal + "|" + string.Join(',', sources);
        var key = title + "|" + artist + "|" + signature;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        LyricsResult? result = null;
        if (settings.EnhancedLocal)
        {
            try
            {
                result = appId.Contains("apple", StringComparison.OrdinalIgnoreCase)
                    ? await AppleLyricsCache.FindAsync(title, durationMs, ct).ConfigureAwait(false)
                    : await NeteaseLyricsLog.FindAsync(title, artist, durationMs, appId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn("本地增强歌词失败：" + ex.Message);
            }
        }

        foreach (var source in sources)
        {
            if (result is not null)
            {
                break;
            }

            try
            {
                result = source switch
                {
                    "lrclib" => await LrclibAsync(title, artist, durationMs, ct).ConfigureAwait(false),
                    "netease" => await NeteaseAsync(title, artist, ct).ConfigureAwait(false),
                    "qq" => await QqAsync(title, artist, durationMs, ct).ConfigureAwait(false),
                    "kugou" => await KugouAsync(title, artist, durationMs, ct).ConfigureAwait(false),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn($"{source} 歌词失败：" + ex.Message);
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

    private static async Task<JsonElement?> GetJsonAsync(string url, string? referer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Kojima/1.3.4-beta.1");
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;

    private static bool Matches(string expectedTitle, string actualTitle, long expectedMs, double actualSeconds)
    {
        if (!string.Equals(expectedTitle.Trim(), WebUtility.HtmlDecode(actualTitle).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return expectedMs <= 0 || actualSeconds <= 0 || Math.Abs(expectedMs / 1000.0 - actualSeconds) <= 3;
    }

    private static async Task<LyricsResult?> LrclibAsync(string title, string artist, long durationMs, CancellationToken ct)
    {
        var seconds = (int)Math.Round(Math.Max(0, durationMs) / 1000.0);
        var query = "track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
        var exact = await GetJsonAsync("https://lrclib.net/api/get?" + query + (seconds > 0 ? "&duration=" + seconds : ""), null, ct);
        if (exact is { ValueKind: JsonValueKind.Object } row && Matches(title, String(row, "trackName"), durationMs, Number(row, "duration")))
        {
            var parsed = ParseLrclib(row);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        var search = await GetJsonAsync("https://lrclib.net/api/search?" + query, null, ct);
        if (search is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }
        foreach (var item in list.EnumerateArray().OrderByDescending(item => String(item, "syncedLyrics").Length > 0))
        {
            if (!Matches(title, String(item, "trackName"), durationMs, Number(item, "duration")))
            {
                continue;
            }
            var parsed = ParseLrclib(item);
            if (parsed is not null)
            {
                return parsed;
            }
        }
        return null;
    }

    private static LyricsResult? ParseLrclib(JsonElement root)
    {
        var synced = String(root, "syncedLyrics");
        var plain = String(root, "plainLyrics");
        return LyricsParser.Parse("LRCLIB", string.IsNullOrWhiteSpace(synced) ? plain : synced);
    }

    private static async Task<LyricsResult?> NeteaseAsync(string title, string artist, CancellationToken ct)
    {
        var query = Uri.EscapeDataString((title + " " + artist).Trim());
        var search = await GetJsonAsync(
            "https://music.163.com/api/search/get/web?s=" + query + "&type=1&offset=0&total=true&limit=5",
            "https://music.163.com/", ct);
        if (search is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var song in songs.EnumerateArray())
        {
            if (!Matches(title, String(song, "name"), 0, 0) ||
                !song.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }
            var lyrics = await NeteaseByIdAsync(id.ToString(), ct).ConfigureAwait(false);
            if (lyrics is not null)
            {
                return lyrics;
            }
        }
        return null;
    }

    internal static async Task<LyricsResult?> NeteaseByIdAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsAsciiDigit))
        {
            return null;
        }
        var lyric = await GetJsonAsync(
            "https://music.163.com/api/song/lyric?id=" + id + "&lv=-1&kv=-1&tv=-1",
            "https://music.163.com/", ct);
        if (lyric is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("lrc", out var lrc))
        {
            return null;
        }
        return LyricsParser.Parse("网易云音乐", String(lrc, "lyric"));
    }
}
