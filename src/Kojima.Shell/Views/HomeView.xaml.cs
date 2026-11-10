using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

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
        ActSub.MouseLeftButtonUp += (_, _) => OpenLiveCaptions();
        WeatherCard.MouseLeftButtonUp += (_, _) => OpenSettings();
    }

    /// <summary>天气卡：城市 / 温度 / 描述。</summary>
    private bool _demo;

    /// <summary>演示播放信息（截图用）。</summary>
    public void ApplyDemoMedia(Kojima.Shell.Services.MediaInfo info) => Media.ApplyDemo(info);

    /// <summary>演示数据：截图用，之后的真实天气不再覆盖。</summary>
    public void ApplyDemo(Kojima.Shell.Services.WeatherInfo info)
    {
        ApplyWeather(info);
        _demo = true;
    }

    private void ApplyWeather(Kojima.Shell.Services.WeatherInfo info)
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
        Kojima.Shell.App.ShowSettingsDialog();
    }

    private void ToggleMute()
    {
        try
        {
            var volume = new Kojima.Shell.Interop.AudioEndpoints();
            var muted = volume.ToggleMute();
            ActMuteText.Text = muted ? "已静音" : "静音";
            ActMuteGlyph.Text = muted ? "\uE74F" : "\uE767";
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("静音切换失败：" + ex.Message);
        }
    }

    private static void OpenLiveCaptions()
    {
        if (!Kojima.Shell.Interop.LiveCaptions.Toggle())
        {
            Run("ms-settings:easeofaccess-hearing");
        }
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
