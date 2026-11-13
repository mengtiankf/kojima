using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

internal static class AppleLyricsCache
{
    public static async Task<LyricsResult?> FindAsync(string title, long durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return null;
        }

        var seconds = durationMs / 1000.0;
        foreach (var file in CacheFiles().Take(80))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (file.Length > 2 * 1024 * 1024)
                {
                    continue;
                }
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("status", out var status) || status.GetString() != "success" ||
                    !root.TryGetProperty("ttml", out var ttml))
                {
                    continue;
                }
                var (raw, length) = ParseTtml(ttml.GetString() ?? "");
                if (raw.Length == 0 || length is null || Math.Abs(length.Value - seconds) > 2)
                {
                    continue;
                }
                var result = LyricsParser.Parse("Apple Music（本地缓存）", raw);
                if (result is not null)
                {
                    return result;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XmlException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<FileInfo> CacheFiles()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            return Directory.GetDirectories(packages, "AppleInc.AppleMusicWin_*")
                .Select(path => Path.Combine(path, "AC", "INetCache"))
                .Where(Directory.Exists)
                .SelectMany(root => new DirectoryInfo(root).EnumerateFiles("ttmlLyrics*.json", options))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static (string Raw, double? Length) ParseTtml(string ttml)
    {
        using var reader = XmlReader.Create(new StringReader(ttml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024,
        });
        var xml = XDocument.Load(reader);
        XNamespace ns = "http://www.w3.org/ns/ttml";
        var body = xml.Root?.Element(ns + "body");
        if (body is null)
        {
            return ("", null);
        }

        var length = AppleTime((string?)body.Attribute("dur"));
        var lines = new List<string>();
        foreach (var row in body.Descendants(ns + "p"))
        {
            var begin = AppleTime((string?)row.Attribute("begin"));
            var text = string.Concat(row.DescendantNodes().Select(node => node is XText text
                ? text.Value
                : node is XElement element && element.Name == ns + "br" ? "\n" : "")).Trim();
            if (begin is null || text.Length == 0)
            {
                continue;
            }
            var time = TimeSpan.FromSeconds(begin.Value);
            lines.Add($"[{Math.Floor(time.TotalMinutes):00}:{time.Seconds:00}.{time.Milliseconds / 10:00}]{text}");
        }
        return (string.Join(Environment.NewLine, lines), length);
    }

    private static double? AppleTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        double seconds = 0;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!double.TryParse(value[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
            {
                return null;
            }
            seconds /= 1000;
        }
        else
        {
            if (value.EndsWith('s'))
            {
                value = value[..^1];
            }
            var parts = value.Split(':');
            if (parts.Length > 3)
            {
                return null;
            }
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    return null;
                }
                seconds = seconds * 60 + number;
            }
        }
        return seconds;
    }
}
