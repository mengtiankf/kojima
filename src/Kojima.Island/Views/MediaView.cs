using System.Windows;
using System.Windows.Controls;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

public sealed class MediaView : UserControl
{
    private readonly AppRuntime _runtime;
    private readonly TextBlock _source;
    private readonly MediaWidget _widget;
    private bool _attached;
    private bool _demo;

    public MediaView(AppRuntime runtime)
    {
        _runtime = runtime;
        Height = 320;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = UiKit.T("播放控制", 12, ThemeRuntime.Text2, FontWeights.SemiBold);
        header.Children.Add(title);
        var refresh = UiKit.Button("刷新", () => { _ = _runtime.Media.RefreshAsync(); }, height: 28);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        root.Children.Add(header);

        _widget = new MediaWidget(runtime.Media, large: true);
        Grid.SetRow(_widget, 1);
        root.Children.Add(_widget);

        _source = UiKit.T("", 10.5, ThemeRuntime.Text3);
        _source.TextTrimming = TextTrimming.CharacterEllipsis;
        _source.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_source, 2);
        root.Children.Add(_source);
        Content = root;

        Loaded += (_, _) =>
        {
            if (!_attached)
            {
                _runtime.Media.Changed += OnMediaChanged;
                _attached = true;
            }
            UpdateSource();
        };
        Unloaded += (_, _) =>
        {
            if (_attached)
            {
                _runtime.Media.Changed -= OnMediaChanged;
                _attached = false;
            }
        };
    }

    public void ApplyDemo(Kojima.Island.Services.MediaInfo info)
    {
        _demo = true;
        _widget.ApplyDemo(info);
        _source.Text = "演示媒体会话 · 正在播放";
        _source.ToolTip = null;
    }

    private void OnMediaChanged() => Dispatcher.BeginInvoke(UpdateSource);

    private void UpdateSource()
    {
        if (_demo)
        {
            return;
        }
        var current = _runtime.Media.Current;
        _source.Text = current is null
            ? "等待 Windows 系统媒体会话"
            : $"{current.AppId} · {(current.IsPlaying ? "正在播放" : "已暂停")}";
        _source.ToolTip = current?.AppId;
    }
}
