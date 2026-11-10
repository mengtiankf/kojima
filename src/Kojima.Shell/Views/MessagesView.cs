using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kojima.Shell.Services;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>消息页：QQ / 微信 / 邮件等系统通知（读取 Windows 通知数据库，移植自 wisland）。</summary>
public sealed class MessagesView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status;
    private readonly TextBlock _empty;

    public MessagesView(AppRuntime runtime)
    {
        _runtime = runtime;
        Height = 292;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = UiKit.T("消息", 11.5, ThemeRuntime.Text3);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_status, 0);
        header.Children.Add(_status);

        var clear = UiKit.Button("清空", () =>
        {
            _runtime.Messages.Clear();
            _runtime.Notify.Clear();
        });
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _empty = UiKit.T("暂无新消息。QQ、微信、邮件的通知会自动出现在这里。", 11.5, ThemeRuntime.Text3);
        _empty.Margin = new Thickness(2, 12, 0, 0);
        _empty.TextWrapping = TextWrapping.Wrap;

        var host = new StackPanel();
        host.Children.Add(_empty);
        host.Children.Add(_list);
        _list.Margin = new Thickness(0, 8, 0, 0);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = host,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;

        _runtime.Messages.Changed += () => Dispatcher.BeginInvoke(Render);
        _runtime.Notify.Changed += () => Dispatcher.BeginInvoke(Render);
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(Render);
        Render();
    }

    private static string KindName(string kind) => kind switch
    {
        "qq" => "QQ",
        "wechat" => "微信",
        "mail" => "邮件",
        _ => "通知",
    };

    private static string KindGlyph(string kind) => kind switch
    {
        "qq" => "\uE8BD",       // 对话气泡
        "wechat" => "\uE8F2",   // 聊天气泡
        "mail" => "\uE715",     // 信封
        _ => "\uE946",          // 信息
    };

    private static Color KindColor(string kind) => kind switch
    {
        "qq" => Color.FromRgb(0x2B, 0x9C, 0xE8),
        "wechat" => Color.FromRgb(0x2A, 0xC7, 0x6B),
        "mail" => Color.FromRgb(0xF5, 0x9E, 0x1F),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    private static (string Name, string Glyph, Color Color) NoticeStyle(string kind) => kind switch
    {
        "warn" => ("监控提醒", "\uE7BA", Color.FromRgb(0xF2, 0x5C, 0x5C)),
        "weather" => ("天气变化", "\uE706", Color.FromRgb(0x4A, 0xA8, 0xF0)),
        "stock" => ("持仓提醒", "\uE9D2", Color.FromRgb(0x3D, 0xC1, 0x7A)),
        _ => ("提醒", "\uE946", Color.FromRgb(0x8E, 0x8E, 0x93)),
    };

    private void Render()
    {
        _list.Children.Clear();
        var notices = _runtime.Notify.Items;
        var items = _runtime.Messages.Items;
        _empty.Visibility = items.Count == 0 && notices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (items.Count == 0 && notices.Count == 0)
        {
            _status.Text = "消息";
        }
        else
        {
            var parts = new List<string>();
            var byNotice = notices.GroupBy(n => NoticeStyle(n.Kind).Name)
                .Select(g => $"{g.Key} {g.Count()}");
            parts.AddRange(byNotice);
            var counts = _runtime.Messages.Counts;
            parts.AddRange(counts.OrderByDescending(p => p.Value).Select(p => $"{KindName(p.Key)} {p.Value}"));
            _status.Text = $"{items.Count + notices.Count} 条 · " + string.Join(" · ", parts);
        }

        foreach (var notice in notices)
        {
            _list.Children.Add(NoticeCard(notice));
        }

        foreach (var item in items)
        {
            var card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(KindColor(item.Kind)) { Opacity = 0.18 },
                Child = UiKit.Glyph(KindGlyph(item.Kind), 13, new SolidColorBrush(KindColor(item.Kind))),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 10, 0),
            };
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 0);
            card.Children.Add(badge);

            var body = new StackPanel();
            var title = UiKit.T(
                string.IsNullOrWhiteSpace(item.Sender)
                    ? (item.Kind == "other" ? item.App : $"{item.App} 有新消息")
                    : item.Sender,
                12.5,
                ThemeRuntime.Text,
                FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(title);

            var sub = UiKit.T(
                item.Kind == "other" ? "系统通知"
                    : item.Source == "flash" ? $"任务栏提醒 · {KindName(item.Kind)}" : $"{KindName(item.Kind)} 通知",
                10.5,
                ThemeRuntime.Text3);
            sub.Margin = new Thickness(0, 3, 0, 0);
            body.Children.Add(sub);
            Grid.SetColumn(body, 1);
            card.Children.Add(body);

            var time = UiKit.T(item.TimeText, 10.5, ThemeRuntime.Text3, num: true);
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
                Cursor = Cursors.Hand,
            };

            border.MouseLeftButtonUp += (_, _) => _runtime.Messages.Open(item.Kind);
            border.MouseEnter += (_, _) => border.Background = ThemeRuntime.Hover;
            border.MouseLeave += (_, _) => border.Background = ThemeRuntime.Card;

            _list.Children.Add(border);
        }
    }

    /// <summary>应用内提醒（CPU 占用、天气、持仓），与系统通知共用这一页。</summary>
    private Border NoticeCard(Notice notice)
    {
        var (name, glyph, color) = NoticeStyle(notice.Kind);

        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(color) { Opacity = 0.18 },
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
            Child = UiKit.Glyph(glyph, 13, new SolidColorBrush(color)),
        };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 0);
        card.Children.Add(badge);

        var body = new StackPanel();
        var title = UiKit.T(notice.Title, 12.5, ThemeRuntime.Text, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(title);

        var detail = UiKit.T(notice.Body, 10.5, ThemeRuntime.Text3);
        detail.Margin = new Thickness(0, 3, 0, 0);
        detail.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(detail);
        Grid.SetColumn(body, 1);
        card.Children.Add(body);

        var time = UiKit.T(notice.TimeText, 10.5, ThemeRuntime.Text3, num: true);
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(10, 2, 0, 0);
        Grid.SetColumn(time, 2);
        card.Children.Add(time);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Background = ThemeRuntime.Card,
            Child = card,
            ToolTip = name,
        };
    }
}
