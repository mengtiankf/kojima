using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kojima.Core.Config;

namespace Kojima.Core.Themes;

/// <summary>一个颜色：#RGB / #RRGGBB / #AARRGGBB（与 BrimDeck 主题文件一致）。</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Parse(string? text, Rgba fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var s = text.Trim().TrimStart('#');
        try
        {
            return s.Length switch
            {
                3 => new Rgba(
                    (byte)(Convert.ToInt32(new string(s[0], 2), 16)),
                    (byte)(Convert.ToInt32(new string(s[1], 2), 16)),
                    (byte)(Convert.ToInt32(new string(s[2], 2), 16))),
                6 => new Rgba(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16)),
                8 => new Rgba(
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16),
                    Convert.ToByte(s.Substring(0, 2), 16)),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    public Rgba WithAlpha(double factor) => this with { A = (byte)Math.Clamp(A * factor, 0, 255) };

    public static Rgba Blend(Rgba from, Rgba to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgba(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount),
            (byte)(from.A + (to.A - from.A) * amount));
    }

    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    public string Hex => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

public sealed class ThemeEffects
{
    public double RimWidth { get; set; } = 1;
    public double ShadowBlur { get; set; } = 14;
    public double ShadowDepth { get; set; } = 4;
    public double ShadowOpacity { get; set; } = 0.28;
    public double Scanlines { get; set; }
}

/// <summary>BrimDeck 主题文档的 C# 映射（带注释的 JSONC）。</summary>
public sealed class ThemeDocument
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? License { get; set; }
    public string? Base { get; set; }
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ThemeEffects Effects { get; set; } = new();
}

/// <summary>解析后可直接绘制的调色板。</summary>
public sealed class DeckPalette
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? NameEn { get; init; }
    public string? Author { get; init; }
    public bool Dark { get; init; }
    public string? FilePath { get; init; }

    public Rgba Surface { get; init; }
    public Rgba Text { get; init; }
    public Rgba Text2 { get; init; }
    public Rgba Text3 { get; init; }
    public Rgba Accent { get; init; }
    public Rgba OnAccent { get; init; }
    public Rgba Danger { get; init; }
    public Rgba Hairline { get; init; }
    public Rgba Track { get; init; }
    public Rgba Fill { get; init; }
    public Rgba Hover { get; init; }
    public Rgba Pressed { get; init; }
    public Rgba Outline { get; init; }
    public Rgba Indicator { get; init; }
    public ThemeEffects Effects { get; init; } = new();

    /// <summary>表面渐变顶端（比基色略亮）。</summary>
    public Rgba SurfaceTop => Dark ? Rgba.Blend(Surface, new Rgba(255, 255, 255), 0.055) : Surface;

    /// <summary>表面渐变底端（比基色略暗）。</summary>
    public Rgba SurfaceBottom => Dark ? Surface : Rgba.Blend(Surface, new Rgba(0, 0, 0), 0.035);
}

