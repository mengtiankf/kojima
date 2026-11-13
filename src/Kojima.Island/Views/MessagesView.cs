using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kojima.Island.Services;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>消息页：QQ / 微信 / 邮件等系统通知（读取 Windows 通知数据库，移植自 wisland）。</summary>
public sealed class MessagesView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status;
    private readonly TextBlock _empty;

    public MessagesView(AppRuntime runtime)
    {
        AutomationProperties.SetName(this, "通知中心");
        _runtime = runtime;
        Height = 300;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(2, 0, 4, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = UiKit.T("消息中心", 12, ThemeRuntime.Text2, FontWeights.SemiBold);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_status, 0);
        header.Children.Add(_status);

        var clear = UiKit.Button("清空", () =>
        {
            _runtime.Messages.Clear();
            _runtime.Notify.Clear();
        }, height: 28);
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _empty = UiKit.T("暂无新消息\nQQ、微信和系统提醒会出现在这里", 12, ThemeRuntime.Text3);
        _empty.TextAlignment = TextAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Margin = new Thickness(12, 62, 12, 0);

        var host = new StackPanel();
        host.Children.Add(_empty);
        host.Children.Add(_list);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Padding = new Thickness(0, 0, 8, 0),
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
        "qq" => "\uE8BD",
        "wechat" => "\uE8F2",
        "mail" => "\uE715",
        _ => "\uE946",
    };

    private static Color KindColor(string kind) => kind switch
    {
        "qq" => Color.FromRgb(0x2B, 0x9C, 0xE8),
        "wechat" => Color.FromRgb(0x2A, 0xC7, 0x6B),
        "mail" => Color.FromRgb(0xF5, 0x9E, 0x1F),
        _ => ThemeRuntime.AccentColor,
    };

    private static (string Name, string Glyph, Color Color) NoticeStyle(string kind) => kind switch
    {
        "warn" => ("监控提醒", "\uE7BA", Color.FromRgb(0xF2, 0x5C, 0x5C)),
        "weather" => ("天气变化", "\uE706", Color.FromRgb(0x4A, 0xA8, 0xF0)),
        "stock" => ("持仓提醒", "\uE9D2", Color.FromRgb(0x3D, 0xC1, 0x7A)),
        _ => ("提醒", "\uE946", ThemeRuntime.AccentColor),
    };

    private void Render()
    {
        _list.Children.Clear();
        var notices = _runtime.Notify.Items;
        var items = _runtime.Messages.Items;
        _empty.Visibility = items.Count == 0 && notices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = items.Count + notices.Count is { } count && count > 0 ? $"{count} 条消息" : "消息中心";

        var counts = notices.GroupBy(n => NoticeStyle(n.Kind).Name).Select(g => $"{g.Key} {g.Count()}").ToList();
        counts.AddRange(_runtime.Messages.Counts.OrderByDescending(p => p.Value).Select(p => $"{KindName(p.Key)} {p.Value}"));
        _status.ToolTip = counts.Count > 0 ? string.Join(" · ", counts) : null;

        if (notices.Count > 0)
        {
            _list.Children.Add(SectionLabel("应用提醒"));
            foreach (var notice in notices)
            {
                var (name, glyph, color) = NoticeStyle(notice.Kind);
                _list.Children.Add(CreateCard(glyph, color, notice.Title, notice.Body, notice.TimeText, name, null));
            }
        }

        if (items.Count > 0)
        {
            _list.Children.Add(SectionLabel("系统通知"));
            foreach (var item in items)
            {
                var title = string.IsNullOrWhiteSpace(item.Sender)
                    ? (item.Kind == "other" ? item.App : $"{item.App} 有新消息")
                    : item.Sender;
                var detail = item.Kind == "other"
                    ? "Windows 通知"
                    : item.Source == "flash" ? $"任务栏提醒 · {KindName(item.Kind)}" : $"{KindName(item.Kind)} 通知";
                _list.Children.Add(CreateCard(
                    KindGlyph(item.Kind), KindColor(item.Kind), title, detail, item.TimeText, title,
                    () => _runtime.Messages.Open(item.Kind)));
            }
        }
    }

    private static TextBlock SectionLabel(string text)
    {
        var label = UiKit.T(text, 10.5, ThemeRuntime.Text3, FontWeights.SemiBold);
        label.Margin = new Thickness(2, 3, 0, 7);
        return label;
    }

    private static Border CreateCard(
        string glyph,
        Color color,
        string titleText,
        string detailText,
        string timeText,
        string automationName,
        Action? onClick)
    {
        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(color) { Opacity = 0.16 },
            Child = UiKit.Glyph(glyph, 15, new SolidColorBrush(color)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0),
        };
        Grid.SetColumn(badge, 0);
        card.Children.Add(badge);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = UiKit.T(titleText, 12.5, ThemeRuntime.Text, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(title);
        var detail = UiKit.T(detailText, 11, ThemeRuntime.Text2);
        detail.Margin = new Thickness(0, 3, 0, 0);
        detail.TextWrapping = TextWrapping.Wrap;
        detail.MaxHeight = 34;
        body.Children.Add(detail);
        Grid.SetColumn(body, 1);
        card.Children.Add(body);

        var time = UiKit.T(timeText, 10.5, ThemeRuntime.Text3, num: true);
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(10, 2, 0, 0);
        Grid.SetColumn(time, 2);
        card.Children.Add(time);

        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Background = ThemeRuntime.Card,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
            Child = card,
            Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand,
        };
        AutomationProperties.SetName(border, automationName);
        if (onClick is not null)
        {
            border.MouseLeftButtonUp += (_, _) => onClick();
        }

        border.MouseEnter += (_, _) =>
        {
            border.Background = ThemeRuntime.CardHover;
            border.BorderBrush = ThemeRuntime.Outline;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = ThemeRuntime.Card;
            border.BorderBrush = ThemeRuntime.Hairline;
        };
        return border;
    }
}
