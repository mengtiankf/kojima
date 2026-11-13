using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Core.Config;

namespace Kojima.Island.Settings;

/// <summary>功能模块列表：排序、开关与二级设置入口。</summary>
public partial class SettingsWindow
{
    private static readonly Dictionary<string, string[]> ModuleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monitor"] = ["ModuleMonitor", "系统监控", "温度、风扇与功耗的读取方式。"],
        ["weather"] = ["ModuleWeather", "天气", "天气地区与定位方式。"],
        ["media"] = ["ModuleMusic", "外部程序", "控制页「音乐」按钮启动的程序。"],
        ["translate"] = ["ModuleTranslate", "翻译", "翻译引擎与各引擎的凭据。"],
        ["stocks"] = ["ModuleStocks", "股票", "自选股与刷新间隔。"],
        ["messages"] = ["ModuleMessages", "通知中心", "通知读取频率与历史。"],
        ["usage"] = ["", "AI 用量", "来源、密钥与刷新间隔。"],
    };

    private readonly List<string> _order = new();

    /// <summary>尚未保存的开关状态（排序重建时保留，避免视觉上被重置）。</summary>
    private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (Button Up, Button Down)> _orderButtons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>开发钩子：像真实点击那样触发上移/下移，用来复现崩溃并抓异常栈。</summary>
    public void ClickOrder(string key, int delta)
    {
        if (!_orderButtons.TryGetValue(key, out var pair))
        {
            AppLog.Warn($"排序探针：找不到 {key}");
            return;
        }

        try
        {
            var button = delta < 0 ? pair.Up : pair.Down;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            AppLog.Info($"排序探针：{key} {(delta < 0 ? "上移" : "下移")} 已触发");
        }
        catch (Exception ex)
        {
            AppLog.Error($"排序探针崩溃：{ex}");
        }
    }

    private void BuildModuleOrder()
    {
        _order.Clear();
        var saved = _runtime.Config.Island.NavOrder ?? new List<string>();
        foreach (var key in saved)
        {
            var match = FeatureDefs.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null && !_order.Contains(match.Key, StringComparer.OrdinalIgnoreCase))
            {
                _order.Add(match.Key);
            }
        }

        foreach (var (key, _, _) in FeatureDefs)
        {
            if (!_order.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                _order.Add(key);
            }
        }
    }

    private void RebuildFeatureList()
    {
        FeatureList.Children.Clear();
        _features.Clear();

        var hint = new TextBlock
        {
            Text = "子进程按需启动，关闭后立即回收。",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 12, 0, 12),
        };
        FeatureList.Children.Add(hint);

        var first = true;
        for (var index = 0; index < _order.Count; index++)
        {
            var key = _order[index];
            var def = FeatureDefs.First(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!first)
            {
                FeatureList.Children.Add(new Border { Style = (Style)FindResource("RowDivider") });
            }

            first = false;
            var enabled = _pending.TryGetValue(def.Key, out var pending)
                ? pending
                : _runtime.FeatureEnabled(def.Key);
            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            };
            toggle.Checked += (_, _) => _pending[def.Key] = true;
            toggle.Unchecked += (_, _) => _pending[def.Key] = false;

            var row = new Grid { Style = (Style)FindResource("SettingRow") };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10),
            };
            text.Children.Add(new TextBlock { Text = def.Label, Style = (Style)FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = def.Desc, Style = (Style)FindResource("RowDesc") });
            row.Children.Add(text);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            };

            // 后台插件没有独立标签，顺序对它们没有意义。
            var reorderable = def.Key is not ("media" or "spectrum" or "updates");
            var up = MakeOrderButton("\uE74A", "上移", reorderable && index > 0, () => MoveKey(def.Key, -1));
            var down = MakeOrderButton("\uE74B", "下移", reorderable && index < _order.Count - 1, () => MoveKey(def.Key, 1));
            actions.Children.Add(up);
            actions.Children.Add(down);
            _orderButtons[def.Key] = (up, down);

            if (ModuleMap.ContainsKey(def.Key))
            {
                var open = new Button
                {
                    Content = "设置",
                    Style = (Style)FindResource("GhostButton"),
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (def.Key.Equals("usage", StringComparison.OrdinalIgnoreCase))
                {
                    open.Click += (_, _) => OpenUsageModule();
                }
                else
                {
                    open.Click += (_, _) => OpenModule(def.Key);
                }
                actions.Children.Add(open);
            }
            else
            {
                actions.Children.Add(new TextBlock
                {
                    Text = "无需设置",
                    Style = (Style)FindResource("Hint"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                });
            }

            var toggleHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            toggleHost.Children.Add(toggle);

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            Grid.SetColumn(toggleHost, 2);
            row.Children.Add(toggleHost);

            FeatureList.Children.Add(row);
            _features.Add(new FeatureRow(def.Key, toggle, enabled));
        }
    }

    private Button MakeOrderButton(string glyph, string tip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            ToolTip = tip,
            Style = (Style)FindResource("GhostButton"),
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>按 key 移动：按钮里不再捕获行号，避免列表重建后用到过期索引而崩溃。</summary>
    private void MoveKey(string key, int delta)
    {
        var index = _order.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        AppLog.Info($"排序：MoveKey({key}, {delta}) index={index} count={_order.Count}");
        if (index < 0)
        {
            return;
        }

        Move(index, delta);
    }

    private void Move(int index, int delta)
    {
        if (index < 0 || index >= _order.Count)
        {
            return;
        }

        var target = index + delta;
        if (target < 0 || target >= _order.Count)
        {
            return;
        }

        (_order[index], _order[target]) = (_order[target], _order[index]);
        AppLog.Info($"排序：移动 {_order[target]} index={index}->{target}，当前顺序 {string.Join(",", _order)}");

        // 排序暂留在设置窗口，应用时才写入配置；取消不会改动运行中的导航。
        Dispatcher.BeginInvoke(new Action(RebuildFeatureList), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>开发钩子：按 key 上移/下移一行（用于自动化验证排序逻辑）。</summary>
    public void DebugMove(string key, int delta)
    {
        var index = _order.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Move(index, delta);
        }
    }

    /// <summary>供开发钩子直接进入某个模块的二级设置。</summary>
    public void SelectModule(string key) => OpenModule(key);

    private bool _usageBackAdded;

    /// <summary>AI 用量：直接把原「用量」页当作二级设置打开（带返回按钮）。</summary>
    private void OpenUsageModule()
    {
        if (!_usageBackAdded && PageUsage.Content is StackPanel panel)
        {
            var back = new Button
            {
                Content = "← 返回",
                Style = (Style)FindResource("GhostButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 14),
            };
            back.Click += (_, _) =>
            {
                PageTitleText.Text = "功能";
                PageDescText.Text = "按需启动的模块、系统监控数据源与空闲回收。";
                ShowPage(2);
            };
            panel.Children.Insert(0, back);
            _usageBackAdded = true;
        }

        PageTitleText.Text = "模块设置";
        PageDescText.Text = "AI 用量的来源、密钥与刷新间隔。";
        NavFeatures.IsChecked = true;
        ShowPage(3);
    }

    private void OpenModule(string key)
    {
        if (!ModuleMap.TryGetValue(key, out var info))
        {
            return;
        }

        foreach (var name in new[] { "ModuleMonitor", "ModuleWeather", "ModuleMusic", "ModuleTranslate", "ModuleStocks", "ModuleMessages" })
        {
            if (FindName(name) is FrameworkElement element)
            {
                element.Visibility = name == info[0] ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        PageTitleText.Text = info[1];
        PageDescText.Text = info[2];
        NavFeatures.IsChecked = true;
        ShowPage(5);
    }

    private void OnClearMessages(object sender, RoutedEventArgs e)
    {
        AppRuntime.Current?.Messages.Clear();
        ClearMessagesButton.Content = "已清空";
    }

    private void OnTranslateAdvanced(object sender, RoutedEventArgs e)
    {
        var show = TranslateAdvanced.Visibility != Visibility.Visible;
        TranslateAdvanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TranslateAdvancedToggle.Content = show ? "收起第三方账号" : "填写第三方账号（可选）";
    }

    private void OnMonitorWizardPick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "auto";
        switch (tag)
        {
            case "lhm":
                MonitorLhm.IsChecked = true;
                break;
            case "ohm":
                MonitorOhm.IsChecked = true;
                break;
            case "internal":
                MonitorInternal.IsChecked = true;
                break;
            default:
                MonitorAuto.IsChecked = true;
                break;
        }

        MonitorWizardSummary.Text = tag switch
        {
            "lhm" => "将读取 LibreHardwareMonitor 的温度与功耗；它需要正在运行并暴露 WMI 数据。",
            "ohm" => "将读取 OpenHardwareMonitor 的温度与功耗；它需要正在运行并暴露 WMI 数据。",
            "internal" => "只使用系统自带接口：不需要额外软件，但部分主板读不到温度。",
            _ => "自动：检测到外部监控软件就用它，否则使用系统自带接口，不需要手动改。",
        };
        MonitorWizardStep1.Visibility = Visibility.Collapsed;
        MonitorWizardStep2.Visibility = Visibility.Visible;
    }

    private void OnMonitorWizardBack(object sender, RoutedEventArgs e)
    {
        MonitorWizardStep2.Visibility = Visibility.Collapsed;
        MonitorWizardStep1.Visibility = Visibility.Visible;
    }

    private void OnTranslateWizardPick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "auto";
        switch (tag)
        {
            case "tencent":
                TranslateTencent.IsChecked = true;
                break;
            case "deepseek":
                TranslateDeepSeek.IsChecked = true;
                break;
            case "more":
                break;
            default:
                TranslateAuto.IsChecked = true;
                break;
        }

        TranslateEngines.Visibility = tag == "more" ? Visibility.Visible : Visibility.Collapsed;
        TranslateWizardSummary.Text = tag switch
        {
            "tencent" => "使用腾讯翻译君：大陆直连、免费，适合日常中英与中日互译。",
            "deepseek" => "使用 DeepSeek：译文更自然，需要在「用量」页填写 DeepSeek Key。",
            "more" => "在下面选择具体引擎；百度、微软、自定义大模型还需要在下方第三方账号里填写凭据。",
            _ => "自动：有 DeepSeek Key 就优先用 DeepSeek，否则用腾讯翻译君；都失败时退到 MyMemory。",
        };
        TranslateWizardStep1.Visibility = Visibility.Collapsed;
        TranslateWizardStep2.Visibility = Visibility.Visible;
    }

    private void OnTranslateWizardBack(object sender, RoutedEventArgs e)
    {
        TranslateWizardStep2.Visibility = Visibility.Collapsed;
        TranslateWizardStep1.Visibility = Visibility.Visible;
        TranslateEngines.Visibility = Visibility.Collapsed;
    }

    private void OnModuleBack(object sender, RoutedEventArgs e)
    {
        ShowPage(2);
        PageTitleText.Text = PageMeta[2].Title;
        PageDescText.Text = PageMeta[2].Desc;
    }
}
