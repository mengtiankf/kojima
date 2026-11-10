using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Microsoft.Win32;

namespace Kojima.Island.Theme;

/// <summary>
/// 主题调度：手动、跟随系统明暗、白天/夜晚定时三选一；系统高对比度开启时可整体接管。
/// 同时监听系统主题变化与每两分钟的时钟，保证跨过日出/日落时间点后自动切换。
/// </summary>
public sealed class ThemeScheduler : IDisposable
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private bool _applying;

    public ThemeScheduler(AppRuntime runtime, Dispatcher dispatcher)
    {
        _runtime = runtime;
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(2), DispatcherPriority.Background, (_, _) => Apply(),
            dispatcher);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public void Apply()
    {
        if (_applying)
        {
            return;
        }

        _applying = true;
        try
        {
            var palette = Resolve();
            ThemeRuntime.Apply(palette);
            _runtime.RaiseThemeChanged(palette.Id);
        }
        finally
        {
            _applying = false;
        }
    }

    public DeckPalette Resolve()
    {
        var appearance = _runtime.Config.Appearance;
        if (appearance.FollowHighContrast && SystemParameters.HighContrast)
        {
            return ThemeRuntime.HighContrastPalette();
        }

        return (appearance.Mode ?? "manual").Trim().ToLowerInvariant() switch
        {
            "system" => ThemeCatalog.Get(SystemUsesLightTheme() ? appearance.LightTheme : appearance.DarkTheme),
            "daylight" => ThemeCatalog.Get(IsDaytime(appearance) ? appearance.LightTheme : appearance.DarkTheme),
            _ => ThemeCatalog.Get(_runtime.Config.Island.Theme),
        };
    }

    public static bool IsDaytime(AppearanceSettings appearance)
    {
        var hour = DateTime.Now.Hour;
        var start = Math.Clamp(appearance.DayStartHour, 0, 23);
        var end = Math.Clamp(appearance.DayEndHour, 0, 23);
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or
            UserPreferenceCategory.VisualStyle)
        {
            _timer.Dispatcher.BeginInvoke(Apply);
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.WindowGlassColor))
        {
            _timer.Dispatcher.BeginInvoke(Apply);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }
}
