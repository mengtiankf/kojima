using System.Windows;
using System.Windows.Controls;
using Kojima.Core.Config;
using Kojima.Island.Settings;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

public sealed class HomeEditorView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly List<string> _order = [];
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HomeCardSize> _sizes = new();
    private readonly StackPanel _items = new();
    private bool _ready;

    public HomeEditorView(AppRuntime runtime, Action close, Action applied)
    {
        _runtime = runtime;
        CloseRequested = close;
        Applied = applied;
        Height = 286;
        var selected = HomeLayout.Resolve(_runtime.Config.Island);
        _enabled.UnionWith(selected);
        _order.AddRange(selected.Concat(HomeLayout.Available).Distinct());
        foreach (var key in _order)
        {
            _sizes[key] = _runtime.Config.Island.HomeCardSizes.GetValueOrDefault(key, DefaultSize(key));
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UiKit.T("自定义主页", 13, ThemeRuntime.Text, FontWeights.SemiBold));
        var done = UiKit.Button("完成", () => CloseRequested(), height: 28);
        Grid.SetColumn(done, 1);
        header.Children.Add(done);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            Content = _items,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var reset = UiKit.Button("恢复默认", Reset, height: 28);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(reset, 2);
        root.Children.Add(reset);
        Content = root;
        BuildItems();
        _ready = true;
    }

    private Action CloseRequested { get; }
    private Action Applied { get; }

    private void BuildItems()
    {
        _items.Children.Clear();
        for (var index = 0; index < _order.Count; index++)
        {
            var key = _order[index];
            var row = new Grid { MinHeight = 44, Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var targetIndex = index;
            DragReorder.Target(row, () => MoveHomeTo(key, targetIndex));
            row.Children.Add(DragReorder.Grip(key));

            var toggle = new CheckBox
            {
                Content = Label(key),
                IsChecked = _enabled.Contains(key),
                Foreground = ThemeRuntime.Text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Checked += (_, _) => { _enabled.Add(key); Apply(); };
            toggle.Unchecked += (_, _) => { _enabled.Remove(key); Apply(); };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);

            var size = new ComboBox { Width = 82, VerticalAlignment = VerticalAlignment.Center };
            foreach (var option in new[] { HomeCardSize.Small, HomeCardSize.Medium, HomeCardSize.Large, HomeCardSize.XLarge })
            {
                size.Items.Add(new ComboBoxItem { Content = SizeLabel(option), Tag = option });
            }
            size.SelectedItem = size.Items.Cast<ComboBoxItem>().First(item => (HomeCardSize)item.Tag == _sizes[key]);
            size.SelectionChanged += (_, _) =>
            {
                if (_ready && size.SelectedItem is ComboBoxItem { Tag: HomeCardSize selected })
                {
                    _sizes[key] = selected;
                    Apply();
                }
            };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);

            var up = MakeButton("↑", index > 0, () => MoveHome(key, -1));
            var down = MakeButton("↓", index < _order.Count - 1, () => MoveHome(key, 1));
            Grid.SetColumn(up, 3);
            row.Children.Add(up);
            Grid.SetColumn(down, 4);
            row.Children.Add(down);
            _items.Children.Add(row);
        }
    }

    private static Button MakeButton(string text, bool enabled, Action click)
    {
        var button = new Button
        {
            Content = text,
            Width = 28,
            Height = 26,
            Margin = new Thickness(3, 0, 0, 0),
            IsEnabled = enabled,
            Background = ThemeRuntime.Fill,
            Foreground = ThemeRuntime.Text2,
            BorderBrush = ThemeRuntime.Hairline,
            BorderThickness = new Thickness(1),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void MoveHome(string key, int delta)
    {
        var index = _order.IndexOf(key);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < _order.Count)
        {
            (_order[index], _order[target]) = (_order[target], _order[index]);
            BuildItems();
            Apply();
        }
    }

    private void MoveHomeTo(string key, int target)
    {
        var index = _order.IndexOf(key);
        if (index < 0)
        {
            return;
        }
        _order.RemoveAt(index);
        _order.Insert(Math.Clamp(target, 0, _order.Count), key);
        BuildItems();
        Apply();
    }

    private void Apply()
    {
        if (!_ready)
        {
            return;
        }
        _runtime.Config.Island.HomeCards = _order.Where(_enabled.Contains).ToList();
        foreach (var (key, size) in _sizes)
        {
            _runtime.Config.Island.HomeCardSizes[key] = size;
        }
        _runtime.SaveConfig();
        Applied();
    }

    private void Reset()
    {
        _enabled.Clear();
        _enabled.UnionWith(HomeLayout.Default);
        _order.Clear();
        _order.AddRange(HomeLayout.Default.Concat(HomeLayout.Available).Distinct());
        foreach (var key in _order)
        {
            _sizes[key] = DefaultSize(key);
        }
        BuildItems();
        Apply();
    }

    internal void SetHomeItemsForTest(IReadOnlyList<string> items)
    {
        _enabled.Clear();
        _enabled.UnionWith(items);
        _order.Clear();
        _order.AddRange(items.Concat(HomeLayout.Available).Distinct());
        BuildItems();
        Apply();
    }

    internal void DropHomeForTest(string key, int target) => MoveHomeTo(key, target);

    internal void SetSizeForTest(string key, HomeCardSize size)
    {
        _sizes[key] = size;
        BuildItems();
        Apply();
    }

    private static HomeCardSize DefaultSize(string key) => key == "media" ? HomeCardSize.Large : HomeCardSize.Medium;

    private static string Label(string key) => key switch
    {
        "media" => "媒体卡片",
        "weather" => "天气卡片",
        "clock" => "时钟卡片",
        "actions" => "快捷操作",
        "stocks" => "股票行情",
        "usage" => "AI 用量",
        "monitor" => "系统监控",
        _ => key,
    };

    private static string SizeLabel(HomeCardSize size) => size switch
    {
        HomeCardSize.Small => "小",
        HomeCardSize.Medium => "中",
        HomeCardSize.Large => "大",
        HomeCardSize.XLarge => "超大",
        _ => "中",
    };
}
