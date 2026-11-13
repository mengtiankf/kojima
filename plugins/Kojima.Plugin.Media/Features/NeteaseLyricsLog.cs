using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

internal static class NeteaseLyricsLog
{
    private static readonly byte[] DecodeTable = Enumerable.Range(0, 256).Select(value =>
    {
        int high = value >> 4, low = value & 15;
        return (byte)((((high ^ ((low + 8) & 15)) & 15) * 16 + (value >> 6) * 4 + (~high & 3)) & 255);
    }).ToArray();

    public static async Task<LyricsResult?> FindAsync(
        string title,
        string artist,
        long durationMs,
        string appId,
        CancellationToken ct)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase", "CloudMusic", "cloudmusic.elog");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16384, FileOptions.Asynchronous);
            var length = (int)Math.Min(stream.Length, 4 * 1024 * 1024);
            stream.Position = stream.Length - length;
            var bytes = new byte[length];
            var read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false);
            for (var i = 0; i < read; i++)
            {
                bytes[i] = DecodeTable[bytes[i]];
            }

            var text = Encoding.UTF8.GetString(bytes, 0, read);
            foreach (var line in text.Split('\n').Reverse())
            {
                if (!TryReadTrack(line, title, artist, durationMs, out var id))
                {
                    continue;
                }
                return await LyricsProvider.NeteaseByIdAsync(id, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
        return null;
    }

    private static bool TryReadTrack(string line, string title, string artist, long durationMs, out string id)
    {
        id = "";
        var marker = line.IndexOf("【playing】,", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }
        var payload = line[(marker + "【playing】,".Length)..];
        var nameEnd = payload.IndexOf('"', 1);
        if (!payload.StartsWith('"') || nameEnd < 0)
        {
            return false;
        }
        var name = payload[1..nameEnd];
        if (name is not ("playOneTrackInPlayingList" or "nativePlay" or "setPlaying"))
        {
            return false;
        }

        using var json = JsonDocument.Parse("[" + payload + "]", new JsonDocumentOptions { MaxDepth = 32 });
        var values = json.RootElement;
        if (values.GetArrayLength() < 2 || values[1].ValueKind != JsonValueKind.Object ||
            !values[1].TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var trackTitle = Text(track, "name");
        if (!string.Equals(trackTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var trackArtist = track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? string.Join(" / ", artists.EnumerateArray().Select(artist => Text(artist, "name")))
            : "";
        var firstArtist = Regex.Split(artist, @"\s*[/、,，&;；]\s*").FirstOrDefault() ?? "";
        if (firstArtist.Length > 0 && trackArtist.Length > 0 &&
            !trackArtist.Contains(firstArtist, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var duration = Number(track, "duration") / 1000;
        if (durationMs > 0 && duration > 0 && Math.Abs(durationMs / 1000.0 - duration) > 3)
        {
            return false;
        }
        id = track.TryGetProperty("id", out var value) &&
            value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
        return id.Length > 0 && id.All(char.IsAsciiDigit);
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
}
