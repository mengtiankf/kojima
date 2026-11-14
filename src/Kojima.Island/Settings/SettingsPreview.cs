using System.Windows;
using System.Windows.Controls;
using Kojima.Core.Config;
using Kojima.Island.Theme;

namespace Kojima.Island.Settings;

public sealed class SettingsPreview : UserControl
{
    private readonly StackPanel _root = new();

    public SettingsPreview()
    {
        Content = _root;
    }

    public void Update(IslandSettings settings, IReadOnlyList<string> compactItems)
    {
        _root.Children.Clear();
        Opacity = settings.Opacity;
        _root.Children.Add(CompactPreview(settings, compactItems));
    }

    private static Border CompactPreview(IslandSettings settings, IReadOnlyList<string> items)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var key in items)
        {
            content.Children.Add(new TextBlock
            {
                Text = Sample(key),
                FontSize = 10.5,
                Foreground = ThemeRuntime.Text2,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            Height = settings.Shape == IslandShape.Indicator ? 4 : 34,
            MaxWidth = 300,
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = settings.Shape switch
            {
                IslandShape.Indicator => new CornerRadius(2),
                IslandShape.Capsule => new CornerRadius(17),
                _ => new CornerRadius(10),
            },
            Background = ThemeRuntime.SurfaceBrush,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
            Child = content,
        };
    }

    private static string Sample(string key) => key switch
    {
        "clock" => "22:30",
        "date" => "11/13",
        "weather" => "☀ 21°",
        "media" => "夜航星",
        "stocks" => "上证 +0.42%",
        "usage" => "AI 34%",
        "monitor" => "CPU 38%",
        _ => key,
    };
}
