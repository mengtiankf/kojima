using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Kojima.Island.Interop;
using Kojima.Island.Services;
using Kojima.Island.Theme;
using Kojima.Core.Config;

namespace Kojima.Island.Views;

/// <summary>控制页：音量、亮度、快捷动作。</summary>
public sealed class ControlsView : UserControl
{
    private readonly AppRuntime? _runtime;
    private readonly Slider _volume;
    private readonly Slider _brightness;
    private readonly TextBlock _volumeText;
    private readonly TextBlock _brightnessText;
    private readonly TextBlock _muteGlyph;
    private bool _syncing;

    public ControlsView()
        : this(null)
    {
    }

    public ControlsView(AppRuntime? runtime)
    {
        _runtime = runtime;
        Height = 236;

        var root = new StackPanel();

        // 音量
        _volumeText = UiKit.T("--", 12, ThemeRuntime.Text2, num: true);
        _volumeText.MinWidth = 52;
        _volumeText.TextAlignment = TextAlignment.Right;
        _volume = new Slider { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true };
        _volume.ValueChanged += (_, _) => OnVolumeChanged();
        StyleSlider(_volume);

        var volumeRow = new Grid();
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _muteGlyph = UiKit.Glyph("\uE767", 15, ThemeRuntime.Text2);
        _muteGlyph.Cursor = System.Windows.Input.Cursors.Hand;
        _muteGlyph.MouseLeftButtonUp += (_, _) => ToggleMute();
        Grid.SetColumn(_muteGlyph, 0);
        volumeRow.Children.Add(_muteGlyph);
        _volume.Margin = new Thickness(10, 0, 12, 0);
        Grid.SetColumn(_volume, 1);
        volumeRow.Children.Add(_volume);
        Grid.SetColumn(_volumeText, 2);
        volumeRow.Children.Add(_volumeText);
        root.Children.Add(UiKit.Card(volumeRow));
        root.Children.Add(new Border { Height = 8 });

        // 亮度
        _brightnessText = UiKit.T("--", 12, ThemeRuntime.Text2, num: true);
        _brightnessText.MinWidth = 52;
        _brightnessText.TextAlignment = TextAlignment.Right;
        _brightness = new Slider { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true };
        _brightness.ValueChanged += (_, _) => OnBrightnessChanged();
        StyleSlider(_brightness);

        var brightnessRow = new Grid();
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sun = UiKit.Glyph("\uE706", 15, ThemeRuntime.Text2);
        Grid.SetColumn(sun, 0);
        brightnessRow.Children.Add(sun);
        _brightness.Margin = new Thickness(10, 0, 12, 0);
        Grid.SetColumn(_brightness, 1);
        brightnessRow.Children.Add(_brightness);
        Grid.SetColumn(_brightnessText, 2);
        brightnessRow.Children.Add(_brightnessText);
        root.Children.Add(UiKit.Card(brightnessRow));
        root.Children.Add(new Border { Height = 8 });

        // 快捷动作
        var actions = new UniformGrid { Rows = 1, Columns = 3 };
        actions.Children.Add(ActionTile("\uE924", "截图", () => Shell("ms-screenclip:")));
        actions.Children.Add(ActionTile("\uE8D6", "音乐", OpenMusic));
        actions.Children.Add(ActionTile("\uE7E8", "锁屏", Lock));
        root.Children.Add(UiKit.Card(actions, padding: 8));

        Content = root;

        Loaded += (_, _) => Sync();
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(ApplyTheme);
    }

    private static void StyleSlider(Slider slider)
    {
        slider.IsSnapToTickEnabled = false;
        slider.TickFrequency = 0;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.Background = Brushes.Transparent;
        slider.Template = SliderTemplate();
        slider.ApplyTemplate();
        ApplySliderResources(slider);
    }

    /// <summary>主题化的滑杆：细轨道 + 圆点滑块，避免默认 WPF 的方块拇指。</summary>
    private static ControlTemplate SliderTemplate()
    {
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(SliderXaml);
    }

