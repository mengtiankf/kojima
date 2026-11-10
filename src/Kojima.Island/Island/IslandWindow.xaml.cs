using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Models;
using Kojima.Core.Themes;
using Kojima.Island.Interop;
using Kojima.Island.Theme;
using Kojima.Island.Views;

namespace Kojima.Island.Island;

/// <summary>灵动岛主窗口：收起态图标 + 悬停展开的面板。</summary>
public partial class IslandWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _hover = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _outside = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _fullscreen = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _tabHover = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
    private string? _tabHoverKey;
    private readonly Dictionary<string, Border> _navPills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _navLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _navGlyphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IslandPage> _pages = [];
    private IslandPage? _current;
    private bool _expanded;
    private bool _pinned;
    private bool _animating;
    private double _targetW;
    private double _targetH;
    private DateTime _hoverSince;
    private DateTime _outsideSince;
    private bool _hiddenForFullscreen;
    private bool _dragArmed;
    private bool _dragging;
    private bool _suppressClick;
    private bool _resizing;
    private Point _pressScreen;
    private double _pressLeft;
    private double _pressTop;
    private double _resizeStartWidth;
    private const double DragThreshold = 4;
    private const double ResizeEdge = 18;
    private readonly string? _displayOverride;

    public IslandWindow(AppRuntime runtime, string? display = null)
    {
        _runtime = runtime;
        _displayOverride = display;
        InitializeComponent();
        _frameTimer.Tick += OnFrame;

        SourceInitialized += (_, _) => ApplyWindowStyles();
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseLeftButtonDown += OnDragPress;
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragRelease;
        Deactivated += (_, _) => RestoreNoActivate();
        Loaded += (_, _) => Initialise();

        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateClock();

        _hover.Interval = TimeSpan.FromMilliseconds(40);
        _hover.Tick += (_, _) => PollHover();

        _outside.Interval = TimeSpan.FromMilliseconds(120);
        _outside.Tick += (_, _) => PollOutside();

        _fullscreen.Interval = TimeSpan.FromSeconds(1);
        _fullscreen.Tick += (_, _) => PollFullscreen();

        // 悬停到选项卡上 90 ms 即切换，不需要点击（BrimDeck 的标签行为）。
        _tabHover.Tick += (_, _) =>
        {
            _tabHover.Stop();
            if (_tabHoverKey is { } key && _navPills.TryGetValue(key, out var pill) && pill.IsMouseOver)
            {
                ShowPage(key);
            }
        };

        ThemeRuntime.Changed += OnThemeChanged;
        SizeChanged += (_, _) => Surface.Refresh();
        _runtime.Weather.Updated += info => Dispatcher.BeginInvoke(() => ApplyWeather(info));
        _runtime.Media.Changed += () => Dispatcher.BeginInvoke(ApplyCompactContent);
        Dispatcher.BeginInvoke(() => ApplyWeather(_runtime.Weather.Current));
    }

    public bool IsExpanded => _expanded;

    public string? CurrentPageKey => _current?.Key;

    private IslandSettings Settings => _runtime.Config.Island;

    private void Initialise()
    {
        BuildPages();
        BuildNav();
        ApplyAppearance();
        UpdateClock();
        PositionWindow();
        _clock.Start();
        _hover.Start();
        _outside.Start();
        _fullscreen.Start();
        if (Settings.ExpandMode == "always")
        {
            Expand();
        }
    }

    private void ApplyWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, exStyle);
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>输入类控件需要键盘焦点：点它们时临时去掉 WS_EX_NOACTIVATE 并激活窗口。</summary>
    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindParent<TextBoxBase>(source) is null && FindParent<PasswordBox>(source) is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
        {
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, exStyle & ~NativeMethods.WS_EX_NOACTIVATE);
        }

        Activate();
        Focus();
    }

    private void RestoreNoActivate()
    {
        if (!_expanded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
    }

    private static T? FindParent<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>设置里改了功能开关后，无需重启即可重建页面与导航。</summary>
    public void ReloadFeatures()
    {
        var currentKey = _current?.Key ?? "home";
        _pages.Clear();
        BuildPages();
        BuildNav();

        var target = _pages.FirstOrDefault(p => p.Key.Equals(currentKey, StringComparison.OrdinalIgnoreCase))
            ?? _pages[0];
        ShowPage(target.Key);
        ApplyCompactContent();
    }

    private void BuildPages()
    {
        var home = new HomeView(_runtime);
        _homeView = home;
        _pages.Add(new IslandPage("home", "\uE80F", "主页", home, 300));

        if (_runtime.FeatureEnabled("usage"))
        {
            var usage = new UsageView();
            _usageView = usage;
            usage.PeriodChanged += period =>
            {
                _ = _runtime.Usage.RefreshAsync(period);
            };
            usage.RefreshRequested += () => _ = RefreshUsageAsync();
            _runtime.Usage.Updated += () => Dispatcher.BeginInvoke(() =>
            {
                usage.Update(_runtime.Usage.Snapshots, _runtime.Usage.UpdatedAt);
                if (_current?.Key == "usage")
                {
                    ResizeToContent();
                }
            });
            _pages.Add(new IslandPage("usage", "\uE9D9", "AI 用量", usage, 300));
        }

        if (_runtime.FeatureEnabled("weather"))
        {
            var weather = new WeatherView(_runtime);
            _weatherView = weather;
            _pages.Add(new IslandPage("weather", "", "天气", weather, 292));
        }

        if (_runtime.FeatureEnabled("stocks"))
        {
            var stocks = new Views.StocksView(_runtime);
            _pages.Add(new IslandPage("stocks", "", "股票", stocks, 300));
        }

        _pages.Add(new IslandPage("ctl", "", "控制", new ControlsView(_runtime), 236));

        if (_runtime.FeatureEnabled("clipboard"))
        {
            var clipboard = new ClipboardView(_runtime);
            _pages.Add(new IslandPage("clip", "", "剪贴板", clipboard, 268));
        }

        if (_runtime.FeatureEnabled("translate"))
        {
            var translate = new Views.TranslateView(_runtime);
            _pages.Add(new IslandPage("translate", "", "翻译", translate, 300));
        }

        if (_runtime.FeatureEnabled("messages"))
        {
            _pages.Add(new IslandPage("msg", "\uE8BD", "消息", new MessagesView(_runtime), 286));
        }

        if (_runtime.FeatureEnabled("monitor"))
        {
            var monitor = new MonitorView();
            _monitorView = monitor;
            _runtime.Monitor.Sample += sample => Dispatcher.BeginInvoke(() =>
            {
                monitor.Update(sample);
                if (_current?.Key == "monitor")
                {
                    ResizeToContent();
                }
            });
            _pages.Add(new IslandPage("monitor", "\uEC4A", "监控", monitor, 372));
        }
    }

    private UsageView? _usageView;
    private MonitorView? _monitorView;
    private WeatherView? _weatherView;
    private HomeView? _homeView;

    /// <summary>演示数据：生成 README 示例图时使用，不含真实数据。</summary>
    private bool _demo;

    /// <summary>开发钩子：把文本填进翻译页并触发翻译。</summary>
    public void TranslateProbe(string text)
    {
        ShowPage("translate");
        var page = _pages.FirstOrDefault(p => p.Key == "translate");
        if (page?.View is TranslateView view)
        {
            view.FillAndTranslate(text);
        }
    }

    public void ApplyDemoData()
    {
        _demo = true;
        _usageView?.Update(DemoData.Usage(), DateTimeOffset.Now);
        _monitorView?.Update(DemoData.Monitor());
        _weatherView?.ApplyDemo(DemoData.Weather());
        _homeView?.ApplyDemo(DemoData.Weather());
        _homeView?.ApplyDemoMedia(DemoData.Media());

        _runtime.Notify.Push("CPU 占用过高", "CPU 已持续 92% 超过 2 分钟，建议检查后台进程。", "warn");
        _runtime.Notify.Push("天气提醒", "半小时后有小雨，出门记得带伞。", "weather");
        _runtime.Notify.Push("持仓提醒", "贵州茅台 +0.07%，宁德时代 -3.73%。", "stock");
        _runtime.Messages.Seed(DemoData.Messages());
        _runtime.Clipboard?.Seed(
        [
            "dotnet publish src/Kojima.Island -c Release -r win-x64",
            "https://github.com/mengtiankf/kojima",
            "Kojima 1.0.0 发布清单：msi + exe + release notes",
            "灵 动 岛 · 主 进 程 / 子 进 程 按 需 启 动",
        ]);
    }

    private void BuildNav()
    {
        NavRow.Children.Clear();
        var host = new Grid();

        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var page in OrderedPages())
        {
            left.Children.Add(CreateNavItem(page));
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(CreateGlyphButton("\uE713", "设置", OpenSettings));

        host.Children.Add(left);
        host.Children.Add(right);
        NavRow.Children.Add(host);
    }

    /// <summary>按设置里的模块顺序排标签；主页固定在最前，控制固定在末尾。</summary>
    private IEnumerable<IslandPage> OrderedPages()
    {
        var home = _pages.FirstOrDefault(p => p.Key == "home");
        if (home is not null)
        {
            yield return home;
        }

        var order = _runtime.Config.Island.NavOrder ?? new List<string>();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["usage"] = "usage",
            ["monitor"] = "monitor",
            ["stocks"] = "stocks",
            ["weather"] = "weather",
            ["clipboard"] = "clip",
            ["translate"] = "translate",
            ["messages"] = "msg",
            ["controls"] = "ctl",
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "home" };
        foreach (var key in order)
        {
            if (map.TryGetValue(key, out var pageKey) && seen.Add(pageKey))
            {
                var page = _pages.FirstOrDefault(p => p.Key == pageKey);
                if (page is not null)
                {
                    yield return page;
                }
            }
        }

        foreach (var page in _pages)
        {
            if (page.Key == "ctl" && !_runtime.FeatureEnabled("controls"))
            {
                continue;
            }

            if (seen.Add(page.Key))
            {
                yield return page;
            }
        }
    }

    private FrameworkElement CreateNavItem(IslandPage page)
    {
        var label = new TextBlock
        {
            Text = page.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        label.Foreground = ThemeRuntime.Text;

        var glyph = new TextBlock
        {
            Text = page.Glyph,
            FontFamily = ThemeRuntime.IconFont,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.Foreground = ThemeRuntime.Text2;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(glyph);
        content.Children.Add(label);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 6, 12, 6),
            Margin = new Thickness(0, 2, 2, 2),
            Background = Brushes.Transparent,
            Child = content,
            Cursor = Cursors.Hand,
        };

        pill.MouseEnter += (_, _) =>
        {
            UpdateNavVisuals(page.Key, hover: true);
            if (_current is { } current && current.Key.Equals(page.Key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _tabHoverKey = page.Key;
            _tabHover.Stop();
            _tabHover.Start();
        };
        pill.MouseLeave += (_, _) =>
        {
            UpdateNavVisuals(page.Key, hover: false);
            if (_tabHoverKey == page.Key)
            {
                _tabHover.Stop();
                _tabHoverKey = null;
            }
        };
        pill.MouseLeftButtonUp += (_, _) => ShowPage(page.Key);

        _navPills[page.Key] = pill;
        _navLabels[page.Key] = label;
        _navGlyphs[page.Key] = glyph;
        return pill;
    }

    private FrameworkElement CreateGlyphButton(string glyph, string tip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = ThemeRuntime.IconFont,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");

        var button = new Border
        {
            Width = 30,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Child = text,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Margin = new Thickness(2, 2, 0, 2),
        };

        button.MouseEnter += (_, _) =>
        {
            button.Background = ThemeRuntime.Hover;
            text.Foreground = ThemeRuntime.Text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            text.Foreground = ThemeRuntime.Text2;
        };
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private void UpdateNavVisuals(string activeKey, bool hover = false)
    {
        foreach (var (key, pill) in _navPills)
        {
            var active = key.Equals(activeKey, StringComparison.OrdinalIgnoreCase);
            pill.Background = active ? ThemeRuntime.Fill : Brushes.Transparent;
            if (hover && !active)
            {
                pill.Background = ThemeRuntime.Hover;
            }

            if (_navLabels.TryGetValue(key, out var label))
            {
                label.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_navGlyphs.TryGetValue(key, out var glyph))
            {
                glyph.Foreground = active ? ThemeRuntime.Text : ThemeRuntime.Text2;
            }
        }
    }

    public void ShowPage(string key)
    {
        var page = _pages.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (page is null)
        {
            return;
        }

        if (_current == page && _expanded)
        {
            return;
        }

        _current?.Hidden?.Invoke();
        _current = page;

        PageHost.Children.Clear();
        PageHost.Children.Add(page.View);
        page.Shown?.Invoke();

        UpdateNavVisuals(page.Key);
        EnsureExpanded();

        if (_demo)
        {
            return;
        }

        if (page.Key == "monitor")
        {
            _ = StartMonitorAsync();
        }
        else
        {
            _ = _runtime.Monitor.StopAsync();
        }

        if (page.Key == "usage")
        {
            _ = RefreshUsageAsync();
        }

        ResizeToContent();
    }

    private async Task StartMonitorAsync()
    {
        try
        {
            _runtime.Monitor.AddSubscriber();
            await _runtime.Monitor.StartAsync();
            var sample = _runtime.Monitor.Last ?? await _runtime.Monitor.SampleOnceAsync();
            if (sample is not null && _current?.Key == "monitor" && _current.View is MonitorView view)
            {
                view.Update(sample);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("监控页启动失败：" + ex.Message);
        }
    }

    private async Task RefreshUsageAsync()
    {
        if (_current?.View is not UsageView view)
        {
            return;
        }

        view.SetBusy(true);
        try
        {
            await _runtime.Usage.RefreshAsync(view.Period);
            view.Update(_runtime.Usage.Snapshots, _runtime.Usage.UpdatedAt);
            ResizeToContent();
        }
        finally
        {
            view.SetBusy(false);
        }
    }

    private void ResizeToContent()
    {
        if (!_expanded || _current is null)
        {
            return;
        }

        var width = Math.Max(320, Settings.PanelWidth);
        var available = new Size(width - 24, 2000);
        _current.View.Measure(available);
        var contentHeight = _current.View.DesiredSize.Height;
        if (double.IsNaN(contentHeight) || contentHeight < 40)
        {
            contentHeight = _current.HeightHint;
        }

        var height = 12 + 38 + contentHeight + 12;
        AnimateTo(width, Math.Clamp(height, 160, 720), expanded: true);
    }

    public void Expand(bool pin = true)
    {
        AppLog.Info($"Expand 请求 page={_current?.Key} pin={pin} at {Left},{Top}");
        _pinned = pin;
        EnsureExpanded();
        ResizeToContent();
    }

    public void Collapse()
    {
        if (!_expanded || _animating)
        {
            return;
        }

        _pinned = false;
        _expanded = false;
        _current?.Hidden?.Invoke();
        _ = _runtime.Monitor.StopAsync();

        // 收起前先淡出内容
        Panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Panel.Opacity, 0, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
        Compact.Visibility = Visibility.Visible;
        Surface.Expanded = false;

        var (w, h) = CollapsedSize();
        AnimateTo(w, h, expanded: false);
    }

    public void Toggle()
    {
        if (_expanded)
        {
            Collapse();
        }
        else
        {
            ShowPage(_current?.Key ?? _pages.FirstOrDefault()?.Key ?? "home");
        }
    }

    private void EnsureExpanded()
    {
        if (_expanded)
        {
            return;
        }

        _expanded = true;
        Compact.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Visible;
        Panel.Opacity = 0;
        Surface.Expanded = true;
        BeginAnimation(OpacityProperty, null);

        var (w, h) = ExpandedSize();
        AnimateTo(w, h, expanded: true);
    }

    private (double Width, double Height) CollapsedSize()
    {
        return Settings.Shape switch
        {
            IslandShape.Capsule => (Settings.CapsuleWidth, Settings.Height),
            IslandShape.Indicator => (Settings.IndicatorWidth, Settings.IndicatorThickness),
            _ => (Settings.NotchWidth, Settings.Height),
        };
    }

    private (double Width, double Height) ExpandedSize()
    {
        var width = Math.Max(320, Settings.PanelWidth);
        var height = 12 + 38 + (_current?.HeightHint ?? 300) + 12;
        return (width, Math.Clamp(height, 160, 720));
    }


    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double AnimMs => Math.Clamp(Settings.AnimationMs, 80, 1200);
    private DateTime _animStart;
    private double _fromW;
    private double _fromH;
    private bool _fadeStarted;

    private void AnimateTo(double width, double height, bool expanded)
    {
        _targetW = Math.Max(1, width);
        _targetH = Math.Max(1, height);

        // 目标变化时以当前尺寸为起点重新计时，避免跳变
        _fromW = Width;
        _fromH = Height;
        _animStart = DateTime.UtcNow;
        _fadeStarted = false;

        if (!_animating)
        {
            _animating = true;
            _frameTimer.Start();
        }
    }

    private static double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);

    private void OnFrame(object? sender, EventArgs e)
    {
        var raw = (DateTime.UtcNow - _animStart).TotalMilliseconds / AnimMs;
        var t = Math.Clamp(raw, 0, 1);
        var eased = EaseOutQuint(t);

        Width = _fromW + (_targetW - _fromW) * eased;
        Height = _fromH + (_targetH - _fromH) * eased;
        PositionWindow();

        // 展开过半后再淡入内容，避免内容被窗口裁切
        if (!_fadeStarted && t > 0.55 && Panel.Visibility == Visibility.Visible && Panel.Opacity < 1)
        {
            _fadeStarted = true;
            Panel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(Panel.Opacity, 1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        if (t >= 1)
        {
            Width = _targetW;
            Height = _targetH;
            PositionWindow();
            _animating = false;
            _frameTimer.Stop();

            if (!_expanded)
            {
                Panel.Visibility = Visibility.Collapsed;
                Panel.BeginAnimation(OpacityProperty, null);
                Panel.Opacity = 1;
            }
        }
    }

    private DisplayInfo? CurrentDisplay() =>
        DisplayInfo.Find(_displayOverride) ?? DisplayInfo.Find(Settings.Monitor) ?? DisplayInfo.Find(null);

    private void PositionWindow()
    {
        var work = CurrentDisplay()?.Work ?? SystemParameters.WorkArea;
        if (_displayOverride is not null)
        {
            // 副岛的职责是“每个显示器都能看到”，固定贴该显示器顶部居中，不参与拖拽记忆
            var gap = Settings.Shape == IslandShape.Capsule || Settings.Shape == IslandShape.Indicator ? 8.0 : -1.0;
            Left = Math.Round(work.Left + (work.Width - Width) / 2);
            Top = Math.Round(work.Top + (_expanded ? -1.0 : gap) + Math.Max(0, Settings.MarginTop));
            return;
        }

        if (Settings.DragX is double dragX && Settings.DragY is double dragY)
        {
            // 拖拽位置：允许略微出界，但保留可抓回的余量
            Left = Math.Round(Math.Clamp(work.Left + dragX, work.Left - Width + 48, work.Right - 48));
            Top = Math.Round(Math.Clamp(work.Top + dragY, work.Top - Height + 24, work.Bottom - 24));
            return;
        }

        var margin = Settings.MarginX;
        var left = Settings.Position switch
        {
            IslandPosition.TopLeft => work.Left + margin,
            IslandPosition.TopRight => work.Right - Width - margin,
            _ => work.Left + (work.Width - Width) / 2,
        };

        Left = Math.Round(left);

        // 刘海贴顶；胶囊与指示条往下让一点，收起时不再“吊”在屏幕最上沿
        var collapsedGap = Settings.Shape switch
        {
            IslandShape.Capsule => 8.0,
            IslandShape.Indicator => 8.0,
            _ => -1.0,
        };
        Top = Math.Round(work.Top + (_expanded ? -1.0 : collapsedGap) + Math.Max(0, Settings.MarginTop));
    }

    public void RefreshAppearance() => ApplyAppearance();

    private void ApplyAppearance()
    {
        Background = Brushes.Transparent;
        Surface.Shape = Settings.Shape;
        Surface.NotchWidth = Settings.NotchWidth;
        Surface.CapsuleWidth = Settings.CapsuleWidth;
        Surface.IndicatorWidth = Settings.IndicatorWidth;
        Surface.IndicatorThickness = Settings.IndicatorThickness;
        Surface.Height = Settings.Height;
        Surface.Shoulder = Settings.Shoulder;
        Surface.Corner = Settings.Corner;
        Surface.PanelRadius = Settings.PanelRadius;
        Surface.Refresh();

        var (w, h) = _expanded ? ExpandedSize() : CollapsedSize();
        Width = w;
        Height = h;
        PositionWindow();

        ApplyCompactContent();

        ClockText.Foreground = ThemeRuntime.Text;
        WeatherText.Foreground = ThemeRuntime.Text;
        WeatherIcon.Foreground = ThemeRuntime.Text2;
        Compact.Margin = Settings.Shape == IslandShape.Indicator
            ? new Thickness(0)
            : new Thickness(0, 0, 0, 0);

        if (Settings.Shape == IslandShape.Indicator)
        {
            ClockText.Visibility = Visibility.Collapsed;
            WeatherStack.Visibility = Visibility.Collapsed;
        }

        UpdateNavVisuals(_current?.Key ?? "home");
    }

    /// <summary>收起态显示什么，由设置里的 CompactContent 决定。</summary>
    private void ApplyCompactContent()
    {
        var content = Settings.CompactContent ?? "clockWeather";

        var clock = Settings.CompactView == "clock";
        var weather = Settings.ShowWeather;

        switch (content)
        {
            case "clock":
                weather = false;
                break;
            case "weather":
                clock = false;
                break;
            case "media":
                clock = false;
                weather = false;
                break;
            case "none":
                clock = false;
                weather = false;
                break;
        }

        if (Settings.Shape == IslandShape.Indicator)
        {
            clock = false;
            weather = false;
        }

        ClockText.Visibility = clock ? Visibility.Visible : Visibility.Collapsed;
        WeatherStack.Visibility = weather ? Visibility.Visible : Visibility.Collapsed;
        MediaText.Visibility = content == "media" && Settings.Shape != IslandShape.Indicator
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (content == "media")
        {
            var media = _runtime.Media.Current;
            MediaText.Text = media is null
                ? "未在播放"
                : (string.IsNullOrWhiteSpace(media.Artist) ? media.Title : media.Title + " · " + media.Artist);
        }
    }

    private void UpdateClock()
    {
        if (Settings.CompactView != "clock" || Settings.Shape == IslandShape.Indicator)
        {
            return;
        }

        var now = DateTime.Now.ToString("HH:mm");
        ClockText.Text = now;
        PanelClock.Text = now;
    }

    /// <summary>紧凑态右下角天气。</summary>
    private void ApplyWeather(Kojima.Core.Models.WeatherInfo info)
    {
        if (info is null || _demo)
        {
            return;
        }

        WeatherIcon.Text = info.Glyph;
        WeatherText.Text = info.TemperatureText;
        WeatherText.Foreground = ThemeRuntime.Text;
        WeatherIcon.Foreground = ThemeRuntime.Text2;
    }

    private bool _lastInside;
    private bool _lastExpanded;

    private void PollHover()
    {
        var inside = IsCursorInside();
        if (inside != _lastInside || _expanded != _lastExpanded)
        {
            _lastInside = inside;
            _lastExpanded = _expanded;
            NativeMethods.GetCursorPos(out var probe);
            AppLog.Info($"hover inside={inside} expanded={_expanded} cursor={probe.X},{probe.Y} " +
                        $"win={Left},{Top} {ActualWidth:0}x{ActualHeight:0} mode={Settings.ExpandMode}");
        }

        if (!inside)
        {
            _hoverSince = default;
            return;
        }

        if (_expanded)
        {
            return;
        }

        if (Settings.ExpandMode == "never")
        {
            return;
        }

        if (_hoverSince == default)
        {
            _hoverSince = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _hoverSince).TotalMilliseconds >= Settings.HoverExpandMs)
        {
            _hoverSince = default;
            ShowPage(_current?.Key ?? "home");
            _pinned = false;
        }
    }

    private void PollOutside()
    {
        if (!_expanded)
        {
            _outsideSince = default;
            return;
        }

        if (_pinned)
        {
            return;
        }

        if (IsCursorInside())
        {
            _outsideSince = default;
            return;
        }

        if (_outsideSince == default)
        {
            _outsideSince = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _outsideSince).TotalMilliseconds >= Settings.CollapseDelayMs)
        {
            _outsideSince = default;
            Collapse();
        }
    }

    private Point CursorDip()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return new Point(double.NaN, double.NaN);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(point.X / dpi.DpiScaleX, point.Y / dpi.DpiScaleY);
    }

    private bool IsCursorInside()
    {
        var cursor = CursorDip();
        if (double.IsNaN(cursor.X))
        {
            return true;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = PointToScreen(new Point(0, 0));
        var screen = new Point(origin.X / dpi.DpiScaleX, origin.Y / dpi.DpiScaleY);
        var inside = cursor.X >= screen.X - 2
                     && cursor.Y >= screen.Y - 2
                     && cursor.X <= screen.X + ActualWidth + 2
                     && cursor.Y <= screen.Y + ActualHeight + 2;

        if (!inside && !_expanded)
        {
            // 收起态感应区：把岛体与屏幕顶边之间的空隙桥接进来（对齐 BrimDeck 的 HoverExtensionBox）
            var work = CurrentDisplay()?.Work ?? SystemParameters.WorkArea;
            var left = Math.Max(work.Left, screen.X - 4);
            var right = Math.Min(work.Right, screen.X + ActualWidth + 4);
            inside = cursor.X >= left
                     && cursor.X <= right
                     && cursor.Y >= work.Top - 1
                     && cursor.Y <= screen.Y + ActualHeight + 4;
        }

        return inside;
    }

    private void PollFullscreen()
    {
        if (!Settings.HideFullscreen)
        {
            if (_hiddenForFullscreen)
            {
                Show();
                _hiddenForFullscreen = false;
            }

            return;
        }

        var fullscreen = NativeMethods.IsForegroundFullscreen();
        if (fullscreen && !_hiddenForFullscreen)
        {
            Hide();
            _hiddenForFullscreen = true;
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            Show();
            _hiddenForFullscreen = false;
        }
    }

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyAppearance();
            Surface.Refresh();
            UpdateNavVisuals(_current?.Key ?? "home");
        });
    }

    private Point ToScreenDip(Point client)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = PointToScreen(client);
        return new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);
    }

    private void OnDragPress(object sender, MouseButtonEventArgs e)
    {
        if (_displayOverride is not null)
        {
            // 副岛固定在所属显示器顶部，不允许拖动/缩放
            return;
        }

        _suppressClick = false;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var point = ToScreenDip(e.GetPosition(this));
        if (_expanded)
        {
            // 展开面板右下角 18px 作为缩放热区
            if (e.GetPosition(this).X >= ActualWidth - ResizeEdge &&
                e.GetPosition(this).Y >= ActualHeight - ResizeEdge)
            {
                _resizing = true;
                _pressScreen = point;
                _resizeStartWidth = Width;
                CaptureMouse();
            }

            return;
        }

        _dragArmed = true;
        _pressScreen = point;
        _pressLeft = Left;
        _pressTop = Top;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = ToScreenDip(e.GetPosition(this));
        if (_resizing)
        {
            Width = Math.Clamp(_resizeStartWidth + (point.X - _pressScreen.X), 420, 900);
            _suppressClick = true;
            return;
        }

        if (!_dragArmed)
        {
            return;
        }

        if (!_dragging)
        {
            if (Math.Abs(point.X - _pressScreen.X) < DragThreshold &&
                Math.Abs(point.Y - _pressScreen.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            CaptureMouse();
        }

        Left = _pressLeft + (point.X - _pressScreen.X);
        Top = _pressTop + (point.Y - _pressScreen.Y);
        _suppressClick = true;
    }

    private void OnDragRelease(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_resizing)
        {
            _resizing = false;
            Settings.PanelWidth = Math.Round(Width);
            _runtime.SaveConfig();
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            _dragArmed = false;
            return;
        }

        _dragging = false;
        _dragArmed = false;
        ReleaseMouseCapture();

        var display = DisplayInfo.At(Left + Width / 2, Top + Height / 2) ?? CurrentDisplay();
        if (display is not null)
        {
            var work = display.Work;
            var x = Left - work.Left;
            var y = Top - work.Top;
            if (Math.Abs(Left - work.Left) <= 16)
            {
                x = 0;
            }
            else if (Math.Abs(Left + Width - work.Right) <= 16)
            {
                x = work.Width - Width;
            }

            if (Math.Abs(Top - work.Top) <= 16)
            {
                y = 0;
            }

            Settings.Monitor = display.DeviceName;
            Settings.DragX = Math.Round(x);
            Settings.DragY = Math.Round(y);
            PositionWindow();
            _runtime.SaveConfig();
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_expanded)
        {
            OpenSettings();
            e.Handled = true;
        }
    }

    private void OpenSettings()
    {
        // 统一入口：避免齿轮/右键各开一个设置窗口
        App.ShowSettingsDialog();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (Settings.ExpandMode != "never")
        {
            _hoverSince = DateTime.UtcNow;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverSince = default;
        if (_expanded && !_pinned)
        {
            _outsideSince = DateTime.UtcNow;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_expanded && !_suppressClick)
        {
            ShowPage(_current?.Key ?? "home");
        }

        _suppressClick = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeRuntime.Changed -= OnThemeChanged;
        _clock.Stop();
        _hover.Stop();
        _outside.Stop();
        _fullscreen.Stop();
        base.OnClosed(e);
    }

    private sealed class IslandPage(string key, string glyph, string title, FrameworkElement view, double heightHint)
    {
        public string Key { get; } = key;
        public string Glyph { get; } = glyph;
        public string Title { get; } = title;
        public FrameworkElement View { get; } = view;
        public double HeightHint { get; } = heightHint;
        public Action? Shown { get; set; }
        public Action? Hidden { get; set; }
    }
}
