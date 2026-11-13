using System.Windows;
using System.Windows.Controls;
using Kojima.Core.Models;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

public sealed class StocksMiniView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly TextBlock _title = UiKit.T("股票", 10.5, ThemeRuntime.Text3);
    private readonly TextBlock _main = UiKit.T("等待行情", 17, ThemeRuntime.Text, FontWeights.SemiBold);
    private readonly TextBlock _detail = UiKit.T("", 11, ThemeRuntime.Text2);
    private bool _attached;

    public StocksMiniView(AppRuntime runtime)
    {
        _runtime = runtime;
        Content = new StackPanel
        {
            Children = { _title, _main, _detail },
        };
        _main.Margin = new Thickness(0, 7, 0, 2);
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _runtime.Stocks.Updated += Render;
                _attached = true;
            }
            Render();
        };
        Unloaded += (_, _) =>
        {
            if (_attached)
            {
                _runtime.Stocks.Updated -= Render;
                _attached = false;
            }
        };
    }

    private void Render()
    {
        void Update()
        {
            var quotes = _runtime.Stocks.Quotes;
            if (quotes.Count == 0)
            {
                _main.Text = "等待行情";
                _detail.Text = "";
                return;
            }
            var first = quotes[0];
            _main.Text = $"{first.Name} {first.Price?.ToString("0.00") ?? "--"}";
            _detail.Text = first.Percent is { } percent
                ? $"{percent:+0.00;-0.00;0.00}% · {_runtime.Stocks.Status}"
                : _runtime.Stocks.Status;
            _detail.Foreground = first.Percent is null or >= 0 ? ThemeRuntime.Accent : ThemeRuntime.Danger;
        }
        if (Dispatcher.CheckAccess()) Update(); else Dispatcher.BeginInvoke(Update);
    }
}

public sealed class UsageMiniView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly TextBlock _title = UiKit.T("AI 用量", 10.5, ThemeRuntime.Text3);
    private readonly TextBlock _main = UiKit.T("等待用量", 17, ThemeRuntime.Text, FontWeights.SemiBold);
    private readonly TextBlock _detail = UiKit.T("", 11, ThemeRuntime.Text2);
    private bool _attached;

    public UsageMiniView(AppRuntime runtime)
    {
        _runtime = runtime;
        Content = new StackPanel { Children = { _title, _main, _detail } };
        _main.Margin = new Thickness(0, 7, 0, 2);
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _runtime.Usage.Updated += Render;
                _attached = true;
            }
            Render();
            if (_runtime.Usage.UpdatedAt is null)
            {
                _ = _runtime.Usage.RefreshAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            if (_attached)
            {
                _runtime.Usage.Updated -= Render;
                _attached = false;
            }
        };
    }

    private void Render()
    {
        void Update()
        {
            var snapshots = _runtime.Usage.Snapshots;
            var quota = snapshots.Where(snapshot => snapshot.Quotas.Count > 0)
                .Select(snapshot => (snapshot.Name, Quota: snapshot.Quotas.OrderByDescending(q => q.UsedPercent).First()))
                .OrderByDescending(row => row.Quota.UsedPercent)
                .FirstOrDefault();
            if (quota.Quota is not null)
            {
                _main.Text = $"{quota.Name} {quota.Quota.UsedPercent:0}%";
                _detail.Text = "最高配额使用率";
                return;
            }
            var tokens = snapshots.Sum(snapshot => snapshot.Totals(DateTime.Today).Tokens);
            _main.Text = tokens > 0 ? UsageFormat.Tokens(tokens) : "等待用量";
            _detail.Text = tokens > 0 ? "今日 Token" : "等待用量数据";
        }
        if (Dispatcher.CheckAccess()) Update(); else Dispatcher.BeginInvoke(Update);
    }
}

public sealed class MonitorMiniView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly TextBlock _title = UiKit.T("系统监控", 10.5, ThemeRuntime.Text3);
    private readonly TextBlock _main = UiKit.T("等待采样", 17, ThemeRuntime.Text, FontWeights.SemiBold);
    private readonly TextBlock _detail = UiKit.T("", 11, ThemeRuntime.Text2);
    private bool _attached;

    public MonitorMiniView(AppRuntime runtime)
    {
        _runtime = runtime;
        Content = new StackPanel { Children = { _title, _main, _detail } };
        _main.Margin = new Thickness(0, 7, 0, 2);
        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _runtime.Monitor.Sample += OnSample;
                _runtime.Monitor.AddSubscriber();
                _attached = true;
            }
            _ = _runtime.Monitor.StartAsync();
            Render(_runtime.Monitor.Last);
        };
        Unloaded += (_, _) =>
        {
            if (_attached)
            {
                _runtime.Monitor.Sample -= OnSample;
                _attached = false;
                _ = _runtime.Monitor.StopAsync();
            }
        };
    }

    private void OnSample(MonitorSample sample) => Dispatcher.BeginInvoke(() => Render(sample));

    private void Render(MonitorSample? sample)
    {
        if (sample is null)
        {
            _main.Text = "等待采样";
            _detail.Text = "";
            return;
        }
        _main.Text = $"CPU {sample.Cpu.Percent:0}%";
        var gpu = sample.Gpus.FirstOrDefault();
        _detail.Text = gpu is null
            ? $"内存 {sample.Mem.Percent:0}%"
            : $"内存 {sample.Mem.Percent:0}% · GPU {gpu.Util:0}%";
    }
}
