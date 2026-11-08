using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>消息页：应用内通知中心（天气/监控/系统提醒）。</summary>
public sealed class MessagesView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly StackPanel _list;
    private readonly TextBlock _status;
    private readonly TextBlock _empty;

    public MessagesView(AppRuntime runtime)
    {
        _runtime = runtime;
        Height = 286;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = UiKit.T("", 11, ThemeRuntime.Text3);
        Grid.SetColumn(_status, 0);
        header.Children.Add(_status);

        var test = UiKit.Button("测试通知", () => _runtime.Notify.Push("测试通知", "这是一条来自 Kojima 的测试消息。", "info"));
        test.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(test, 1);
        header.Children.Add(test);

        var clear = UiKit.Button("清空", () => _runtime.Notify.Clear());
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _empty = UiKit.T("暂无消息。天气、监控等事件会在这里提醒。", 11.5, ThemeRuntime.Text3);
        _empty.Margin = new Thickness(2, 10, 0, 0);

        var host = new StackPanel();
        host.Children.Add(_empty);
        host.Children.Add(_list);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = host,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;

        _runtime.Notify.Changed += () => Dispatcher.BeginInvoke(Render);
        Render();
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        _list.Children.Clear();
        var items = _runtime.Notify.Items;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = items.Count == 0 ? "消息中心" : $"{items.Count} 条消息";

        foreach (var notice in items)
        {
            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = UiKit.Glyph(notice.Kind switch
            {
                "warn" => "\uE7BA",
                "stock" => "\uEAFC",
                "weather" => "\uE753",
                _ => "\uE946",
            }, 14, notice.Kind == "warn" ? ThemeRuntime.Danger : ThemeRuntime.Text2);
            glyph.Margin = new Thickness(0, 1, 10, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(glyph, 0);
            card.Children.Add(glyph);

            var body = new StackPanel();
            body.Children.Add(UiKit.T(notice.Title, 12.5, ThemeRuntime.Text, FontWeights.SemiBold));
            var text = UiKit.T(notice.Body, 11.5, ThemeRuntime.Text2);
            text.Margin = new Thickness(0, 3, 0, 0);
            text.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(text);
            Grid.SetColumn(body, 1);
            card.Children.Add(body);

            var time = UiKit.T(notice.TimeText, 10.5, ThemeRuntime.Text3, num: true);
            time.VerticalAlignment = VerticalAlignment.Top;
            time.Margin = new Thickness(10, 2, 0, 0);
            Grid.SetColumn(time, 2);
            card.Children.Add(time);

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Background = ThemeRuntime.Card,
                Child = card,
            };
            _list.Children.Add(border);
        }
    }
}
