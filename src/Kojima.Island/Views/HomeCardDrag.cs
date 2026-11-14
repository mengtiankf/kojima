using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

internal static class HomeCardDrag
{
    public static void Attach(HomeView owner, Border card, string key)
    {
        card.AllowDrop = true;
        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            if (card.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(card, key, DragDropEffects.Move);
            }
        };

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Interactive(e.OriginalSource as DependencyObject))
            {
                return;
            }
            hold.Stop();
            hold.Start();
        };
        card.PreviewMouseLeftButtonUp += (_, _) => hold.Stop();
        card.PreviewMouseMove += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Released)
            {
                hold.Stop();
            }
        };
        card.MouseLeave += (_, _) => hold.Stop();
        card.DragEnter += (_, e) =>
        {
            if (e.Data.GetData(typeof(string)) is string source && source != key)
            {
                card.BorderBrush = ThemeRuntime.Accent;
            }
        };
        card.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        card.DragLeave += (_, _) => card.BorderBrush = ThemeRuntime.Hairline;
        card.Drop += (_, e) =>
        {
            card.BorderBrush = ThemeRuntime.Hairline;
            if (e.Data.GetData(typeof(string)) is not string source || source == key)
            {
                return;
            }
            e.Handled = true;
            var after = e.GetPosition(card).X >= card.ActualWidth / 2;
            owner.Dispatcher.BeginInvoke(() => owner.ReorderCard(source, key, after));
        };
    }

    private static bool Interactive(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or Slider or ScrollBar or Thumb)
            {
                return true;
            }
        }
        return false;
    }
}
