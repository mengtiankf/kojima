using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kojima.Shell.Services;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Views;

/// <summary>首页媒体卡：封面、曲目信息、进度与播放控制。</summary>
public sealed class MediaWidget : UserControl
{
    private readonly MediaProxy? _service;

    private readonly Border _cover = new();
    private readonly TextBlock _coverGlyph;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _album;
    private readonly TextBlock _position;
    private readonly TextBlock _duration;
    private readonly SpectrumView _spectrum = new();
    private readonly TextBlock _playGlyph;
    private readonly Border _playButton = new();
    private readonly TextBlock _prevGlyph;
    private readonly TextBlock _nextGlyph;
    private readonly Border _empty;

    /// <summary>XAML 用：从全局运行时取媒体服务。</summary>
    public MediaWidget()
        : this(AppRuntime.Current?.Media)
    {
    }

    public MediaWidget(MediaProxy? service)
    {
        _service = service;
        Height = 210;

        _coverGlyph = UiKit.Glyph("\uE8D6", 34, ThemeRuntime.Text3);
        _cover.Child = _coverGlyph;
        _cover.CornerRadius = new CornerRadius(10);
        _cover.Width = 96;
        _cover.Height = 96;
        _cover.HorizontalAlignment = HorizontalAlignment.Left;
        _cover.VerticalAlignment = VerticalAlignment.Top;

        _title = UiKit.T("未在播放", 15, ThemeRuntime.Text, FontWeights.SemiBold);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist = UiKit.T("打开任意播放器试试", 12, ThemeRuntime.Text2);
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        _album = UiKit.T("", 11, ThemeRuntime.Text3);
        _album.TextTrimming = TextTrimming.CharacterEllipsis;

        var info = new StackPanel { Margin = new Thickness(14, 2, 0, 0) };
        info.Children.Add(_title);
        info.Children.Add(_artist);
        info.Children.Add(_album);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cover, 0);
        top.Children.Add(_cover);
        Grid.SetColumn(info, 1);
        top.Children.Add(info);

        // 频谱（替代进度条）
        _spectrum.Height = 34;
        _spectrum.Margin = new Thickness(0, 10, 0, 0);

        _position = UiKit.T("0:00", 10.5, ThemeRuntime.Text3, num: true);
        _duration = UiKit.T("0:00", 10.5, ThemeRuntime.Text3, num: true);
        _duration.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        times.Children.Add(_position);
        times.Children.Add(_duration);

        // 控制
        _prevGlyph = UiKit.Glyph("\uE892", 16, ThemeRuntime.Text2);
        _playGlyph = UiKit.Glyph("\uE768", 16, ThemeRuntime.OnAccent);
        _nextGlyph = UiKit.Glyph("\uE893", 16, ThemeRuntime.Text2);

        _playButton.Width = 36;
        _playButton.Height = 36;
        _playButton.CornerRadius = new CornerRadius(18);
        _playButton.Child = _playGlyph;
        _playButton.Cursor = Cursors.Hand;
        _playButton.HorizontalAlignment = HorizontalAlignment.Center;
        _playButton.MouseLeftButtonUp += (_, _) => { if (_service is not null) { _ = _service.TogglePlayPauseAsync(); } };

        var prev = GlyphButton(_prevGlyph, () => { if (_service is not null) { _ = _service.PreviousAsync(); } });
        var next = GlyphButton(_nextGlyph, () => { if (_service is not null) { _ = _service.NextAsync(); } });

        var controls = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        prev.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(prev, 0);
        Grid.SetColumn(_playButton, 1);
        Grid.SetColumn(next, 2);
        next.HorizontalAlignment = HorizontalAlignment.Left;
        controls.Children.Add(prev);
        controls.Children.Add(_playButton);
        controls.Children.Add(next);

        _empty = new Border
        {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    UiKit.T("未在播放", 13, ThemeRuntime.Text2),
                    UiKit.T("打开任意播放器试试", 11, ThemeRuntime.Text3),
                },
            },
        };
        Grid.SetRow(_empty, 0);
        Grid.SetRowSpan(_empty, 4);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(top, 0);
        root.Children.Add(top);
        Grid.SetRow(_spectrum, 1);
        root.Children.Add(_spectrum);
        Grid.SetRow(times, 2);
        root.Children.Add(times);
        Grid.SetRow(controls, 3);
        root.Children.Add(controls);
        root.Children.Add(_empty);
        Content = root;

        _spectrum.SetSource(() => AppRuntime.Current?.Spectrum?.Levels);
        if (_service is not null) { _service.Changed += OnChanged; }
        Unloaded += (_, _) => { if (_service is not null) { _service.Changed -= OnChanged; } };
        ThemeRuntime.Changed += OnTheme;
        Unloaded += (_, _) => ThemeRuntime.Changed -= OnTheme;
        Render();
    }

    private static Border GlyphButton(TextBlock glyph, Action onClick)
    {
        var border = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            Child = glyph,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 12, 0),
        };
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private bool _demo;
    private MediaInfo? _demoInfo;

    /// <summary>演示数据：截图用，之后真实播放状态不再覆盖。</summary>
    public void ApplyDemo(MediaInfo info)
    {
        _demoInfo = info;
        _demo = true;
        RenderInfo(info);
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void OnTheme()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _title.Foreground = ThemeRuntime.Text;
            _artist.Foreground = ThemeRuntime.Text2;
            _album.Foreground = ThemeRuntime.Text3;
            _position.Foreground = ThemeRuntime.Text3;
            _duration.Foreground = ThemeRuntime.Text3;
            _prevGlyph.Foreground = ThemeRuntime.Text2;
            _nextGlyph.Foreground = ThemeRuntime.Text2;
            _playGlyph.Foreground = ThemeRuntime.OnAccent;
            _coverGlyph.Foreground = ThemeRuntime.Text3;
            Render();
        });
    }

    private void Render() => RenderInfo(_demo ? _demoInfo : null);

    private void RenderInfo(MediaInfo? overrideInfo)
    {
        var info = overrideInfo ?? _service?.Current;
        var hasMedia = info is not null;

        _empty.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        _cover.Background = hasMedia && info!.Artwork is not null
            ? Brushes.Transparent
            : ThemeRuntime.Fill;
        _cover.Child = hasMedia && info!.Artwork is not null
            ? new Image { Source = info.Artwork, Stretch = Stretch.UniformToFill }
            : _coverGlyph;
        _cover.Clip = new RectangleGeometry(new Rect(0, 0, 96, 96), 10, 10);

        _title.Text = hasMedia ? (string.IsNullOrWhiteSpace(info!.Title) ? "未知曲目" : info.Title) : "未在播放";
        _artist.Text = hasMedia
            ? (string.IsNullOrWhiteSpace(info!.Artist) ? info.AppId : info.Artist)
            : "打开任意播放器试试";
        _album.Text = hasMedia ? info!.Album : "";
        _playGlyph.Text = hasMedia && info!.IsPlaying ? "\uE769" : "\uE768";
        _playButton.Background = hasMedia ? ThemeRuntime.Accent : ThemeRuntime.Track;
        _playButton.IsEnabled = hasMedia;
        _playButton.Opacity = hasMedia ? 1 : 0.4;

        var position = info?.Position ?? TimeSpan.Zero;
        var duration = info?.Duration ?? TimeSpan.Zero;
        _position.Text = Format(position);
        _duration.Text = Format(duration);
        _spectrum.Active = info?.IsPlaying == true;
        _spectrum.Demo = _demo;
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
}
