using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kojima.Island.Theme;

namespace Kojima.Island.Settings;

internal static class DragReorder
{
    public static Border Grip(string key)
    {
        var grip = new Border
        {
            Width = 24,
            Height = 28,
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动调整顺序",
            Child = new TextBlock
            {
                Text = "⠿",
                FontSize = 15,
                Foreground = ThemeRuntime.Text3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(grip, key, DragDropEffects.Move);
            }
        };
        grip.MouseEnter += (_, _) => grip.Background = ThemeRuntime.Hover;
        grip.MouseLeave += (_, _) => grip.Background = Brushes.Transparent;
        return grip;
    }

    public static void Target(Grid row, Action drop)
    {
        row.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        row.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(string)))
            {
                return;
            }
            e.Handled = true;
            drop();
        };
    }
}
