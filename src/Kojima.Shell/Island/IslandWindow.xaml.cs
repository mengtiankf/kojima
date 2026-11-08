using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Kojima.Shell.Interop;
using Kojima.Shell.Theme;
using Kojima.Shell.Views;

namespace Kojima.Shell.Island;

/// <summary>灵动岛主窗口：收起态图标 + 悬停展开的面板。</summary>
public partial class IslandWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _hover = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _outside = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _fullscreen = new(DispatcherPriority.Background);
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

    public IslandWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowStyles();
        Loaded += (_, _) => Initialise();

        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateClock();

        _hover.Interval = TimeSpan.FromMilliseconds(40);
        _hover.Tick += (_, _) => PollHover();

        _outside.Interval = TimeSpan.FromMilliseconds(120);
        _outside.Tick += (_, _) => PollOutside();

        _fullscreen.Interval = TimeSpan.FromSeconds(1);
        _fullscreen.Tick += (_, _) => PollFullscreen();

        ThemeRuntime.Changed += OnThemeChanged;
        SizeChanged += (_, _) => Surface.Refresh();
        _runtime.Weather.Updated += info => Dispatcher.BeginInvoke(() => ApplyWeather(info));
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

    private void BuildPages()
    {
        var home = new HomeView(_runtime);
        _pages.Add(new IslandPage("home", "\uE80F", "主页", home, 300));

        if (_runtime.FeatureEnabled("usage"))
        {
            var usage = new UsageView();
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

        if (_runtime.FeatureEnabled("monitor"))
        {
            var monitor = new MonitorView();
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

    private void BuildNav()
    {
        NavRow.Children.Clear();
        var host = new Grid();

        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var page in _pages)
        {
            left.Children.Add(CreateNavItem(page));
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(CreateGlyphButton("\uE713", "设置", OpenSettings));

        host.Children.Add(left);
        host.Children.Add(right);
        NavRow.Children.Add(host);
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

        pill.MouseEnter += (_, _) => UpdateNavVisuals(page.Key, hover: true);
        pill.MouseLeave += (_, _) => UpdateNavVisuals(page.Key, hover: false);
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

        Panel.Visibility = Visibility.Collapsed;
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
        Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
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

    private void AnimateTo(double width, double height, bool expanded)
    {
        _targetW = Math.Max(1, width);
        _targetH = Math.Max(1, height);
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var k = 0.28;
        Width += (_targetW - Width) * k;
        Height += (_targetH - Height) * k;
        PositionWindow();

        if (Math.Abs(_targetW - Width) < 0.6 && Math.Abs(_targetH - Height) < 0.6)
        {
            Width = _targetW;
            Height = _targetH;
            PositionWindow();
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void PositionWindow()
    {
        var work = SystemParameters.WorkArea;
        var margin = Settings.MarginX;
        var left = Settings.Position switch
        {
            IslandPosition.TopLeft => work.Left + margin,
            IslandPosition.TopRight => work.Right - Width - margin,
            _ => work.Left + (work.Width - Width) / 2,
        };

        Left = Math.Round(left);
        Top = Math.Round(work.Top - 1);
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

        ClockText.Visibility = Settings.CompactView == "clock" ? Visibility.Visible : Visibility.Collapsed;
        WeatherStack.Visibility = Settings.ShowWeather ? Visibility.Visible : Visibility.Collapsed;

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

    private void UpdateClock()
    {
        if (Settings.CompactView != "clock" || Settings.Shape == IslandShape.Indicator)
        {
            return;
        }

        ClockText.Text = DateTime.Now.ToString("HH:mm");
    }

    /// <summary>紧凑态右下角天气。</summary>
    private void ApplyWeather(Kojima.Shell.Services.WeatherInfo info)
    {
        if (info is null)
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

    private bool IsCursorInside()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return true;
        }

        var screen = PointToScreen(new Point(0, 0));
        return point.X >= screen.X - 2
               && point.Y >= screen.Y - 2
               && point.X <= screen.X + ActualWidth + 2
               && point.Y <= screen.Y + ActualHeight + 2;
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

    private void OpenSettings()
    {
        var window = new Settings.SettingsWindow(_runtime);
        window.Saved += () =>
        {
            ApplyAppearance();
            BuildNav();
        };
        window.Show();
        window.Activate();
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
        if (!_expanded)
        {
            ShowPage(_current?.Key ?? "home");
        }
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
