using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
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

public partial class SettingsWindow : Window
{
    private const string SecretMask = "••••••";

    private static readonly (string Key, string Label)[] FeatureDefs =
    {
        ("usage", "AI 用量"), ("monitor", "系统监控"), ("media", "媒体"), ("weather", "天气"),
        ("stocks", "股票"), ("clipboard", "剪贴板"), ("translate", "翻译"), ("live", "实时字幕"),
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

    private readonly AppRuntime _runtime;
    private readonly List<FeatureRow> _features = new();
    private readonly List<SourceRow> _sources = new();
    private readonly List<SecretRow> _secrets = new();
    private bool _loading = true;
    private bool _saving;

    public SettingsWindow(AppRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
        ApplyThemeResources();
        LoadConfig();
        _loading = false;
        ThemeRuntime.Changed += OnThemeChanged;
    }

    public event Action? Saved;

    protected override void OnClosed(EventArgs e)
    {
        ThemeRuntime.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }

    private void LoadConfig()
    {
        var cfg = _runtime.Config;
        StartSwitch.IsChecked = cfg.StartWithWindows;
        LanguageBox.ItemsSource = new[] { "简体中文", "English" };
        LanguageBox.SelectedIndex = string.Equals(cfg.Language, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PosCenter.IsChecked = cfg.Island.Position == IslandPosition.TopCenter;
        PosLeft.IsChecked = cfg.Island.Position == IslandPosition.TopLeft;
        PosRight.IsChecked = cfg.Island.Position == IslandPosition.TopRight;
        ShapeNotch.IsChecked = cfg.Island.Shape == IslandShape.Notch;
        ShapeCapsule.IsChecked = cfg.Island.Shape == IslandShape.Capsule;
        ShapeIndicator.IsChecked = cfg.Island.Shape == IslandShape.Indicator;
        HoverSlider.Value = Math.Clamp(cfg.Island.HoverExpandMs, 0, 1000);
        CollapseSlider.Value = Math.Clamp(cfg.Island.CollapseDelayMs, 0, 1500);
        HideFullscreenSwitch.IsChecked = cfg.Island.HideFullscreen;
        OpacitySlider.Value = Math.Clamp(cfg.Island.Opacity, 0.6, 1.0);
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
        var cards = ThemeCatalog.All.Select(p => ThemeCard.From(p)).ToList();
        ThemeList.ItemsSource = cards;
        ThemeList.SelectedItem = cards.FirstOrDefault(c => string.Equals(c.Id, _runtime.Config.Island.Theme, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCurrentThemeText()
    {
        var p = ThemeCatalog.Get(_runtime.Config.Island.Theme);
        CurrentThemeText.Text = $"{p.Name} · {(string.IsNullOrWhiteSpace(p.Author) ? p.Id : p.Author)}";
    }

    private void BuildFeatures()
    {
        foreach (var (key, label) in FeatureDefs)
        {
            var enabled = _runtime.FeatureEnabled(key);
            var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowLabel"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            FeatureList.Children.Add(row);
            _features.Add(new FeatureRow(key, toggle, enabled));
        }
    }

    private void BuildSources()
    {
        var selected = new HashSet<string>(_runtime.Config.Usage.Sources, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in SourceDefs)
        {
            var check = new CheckBox { Style = (Style)FindResource("Check"), Content = label, IsChecked = selected.Contains(key), Margin = new Thickness(0, 0, 20, 10) };
            SourceList.Children.Add(check);
            _sources.Add(new SourceRow(key, check));
        }
    }

    private void BuildSecrets()
    {
        var keys = _runtime.Config.Usage.Keys;
        foreach (var (key, label) in SecretDefs)
        {
            var existing = keys.TryGetValue(key, out var stored) ? stored : string.Empty;
            var box = new PasswordBox { Style = (Style)FindResource("PwdField"), Height = 34, Password = string.IsNullOrEmpty(existing) ? string.Empty : SecretMask, VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(existing))
            {
                box.GotKeyboardFocus += (_, _) => box.SelectAll();
                box.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    if (box.Password != SecretMask) return;
                    box.Focus();
                    box.SelectAll();
                    args.Handled = true;
                };
            }
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            SecretsHost.Children.Add(row);
            _secrets.Add(new SecretRow(key, box));
        }
    }

    private void ApplyThemeResources()
    {
        var p = ThemeRuntime.Palette;
        var brushes = new (string Key, Brush Brush)[]
        {
            ("SurfaceBrush", ThemeRuntime.SurfaceBrush), ("TextBrush", ThemeRuntime.Text), ("Text2Brush", ThemeRuntime.Text2),
            ("Text3Brush", ThemeRuntime.Text3), ("AccentBrush", ThemeRuntime.Accent), ("OnAccentBrush", ThemeRuntime.OnAccent),
            ("HairlineBrush", ThemeRuntime.Hairline), ("TrackBrush", ThemeRuntime.Track), ("FillBrush", ThemeRuntime.Fill),
            ("HoverBrush", ThemeRuntime.Hover), ("OutlineBrush", ThemeRuntime.Outline), ("CardBrush", ThemeRuntime.Card),
            ("AccentSoftBrush", new SolidColorBrush(Color.FromArgb(0x2E, p.Accent.R, p.Accent.G, p.Accent.B))),
            ("PopupBrush", new SolidColorBrush(Color.FromArgb(0xFF, p.Surface.R, p.Surface.G, p.Surface.B))),
            ("ShadowBrush", new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0))),
        };
        foreach (var (key, brush) in brushes) SetBrush(key, brush);
    }

    private void SetBrush(string key, Brush brush)
    {
        if (brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
        Resources[key] = brush;
    }

    private void OnThemeChanged() => ApplyThemeResources();

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeList.SelectedItem is not ThemeCard card) return;
        _runtime.ApplyTheme(card.Id);
        UpdateCurrentThemeText();
        StatusText.Text = $"已应用主题：{card.DisplayName}（主题会立即保存）";
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        var pages = new[] { PageGeneral, PageAppearance, PageFeatures, PageUsage, PageAbout };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (index >= 0 && index < pages.Length) pages[index].ScrollToTop();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnGitHubNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "无法打开链接：" + ex.Message; }
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在保存…";
        try
        {
            var cfg = _runtime.Config;
            cfg.StartWithWindows = StartSwitch.IsChecked == true;
            cfg.Language = LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN";
            cfg.Island.Position = PosLeft.IsChecked == true ? IslandPosition.TopLeft
                : PosRight.IsChecked == true ? IslandPosition.TopRight : IslandPosition.TopCenter;
            cfg.Island.Shape = ShapeCapsule.IsChecked == true ? IslandShape.Capsule
                : ShapeIndicator.IsChecked == true ? IslandShape.Indicator : IslandShape.Notch;
            cfg.Island.HoverExpandMs = (int)Math.Round(HoverSlider.Value);
            cfg.Island.CollapseDelayMs = (int)Math.Round(CollapseSlider.Value);
            cfg.Island.HideFullscreen = HideFullscreenSwitch.IsChecked == true;
            cfg.Island.Opacity = Math.Round(OpacitySlider.Value, 2);
            cfg.Usage.RefreshSeconds = Math.Clamp(ParseInt(RefreshBox.Text, cfg.Usage.RefreshSeconds), 30, 3600);
            var idle = Math.Clamp(ParseInt(IdleExitBox.Text, cfg.Feature("monitor").IdleExitSeconds), 0, 3600);
            foreach (var feature in cfg.Features.Values) feature.IdleExitSeconds = idle;
            cfg.Usage.Sources = _sources.Where(row => row.Toggle.IsChecked == true).Select(row => row.Key).ToList();
            foreach (var secret in _secrets)
            {
                var value = secret.Box.Password;
                if (value == SecretMask || value.Contains(SecretMask, StringComparison.Ordinal)) continue;
                cfg.Usage.Keys[secret.Key] = value.Trim();
            }
            var changed = new List<(string Key, bool Enabled)>();
            foreach (var row in _features)
            {
                var enabled = row.Toggle.IsChecked == true;
                cfg.Feature(row.Key).Enabled = enabled;
                if (enabled != row.Initial) changed.Add((row.Key, enabled));
            }
            _runtime.SaveConfig();
            foreach (var (key, enabled) in changed)
            {
                try { await _runtime.SetFeatureAsync(key, enabled); }
                catch (Exception ex) { AppLog.Warn($"切换功能 {key} 失败：{ex.Message}"); }
            }
            StatusText.Text = "已保存";
            Saved?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
            _saving = false;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private sealed record FeatureRow(string Key, CheckBox Toggle, bool Initial);

    private sealed record SourceRow(string Key, CheckBox Toggle);

    private sealed record SecretRow(string Key, PasswordBox Box);

    private sealed record ThemeCard(string Id, string DisplayName, string ByLine, Brush SurfaceSwatch, Brush TextSwatch, Brush AccentSwatch)
    {
        public static ThemeCard From(DeckPalette p) => new(
            p.Id,
            string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
            string.IsNullOrWhiteSpace(p.Author) ? "Kojima 内置" : p.Author,
            ThemeRuntime.Brush(p.Surface), ThemeRuntime.Brush(p.Text), ThemeRuntime.Brush(p.Accent));
    }
}
