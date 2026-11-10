using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Threading.Tasks;
using Kojima.Core.Config;
using Kojima.Core.Themes;
using Kojima.Shell.Theme;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using PasswordBox = System.Windows.Controls.PasswordBox;
using RadioButton = System.Windows.Controls.RadioButton;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Window = System.Windows.Window;

namespace Kojima.Shell.Settings;

/// <summary>设置窗口：左侧导航 + 分组卡片 + 沉浸式滚动条。</summary>
public partial class SettingsWindow : Window
{
    private const string SecretMask = "••••••";

    private static readonly (string Key, string Label, string Desc)[] FeatureDefs =
    {
        ("usage", "AI 用量", "Claude、Codex、Cursor 等来源的额度与消耗"),
        ("monitor", "系统监控", "CPU、内存、温度与功耗"),
        ("media", "媒体", "正在播放的封面与播放控制"),
        ("weather", "天气", "当前天气与短期预报"),
        ("stocks", "股票", "自选行情与涨跌"),
        ("clipboard", "剪贴板", "最近复制的文本与图片"),
        ("translate", "翻译", "选中文本的快速翻译"),
        ("live", "实时字幕", "系统音频的实时转写"),
    };

    private static readonly (string Key, string Label)[] SourceDefs =
    {
        ("claude", "Claude"), ("codex", "Codex"), ("cursor", "Cursor"), ("antigravity", "Antigravity"),
        ("dsh", "DSH"), ("kimi", "Kimi"), ("opencode", "OpenCode"), ("zcode", "ZCode"),
        ("grokbot", "GrokBot"), ("deepseek", "DeepSeek"), ("openrouter", "OpenRouter"), ("glm", "GLM 智谱"),
        ("newapi", "NewAPI"), ("sub2api", "Sub2API"),
    };

    private static readonly (string Key, string Label)[] SecretDefs =
    {
        ("claude", "Claude"), ("cursor", "Cursor"), ("zcode", "ZCode"), ("kimi", "Kimi"),
        ("deepseek", "DeepSeek"), ("openrouter", "OpenRouter"), ("glm", "GLM 智谱"),
        ("newapi", "NewAPI"), ("sub2api", "Sub2API"),
    };

    private static readonly (string Title, string Desc)[] PageMeta =
    {
        ("通用", "启动方式、灵动岛的形态与基础交互。"),
        ("外观", "主题与配色；点击主题会立即应用并保存。"),
        ("功能", "按需启动的模块、系统监控数据源与空闲回收。"),
        ("用量", "各 AI 服务的用量来源、密钥与刷新节奏。"),
        ("关于", "版本信息、仓库链接与开源致谢。"),
        ("模块设置", "该模块的详细设置。"),
    };

    private readonly AppRuntime _runtime;
    private readonly List<FeatureRow> _features = new();
    private readonly List<SourceRow> _sources = new();
    private readonly List<SecretRow> _secrets = new();
    private readonly Dictionary<ScrollViewer, ScrollBar> _bars = new();
    private readonly Dictionary<ScrollViewer, DispatcherTimer> _hideTimers = new();
    private ScrollViewer[] _pages = Array.Empty<ScrollViewer>();
    private bool _loading = true;
    private bool _saving;

    public SettingsWindow(AppRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
        _pages = new[] { PageGeneral, PageAppearance, PageFeatures, PageUsage, PageAbout, PageModule };
        foreach (var page in _pages)
        {
            page.RenderTransform = new TranslateTransform();
        }

        // 主题列表自身不滚动，把滚轮交给外观页，避免滚轮被吃掉
        ThemeList.PreviewMouseWheel += (_, e) =>
        {
            PageAppearance.ScrollToVerticalOffset(PageAppearance.VerticalOffset - (e.Delta * 0.5));
            e.Handled = true;
        };

        BrowseMusic.Click += (_, _) => BrowseForMusic();
        ApplyVersion();
        ApplyThemeResources();
        LoadConfig();
        _loading = false;
        ThemeRuntime.Changed += OnThemeRuntimeChanged;
        Loaded += OnLoaded;
    }

    public event Action? Saved;

