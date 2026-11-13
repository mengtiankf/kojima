using System.Windows;
using System.Windows.Controls;
using Kojima.Core.Config;

namespace Kojima.Island.Settings;

public partial class SettingsWindow
{
    private readonly List<string> _homeOrder = [];
    private readonly HashSet<string> _homeEnabled = new(StringComparer.OrdinalIgnoreCase);

    private void LoadHomeEditor() => SetHomeItems(HomeLayout.Resolve(_runtime.Config.Island), markCustom: false);

    private void SetHomeItems(IReadOnlyList<string> selected, bool markCustom = true)
    {
        _homeEnabled.Clear();
        foreach (var key in selected.Where(key => HomeLayout.Available.Contains(key)))
        {
            _homeEnabled.Add(key);
        }
        _homeOrder.Clear();
        foreach (var key in selected.Concat(HomeLayout.Available))
        {
            if (!_homeOrder.Contains(key))
            {
                _homeOrder.Add(key);
            }
        }
        BuildHomeEditor();
        RefreshPreview();
    }

    private void BuildHomeEditor()
    {
        if (HomeItemsPanel is null)
        {
            return;
        }

        HomeItemsPanel.Children.Clear();
        for (var index = 0; index < _homeOrder.Count; index++)
        {
            var key = _homeOrder[index];
            var row = new Grid { MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var targetIndex = index;
            DragReorder.Target(row, () => MoveHomeTo(key, targetIndex));
            row.Children.Add(DragReorder.Grip(key));
            var toggle = new CheckBox
            {
                Content = HomeLabel(key),
                Style = (Style)FindResource("Check"),
                IsChecked = _homeEnabled.Contains(key),
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Checked += (_, _) => { _homeEnabled.Add(key); RefreshPreview(); };
            toggle.Unchecked += (_, _) => { _homeEnabled.Remove(key); RefreshPreview(); };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            var up = MakeOrderButton("\uE74A", "上移", index > 0, () => MoveHome(key, -1));
            var down = MakeOrderButton("\uE74B", "下移", index < _homeOrder.Count - 1, () => MoveHome(key, 1));
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            HomeItemsPanel.Children.Add(row);
        }
    }

    private void MoveHome(string key, int delta)
    {
        var index = _homeOrder.IndexOf(key);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _homeOrder.Count)
        {
            return;
        }
        (_homeOrder[index], _homeOrder[target]) = (_homeOrder[target], _homeOrder[index]);
        BuildHomeEditor();
    }

    private void MoveHomeTo(string key, int target)
    {
        var index = _homeOrder.IndexOf(key);
        if (index < 0)
        {
            return;
        }
        _homeOrder.RemoveAt(index);
        _homeOrder.Insert(Math.Clamp(target, 0, _homeOrder.Count), key);
        BuildHomeEditor();
        RefreshPreview();
    }

    internal void DropHomeForTest(string key, int target) => MoveHomeTo(key, target);

    private void ApplyHomeEditor(IslandSettings settings) =>
        settings.HomeCards = _homeOrder.Where(key => _homeEnabled.Contains(key)).ToList();

    internal void SetHomeItemsForTest(IReadOnlyList<string> items) => SetHomeItems(items);

    private void OnHomeResetClick(object sender, RoutedEventArgs e) => SetHomeItems(HomeLayout.Default);

    private static string HomeLabel(string key) => key switch
    {
        "media" => "媒体卡片",
        "weather" => "天气卡片",
        "clock" => "时钟卡片",
        "actions" => "快捷操作（静音 / 截图 / 设置）",
        "stocks" => "股票行情",
        "usage" => "AI 用量",
        "monitor" => "系统监控",
        _ => key,
    };
}
