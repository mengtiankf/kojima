using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Kojima.Shell.Interop;
using Kojima.Shell.Services;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>控制页：音量、亮度、快捷动作。</summary>
public sealed class ControlsView : UserControl
{
    private readonly Slider _volume;
    private readonly Slider _brightness;
    private readonly TextBlock _volumeText;
    private readonly TextBlock _brightnessText;
    private readonly TextBlock _muteGlyph;
    private bool _syncing;
    private readonly AudioEndpoints _audio = new();

    public ControlsView()
    {
        Height = 236;

        var root = new StackPanel();

        // 音量
        _volumeText = UiKit.T("--", 12, ThemeRuntime.Text2, num: true);
        _volumeText.MinWidth = 38;
        _volumeText.TextAlignment = TextAlignment.Right;
        _volume = new Slider { Minimum = 0, Maximum = 100, Width = 360, IsMoveToPointEnabled = true };
        _volume.ValueChanged += (_, _) => OnVolumeChanged();
        StyleSlider(_volume);

        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal };
        _muteGlyph = UiKit.Glyph("\uE767", 15, ThemeRuntime.Text2);
        _muteGlyph.Margin = new Thickness(0, 0, 10, 0);
        _muteGlyph.Cursor = System.Windows.Input.Cursors.Hand;
        _muteGlyph.MouseLeftButtonUp += (_, _) => ToggleMute();
        volumeRow.Children.Add(_muteGlyph);
        volumeRow.Children.Add(_volume);
        _volume.Margin = new Thickness(0, 0, 10, 0);
        volumeRow.Children.Add(_volumeText);
        root.Children.Add(UiKit.Card(volumeRow));
        root.Children.Add(new Border { Height = 8 });

        // 亮度
        _brightnessText = UiKit.T("--", 12, ThemeRuntime.Text2, num: true);
        _brightnessText.MinWidth = 38;
        _brightnessText.TextAlignment = TextAlignment.Right;
        _brightness = new Slider { Minimum = 0, Maximum = 100, Width = 360, IsMoveToPointEnabled = true };
        _brightness.ValueChanged += (_, _) => OnBrightnessChanged();
        StyleSlider(_brightness);

        var brightnessRow = new StackPanel { Orientation = Orientation.Horizontal };
        var sun = UiKit.Glyph("\uE706", 15, ThemeRuntime.Text2);
        sun.Margin = new Thickness(0, 0, 10, 0);
        brightnessRow.Children.Add(sun);
        brightnessRow.Children.Add(_brightness);
        _brightness.Margin = new Thickness(0, 0, 10, 0);
        brightnessRow.Children.Add(_brightnessText);
        root.Children.Add(UiKit.Card(brightnessRow));
        root.Children.Add(new Border { Height = 8 });

        // 快捷动作
        var actions = new UniformGrid { Rows = 1, Columns = 4 };
        actions.Children.Add(ActionTile("\uE924", "截图", () => Shell("ms-screenclip:")));
        actions.Children.Add(ActionTile("\uE720", "字幕", () => Shell("ms-settings:easeofaccess-hearing")));
        actions.Children.Add(ActionTile("\uE8D6", "音乐", () => Shell("ms-music:")));
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

    private void Sync()
    {
        _syncing = true;
        try
        {
            _volume.Value = Math.Round(_audio.GetVolume() * 100);
            _volumeText.Text = $"{_volume.Value:0}%";
            var muted = _audio.IsMuted();
            _muteGlyph.Text = muted ? "\uE74F" : "\uE767";
            _muteGlyph.Foreground = muted ? ThemeRuntime.Danger : ThemeRuntime.Text2;

            if (BrightnessService.Supported)
            {
                _brightness.Value = BrightnessService.Get();
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

        _audio.SetVolume((float)(_volume.Value / 100));
        _volumeText.Text = $"{_volume.Value:0}%";
    }

    private void OnBrightnessChanged()
    {
        if (_syncing)
        {
            return;
        }

        BrightnessService.Set((int)_brightness.Value);
        _brightnessText.Text = $"{_brightness.Value:0}%";
    }

    private void ToggleMute()
    {
        var muted = _audio.ToggleMute();
        _muteGlyph.Text = muted ? "\uE74F" : "\uE767";
        _muteGlyph.Foreground = muted ? ThemeRuntime.Danger : ThemeRuntime.Text2;
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
        Sync();
    }
}