    private void ApplyVersion()
    {
        var version = typeof(SettingsWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.2.1";
        var dash = version.IndexOf('+', StringComparison.Ordinal);
        if (dash > 0)
        {
            version = version[..dash];
        }

        VersionLabel.Text = version;
        AboutVersionBadge.Text = version;
        AboutVersionLine.Text = $"Kojima {version} · Windows · .NET 10 · WPF";
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeRuntime.Changed -= OnThemeRuntimeChanged;
        foreach (var timer in _hideTimers.Values)
        {
            timer.Stop();
        }

        _hideTimers.Clear();
        _bars.Clear();
        base.OnClosed(e);
    }

    // ==================================================================
    // 主题画刷注入（全部 DynamicResource，主题切换即时刷新）
    // ==================================================================
    private void ApplyThemeResources()
    {
        var p = ThemeRuntime.Palette;
        var dark = p.Dark;

        Set("SurfaceBrush", ThemeRuntime.SurfaceBrush);
        Set("TextBrush", ThemeRuntime.Text);
        Set("Text2Brush", ThemeRuntime.Text2);
        Set("Text3Brush", ThemeRuntime.Text3);
        Set("AccentBrush", ThemeRuntime.Accent);
        Set("OnAccentBrush", ThemeRuntime.OnAccent);
        Set("DangerBrush", ThemeRuntime.Danger);
        Set("HairlineBrush", ThemeRuntime.Hairline);
        Set("TrackBrush", ThemeRuntime.Track);
        Set("FillBrush", ThemeRuntime.Fill);
        Set("HoverBrush", ThemeRuntime.Hover);
        Set("PressedBrush", ThemeRuntime.Pressed);
        Set("OutlineBrush", ThemeRuntime.Outline);
        Set("IndicatorBrush", ThemeRuntime.Indicator);
        // 卡片比面板底略亮一档；hover 再亮一点
        Set("CardBrush", Tint(dark ? (byte)0x16 : (byte)0x0A, p.Text));
        Set("CardHoverBrush", Tint(dark ? (byte)0x26 : (byte)0x16, p.Text));
        Set("NavBrush", Tint(dark ? (byte)0x0A : (byte)0x05, p.Text));
        Set("AccentSoftBrush", Tint(0x2E, p.Accent));
        Set("ScrollThumbBrush", Tint(0x73, p.Text3));
        Set("ScrollThumbHoverBrush", Tint(0xBF, p.Text3));

        void Set(string key, Brush brush)
        {
            if (brush.CanFreeze && !brush.IsFrozen)
            {
                brush.Freeze();
            }

            Resources[key] = brush;
        }
    }

    private static SolidColorBrush Tint(byte alpha, Rgba color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void OnThemeRuntimeChanged()
    {
        ApplyThemeResources();
        UpdateCurrentThemeText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var page in _pages)
        {
            HookScrollbar(page);
        }

        // 轻淡入，避免窗口直接“砸”出来（不做位移，免得圆角被窗口边界裁掉）
        Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ==================================================================
    // 加载
    // ==================================================================
    private void LoadConfig()
    {
        var cfg = _runtime.Config;

        StartSwitch.IsChecked = cfg.StartWithWindows;
        (string.Equals(cfg.Language, "en-US", StringComparison.OrdinalIgnoreCase) ? LanguageEn : LanguageZh).IsChecked = true;

        PosCenter.IsChecked = cfg.Island.Position == IslandPosition.TopCenter;
        PosLeft.IsChecked = cfg.Island.Position == IslandPosition.TopLeft;
        PosRight.IsChecked = cfg.Island.Position == IslandPosition.TopRight;

        ShapeNotch.IsChecked = cfg.Island.Shape == IslandShape.Notch;
        ShapeCapsule.IsChecked = cfg.Island.Shape == IslandShape.Capsule;
        ShapeIndicator.IsChecked = cfg.Island.Shape == IslandShape.Indicator;

        var compact = (cfg.Island.CompactContent ?? "clockWeather").Trim();
        (compact switch
        {
            "clock" => CompactClock,
            "weather" => CompactWeather,
            "media" => CompactMedia,
            "none" => CompactNone,
            _ => CompactClockWeather,
        }).IsChecked = true;

        HideFullscreenSwitch.IsChecked = cfg.Island.HideFullscreen;
        HoverSlider.Value = Math.Clamp(cfg.Island.HoverExpandMs, 0, 600);
        CollapseSlider.Value = Math.Clamp(cfg.Island.CollapseDelayMs, 0, 1000);
        OpacitySlider.Value = Math.Clamp(cfg.Island.Opacity, 0.6, 1.0);

        var monitorSource = (cfg.Monitor.Source ?? "auto").Trim().ToLowerInvariant();
        (monitorSource switch
        {
            "internal" => MonitorInternal,
            "lhm" => MonitorLhm,
            "ohm" => MonitorOhm,
            _ => MonitorAuto,
        }).IsChecked = true;
        DetectedSourcesText.Text = cfg.Monitor.DetectedSources.Count > 0
            ? "检测到：" + string.Join("、", cfg.Monitor.DetectedSources)
            : "未检测到外部监控软件";

        StockSymbols.Text = string.Join(",", cfg.Stocks.Symbols);
        StockRefresh.Text = cfg.Stocks.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        WeatherLocation.Text = cfg.Weather.Location ?? "";
        MusicPath.Text = cfg.Apps.Music ?? "";
        var engine = (cfg.Translate.Engine ?? "auto").Trim().ToLowerInvariant();
        (engine switch
        {
            "free" => TranslateGoogle,
            "google" => TranslateGoogle,
            "mymemory" => TranslateMyMemory,
            "deepseek" => TranslateDeepSeek,
            "baidu" => TranslateBaidu,
            "microsoft" => TranslateMicrosoft,
            "custom" => TranslateCustom,
            _ => TranslateAuto,
        }).IsChecked = true;

        BaiduAppId.Text = cfg.Translate.BaiduAppId ?? "";
        SetSecret(BaiduKey, SecretNames.TranslateBaiduKey);
        SetSecret(MicrosoftKey, SecretNames.TranslateMicrosoftKey);
        MicrosoftRegion.Text = cfg.Translate.MicrosoftRegion ?? "";
        CustomEndpoint.Text = cfg.Translate.CustomEndpoint ?? "";
        CustomModel.Text = cfg.Translate.CustomModel ?? "";
        SetSecret(CustomKey, SecretNames.TranslateCustomKey);

        IdleExitBox.Text = cfg.Feature("monitor").IdleExitSeconds.ToString(CultureInfo.InvariantCulture);
        RefreshBox.Text = cfg.Usage.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        ThemePathText.Text = ConfigStore.ThemesDirectory;

        BuildThemes();
        UpdateCurrentThemeText();
        BuildFeatures();
        BuildSources();
        BuildSecrets();

        NavGeneral.IsChecked = true;
    }

    private void BuildThemes()
    {
        var cards = ThemeCatalog.All.Select(ThemeCard.From).ToList();
        ThemeList.ItemsSource = cards;
        ThemeList.SelectedItem = cards.FirstOrDefault(c =>
            string.Equals(c.Id, _runtime.Config.Island.Theme, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCurrentThemeText()
    {
        var palette = ThemeCatalog.Get(_runtime.Config.Island.Theme);
        var byLine = string.IsNullOrWhiteSpace(palette.Author) ? "Kojima 内置" : palette.Author;
        CurrentThemeText.Text = $"{palette.Name} · {byLine}";
    }

    private void BuildFeatures()
    {
        if (FeatureList.Children.Count > 0)
        {
            return;
        }

        BuildModuleOrder();
        RebuildFeatureList();
    }

    private void BuildSources()
    {
        var selected = new HashSet<string>(
            _runtime.Config.Usage.Sources ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, label) in SourceDefs)
        {
            var check = new CheckBox
            {
                Style = (Style)FindResource("Check"),
                Content = label,
                IsChecked = selected.Contains(key),
                Margin = new Thickness(0, 0, 18, 12),
            };
            SourceList.Children.Add(check);
            _sources.Add(new SourceRow(key, check));
        }
    }

    private void BuildSecrets()
    {
        foreach (var (key, label) in SecretDefs)
        {
            var box = new PasswordBox
            {
                Style = (Style)FindResource("PwdField"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            SetSecret(box, SecretNames.Usage(key));

            var row = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var caption = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");

            row.Children.Add(caption);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            SecretsHost.Children.Add(row);
            _secrets.Add(new SecretRow(key, box));
        }
    }

    /// <summary>
    /// 密钥框：已有密钥只显示掩码；聚焦时全选，输入即替换，清空后保存即删除。
    /// 真实值不会进入控件，避免界面/屏幕录制泄露。
    /// </summary>
    private static void SetSecret(PasswordBox box, string name)
    {
        var stored = SecretStore.Get(name);
        if (stored.Length == 0)
        {
            return;
        }

        box.Password = SecretMask;
        box.ToolTip = "已用 Windows DPAPI 加密保存；清空后保存可删除";
        box.GotKeyboardFocus += (_, _) =>
        {
            if (box.Password == SecretMask)
            {
                box.SelectAll();
            }
        };
        box.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (box.Password != SecretMask)
            {
                return;
            }

            box.Focus();
            box.SelectAll();
            args.Handled = true;
        };
    }

    /// <summary>把未改动的掩码跳过，其余（含空串=删除）交给密钥库。</summary>
    private static void CollectSecret(
        List<KeyValuePair<string, string?>> updates, PasswordBox box, string name)
    {
        var value = box.Password;
        if (value == SecretMask || value.Contains(SecretMask, StringComparison.Ordinal))
        {
            return;
        }

        updates.Add(new KeyValuePair<string, string?>(name, value.Trim()));
    }

    // ==================================================================
    // 沉浸式滚动条：默认透明，进入滚动区域淡入，离开 1s 后淡出
    // ==================================================================
    private void HookScrollbar(ScrollViewer page)
    {
        if (!_bars.ContainsKey(page))
        {
            page.ApplyTemplate();
            if (page.Template?.FindName("PART_VerticalScrollBar", page) is not ScrollBar bar)
            {
                return;
            }

            _bars[page] = bar;
            page.MouseEnter += (_, _) => ShowBar(page);
            page.MouseLeave += (_, _) => HideBarLater(page);
            page.ScrollChanged += (_, _) =>
            {
                if (page.IsMouseOver)
                {
                    ShowBar(page);
                }
            };
        }

        if (page.IsMouseOver && _bars.TryGetValue(page, out var current))
        {
            FadeBar(current, 1);
        }
    }

    private void ShowBar(ScrollViewer page)
    {
        if (_hideTimers.TryGetValue(page, out var timer))
        {
            timer.Stop();
        }

        if (_bars.TryGetValue(page, out var bar))
        {
            FadeBar(bar, 1);
        }
    }

    private void HideBarLater(ScrollViewer page)
    {
        if (!_hideTimers.TryGetValue(page, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (page.IsMouseOver)
                {
                    return;
                }

                if (_bars.TryGetValue(page, out var bar))
                {
                    FadeBar(bar, 0);
                }
            };
            _hideTimers[page] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private static void FadeBar(UIElement bar, double target)
    {
        bar.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(target > 0 ? 140 : 260),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    // ==================================================================
    // 导航 / 页面切换
    // ==================================================================
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index))
        {
            return;
        }

        if (_pages.Length == 0 || index < 0 || index >= _pages.Length)
        {
            return;
        }

        ShowPage(index);
    }

    /// <summary>供开发钩子直接切页（0 通用 / 1 外观 / 2 功能 / 3 用量 / 4 关于）。</summary>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        var radio = NavList.Children.OfType<RadioButton>()
            .FirstOrDefault(r => (r.Tag as string) == index.ToString(CultureInfo.InvariantCulture));
        if (radio is not null)
        {
            radio.IsChecked = true;
        }

        ShowPage(index);
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var page = _pages[i];
            if (i != index)
            {
                page.BeginAnimation(OpacityProperty, null);
                page.Visibility = Visibility.Collapsed;
                continue;
            }

            page.Visibility = Visibility.Visible;
            page.ScrollToTop();
            HookScrollbar(page);
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
            if (page.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            }
        }

        PageTitleText.Text = PageMeta[index].Title;
        PageDescText.Text = PageMeta[index].Desc;
    }

    // ==================================================================
    // 窗口交互
    // ==================================================================
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1)
        {
            return;
        }

        // 标题栏里的按钮 / 输入控件自己处理点击，不要被拖动吃掉
        for (var node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (node is ButtonBase or TextBoxBase or ScrollBar or Thumb or Slider)
            {
                return;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 拖动被打断，忽略
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnGitHubNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开链接：" + ex.Message;
        }

        e.Handled = true;
    }

    private void OnOpenThemesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.ThemesDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", ConfigStore.ThemesDirectory) { UseShellExecute = true });
            StatusText.Text = "已在资源管理器中打开主题目录";
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开目录：" + ex.Message;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeList.SelectedItem is not ThemeCard card)
        {
            return;
        }

        _runtime.ApplyTheme(card.Id);
        UpdateCurrentThemeText();
        StatusText.Text = $"已应用主题：{card.DisplayName}（主题会立即保存）";
    }

    // ==================================================================
    // 保存
    // ==================================================================
    private async void OnSaveClick(object sender, RoutedEventArgs e) => await ApplyAsync(close: true);

    private async void OnApplyClick(object sender, RoutedEventArgs e) => await ApplyAsync(close: false);

    private async Task ApplyAsync(bool close)
    {
        if (_saving)
        {
            return;
        }

        _saving = true;
        ApplyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在保存…";
        try
        {
            var cfg = _runtime.Config;
            cfg.StartWithWindows = StartSwitch.IsChecked == true;
            cfg.Language = LanguageEn.IsChecked == true ? "en-US" : "zh-CN";

            cfg.Island.Position = PosLeft.IsChecked == true ? IslandPosition.TopLeft
                : PosRight.IsChecked == true ? IslandPosition.TopRight
                : IslandPosition.TopCenter;
            cfg.Island.Shape = ShapeCapsule.IsChecked == true ? IslandShape.Capsule
                : ShapeIndicator.IsChecked == true ? IslandShape.Indicator
                : IslandShape.Notch;
            cfg.Island.HideFullscreen = HideFullscreenSwitch.IsChecked == true;
            cfg.Island.HoverExpandMs = (int)Math.Round(HoverSlider.Value);
            cfg.Island.CollapseDelayMs = (int)Math.Round(CollapseSlider.Value);
            cfg.Island.Opacity = Math.Round(OpacitySlider.Value, 2);
            cfg.Island.CompactContent = CompactClock.IsChecked == true ? "clock"
                : CompactWeather.IsChecked == true ? "weather"
                : CompactMedia.IsChecked == true ? "media"
                : CompactNone.IsChecked == true ? "none"
                : "clockWeather";

            cfg.Monitor.Source = MonitorInternal.IsChecked == true ? "internal"
                : MonitorLhm.IsChecked == true ? "lhm"
                : MonitorOhm.IsChecked == true ? "ohm"
                : "auto";

            var symbols = (StockSymbols.Text ?? "")
                .Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (symbols.Count > 0)
            {
                cfg.Stocks.Symbols = symbols;
            }

            if (int.TryParse(StockRefresh.Text, out var refreshSeconds))
            {
                cfg.Stocks.RefreshSeconds = Math.Clamp(refreshSeconds, 5, 600);
            }

            cfg.Weather.Location = (WeatherLocation.Text ?? "").Trim();
            cfg.Apps.Music = (MusicPath.Text ?? "").Trim();
            cfg.Translate.Engine = TranslateGoogle.IsChecked == true ? "google"
                : TranslateMyMemory.IsChecked == true ? "mymemory"
                : TranslateDeepSeek.IsChecked == true ? "deepseek"
                : TranslateBaidu.IsChecked == true ? "baidu"
                : TranslateMicrosoft.IsChecked == true ? "microsoft"
                : TranslateCustom.IsChecked == true ? "custom"
                : "auto";
            cfg.Translate.BaiduAppId = (BaiduAppId.Text ?? "").Trim();
            cfg.Translate.MicrosoftRegion = (MicrosoftRegion.Text ?? "").Trim();
            cfg.Translate.CustomEndpoint = (CustomEndpoint.Text ?? "").Trim();
            cfg.Translate.CustomModel = (CustomModel.Text ?? "").Trim();

            // 密钥只进 secrets.dat（DPAPI 加密），config.json 永远不落明文。
            var secretUpdates = new List<KeyValuePair<string, string?>>();
            CollectSecret(secretUpdates, BaiduKey, SecretNames.TranslateBaiduKey);
            CollectSecret(secretUpdates, MicrosoftKey, SecretNames.TranslateMicrosoftKey);
            CollectSecret(secretUpdates, CustomKey, SecretNames.TranslateCustomKey);

            cfg.Island.NavOrder = _order.ToList();

            // 数据源：保留清单外的历史来源，避免升级后丢失
            var known = new HashSet<string>(_sources.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);
            var sources = _sources.Where(s => s.Toggle.IsChecked == true).Select(s => s.Key).ToList();
            foreach (var extra in cfg.Usage.Sources ?? new List<string>())
            {
                if (!known.Contains(extra))
                {
                    sources.Add(extra);
                }
            }

            cfg.Usage.Sources = sources;
            cfg.Usage.RefreshSeconds = Math.Clamp(ParseInt(RefreshBox.Text, cfg.Usage.RefreshSeconds), 30, 3600);

            var idle = Math.Clamp(ParseInt(IdleExitBox.Text, cfg.Feature("monitor").IdleExitSeconds), 0, 600);
            foreach (var feature in cfg.Features.Values)
            {
                feature.IdleExitSeconds = idle;
            }

            foreach (var secret in _secrets)
            {
                CollectSecret(secretUpdates, secret.Box, SecretNames.Usage(secret.Key));
            }

            if (secretUpdates.Count > 0)
            {
                SecretStore.SetMany(secretUpdates);
            }

            var changed = new List<(string Key, bool Enabled)>();
            foreach (var row in _features)
            {
                var enabled = row.Toggle.IsChecked == true;
                cfg.Feature(row.Key).Enabled = enabled;
                if (enabled != row.Initial)
                {
                    changed.Add((row.Key, enabled));
                }
            }

            _runtime.SaveConfig();
            foreach (var (key, enabled) in changed)
            {
                try
                {
                    await _runtime.SetFeatureAsync(key, enabled);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"切换功能 {key} 失败：{ex.Message}");
                }
            }

            _runtime.Weather.ApplySettings();
            _ = _runtime.Weather.RefreshAsync();

            _pending.Clear();
            _runtime.Island?.ReloadFeatures();

            foreach (var row in _features)
            {
                row.Initial = row.Toggle.IsChecked == true;
            }

            var restartWanted = false;
            if (changed.Count > 0)
            {
                var choice = MessageBox.Show(
                    this,
                    "功能模块的开/关会立即生效。" + Environment.NewLine + "要让所有界面与子进程完全按新配置运行，建议重启 Kojima。" + Environment.NewLine + Environment.NewLine + "现在重启吗？",
                    "重启提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                restartWanted = choice == MessageBoxResult.Yes;
            }

            StatusText.Text = changed.Count > 0 ? "已应用（建议重启一次）" : "已保存";
            Saved?.Invoke();

            if (restartWanted)
            {
                RestartApp();
                return;
            }

            if (close)
            {
                Close();
            }
            else
            {
                _saving = false;
                ApplyButton.IsEnabled = true;
                SaveButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
            _saving = false;
            ApplyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    /// <summary>重启自己：先起新进程，再关掉当前实例（单实例互斥会在旧进程退出后接上）。</summary>
    private void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("重启失败：" + ex.Message);
        }

        Application.Current.Shutdown();
    }

    private void BrowseForMusic()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择音乐程序",
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            MusicPath.Text = dialog.FileName;
        }
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private sealed class FeatureRow(string key, CheckBox toggle, bool initial)
    {
        public string Key { get; } = key;
        public CheckBox Toggle { get; } = toggle;
        public bool Initial { get; set; } = initial;
    }

    private sealed record SourceRow(string Key, CheckBox Toggle);

    private sealed record SecretRow(string Key, PasswordBox Box);

    private sealed record ThemeCard(string Id, string DisplayName, string ByLine, Brush SurfaceSwatch, Brush TextSwatch, Brush AccentSwatch)
    {
        public static ThemeCard From(DeckPalette p) => new(
            p.Id,
            string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
            string.IsNullOrWhiteSpace(p.Author) ? "Kojima 内置" : p.Author,
            ThemeRuntime.Brush(p.Surface),
            ThemeRuntime.Brush(p.Text),
            ThemeRuntime.Brush(p.Accent));
    }
}
