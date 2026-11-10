using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Settings;

/// <summary>股票设置：行情源、搜索添加、自选列表，全部用可视化控件而不是手写代码串。</summary>
public partial class SettingsWindow
{
    private static readonly (string Code, string Name)[] PresetStocks =
    [
        ("sh000001", "上证指数"), ("sz399001", "深证成指"), ("sh600519", "贵州茅台"),
        ("sz300750", "宁德时代"), ("sz002594", "比亚迪"), ("hk00700", "腾讯控股"),
    ];

    private readonly List<StockSuggestion> _stockWatch = [];
    private readonly List<StockSuggestion> _stockResults = [];

    private void LoadStockSettings(StockSettings cfg)
    {
        _stockWatch.Clear();
        foreach (var code in cfg.Symbols ?? [])
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var preset = PresetStocks.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            _stockWatch.Add(new StockSuggestion(code, preset.Name ?? code.ToUpperInvariant()));
        }

        StockSourceAuto.IsChecked = !string.Equals(cfg.Source, "tencent", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(cfg.Source, "sina", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(cfg.Source, "eastmoney", StringComparison.OrdinalIgnoreCase);
        StockSourceTencent.IsChecked = string.Equals(cfg.Source, "tencent", StringComparison.OrdinalIgnoreCase);
        StockSourceSina.IsChecked = string.Equals(cfg.Source, "sina", StringComparison.OrdinalIgnoreCase);
        StockSourceEastMoney.IsChecked = string.Equals(cfg.Source, "eastmoney", StringComparison.OrdinalIgnoreCase);
        StockRefresh.Text = cfg.RefreshSeconds.ToString();

        BuildStockWatch();
        BuildStockPresets();
        StockSearchResults.Children.Clear();
    }

    private void ApplyStockSettings(StockSettings cfg)
    {
        cfg.Symbols = _stockWatch.Select(s => s.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        cfg.Source = StockSourceTencent.IsChecked == true ? "tencent"
            : StockSourceSina.IsChecked == true ? "sina"
            : StockSourceEastMoney.IsChecked == true ? "eastmoney"
            : "auto";
        if (int.TryParse(StockRefresh.Text, out var seconds))
        {
            cfg.RefreshSeconds = Math.Clamp(seconds, 5, 600);
        }
    }

    private void BuildStockWatch()
    {
        StockWatchList.Children.Clear();
        if (_stockWatch.Count == 0)
        {
            StockWatchList.Children.Add(new TextBlock
            {
                Text = "还没有自选股，从上面的搜索结果或常用列表里添加。",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(2, 6, 0, 6),
            });
            return;
        }

        foreach (var item in _stockWatch)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = item.Name,
                Style = (Style)FindResource("RowTitle"),
                FontSize = 13,
            });
            text.Children.Add(new TextBlock
            {
                Text = item.Code.ToUpperInvariant(),
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            row.Children.Add(text);

            var remove = new Button
            {
                Content = "移除",
                Style = (Style)FindResource("GhostButton"),
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Tag = item.Code,
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += OnRemoveStock;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            StockWatchList.Children.Add(row);
        }
    }

    private void BuildStockPresets()
    {
        StockPresets.Children.Clear();
        foreach (var (code, name) in PresetStocks)
        {
            if (_stockWatch.Any(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var button = new Button
            {
                Content = name,
                Style = (Style)FindResource("GhostButton"),
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 8),
                Tag = new StockSuggestion(code, name),
            };
            button.Click += (_, _) =>
            {
                if (button.Tag is StockSuggestion suggestion)
                {
                    AddStock(suggestion);
                }
            };
            StockPresets.Children.Add(button);
        }

        if (StockPresets.Children.Count == 0)
        {
            StockPresets.Children.Add(new TextBlock
            {
                Text = "常用股票都已加入自选。",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(2, 2, 0, 2),
            });
        }
    }

    private async void OnStockSearchClick(object sender, RoutedEventArgs e) => await SearchStocksAsync();

    private async void OnStockSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchStocksAsync();
        }
    }

    private async Task SearchStocksAsync()
    {
        var keyword = (StockSearch.Text ?? "").Trim();
        StockSearchResults.Children.Clear();
        if (keyword.Length == 0)
        {
            return;
        }

        var agents = AppRuntime.Current?.Agents;
        if (agents is null)
        {
            ShowStockResultsHint("运行时尚未就绪，请稍后重试。");
            return;
        }

        StockSearchButton.IsEnabled = false;
        ShowStockResultsHint("搜索中…");
        try
        {
            var results = await agents.RequestAsync<List<StockSuggestion>>("stocks", "stocks.search",
                new { keyword }, 15000) ?? [];
            _stockResults.Clear();
            _stockResults.AddRange(results.Where(r => !string.IsNullOrWhiteSpace(r.Code)));
            RenderStockResults();
        }
        catch (Exception ex)
        {
            ShowStockResultsHint("搜索失败，可直接输入腾讯代码（如 sh600519）后点「添加」。");
            AppLog.Warn("股票搜索失败：" + ex.Message);
            var direct = NormalizeCode(keyword);
            if (direct.Length > 2)
            {
                AddDirectButton(direct);
            }
        }
        finally
        {
            StockSearchButton.IsEnabled = true;
        }
    }

    private void RenderStockResults()
    {
        StockSearchResults.Children.Clear();
        if (_stockResults.Count == 0)
        {
            ShowStockResultsHint("没有找到，试试公司简称或代码。");
            return;
        }

        foreach (var item in _stockResults.Take(8))
        {
            var button = new Button
            {
                Content = $"{item.Name}    {item.Code.ToUpperInvariant()}",
                Style = (Style)FindResource("GhostButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 6),
                Tag = item,
            };
            button.Click += (_, _) =>
            {
                if (button.Tag is StockSuggestion suggestion)
                {
                    AddStock(suggestion);
                }
            };
            StockSearchResults.Children.Add(button);
        }
    }

    private void AddDirectButton(string code)
    {
        var button = new Button
        {
            Content = "添加 " + code.ToUpperInvariant(),
            Style = (Style)FindResource("GhostButton"),
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        button.Click += (_, _) => AddStock(new StockSuggestion(code, code.ToUpperInvariant()));
        StockSearchResults.Children.Add(button);
    }

    private void ShowStockResultsHint(string text)
    {
        StockSearchResults.Children.Clear();
        StockSearchResults.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(2, 4, 0, 4),
        });
    }

    private void OnRemoveStock(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string code })
        {
            _stockWatch.RemoveAll(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            BuildStockWatch();
            BuildStockPresets();
        }
    }

    private static string NormalizeCode(string raw)
    {
        var code = (raw ?? "").Trim().ToLowerInvariant();
        if (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("hk") || code.StartsWith("us"))
        {
            return code;
        }

        if (code.Length == 6 && code.All(char.IsDigit))
        {
            return code[0] is '6' or '9' ? "sh" + code : "sz" + code;
        }

        if (code.Length == 5 && code.All(char.IsDigit))
        {
            return "hk" + code;
        }

        return "";
    }

    private void AddStock(StockSuggestion suggestion)
    {
        if (_stockWatch.Any(s => s.Code.Equals(suggestion.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _stockWatch.Add(suggestion);
        BuildStockWatch();
        BuildStockPresets();
        StockSearchResults.Children.Clear();
        StockSearch.Text = "";
    }
}
