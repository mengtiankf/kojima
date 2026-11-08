using System.Windows;
using System.Windows.Media;
using Kojima.Core.Config;
using Kojima.Core.Themes;

namespace Kojima.Shell.Theme;

/// <summary>把主题色板包装成 WPF 画笔，供各视图绑定。</summary>
public static class ThemeRuntime
{
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
    public static readonly FontFamily NumFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static DeckPalette _palette = Fallback();

    public static event Action? Changed;

    public static DeckPalette Palette => _palette;

    public static bool Dark => _palette.Dark;

    public static void Apply(DeckPalette palette)
    {
        _palette = palette;
        Changed?.Invoke();
    }

    public static void Apply(string themeId)
    {
        var palette = ThemeCatalog.Get(themeId);
        if (palette is not null)
        {
            Apply(palette);
        }
    }

    public static Brush Brush(Rgba color) => new SolidColorBrush(color.ToColor());

    /// <summary>面板底色：自顶向下的竖向渐变。</summary>
    public static LinearGradientBrush SurfaceBrush
    {
        get
        {
            var brush = new LinearGradientBrush(_palette.SurfaceTop.ToColor(), _palette.SurfaceBottom.ToColor(), 90);
            brush.Freeze();
            return brush;
        }
    }

    public static Brush Surface => Brush(_palette.Surface);
    public static Brush Text => Brush(_palette.Text);
    public static Brush Text2 => Brush(_palette.Text2);
    public static Brush Text3 => Brush(_palette.Text3);
    public static Brush Accent => Brush(_palette.Accent);
    public static Brush OnAccent => Brush(_palette.OnAccent);
    public static Brush Danger => Brush(_palette.Danger);
    public static Brush Hairline => Brush(_palette.Hairline);
    public static Brush Track => Brush(_palette.Track);
    public static Brush Fill => Brush(_palette.Fill);
    public static Brush Hover => Brush(_palette.Hover);
    public static Brush Pressed => Brush(_palette.Pressed);
    public static Brush Outline => Brush(_palette.Outline);
    public static Brush Indicator => Brush(_palette.Indicator);
    public static Color AccentColor => _palette.Accent.ToColor();

    /// <summary>半透明卡片底（面板内的分组）。</summary>
    public static Brush Card => new SolidColorBrush(Color.FromArgb(
        _palette.Dark ? (byte)0x3C : (byte)0x22,
        _palette.Text.R, _palette.Text.G, _palette.Text.B));

    public static Color ColorOf(string hex)
    {
        return Rgba.Parse(hex, _palette.Text).ToColor();
    }

    private static DeckPalette Fallback() => ThemeCatalog.Get("deck-dark") ?? new DeckPalette();
}

internal static class RgbaExtensions
{
    public static Color ToColor(this Rgba rgba) => Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B);
}
