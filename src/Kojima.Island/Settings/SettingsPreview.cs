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

    public void Update(IslandSettings settings, IReadOnlyList<string> compactItems, IReadOnlyList<string> homeItems)
    {
        _root.Children.Clear();
        Opacity = settings.Opacity;
        _root.Children.Add(CompactPreview(settings, compactItems));
        _root.Children.Add(HomePreview(homeItems));
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

    private static FrameworkElement HomePreview(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return new TextBlock
            {
                Text = "主页卡片已全部隐藏",
                Foreground = ThemeRuntime.Text3,
                FontSize = 11,
                Margin = new Thickness(2, 8, 0, 0),
            };
        }

        var root = new Grid { Height = 170 };
        if (items.Count > 4)
        {
            var feed = new Grid();
            Columns(feed, 1, 6, 1);
            for (var index = 0; index < items.Count; index++)
            {
                if (index % 2 == 0)
                {
                    feed.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
                    if (index > 0)
                    {
                        feed.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
                    }
                }
                AddCard(feed, items[index], feed.RowDefinitions.Count - 1, index % 2 == 0 ? 0 : 2);
            }
            root.Children.Add(new ScrollViewer
            {
                Content = feed,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            return root;
        }

        if (items.Count == 1)
        {
            AddCard(root, items[0], 0, 0);
            return root;
        }

        if (items.Count == 2)
        {
            Columns(root, 1, 6, 1);
            AddCard(root, items[0], 0, 0);
            AddCard(root, items[1], 0, 2);
            return root;
        }

        Columns(root, 1.05, 6, 1);
        Rows(root, 1, 6, 1);
        AddCard(root, items[0], 0, 0, rowSpan: 3);
        var right = new Grid();
        Columns(right, 1, 6, 1);
        Rows(right, 1, 6, 1);
        AddCard(right, items[1], 0, 0);
        if (items.Count == 3)
        {
            AddCard(right, items[2], 2, 0, colSpan: 3);
        }
        else
        {
            AddCard(right, items[2], 0, 2);
            AddCard(right, items[3], 2, 0, colSpan: 3);
        }
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        return root;
    }

    private static void Columns(Grid grid, double first, double gap, double second)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(first, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(second, GridUnitType.Star) });
    }

    private static void Rows(Grid grid, double first, double gap, double second)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(first, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(second, GridUnitType.Star) });
    }

    private static void AddCard(Grid host, string key, int row, int column, int rowSpan = 1, int colSpan = 1)
    {
        var card = new Border
        {
            Background = ThemeRuntime.Card,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Child = new TextBlock
            {
                Text = Label(key),
                Foreground = ThemeRuntime.Text2,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetRowSpan(card, rowSpan);
        Grid.SetColumnSpan(card, colSpan);
        host.Children.Add(card);
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

    private static string Label(string key) => key switch
    {
        "media" => "媒体",
        "weather" => "天气",
        "clock" => "时钟",
        "actions" => "快捷操作",
        "stocks" => "股票",
        "usage" => "AI 用量",
        "monitor" => "系统监控",
        _ => key,
    };
}
