using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>岛屿页面里复用的几个小控件构造器（统一字体/配色）。</summary>
internal static class UiKit
{
    public static TextBlock T(string text, double px, Brush? brush = null, FontWeight? weight = null, bool num = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = px,
            Foreground = brush ?? ThemeRuntime.Text,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = num ? ThemeRuntime.NumFont : ThemeRuntime.UiFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return block;
    }

    public static TextBlock Glyph(string glyph, double px, Brush? brush = null)
        => new()
        {
            Text = glyph,
            FontFamily = ThemeRuntime.IconFont,
            FontSize = px,
            Foreground = brush ?? ThemeRuntime.Text2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

    public static Border Card(UIElement child, double radius = 12, double padding = 14, Brush? background = null)
        => new()
        {
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Background = background ?? ThemeRuntime.Card,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
            Child = child,
        };

    /// <summary>设置/控制页里常见的一行：左侧图标+标题，右侧控件。</summary>
    public static Grid Row(string glyph, string title, UIElement right, double glyphPx = 15)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Glyph(glyph, glyphPx);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = T(title, 12.5, ThemeRuntime.Text);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    public static Border Separator()
        => new()
        {
            Height = 1,
            Background = ThemeRuntime.Hairline,
            Margin = new Thickness(0, 8, 0, 8),
            Opacity = 0.6,
        };

    /// <summary>可点击的圆角小按钮（图标或文字）。</summary>
    public static Border Button(string text, Action onClick, bool glyph = false, double height = 30, Brush? background = null)
    {
        var block = glyph ? Glyph(text, 14, ThemeRuntime.Text) : T(text, 12, ThemeRuntime.Text, FontWeights.SemiBold);
        block.HorizontalAlignment = HorizontalAlignment.Center;

        var border = new Border
        {
            CornerRadius = new CornerRadius(Math.Min(8, height / 2)),
            Padding = new Thickness(13, 0, 13, 0),
            Height = height,
            Background = background ?? ThemeRuntime.Fill,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
            Child = block,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(border, text);
        border.MouseEnter += (_, _) =>
        {
            border.Background = background ?? ThemeRuntime.CardHover;
            border.BorderBrush = ThemeRuntime.Outline;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = background ?? ThemeRuntime.Fill;
            border.BorderBrush = ThemeRuntime.Hairline;
        };
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }
}
