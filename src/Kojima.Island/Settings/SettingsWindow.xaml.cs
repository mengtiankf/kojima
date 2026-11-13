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
using Kojima.Island.Interop;
using Kojima.Island.Theme;
using Kojima.Island.Ui;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using PasswordBox = System.Windows.Controls.PasswordBox;
using RadioButton = System.Windows.Controls.RadioButton;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Window = System.Windows.Window;

namespace Kojima.Island.Settings;

/// <summary>设置窗口：左侧导航 + 分组卡片 + 沉浸式滚动条。</summary>
public partial class SettingsWindow : Window
{
    private const string SecretMask = "••••••";

    private static readonly (string Key, string Label, string Desc)[] FeatureDefs =
    {
        ("usage", "AI 用量", "Claude、Codex、Cursor 等来源的额度与消耗"),
        ("monitor", "系统监控", "CPU、内存、温度与功耗"),
        ("media", "媒体", "正在播放的封面、频谱与播放控制"),
        ("messages", "通知中心", "QQ、微信等应用通知与系统提醒"),
        ("weather", "天气", "当前天气与短期预报"),
        ("stocks", "股票", "自选行情与涨跌"),
        ("clipboard", "剪贴板", "最近复制的文本"),
        ("translate", "翻译", "选中文本的快速翻译"),
        ("controls", "控制", "音量、亮度与截图等快捷操作"),
    };


    internal static IReadOnlyList<string> ConfigurableFeatureKeys => FeatureDefs.Select(def => def.Key).ToArray();

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
        foreach (var radio in new[] { PosCenter, PosLeft, PosRight, ShapeNotch, ShapeCapsule, ShapeIndicator })
        {
            radio.Checked += OnVisualSettingChanged;
        }
        OpacitySlider.ValueChanged += OnVisualSettingChanged;
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
            ?? "1.3.3";
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
        RefreshPreview();
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

        StartSwitch.IsChecked = StartupRegistration.IsEnabled() || cfg.StartWithWindows;
        HotkeyToggle.Text = cfg.Hotkeys.Toggle ?? "";
        HotkeySettings.Text = cfg.Hotkeys.Settings ?? "";
        HotkeyMonitor.Text = cfg.Hotkeys.Monitor ?? "";

        PosCenter.IsChecked = cfg.Island.Position == IslandPosition.TopCenter;
        PosLeft.IsChecked = cfg.Island.Position == IslandPosition.TopLeft;
        PosRight.IsChecked = cfg.Island.Position == IslandPosition.TopRight;

        ShapeNotch.IsChecked = cfg.Island.Shape == IslandShape.Notch;
        ShapeCapsule.IsChecked = cfg.Island.Shape == IslandShape.Capsule;
        ShapeIndicator.IsChecked = cfg.Island.Shape == IslandShape.Indicator;

        var compact = cfg.Island.CompactItems is null
            ? (cfg.Island.CompactContent ?? "clockWeather").Trim()
            : "custom";
        (compact switch
        {
            "clock" => CompactClock,
            "weather" => CompactWeather,
            "media" => CompactMedia,
            "none" => CompactNone,
            "custom" => CompactCustom,
            _ => CompactClockWeather,
        }).IsChecked = true;
        LoadCompactEditor();
        LoadHomeEditor();

        HideFullscreenSwitch.IsChecked = cfg.Island.HideFullscreen;
        HoverSlider.Value = Math.Clamp(cfg.Island.HoverExpandMs, 0, 600);
        CollapseSlider.Value = Math.Clamp(cfg.Island.CollapseDelayMs, 0, 1000);
        OpacitySlider.Value = Math.Clamp(cfg.Island.Opacity, 0.6, 1.0);
        AnimationSlider.Value = Math.Clamp(cfg.Island.AnimationMs, 80, 600);
        MonitorIntervalBox.Text = cfg.Monitor.IntervalMs.ToString(CultureInfo.InvariantCulture);

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

