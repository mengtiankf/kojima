using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>主页：媒体、天气、时钟与快捷操作。</summary>
public partial class HomeView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);

    public HomeView(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += (_, _) => UpdateClock();

        ActSettings.MouseLeftButtonUp += (_, _) => OpenSettings();
        ActMute.MouseLeftButtonUp += (_, _) => ToggleMute();
        ActSnip.MouseLeftButtonUp += (_, _) => Run("ms-screenclip:");
        WeatherCard.MouseLeftButtonUp += (_, _) => OpenSettings();
        WeatherCard.MouseEnter += (_, _) => WeatherCard.Background = ThemeRuntime.CardHover;
        WeatherCard.MouseLeave += (_, _) => WeatherCard.Background = ThemeRuntime.Card;

        BuildHomeLayout();
    }

    private readonly List<string> _cardOrder = [];
    internal IReadOnlyList<string> CardOrder => _cardOrder.ToArray();

    private void BuildHomeLayout()
    {
        var cards = new Dictionary<string, Border>
        {
            ["media"] = MediaCard,
            ["weather"] = WeatherCard,
            ["clock"] = ClockCard,
            ["actions"] = ActionCard,
            ["stocks"] = MiniCard(new StocksMiniView(_runtime)),
            ["usage"] = MiniCard(new UsageMiniView(_runtime)),
            ["monitor"] = MiniCard(new MonitorMiniView(_runtime)),
        };
        foreach (var card in cards.Values)
        {
            if (card.Parent is Panel parent)
            {
                parent.Children.Remove(card);
            }
            card.Visibility = Visibility.Collapsed;
        }

        var root = (Grid)Content;
        root.Children.Clear();
        root.ColumnDefinitions.Clear();
        root.RowDefinitions.Clear();
        _cardOrder.Clear();
        var selected = HomeLayout.Resolve(_runtime.Config.Island)
            .Where(key => key switch
            {
                "media" => _runtime.FeatureEnabled("media"),
                "weather" => _runtime.FeatureEnabled("weather"),
                "actions" => _runtime.FeatureEnabled("controls"),
                "stocks" => _runtime.FeatureEnabled("stocks"),
                "usage" => _runtime.FeatureEnabled("usage"),
                "monitor" => _runtime.FeatureEnabled("monitor"),
                _ => true,
            })
            .Where(cards.ContainsKey)
            .ToArray();
        _cardOrder.AddRange(selected);
        if (selected.Length == 0)
        {
            return;
        }

        if (selected.Length > 4)
        {
            BuildScrollableHome(root, cards, selected);
            return;
        }

        if (selected.Length == 1)
        {
            AddCard(root, cards[selected[0]], 0, 0);
            return;
        }

        if (selected.Length == 2)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddCard(root, cards[selected[0]], 0, 0);
            AddCard(root, cards[selected[1]], 0, 2);
            return;
        }

        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddCard(root, cards[selected[0]], 0, 0, rowSpan: 3);

        var right = new Grid();
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddCard(right, cards[selected[1]], 0, 0);
        if (selected.Length == 3)
        {
            AddCard(right, cards[selected[2]], 2, 0, colSpan: 3);
        }
        else
        {
            AddCard(right, cards[selected[2]], 0, 2);
            AddCard(right, cards[selected[3]], 2, 0, colSpan: 3);
        }
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
    }

    private static Border MiniCard(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(14),
        Child = content,
    };

    private static void BuildScrollableHome(Grid root, Dictionary<string, Border> cards, string[] selected)
    {
        var feed = new Grid();
        feed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        feed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        feed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < selected.Length; index++)
        {
            if (index % 2 == 0)
            {
                feed.RowDefinitions.Add(new RowDefinition { Height = new GridLength(132) });
                if (index > 0)
                {
                    feed.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                }
            }
            var row = feed.RowDefinitions.Count - 1;
            AddCard(feed, cards[selected[index]], row, index % 2 == 0 ? 0 : 2);
        }
        var scroll = new ScrollViewer
        {
            Content = feed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        root.Children.Add(scroll);
    }

    private static void AddCard(Grid host, Border card, int row, int column, int rowSpan = 1, int colSpan = 1)
    {
        card.Visibility = Visibility.Visible;
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetRowSpan(card, rowSpan);
        Grid.SetColumnSpan(card, colSpan);
        host.Children.Add(card);
    }

    private bool _eventsAttached;
    private bool _demo;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_eventsAttached)
        {
            ThemeRuntime.Changed += OnThemeChanged;
            _runtime.Weather.Updated += OnWeatherUpdated;
            _runtime.Controls.Changed += OnControlsChanged;
            _eventsAttached = true;
        }

        ApplyTheme();
        ApplyWeather(_runtime.Weather.Current);
        UpdateClock();
        UpdateMuteState();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_eventsAttached)
        {
            ThemeRuntime.Changed -= OnThemeChanged;
            _runtime.Weather.Updated -= OnWeatherUpdated;
            _runtime.Controls.Changed -= OnControlsChanged;
            _eventsAttached = false;
        }

        _timer.Stop();
    }

    private void OnWeatherUpdated(Kojima.Core.Models.WeatherInfo info) => Dispatcher.BeginInvoke(() => ApplyWeather(info));

    private void OnControlsChanged() => Dispatcher.BeginInvoke(UpdateMuteState);

    private void UpdateMuteState()
    {
        var muted = _runtime.Controls.Muted;
        ActMuteText.Text = muted ? "已静音" : "静音";
        ActMuteGlyph.Text = muted ? "\uE74F" : "\uE767";
        ActMuteGlyph.Foreground = muted ? ThemeRuntime.Danger : ThemeRuntime.Text2;
    }

    internal bool MediaCardVisible => MediaCard.Visibility == Visibility.Visible;

    internal bool WeatherCardVisible => WeatherCard.Visibility == Visibility.Visible;

    internal bool ControlsCardVisible => ActionCard.Visibility == Visibility.Visible;

    /// <summary>演示播放信息（截图用）。</summary>
    public void ApplyDemoMedia(Kojima.Island.Services.MediaInfo info) => Media.ApplyDemo(info);

    /// <summary>演示数据：截图用，之后的真实天气不再覆盖。</summary>
    public void ApplyDemo(Kojima.Core.Models.WeatherInfo info)
    {
        ApplyWeather(info);
        _demo = true;
    }

    private void ApplyWeather(Kojima.Core.Models.WeatherInfo info)
    {
        if (info is null || _demo)
        {
            return;
        }

        WeatherCity.Text = string.IsNullOrWhiteSpace(info.City) ? "本地" : info.City;
        WeatherGlyph.Code = info.Code;
        WeatherTemp.Text = info.TemperatureText;
        WeatherDesc.Text = string.IsNullOrWhiteSpace(info.Description) ? "等待天气数据" : info.Description;
        WeatherTemp.Foreground = ThemeRuntime.Text;
        WeatherDesc.Foreground = ThemeRuntime.Text2;
        WeatherCity.Foreground = ThemeRuntime.Text3;
        WeatherGlyph.Brush = ThemeRuntime.Text2;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("M 月 d 日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        WeekText.Text = now.ToString("yyyy");
    }

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        Background = Brushes.Transparent;
        foreach (var card in new[] { MediaCard, WeatherCard, ClockCard, ActionCard })
        {
            card.Background = ThemeRuntime.Card;
            card.BorderBrush = ThemeRuntime.Hairline;
            card.BorderThickness = new Thickness(1);
        }

        WeatherCity.Foreground = ThemeRuntime.Text3;
        WeatherGlyph.Brush = ThemeRuntime.Text2;
        WeatherTemp.Foreground = ThemeRuntime.Text;
        WeatherDesc.Foreground = ThemeRuntime.Text2;

        DateText.Foreground = ThemeRuntime.Text3;
        TimeText.Foreground = ThemeRuntime.Text;
        WeekText.Foreground = ThemeRuntime.Text3;

        foreach (var child in Descendants(ActionCard))
        {
            if (child is TextBlock text)
            {
                text.Foreground = ThemeRuntime.Text2;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private void OpenSettings()
    {
        Kojima.Island.App.ShowSettingsDialog();
    }

    private async void ToggleMute()
    {
        var muted = await _runtime.Controls.ToggleMuteAsync();
        if (muted is null)
        {
            return;
        }

        UpdateMuteState();
    }

    private static void Run(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn($"启动 {target} 失败：{ex.Message}");
        }
    }
}
