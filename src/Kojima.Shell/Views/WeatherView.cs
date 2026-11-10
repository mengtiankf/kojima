using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Core.Models;
using Kojima.Shell.Services;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>天气页：当前实况 + 三天预报。</summary>
public sealed class WeatherView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly TextBlock _temp;
    private readonly TextBlock _desc;
    private readonly WeatherIcon _glyph;
    private readonly TextBlock _status;
    private readonly TextBlock _feels;
    private readonly TextBlock _humidity;
    private readonly TextBlock _wind;
    private readonly StackPanel _days;
    private readonly List<Border> _chips = new();
    private readonly List<TextBlock> _chipIcons = new();
    private readonly List<TextBlock> _chipLabels = new();

    public WeatherView(AppRuntime runtime)
    {
        _runtime = runtime;
        Height = 292;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左：当前实况
        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        _glyph = new WeatherIcon { Width = 44, Height = 44, Brush = ThemeRuntime.Text2, VerticalAlignment = VerticalAlignment.Center };
        topRow.Children.Add(_glyph);
        _temp = UiKit.T("--°", 46, ThemeRuntime.Text, FontWeights.SemiBold, num: true);
        _temp.Margin = new Thickness(12, 0, 0, 0);
        topRow.Children.Add(_temp);
        left.Children.Add(topRow);

        _desc = UiKit.T("等待天气数据", 13, ThemeRuntime.Text2);
        _desc.Margin = new Thickness(2, 6, 0, 0);
        left.Children.Add(_desc);

        _status = UiKit.T("", 10.5, ThemeRuntime.Text3);
        _status.Margin = new Thickness(2, 4, 0, 0);
        left.Children.Add(_status);

        var chips = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        _feels = Chip(chips, "\uE9CA", "体感 --°");
        _humidity = Chip(chips, "\uE9CA", "湿度 --%");
        _wind = Chip(chips, "\uE9CA", "风速 --");
        left.Children.Add(chips);

        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        // 右：三天预报
        var right = new StackPanel();
        right.Children.Add(UiKit.T("三日预报", 11, ThemeRuntime.Text3));
        _days = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        right.Children.Add(_days);

        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        Content = root;

        _runtime.Weather.Updated += info => Dispatcher.BeginInvoke(() =>
        {
            if (!_demo)
            {
                Apply(info);
            }
        });
        Apply(_runtime.Weather.Current);
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(ApplyTheme);
    }

    private TextBlock Chip(WrapPanel host, string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = UiKit.Glyph(glyph, 11, ThemeRuntime.Text3);
        icon.Margin = new Thickness(0, 0, 6, 0);
        content.Children.Add(icon);
        var label = UiKit.T(text, 11, ThemeRuntime.Text2);
        content.Children.Add(label);
        _chipIcons.Add(icon);
        _chipLabels.Add(label);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Background = ThemeRuntime.Hover,
            Child = content,
        };
        host.Children.Add(chip);
        _chips.Add(chip);
        return label;
    }

    private void Apply(WeatherInfo info)
    {
        _temp.Text = info.TemperatureText;
        _desc.Text = string.IsNullOrWhiteSpace(info.Description) ? "等待天气数据" : info.Description;
        _glyph.Code = info.Code;
        _status.Text = $"更新于 {info.UpdatedAt:HH:mm}";
        _feels.Text = info.FeelsLike is { } f ? $"体感 {f:0}°" : "体感 --°";
        _humidity.Text = info.Humidity is { } h ? $"湿度 {h:0}%" : "湿度 --%";
        _wind.Text = info.WindKph is { } w ? $"风速 {w:0} km/h" : "风速 --";

        _days.Children.Clear();
        foreach (var day in info.Days.Take(3))
        {
            var row = new Grid { Height = 64 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = UiKit.T(day.Date.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日",
            }, 11.5, ThemeRuntime.Text2);
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var icon = new WeatherIcon { Width = 18, Height = 18, Brush = ThemeRuntime.Text2, Code = day.Code };
            icon.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);

            var desc = UiKit.T(day.Description, 11, ThemeRuntime.Text3);
            Grid.SetColumn(desc, 2);
            row.Children.Add(desc);

            var range = new StackPanel { Orientation = Orientation.Horizontal };
            range.Children.Add(UiKit.T(day.Max is { } max ? $"{max:0}°" : "--", 14, ThemeRuntime.Text, FontWeights.SemiBold, num: true));
            var min = UiKit.T(day.Min is { } m ? $" / {m:0}°" : " / --", 12, ThemeRuntime.Text3, num: true);
            min.Margin = new Thickness(4, 0, 0, 0);
            range.Children.Add(min);
            Grid.SetColumn(range, 3);
            row.Children.Add(range);

            _days.Children.Add(row);
            _days.Children.Add(UiKit.Separator());
        }

        if (info.Days.Count == 0)
        {
            _days.Children.Add(UiKit.T("暂无预报数据", 11.5, ThemeRuntime.Text3));
        }
    }

    /// <summary>演示数据用：直接套用一份天气信息。</summary>
    private bool _demo;

    public void ApplyDemo(WeatherInfo info)
    {
        Apply(info);
        _demo = true;
    }

    private void ApplyTheme()
    {
        // 静态文本在构造时抓的是当时的画刷，切换主题后必须重新上色
        _temp.Foreground = ThemeRuntime.Text;
        _desc.Foreground = ThemeRuntime.Text2;
        _status.Foreground = ThemeRuntime.Text3;
        _feels.Foreground = ThemeRuntime.Text2;
        _humidity.Foreground = ThemeRuntime.Text2;
        _wind.Foreground = ThemeRuntime.Text2;
        _glyph.Brush = ThemeRuntime.Text2;

        for (var i = 0; i < _chips.Count; i++)
        {
            _chips[i].Background = ThemeRuntime.Hover;
            _chipIcons[i].Foreground = ThemeRuntime.Text3;
            _chipLabels[i].Foreground = ThemeRuntime.Text2;
        }

        Apply(_runtime.Weather.Current);
    }
}