public static class ThemeCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Dictionary<string, DeckPalette> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(ThemeDocument Doc, string? Path, bool Embedded)> Raw = [];

    public static IReadOnlyList<DeckPalette> All
    {
        get
        {
            EnsureLoaded();
            return Cache.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static DeckPalette Get(string? id)
    {
        EnsureLoaded();
        if (!string.IsNullOrWhiteSpace(id) && Cache.TryGetValue(id!, out var found))
        {
            return found;
        }

        if (Cache.TryGetValue("deck-dark", out var dark))
        {
            return dark;
        }

        return Cache.Values.First();
    }

    public static void Reload()
    {
        Cache.Clear();
        Raw.Clear();
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (Raw.Count > 0)
        {
            return;
        }

        foreach (var name in typeof(ThemeCatalog).Assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("themes/", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            Add(reader.ReadToEnd(), null, embedded: true);
        }

        try
        {
            ConfigStore.EnsureDirectories();
            foreach (var file in Directory.EnumerateFiles(ConfigStore.ThemesDirectory, "*.json"))
            {
                try
                {
                    Add(File.ReadAllText(file), file, embedded: false);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"theme {Path.GetFileName(file)} failed: {ex.Message}");
                }
            }
        }
        catch
        {
            // 用户主题目录不可用不影响内置主题
        }
    }

    private static void Add(string json, string? path, bool embedded)
    {
        ThemeDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ThemeDocument>(json, Options);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"theme parse failed ({(path ?? "embedded")}): {ex.Message}");
            return;
        }

        if (doc is null || string.IsNullOrWhiteSpace(doc.Id))
        {
            return;
        }

        // 用户主题与内置同名时，用户目录优先
        var existing = Raw.FindIndex(r => string.Equals(r.Doc.Id, doc.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (embedded)
            {
                return;
            }

            Raw.RemoveAt(existing);
        }

        Raw.Add((doc, path, embedded));
        Rebuild();
    }

    private static void Rebuild()
    {
        // 先建索引，再处理 base 继承（最多两层）
        var byId = Raw.ToDictionary(r => r.Doc.Id, r => r, StringComparer.OrdinalIgnoreCase);
        Cache.Clear();
        foreach (var (doc, path, _) in Raw)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(doc.Base) && byId.TryGetValue(doc.Base!, out var parent))
            {
                foreach (var pair in parent.Doc.Colors)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in doc.Colors)
            {
                merged[pair.Key] = pair.Value;
            }

            var dark = !string.Equals(merged.GetValueOrDefault("mode"), "light", StringComparison.OrdinalIgnoreCase);
            if (merged.TryGetValue("surface", out var surfaceText))
            {
                var surface = Rgba.Parse(surfaceText, new Rgba(0, 0, 0));
                dark = surface.Luminance < 0.5;
            }

            var id = doc.Id.StartsWith("deck-", StringComparison.OrdinalIgnoreCase) ? doc.Id : "deck-" + doc.Id;
            Cache[id] = new DeckPalette
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(doc.Name) ? doc.Id : doc.Name,
                NameEn = doc.NameEn,
                Author = doc.Author,
                Dark = dark,
                FilePath = path,
                Surface = Rgba.Parse(merged.GetValueOrDefault("surface"), new Rgba(0, 0, 0)),
                Text = Rgba.Parse(merged.GetValueOrDefault("textPrimary"), new Rgba(245, 245, 247)),
                Text2 = Rgba.Parse(merged.GetValueOrDefault("textSecondary"), new Rgba(235, 235, 245, 158)),
                Text3 = Rgba.Parse(merged.GetValueOrDefault("textTertiary"), new Rgba(235, 235, 245, 140)),
                Accent = Rgba.Parse(merged.GetValueOrDefault("accent"), new Rgba(10, 132, 255)),
                OnAccent = Rgba.Parse(merged.GetValueOrDefault("onAccent"), new Rgba(0, 0, 0)),
                Danger = Rgba.Parse(merged.GetValueOrDefault("danger"), new Rgba(242, 119, 127)),
                Hairline = Rgba.Parse(merged.GetValueOrDefault("hairline"), new Rgba(255, 255, 255, 23)),
                Track = Rgba.Parse(merged.GetValueOrDefault("track"), new Rgba(255, 255, 255, 31)),
                Fill = Rgba.Parse(merged.GetValueOrDefault("fill"), new Rgba(255, 255, 255, 18)),
                Hover = Rgba.Parse(merged.GetValueOrDefault("hover"), new Rgba(255, 255, 255, 24)),
                Pressed = Rgba.Parse(merged.GetValueOrDefault("pressed"), new Rgba(255, 255, 255, 36)),
                Outline = Rgba.Parse(merged.GetValueOrDefault("outline"), new Rgba(255, 255, 255, 36)),
                Indicator = Rgba.Parse(merged.GetValueOrDefault("indicator"), new Rgba(142, 142, 147)),
                Effects = doc.Effects,
            };
        }
    }

    public static string? Describe(DeckPalette palette) =>
        string.IsNullOrWhiteSpace(palette.Author) ? null : string.Create(CultureInfo.InvariantCulture, $"主题作者：{palette.Author}");
}
