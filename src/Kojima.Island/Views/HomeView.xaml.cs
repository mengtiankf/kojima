using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        Loaded += (_, _) =>
        {
            ApplyTheme();
            UpdateClock();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();

        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += (_, _) => UpdateClock();
        ThemeRuntime.Changed += OnThemeChanged;
        _runtime.Weather.Updated += info => Dispatcher.BeginInvoke(() => ApplyWeather(info));
        Dispatcher.BeginInvoke(() => ApplyWeather(_runtime.Weather.Current));

        ActSettings.MouseLeftButtonUp += (_, _) => OpenSettings();
        ActMute.MouseLeftButtonUp += (_, _) => ToggleMute();
        ActSnip.MouseLeftButtonUp += (_, _) => Run("ms-screenclip:");
        WeatherCard.MouseLeftButtonUp += (_, _) => OpenSettings();

        // 主页卡片跟随功能开关：关掉的功能不在主页留一张空卡。
        var showWeather = _runtime.FeatureEnabled("weather");
        var showMedia = _runtime.FeatureEnabled("media");
        var showControls = _runtime.FeatureEnabled("controls");
        WeatherCard.Visibility = showWeather ? Visibility.Visible : Visibility.Collapsed;
        MediaCard.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;
        ActionCard.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>天气卡：城市 / 温度 / 描述。</summary>
    private bool _demo;

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

        ActMuteText.Text = muted.Value ? "已静音" : "静音";
        ActMuteGlyph.Text = muted.Value ? "\uE74F" : "\uE767";
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
