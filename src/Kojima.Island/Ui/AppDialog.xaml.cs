using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Kojima.Core.Themes;
using Kojima.Island.Theme;

namespace Kojima.Island.Ui;

public enum DialogTone
{
    Info,
    Question,
    Warning,
}

/// <summary>
/// 应用内模态对话框：无边框、跟随主题、圆角与发丝描边，替代系统 MessageBox。
/// </summary>
public partial class AppDialog : Window
{
    private AppDialog(string title, string message, string primary, string? secondary, DialogTone tone, bool standalone)
    {
        InitializeComponent();
        ApplyPalette(tone);

        Title = title;
        TitleText.Text = title;
        BodyText.Text = message;
        PrimaryButton.Content = primary;

        if (string.IsNullOrWhiteSpace(secondary))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondary;
        }

        switch (tone)
        {
            case DialogTone.Warning:
                IconGlyph.Text = "\uE7BA";
                IconGlyph.Foreground = ThemeRuntime.Danger;
                IconBadge.Background = Tint(ThemeRuntime.Palette.Danger, 0x28);
                break;
            case DialogTone.Question:
                IconGlyph.Text = "\uE897";
                break;
            default:
                IconGlyph.Text = "\uE946";
                break;
        }

        if (standalone)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        Opacity = 0;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
            PrimaryButton.Focus();
        };
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string primary,
        string secondary = "取消",
        DialogTone tone = DialogTone.Question)
    {
        var dialog = new AppDialog(title, message, primary, secondary, tone, owner is null);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    public static void Notify(
        Window? owner,
        string title,
        string message,
        string ok = "知道了",
        DialogTone tone = DialogTone.Info)
    {
        var dialog = new AppDialog(title, message, ok, null, tone, owner is null);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }

    private void ApplyPalette(DialogTone tone)
    {
        Resources["DialogSurfaceBrush"] = ThemeRuntime.SurfaceBrush;
        Resources["DialogHairlineBrush"] = ThemeRuntime.Hairline;
        Resources["DialogTextBrush"] = ThemeRuntime.Text;
        Resources["DialogText2Brush"] = ThemeRuntime.Text2;
        Resources["DialogAccentBrush"] = ThemeRuntime.Accent;
        Resources["DialogOnAccentBrush"] = ThemeRuntime.OnAccent;
        Resources["DialogFillBrush"] = ThemeRuntime.Fill;
        Resources["DialogDangerBrush"] = ThemeRuntime.Danger;
        Resources["DialogAccentSoftBrush"] = Tint(
            tone == DialogTone.Warning ? ThemeRuntime.Palette.Danger : ThemeRuntime.Palette.Accent,
            0x28);
    }

    private static SolidColorBrush Tint(Rgba color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起，忽略。
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter && PrimaryButton.IsEnabled)
        {
            e.Handled = true;
            DialogResult = true;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
