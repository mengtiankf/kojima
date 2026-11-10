using System.Globalization;
using System.Text.Json;

using System.Runtime.Versioning;

namespace Kojima.Core.Themes;

/// <summary>
/// 从「背景 / 正文 / 强调色」三个种子派生出完整主题，规则与 BrimDeck 的派生色一致：
/// 次要文字用透明度，分隔线与填充按明暗取黑/白叠加，强调色决定 onAccent 用黑还是白。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ThemeDerivation
{
    public static ThemeDocument FromSeeds(
        string id,
        string name,
        string surfaceHex,
        string textHex,
        string accentHex)
    {
        if (!Rgba.TryParse(surfaceHex, out var surface) ||
            !Rgba.TryParse(textHex, out var text) ||
            !Rgba.TryParse(accentHex, out var accent))
        {
            throw new ArgumentException("颜色必须是 #RGB / #RRGGBB / #AARRGGBB");
        }

        var dark = surface.Luminance < 0.5;
        var overlay = dark ? new Rgba(255, 255, 255) : new Rgba(0, 0, 0);
        var onAccent = accent.Luminance > 0.55 ? new Rgba(0, 0, 0) : new Rgba(255, 255, 255);

        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = dark ? "dark" : "light",
            ["surface"] = surface.Hex,
            ["textPrimary"] = text.Hex,
            ["textSecondary"] = text.WithAlpha(0.62).Hex,
            ["textTertiary"] = text.WithAlpha(0.44).Hex,
            ["accent"] = accent.Hex,
            ["onAccent"] = onAccent.Hex,
            ["danger"] = (dark ? new Rgba(242, 119, 127) : new Rgba(196, 43, 58)).Hex,
            ["hairline"] = overlay.WithAlpha(23 / 255.0).Hex,
            ["track"] = overlay.WithAlpha(31 / 255.0).Hex,
            ["fill"] = overlay.WithAlpha(18 / 255.0).Hex,
            ["hover"] = overlay.WithAlpha(24 / 255.0).Hex,
            ["pressed"] = overlay.WithAlpha(36 / 255.0).Hex,
            ["outline"] = overlay.WithAlpha(36 / 255.0).Hex,
            ["indicator"] = (dark ? new Rgba(142, 142, 147) : new Rgba(60, 60, 67)).Hex,
        };

        return new ThemeDocument
        {
            FormatVersion = 1,
            Id = NormalizeId(id),
            Name = name,
            Author = "Kojima 主题编辑器",
            License = "theme",
            Description = "由背景 / 正文 / 强调色派生。",
            Colors = colors,
            Effects = new ThemeEffects
            {
                RimWidth = 1,
                ShadowBlur = 14,
                ShadowDepth = 4,
                ShadowOpacity = 0.28,
            },
        };
    }

    public static ThemeDocument FromSeeds(string name, string surfaceHex, string textHex, string accentHex)
        => FromSeeds("custom-" + Slug(name), name, surfaceHex, textHex, accentHex);

    /// <summary>写入用户主题目录；同 id 覆盖。返回文件路径。</summary>
    public static string Save(ThemeDocument document)
    {
        Directory.CreateDirectory(Kojima.Core.Config.ConfigStore.ThemesDirectory);
        var path = Path.Combine(Kojima.Core.Config.ConfigStore.ThemesDirectory, NormalizeId(document.Id) + ".json");
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static string NormalizeId(string id)
    {
        var text = (id ?? "").Trim().ToLowerInvariant();
        if (text.StartsWith("deck-", StringComparison.Ordinal))
        {
            text = text[5..];
        }

        var chars = text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return "deck-" + (slug.Length == 0 ? "custom" : slug);
    }

    private static string Slug(string name)
    {
        var text = (name ?? "").Trim().ToLowerInvariant();
        var chars = text.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length == 0
            ? "theme-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            : slug;
    }
}