    private const string SliderXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="{x:Type Slider}">
            <Grid Height="22">
                <Border Height="4" CornerRadius="2" VerticalAlignment="Center"
                        Background="{DynamicResource SliderTrack}"/>
                <Track x:Name="PART_Track" Height="22">
                    <Track.Thumb>
                        <Thumb>
                            <Thumb.Template>
                                <ControlTemplate TargetType="{x:Type Thumb}">
                                    <Border Width="14" Height="14" CornerRadius="7"
                                            Background="{DynamicResource SliderFill}"
                                            BorderBrush="{DynamicResource SliderSurface}"
                                            BorderThickness="2"/>
                                </ControlTemplate>
                            </Thumb.Template>
                        </Thumb>
                    </Track.Thumb>
                </Track>
            </Grid>
        </ControlTemplate>
        """;

    private static void ApplySliderResources(Slider slider)
    {
        slider.Resources["SliderTrack"] = ThemeRuntime.Track;
        slider.Resources["SliderFill"] = ThemeRuntime.Accent;
        slider.Resources["SliderSurface"] = ThemeRuntime.Surface;
    }

    private Border ActionTile(string glyph, string title, Action onClick)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = UiKit.Glyph(glyph, 17, ThemeRuntime.Text2);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(icon);
        var label = UiKit.T(title, 11, ThemeRuntime.Text3);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 6, 0, 0);
        content.Children.Add(label);

        var tile = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0, 12, 0, 12),
            Margin = new Thickness(3),
            Background = Brushes.Transparent,
            Child = content,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.MouseEnter += (_, _) => tile.Background = ThemeRuntime.Hover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => onClick();
        return tile;
    }

    private async void Sync()
    {
        if (_runtime is null)
        {
            return;
        }

        var state = await _runtime.Controls.LoadAsync();
        if (state is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            _volume.Value = Math.Round(state.Volume * 100);
            _volumeText.Text = $"{_volume.Value:0}%";
            _muteGlyph.Text = state.Muted ? "\uE74F" : "\uE767";
            _muteGlyph.Foreground = state.Muted ? ThemeRuntime.Danger : ThemeRuntime.Text2;

            if (state.BrightnessSupported)
            {
                _brightness.Value = state.Brightness;
                _brightnessText.Text = $"{_brightness.Value:0}%";
            }
            else
            {
                _brightnessText.Text = "不支持";
                _brightness.IsEnabled = false;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnVolumeChanged()
    {
        if (_syncing)
        {
            return;
        }

        _runtime?.Controls.SetVolume(_volume.Value / 100);
        _volumeText.Text = $"{_volume.Value:0}%";
    }

    private void OnBrightnessChanged()
    {
        if (_syncing)
        {
            return;
        }

        _runtime?.Controls.SetBrightness((int)_brightness.Value);
        _brightnessText.Text = $"{_brightness.Value:0}%";
    }

    private async void ToggleMute()
    {
        if (_runtime is null)
        {
            return;
        }

        var muted = await _runtime.Controls.ToggleMuteAsync();
        if (muted is null)
        {
            return;
        }

        _muteGlyph.Text = muted.Value ? "\uE74F" : "\uE767";
        _muteGlyph.Foreground = muted.Value ? ThemeRuntime.Danger : ThemeRuntime.Text2;
    }

    /// <summary>「音乐」按钮：优先打开用户自定义的程序，否则退回系统音乐应用。</summary>
    private void OpenMusic() => Shell(ResolveMusicTarget(_runtime?.Config.Apps.Music));

    internal static string ResolveMusicTarget(string? custom)
    {
        var path = (custom ?? "").Trim();
        return path.Length > 0 && System.IO.File.Exists(path) ? path : "ms-music:";
    }

    private static void Shell(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    private static void Lock()
    {
        try
        {
            System.Diagnostics.Process.Start("rundll32.exe", "user32.dll,LockWorkStation");
        }
        catch
        {
            // 忽略
        }
    }

    private void ApplyTheme()
    {
        ApplySliderResources(_volume);
        ApplySliderResources(_brightness);
        Sync();
    }
}