        LoadStockSettings(cfg.Stocks);
        MessagePoll.Text = cfg.Messages.PollMs.ToString(CultureInfo.InvariantCulture);
        WeatherLocation.Text = cfg.Weather.Location ?? "";
        WeatherRefreshBox.Text = cfg.Weather.RefreshMinutes.ToString(CultureInfo.InvariantCulture);
        MusicPath.Text = cfg.Apps.Music ?? "";
        LoadLyricsSettings(cfg.Lyrics);
        var engine = (cfg.Translate.Engine ?? "auto").Trim().ToLowerInvariant();
        (engine switch
        {
            "free" => TranslateTencent,
            "tencent" => TranslateTencent,
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

        var displays = DisplayInfo.All();
        MonitorBox.ItemsSource = displays;
        MonitorBox.DisplayMemberPath = nameof(DisplayInfo.DeviceName);
        MonitorBox.SelectedItem = displays.FirstOrDefault(d =>
                                     string.Equals(d.DeviceName, cfg.Island.Monitor, StringComparison.OrdinalIgnoreCase))
                                 ?? displays.FirstOrDefault(d => d.Primary)
                                 ?? displays.FirstOrDefault();
        PanelWidthSlider.Value = Math.Clamp(cfg.Island.PanelWidth, 420, 900);
        PanelWidthValue.Text = ((int)Math.Round(PanelWidthSlider.Value)) + " px";
        BuildExtraMonitors(cfg, displays);

        var appearance = cfg.Appearance ??= new AppearanceSettings();
        (appearance.Mode switch
        {
            "system" => ModeSystem,
            "daylight" => ModeDaylight,
            _ => ModeManual,
        }).IsChecked = true;
        HighContrastSwitch.IsChecked = appearance.FollowHighContrast;
        AutoUpdateSwitch.IsChecked = cfg.Update.AutoCheck;

        BuildThemes();
        BuildThemeChoices(appearance);
        UpdateCurrentThemeText();
        BuildFeatures();
        BuildUsageSources(cfg);
        RefreshPreview();

        NavGeneral.IsChecked = true;
    }

    private void BuildThemes()
    {
        var cards = ThemeCatalog.All.Select(ThemeCard.From).ToList();
        ThemeList.ItemsSource = cards;
        ThemeList.SelectedItem = cards.FirstOrDefault(c =>
            string.Equals(c.Id, _runtime.Config.Island.Theme, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildExtraMonitors(KojimaConfig cfg, IReadOnlyList<DisplayInfo> displays)
    {
        ExtraMonitorsPanel.Children.Clear();
        var selected = (MonitorBox.SelectedItem as DisplayInfo)?.DeviceName;
        foreach (var display in displays)
        {
            if (string.Equals(display.DeviceName, selected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = display.Primary ? display.DeviceName + "（主显示器）" : display.DeviceName;
            var box = new CheckBox
            {
                Content = label,
                Tag = display.DeviceName,
                Margin = new Thickness(0, 0, 18, 8),
                IsChecked = cfg.Island.ExtraMonitors.Any(name =>
                    string.Equals(name, display.DeviceName, StringComparison.OrdinalIgnoreCase)),
            };
            ExtraMonitorsPanel.Children.Add(box);
        }
    }

    private void BuildThemeChoices(AppearanceSettings appearance)

    {
        var cards = ThemeCatalog.All.Select(ThemeCard.From).ToList();
        LightThemeBox.ItemsSource = cards;
        LightThemeBox.DisplayMemberPath = nameof(ThemeCard.DisplayName);
        LightThemeBox.SelectedItem = cards.FirstOrDefault(c =>
            string.Equals(c.Id, appearance.LightTheme, StringComparison.OrdinalIgnoreCase)) ?? cards.FirstOrDefault();

        DarkThemeBox.ItemsSource = cards;
        DarkThemeBox.DisplayMemberPath = nameof(ThemeCard.DisplayName);
        DarkThemeBox.SelectedItem = cards.FirstOrDefault(c =>
            string.Equals(c.Id, appearance.DarkTheme, StringComparison.OrdinalIgnoreCase)) ?? cards.FirstOrDefault();
    }

    private void OnLoadCurrentThemeClick(object sender, RoutedEventArgs e)
    {
        var palette = ThemeRuntime.Palette;
        EditThemeName.Text = palette.Name + " 副本";
        EditSurface.Text = palette.Surface.Hex;
        EditText.Text = palette.Text.Hex;
        EditAccent.Text = palette.Accent.Hex;
        StatusText.Text = "已取当前主题颜色，可继续微调后保存。";
    }

    private void OnSaveThemeEditorClick(object sender, RoutedEventArgs e)
    {
        if (!Rgba.TryParse(EditSurface.Text, out var surface) ||
            !Rgba.TryParse(EditText.Text, out var text) ||
            !Rgba.TryParse(EditAccent.Text, out var accent))
        {
            StatusText.Text = "颜色格式不正确：#RGB / #RRGGBB / #AARRGGBB";
            return;
        }

        var name = string.IsNullOrWhiteSpace(EditThemeName.Text) ? "我的主题" : EditThemeName.Text.Trim();
        try
        {
            var document = ThemeDerivation.FromSeeds(name, surface.Hex, text.Hex, accent.Hex);
            ThemeDerivation.Save(document);
            ThemeCatalog.Reload();
            _runtime.Config.Appearance.Mode = "manual";
            _runtime.Config.Island.Theme = document.Id;
            BuildThemes();
            BuildThemeChoices(_runtime.Config.Appearance);
            ThemeList.SelectedItem = (ThemeList.ItemsSource as IEnumerable<ThemeCard>)?
                .FirstOrDefault(c => string.Equals(c.Id, document.Id, StringComparison.OrdinalIgnoreCase));
            _runtime.ApplyTheme(document.Id);
            UpdateCurrentThemeText();
            StatusText.Text = $"已保存并应用主题：{document.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存主题失败：" + ex.Message;
        }
    }

    private void OnPanelWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PanelWidthValue is not null)
        {
            PanelWidthValue.Text = ((int)Math.Round(e.NewValue)) + " px";
        }
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        var cfg = _runtime.Config;
        cfg.Island.DragX = null;
        cfg.Island.DragY = null;
        cfg.Island.Monitor = "";
        _runtime.SaveConfig();
        var displays = DisplayInfo.All();
        MonitorBox.SelectedItem = displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
        App.RefreshIsland();
        StatusText.Text = "已清除拖动位置，岛体回到吸附位置。";
    }

    private void UpdateCurrentThemeText()
    {
        var palette = ThemeRuntime.Palette;
        var byLine = string.IsNullOrWhiteSpace(palette.Author) ? "Kojima 内置" : palette.Author;
        var mode = _runtime.Config.Appearance.Mode switch
        {
            "system" => "跟随系统",
            "daylight" => "白天/夜晚",
            _ => null,
        };
        CurrentThemeText.Text = mode is null
            ? $"{palette.Name} · {byLine}"
            : $"{palette.Name} · {byLine} · {mode}";
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

    /// <summary>供开发钩子展开指定下拉框（截图/样式检查）。</summary>
    public void DebugOpenCombo(string name)
    {
        if (FindName(name) is System.Windows.Controls.ComboBox box)
        {
            if (FindName("PageAppearance") is System.Windows.Controls.ScrollViewer page)
            {
                page.ScrollToEnd();
            }

            box.IsDropDownOpen = true;
        }
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

    internal void ScrollCurrentPageToEnd()
    {
        _pages.FirstOrDefault(page => page.Visibility == Visibility.Visible)?.ScrollToEnd();
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

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            var release = await _runtime.Updates.CheckAsync(manual: true);
            if (release is null)
            {
                UpdateStatusText.Text = "已是最新版本";
                return;
            }

            UpdateStatusText.Text = $"发现 {release.Version}";
            var notes = release.Notes.Length > 600 ? release.Notes[..600] + "…" : release.Notes;
            var answer = AppDialog.Confirm(
                this,
                "Kojima 更新",
                $"发现新版本 {release.Version}，是否现在下载并安装？\n\n{notes}",
                "下载并安装",
                "稍后",
                DialogTone.Info);
            if (!answer)
            {
                return;
            }

            var progress = new Progress<double>(value =>
                UpdateStatusText.Text = $"正在下载 {value:P0}");
            var installed = await _runtime.Updates.InstallAsync(release, progress);
            if (installed)
            {
                _runtime.Updates.ShutdownForUpdate();
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

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

    private async Task ApplyAsync(bool close, bool applyServices = true)
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
            var previousPollMs = cfg.Messages.PollMs;
            var previousMonitorSource = cfg.Monitor.Source;
            var previousMonitorInterval = cfg.Monitor.IntervalMs;
            var previousIdle = cfg.Feature("monitor").IdleExitSeconds;
            var previousHotkeys = HotkeySignature(cfg.Hotkeys);
            var hotkeyToggle = (HotkeyToggle.Text ?? "").Trim();
            var hotkeySettings = (HotkeySettings.Text ?? "").Trim();
            var hotkeyMonitor = (HotkeyMonitor.Text ?? "").Trim();
            if (!ValidHotkey(hotkeyToggle) || !ValidHotkey(hotkeySettings) || !ValidHotkey(hotkeyMonitor))
            {
                StatusText.Text = "快捷键格式不正确：请使用 Ctrl+Alt+K 这样的组合，留空表示不注册。";
                var invalid = !ValidHotkey(hotkeyToggle) ? HotkeyToggle
                    : !ValidHotkey(hotkeySettings) ? HotkeySettings
                    : HotkeyMonitor;
                invalid.Focus();
                invalid.SelectAll();
                return;
            }

            if (!TryReadRange(MessagePoll, 500, 10000, "通知读取频率", out var pollMs) ||
                !TryReadRange(StockRefresh, 5, 600, "股票刷新间隔", out var stockRefresh) ||
                !TryReadRange(RefreshBox, 30, 3600, "用量刷新间隔", out var usageRefresh) ||
                !TryReadRange(IdleExitBox, 0, 600, "插件空闲退出秒数", out var idle) ||
                !TryReadRange(WeatherRefreshBox, 5, 180, "天气刷新间隔", out var weatherRefresh) ||
                !TryReadRange(MonitorIntervalBox, 400, 10000, "监控采样间隔", out var monitorInterval))
            {
                return;
            }

            cfg.Hotkeys.Toggle = hotkeyToggle;
            cfg.Hotkeys.Settings = hotkeySettings;
            cfg.Hotkeys.Monitor = hotkeyMonitor;
            cfg.StartWithWindows = StartSwitch.IsChecked == true;

            cfg.Island.Position = PosLeft.IsChecked == true ? IslandPosition.TopLeft
                : PosRight.IsChecked == true ? IslandPosition.TopRight
                : IslandPosition.TopCenter;

            var previousMonitor = cfg.Island.Monitor;
            if (MonitorBox.SelectedItem is DisplayInfo display)
            {
                cfg.Island.Monitor = display.DeviceName;
            }

            if (!string.Equals(previousMonitor, cfg.Island.Monitor, StringComparison.OrdinalIgnoreCase))
            {
                // 换显示器后旧的拖拽偏移不再适用，回到吸附位置
                cfg.Island.DragX = null;
                cfg.Island.DragY = null;
            }

            cfg.Island.PanelWidth = Math.Round(PanelWidthSlider.Value);
            cfg.Island.ExtraMonitors = ExtraMonitorsPanel.Children.OfType<CheckBox>()
                .Where(box => box.IsChecked == true)
                .Select(box => box.Tag as string)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            cfg.Island.Shape = ShapeCapsule.IsChecked == true ? IslandShape.Capsule
                : ShapeIndicator.IsChecked == true ? IslandShape.Indicator
                : IslandShape.Notch;
            cfg.Island.HideFullscreen = HideFullscreenSwitch.IsChecked == true;
            cfg.Island.HoverExpandMs = (int)Math.Round(HoverSlider.Value);
            cfg.Island.CollapseDelayMs = (int)Math.Round(CollapseSlider.Value);
            cfg.Island.Opacity = Math.Round(OpacitySlider.Value, 2);
            cfg.Island.AnimationMs = (int)Math.Round(AnimationSlider.Value);
            ApplyCompactEditor(cfg.Island);
            ApplyHomeEditor(cfg.Island);

            cfg.Appearance.Mode = ModeSystem.IsChecked == true ? "system"
                : ModeDaylight.IsChecked == true ? "daylight"
                : "manual";
            if (LightThemeBox.SelectedItem is ThemeCard lightCard)
            {
                cfg.Appearance.LightTheme = lightCard.Id;
            }

            if (DarkThemeBox.SelectedItem is ThemeCard darkCard)
            {
                cfg.Appearance.DarkTheme = darkCard.Id;
            }

            cfg.Appearance.FollowHighContrast = HighContrastSwitch.IsChecked == true;
            cfg.Update.AutoCheck = AutoUpdateSwitch.IsChecked == true;

            cfg.Monitor.Source = MonitorInternal.IsChecked == true ? "internal"
                : MonitorLhm.IsChecked == true ? "lhm"
                : MonitorOhm.IsChecked == true ? "ohm"
                : "auto";
            cfg.Monitor.IntervalMs = monitorInterval;

            ApplyStockSettings(cfg.Stocks);

            cfg.Messages.PollMs = pollMs;

            cfg.Weather.Location = (WeatherLocation.Text ?? "").Trim();
            cfg.Weather.RefreshMinutes = weatherRefresh;
            cfg.Apps.Music = (MusicPath.Text ?? "").Trim();
            ApplyLyricsSettings(cfg.Lyrics);
            cfg.Translate.Engine = TranslateTencent.IsChecked == true ? "tencent"
                : TranslateGoogle.IsChecked == true ? "google"
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

            ApplyUsageSettings(cfg, secretUpdates);
            cfg.Usage.RefreshSeconds = usageRefresh;

            foreach (var feature in cfg.Features.Values)
            {
                feature.IdleExitSeconds = idle;
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

            cfg.Stocks.RefreshSeconds = stockRefresh;
            _runtime.Agents.SetIdleSeconds(idle);
            if (applyServices)
            {
                StartupRegistration.Apply(cfg.StartWithWindows);
            }
            _runtime.SaveConfig();
            _runtime.Themes.Apply();
            App.RefreshIsland();
            App.RefreshStocks();
            _runtime.Stocks.ApplySettings();
            if (applyServices)
            {
                _ = _runtime.Stocks.RefreshAsync();
            }
            App.RebuildDecks();
            UpdateCurrentThemeText();
            if (applyServices)
            {
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

                _ = _runtime.Weather.RefreshAsync();
                _ = _runtime.Usage.RefreshAsync();
                if (cfg.Messages.PollMs != previousPollMs)
                {
                    await _runtime.Messages.RestartAsync();
                }

                if (!string.Equals(cfg.Monitor.Source, previousMonitorSource, StringComparison.OrdinalIgnoreCase) ||
                    cfg.Monitor.IntervalMs != previousMonitorInterval)
                {
                    await _runtime.Monitor.ApplySettingsAsync();
                }
            }

            _runtime.Weather.ApplySettings();
            _runtime.Usage.ApplySettings();
            if (applyServices)
            {
                _runtime.Media.ReloadLyrics();
            }

            _pending.Clear();
            App.ReloadFeatures();

            foreach (var row in _features)
            {
                row.Initial = row.Toggle.IsChecked == true;
            }

            StatusText.Text = "已保存并立即生效";
            Saved?.Invoke();

            var restartReasons = new List<string>();
            if (cfg.Feature("monitor").IdleExitSeconds != previousIdle)
            {
                restartReasons.Add("子进程空闲回收时长已保存，已启动的子进程仍按旧设置运行。");
            }

            if (applyServices && HotkeySignature(cfg.Hotkeys) != previousHotkeys)
            {
                App.RehookHotkeys();
            }

            if (applyServices && restartReasons.Count > 0)
            {
                var restart = AppDialog.Confirm(
                    this,
                    "需要重启",
                    string.Join(Environment.NewLine, restartReasons) + Environment.NewLine + "现在重启吗？",
                    "立即重启",
                    "稍后");
                if (restart)
                {
                    RestartApp();
                    return;
                }
            }
            if (close)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
        finally
        {
            _saving = false;
            ApplyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    internal Task ApplyForTestAsync() => ApplyAsync(close: false, applyServices: false);

    /// <summary>重启：交给 Shell 重新拉起灵动岛子进程。</summary>
    private void RestartApp()
    {
        if (Application.Current is App app)
        {
            app.RestartViaShell();
            return;
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

    private bool TryReadRange(TextBox box, int minimum, int maximum, string label, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
        {
            return true;
        }

        StatusText.Text = $"{label}请输入 {minimum} - {maximum} 之间的整数。";
        box.Focus();
        box.SelectAll();
        return false;
    }

    private static string HotkeySignature(HotkeySettings keys) =>
        $"{keys.Toggle}|{keys.Settings}|{keys.Monitor}";

    private static bool ValidHotkey(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && parts.All(part => part.Length > 0 && !part.Contains(' '));
    }

    private sealed class FeatureRow(string key, CheckBox toggle, bool initial)
    {
        public string Key { get; } = key;
        public CheckBox Toggle { get; } = toggle;
        public bool Initial { get; set; } = initial;
    }


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
