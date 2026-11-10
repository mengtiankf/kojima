using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>剪贴板页：最近复制的文本，点一下写回剪贴板。</summary>
public sealed class ClipboardView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly StackPanel _list;
    private readonly TextBlock _status;

    public ClipboardView(AppRuntime runtime)
    {
        _runtime = runtime;
        Height = 268;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status = UiKit.T("", 11, ThemeRuntime.Text3);
        Grid.SetColumn(_status, 0);
        header.Children.Add(_status);

        var clear = UiKit.Button("清空", () =>
        {
            _runtime.Clipboard?.Clear();
            Render();
        });
        clear.Margin = new Thickness(0, 0, 0, 0);
        Grid.SetColumn(clear, 1);
        header.Children.Add(clear);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;

        if (_runtime.Clipboard is { } clipboard)
        {
            clipboard.Changed += () => Dispatcher.BeginInvoke(Render);
        }

        Render();
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(Render);
    }

    private void Render()
    {
        _list.Children.Clear();
        var items = _runtime.Clipboard?.Items ?? [];
        _status.Text = items.Count == 0 ? "还没有剪贴板记录" : $"最近 {items.Count} 条 · 点击写回剪贴板";

        if (items.Count == 0)
        {
            var hint = UiKit.T("复制任意文本后会出现在这里", 11.5, ThemeRuntime.Text3);
            hint.Margin = new Thickness(2, 8, 0, 0);
            _list.Children.Add(hint);
            return;
        }

        foreach (var item in items.Take(40))
        {
            var row = new Grid { Height = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = UiKit.T(item.Preview, 11.5, ThemeRuntime.Text2);
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            var time = UiKit.T(item.TimeText, 10.5, ThemeRuntime.Text3, num: true);
            time.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(time, 1);
            row.Children.Add(time);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 0),
                Background = Brushes.Transparent,
                Child = row,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            border.MouseEnter += (_, _) => border.Background = ThemeRuntime.Hover;
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            border.MouseLeftButtonUp += (_, _) =>
            {
                _runtime.Clipboard?.Copy(item.Text);
                _status.Text = "已写回剪贴板";
            };
            _list.Children.Add(border);
        }
    }
}
