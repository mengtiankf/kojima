using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>翻译页：中英日互译，调用 DeepSeek OpenAI 兼容接口。</summary>
public partial class TranslateView : UserControl
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private const string SystemPrompt = "你是专业翻译，只输出译文";
    private const string NoKey = "未配置 DeepSeek API Key：设置 → 用量 → 来源密钥 里填入后即可翻译";
    private const string FreeHint = "使用免费翻译通道（MyMemory / Google）。想要更好的译文可在设置里填入 DeepSeek API Key。";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppRuntime _runtime;
    private readonly TextBox _input = new();
    private readonly TextBlock _placeholder = new();
    private readonly Border _inputBox = new();
    private readonly Border _segTrack = new();
    private readonly Border[] _segments = [new Border(), new Border()];
    private readonly TextBlock[] _segmentLabels = [new TextBlock(), new TextBlock()];
    private readonly Border _translateButton = new();
    private readonly TextBlock _translateLabel = new();
    private readonly Border _swapButton = new();
    private readonly TextBlock _swapGlyph = new();
    private readonly Border _resultBox = new();
    private readonly TextBox _result = new();
    private string _target = "日";
    private ResultKind _resultKind = ResultKind.Hint;
    private bool _reversed, _translating, _loaded;

    public TranslateView(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Build();
        ApplyTheme();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── 界面 ────────────────────────────────────────────────────────────────

    private void Build()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Host.Children.Add(layout);

        // 输入区：圆角底 + 占位文字 + 多行文本框。
        _inputBox.CornerRadius = new CornerRadius(10);
        _inputBox.BorderThickness = new Thickness(1);
        _inputBox.Height = 96;
        var inputHost = new Grid();
        _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.BorderThickness = new Thickness(0);
        _input.Background = Brushes.Transparent;
        _input.Padding = new Thickness(10, 8, 8, 8);
        _input.FontSize = 13;
        _input.FontFamily = ThemeRuntime.UiFont;
        SpellCheck.SetIsEnabled(_input, false);
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _input.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _input.TextChanged += (_, _) => _placeholder.Visibility =
            _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.PreviewKeyDown += OnInputKeyDown;
        _placeholder.Text = "输入要翻译的文字，Ctrl+Enter 翻译";
        _placeholder.FontSize = 12.5;
        _placeholder.FontFamily = ThemeRuntime.UiFont;
        _placeholder.Margin = new Thickness(12, 9, 10, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.IsHitTestVisible = false;
        inputHost.Children.Add(_input);
        inputHost.Children.Add(_placeholder);
        _inputBox.Child = inputHost;
        Grid.SetRow(_inputBox, 0);
        layout.Children.Add(_inputBox);

        // 按钮行：分段切换 / 交换 / 翻译。
        var bar = new Grid { Margin = new Thickness(0, 8, 0, 0), Height = 30 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _segTrack.CornerRadius = new CornerRadius(9);
        _segTrack.Padding = new Thickness(2);
        _segTrack.VerticalAlignment = VerticalAlignment.Center;
        var segHost = new StackPanel { Orientation = Orientation.Horizontal };
        _segTrack.Child = segHost;
        for (var i = 0; i < 2; i++)
        {
            var segment = _segments[i];
            var label = _segmentLabels[i];
            segment.Width = 76;
            segment.Height = 26;
            segment.CornerRadius = new CornerRadius(7);
            segment.BorderThickness = new Thickness(1);
            segment.BorderBrush = Brushes.Transparent;
            segment.Cursor = Cursors.Hand;
            label.FontSize = 12;
            label.FontFamily = ThemeRuntime.UiFont;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            segment.Child = label;
            var index = i;
            segment.MouseLeftButtonUp += (_, _) => SelectLanguage(index);
            segment.MouseEnter += (_, _) =>
            {
                if (!IsSelected(index)) { segment.Background = ThemeRuntime.Hover; }
            };
            segment.MouseLeave += (_, _) => UpdateSegments();
            segHost.Children.Add(segment);
        }

        Grid.SetColumn(_segTrack, 0);
        bar.Children.Add(_segTrack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _swapButton.Width = 30;
        _swapButton.Height = 30;
        _swapButton.CornerRadius = new CornerRadius(8);
        _swapButton.Margin = new Thickness(0, 0, 6, 0);
        _swapButton.Cursor = Cursors.Hand;
        _swapButton.ToolTip = "交换翻译方向";
        _swapGlyph.Text = "\uE8AB";
        _swapGlyph.FontFamily = ThemeRuntime.IconFont;
        _swapGlyph.FontSize = 13;
        _swapGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _swapGlyph.VerticalAlignment = VerticalAlignment.Center;
        _swapButton.Child = _swapGlyph;
        _swapButton.MouseEnter += (_, _) => _swapButton.Background = ThemeRuntime.Hover;
        _swapButton.MouseLeave += (_, _) => _swapButton.Background = ThemeRuntime.Fill;
        _swapButton.MouseLeftButtonUp += (_, _) =>
        {
            _reversed = !_reversed;
            UpdateSegments();
        };

        _translateButton.Width = 76;
        _translateButton.Height = 30;
        _translateButton.CornerRadius = new CornerRadius(9);
        _translateButton.Cursor = Cursors.Hand;
        _translateLabel.Text = "翻译";
        _translateLabel.FontSize = 12.5;
        _translateLabel.FontWeight = FontWeights.SemiBold;
        _translateLabel.FontFamily = ThemeRuntime.UiFont;
        _translateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _translateLabel.VerticalAlignment = VerticalAlignment.Center;
        _translateButton.Child = _translateLabel;
        _translateButton.MouseEnter += (_, _) => _translateButton.Opacity = 0.9;
        _translateButton.MouseLeave += (_, _) => _translateButton.Opacity = 1;
        _translateButton.MouseLeftButtonUp += (_, _) => _ = TranslateAsync();

        actions.Children.Add(_swapButton);
        actions.Children.Add(_translateButton);
        Grid.SetColumn(actions, 2);
        bar.Children.Add(actions);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);

        // 结果区：只读、可滚动。
        _resultBox.CornerRadius = new CornerRadius(10);
        _resultBox.BorderThickness = new Thickness(1);
        _resultBox.Margin = new Thickness(0, 8, 0, 0);
        var resultHost = new Grid();
        _result.IsReadOnly = true;
        _result.IsReadOnlyCaretVisible = false;
        _result.AcceptsReturn = true;
        _result.TextWrapping = TextWrapping.Wrap;
        _result.BorderThickness = new Thickness(0);
        _result.Background = Brushes.Transparent;
        _result.Padding = new Thickness(10, 8, 6, 8);
        _result.FontSize = 13;
        _result.FontFamily = ThemeRuntime.UiFont;
        _result.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _result.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        resultHost.Children.Add(_result);

        var copyButton = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 8, 0),
            Cursor = Cursors.Hand,
            Background = ThemeRuntime.Fill,
            ToolTip = "复制译文",
        };
        var copyLabel = new TextBlock
        {
            Text = "复制",
            FontSize = 11,
            FontFamily = ThemeRuntime.UiFont,
            Foreground = ThemeRuntime.Text2,
        };
        copyButton.Child = copyLabel;
        copyButton.MouseEnter += (_, _) => copyButton.Background = ThemeRuntime.Hover;
        copyButton.MouseLeave += (_, _) => copyButton.Background = ThemeRuntime.Fill;
        copyButton.MouseLeftButtonUp += (_, _) =>
        {
            var text = _result.Text ?? "";
            if (text.Length == 0 || _resultKind != ResultKind.Text)
            {
                SetResult("还没有可复制的译文。", ResultKind.Hint);
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(text);
                copyLabel.Text = "已复制";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    copyLabel.Text = "复制";
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Warn("复制译文失败：" + ex.Message);
            }
        };

        resultHost.Children.Add(copyButton);
        _resultBox.Child = resultHost;
        Grid.SetRow(_resultBox, 2);
        layout.Children.Add(_resultBox);

        SetResult("译文会显示在这里。", ResultKind.Hint);
        UpdateSegments();
    }

    private void ApplyTheme()
    {
        Background = Brushes.Transparent;
        _inputBox.Background = ThemeRuntime.Fill;
        _inputBox.BorderBrush = ThemeRuntime.Hairline;
        _input.Foreground = ThemeRuntime.Text;
        _input.CaretBrush = ThemeRuntime.Accent;
        _input.SelectionBrush = ThemeRuntime.Accent;
        _placeholder.Foreground = ThemeRuntime.Text3;

        _segTrack.Background = ThemeRuntime.Track;
        UpdateSegments();
        _swapButton.Background = ThemeRuntime.Fill;
        _swapGlyph.Foreground = ThemeRuntime.Text2;
        _translateButton.Background = ThemeRuntime.Accent;
        _translateLabel.Foreground = ThemeRuntime.OnAccent;

        _resultBox.Background = ThemeRuntime.Card;
        _resultBox.BorderBrush = ThemeRuntime.Hairline;
        SetResult(_result.Text, _resultKind);
    }

    private void UpdateSegments()
    {
        var labels = _reversed
            ? new[] { "日 → 中", "英 → 中" }
            : new[] { "中 → 日", "中 → 英" };
        for (var i = 0; i < 2; i++)
        {
            var selected = IsSelected(i);
            _segmentLabels[i].Text = labels[i];
            _segmentLabels[i].Foreground = selected ? ThemeRuntime.Text : ThemeRuntime.Text2;
            _segmentLabels[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            _segments[i].Background = selected ? ThemeRuntime.Card : Brushes.Transparent;
            _segments[i].BorderBrush = selected ? ThemeRuntime.Hairline : Brushes.Transparent;
        }
    }

    private bool IsSelected(int index) => _target == (index == 0 ? "日" : "英");

    private void SelectLanguage(int index)
    {
        _target = index == 0 ? "日" : "英";
        _runtime.Config.Translate.Target = _target == "日" ? "ja" : "en";
        _runtime.SaveConfig();
        UpdateSegments();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) { return; }
        e.Handled = true;
        _ = TranslateAsync();
    }

    // ── 翻译 ────────────────────────────────────────────────────────────────

    /// <summary>开发钩子：填入文本并立即翻译。</summary>
    public void FillAndTranslate(string text)
    {
        _input.Text = text;
        _ = TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        if (_translating) { return; }
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            SetResult("请先输入要翻译的文字。", ResultKind.Hint);
            return;
        }

        var settings = _runtime.Config.Translate;
        var key = ApiKey();

        _translating = true;
        _translateLabel.Text = "翻译中…";
        SetResult("翻译中…", ResultKind.Hint);
        try
        {
            var translated = await Services.TranslationService.TranslateAsync(text, settings, key);

            if (translated.Trim().Length == 0)
            {
                SetResult("翻译失败：接口未返回内容", ResultKind.Error);
                return;
            }

            SetResult(translated.Trim(), ResultKind.Text);
        }
        catch (TaskCanceledException)
        {
            SetResult("翻译失败：请求超时", ResultKind.Error);
        }
        catch (Exception ex)
        {
            AppLog.Warn("翻译失败：" + ex.Message);
            SetResult("翻译失败：" + ex.Message, ResultKind.Error);
        }
        finally
        {
            _translating = false;
            _translateLabel.Text = "翻译";
        }
    }

    private string ApiKey()
    {
        _runtime.Config.Usage.Keys.TryGetValue("deepseek", out var key);
        return (key ?? "").Trim();
    }

    private void SetResult(string text, ResultKind kind)
    {
        _resultKind = kind;
        _result.Text = text;
        _result.Foreground = kind switch
        {
            ResultKind.Error => ThemeRuntime.Danger,
            ResultKind.Hint => ThemeRuntime.Text3,
            _ => ThemeRuntime.Text,
        };
        _result.CaretBrush = ThemeRuntime.Accent;
        _result.SelectionBrush = ThemeRuntime.Accent;
    }

    // ── 生命周期 ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            ThemeRuntime.Changed += OnThemeChanged;
            _target = (_runtime.Config.Translate.Target ?? "ja").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "英" : "日";
            UpdateSegments();
            if (ApiKey().Length == 0 && _runtime.Config.Translate.Engine == "deepseek") { SetResult(FreeHint, ResultKind.Hint); }
        }

        _loaded = true;
        ApplyTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) { ThemeRuntime.Changed -= OnThemeChanged; }
        _loaded = false;
    }

    private void OnThemeChanged()
    {
        if (Dispatcher.CheckAccess()) { ApplyTheme(); } else { Dispatcher.BeginInvoke(new Action(ApplyTheme)); }
    }

    private enum ResultKind
    {
        Hint,
        Text,
        Error,
    }
}
