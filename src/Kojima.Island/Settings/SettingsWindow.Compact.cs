using System.Windows;
using System.Windows.Controls;
using Kojima.Core.Config;

namespace Kojima.Island.Settings;

public partial class SettingsWindow
{
    private readonly List<string> _compactOrder = [];
    private readonly HashSet<string> _compactEnabled = new(StringComparer.OrdinalIgnoreCase);

    private void LoadCompactEditor()
    {
        var selected = CompactLayout.Resolve(_runtime.Config.Island);
        SetCompactItems(selected, markCustom: false);
    }

    private void OnCompactPresetChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true, Tag: string preset } || preset == "custom")
        {
            return;
        }

        IReadOnlyList<string> selected = preset switch
        {
            "clock" => ["clock"],
            "weather" => ["weather"],
            "media" => ["media"],
            "none" => [],
            _ => ["clock", "weather"],
        };
        SetCompactItems(selected, markCustom: false);
    }

    private void SetCompactItems(IReadOnlyList<string> selected, bool markCustom = true)
    {
        _compactEnabled.Clear();
        foreach (var key in selected.Where(key => CompactLayout.Available.Contains(key)))
        {
            _compactEnabled.Add(key);
        }

        _compactOrder.Clear();
        foreach (var key in selected.Concat(CompactLayout.Available))
        {
            if (!_compactOrder.Contains(key))
            {
                _compactOrder.Add(key);
            }
        }

        BuildCompactEditor();
        RefreshPreview();
        if (markCustom)
        {
            CompactCustom.IsChecked = true;
        }
    }

    private void BuildCompactEditor()
    {
        if (CompactItemsPanel is null)
        {
            return;
        }

        CompactItemsPanel.Children.Clear();
        for (var index = 0; index < _compactOrder.Count; index++)
        {
            var key = _compactOrder[index];
            var row = new Grid { MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var targetIndex = index;
            DragReorder.Target(row, () => MoveCompactTo(key, targetIndex));
            row.Children.Add(DragReorder.Grip(key));

            var toggle = new CheckBox
            {
                Content = CompactLabel(key),
                Style = (Style)FindResource("Check"),
                IsChecked = _compactEnabled.Contains(key),
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Checked += (_, _) =>
            {
                _compactEnabled.Add(key);
                CompactCustom.IsChecked = true;
                RefreshPreview();
            };
            toggle.Unchecked += (_, _) =>
            {
                _compactEnabled.Remove(key);
                CompactCustom.IsChecked = true;
                RefreshPreview();
            };
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);

            var up = MakeOrderButton("\uE74A", "上移", index > 0, () => MoveCompact(key, -1));
            var down = MakeOrderButton("\uE74B", "下移", index < _compactOrder.Count - 1, () => MoveCompact(key, 1));
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            CompactItemsPanel.Children.Add(row);
        }
    }

    private void MoveCompact(string key, int delta)
    {
        var index = _compactOrder.IndexOf(key);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _compactOrder.Count)
        {
            return;
        }

        (_compactOrder[index], _compactOrder[target]) = (_compactOrder[target], _compactOrder[index]);
        BuildCompactEditor();
        CompactCustom.IsChecked = true;
        RefreshPreview();
    }

    private void MoveCompactTo(string key, int target)
    {
        var index = _compactOrder.IndexOf(key);
        if (index < 0)
        {
            return;
        }
        _compactOrder.RemoveAt(index);
        _compactOrder.Insert(Math.Clamp(target, 0, _compactOrder.Count), key);
        BuildCompactEditor();
        CompactCustom.IsChecked = true;
        RefreshPreview();
    }

    internal void DropCompactForTest(string key, int target) => MoveCompactTo(key, target);

    private void ApplyCompactEditor(IslandSettings settings)
    {
        var items = _compactOrder.Where(key => _compactEnabled.Contains(key)).ToArray();
        settings.CompactItems = items.ToList();
        settings.CompactContent = CompactLayout.LegacyContent(items);
    }

    internal void SetCompactItemsForTest(IReadOnlyList<string> items) => SetCompactItems(items);

    private static string CompactLabel(string key) => key switch
    {
        "clock" => "时钟（时:分）",
        "date" => "日期（月/日）",
        "weather" => "天气（图标与温度）",
        "media" => "媒体（当前曲目）",
        "stocks" => "股票（首只自选）",
        "usage" => "AI 用量（最高配额）",
        "monitor" => "系统监控（CPU）",
        _ => key,
    };
}
