using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>媒体卡上的实时频谱：从取数委托拉频段能量，绘制圆角竖条。</summary>
public sealed class SpectrumView : FrameworkElement
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Random _random = new(20261109);
    private Func<double[]>? _source;
    private double[] _target = new double[Services.SpectrumProxy.Bands];
    private double[] _shown = new double[Services.SpectrumProxy.Bands];

    public SpectrumView()
    {
        AutomationProperties.SetName(this, "音频频谱");
        SnapsToDevicePixels = true;
        _timer.Tick += (_, _) => Step();
    }

    /// <summary>是否播放中；暂停时停止刷新，条形慢慢落下去。</summary>
    public bool Active
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>演示模式：不取真实音频，用固定的合成波形（仅用于文档截图）。</summary>
    public bool Demo { get; set; }

    public void SetSource(Func<double[]>? source) => _source = source;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        var count = _shown.Length;
        var gap = 2.0;
        var barWidth = Math.Max(2.0, (w - gap * (count - 1)) / count);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var level = Math.Clamp(_shown[i], 0, 1);
                var barHeight = Math.Max(3.0, h * level);
                var x = i * (barWidth + gap);
                var y = h - barHeight;
                var radius = Math.Min(barWidth / 2, 2.0);
                var rect = new Rect(x, y, barWidth, barHeight);
                ctx.BeginFigure(new Point(rect.Left + radius, rect.Top), true, true);
                ctx.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
                ctx.ArcTo(new Point(rect.Right, rect.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(rect.Right, rect.Bottom - radius), true, false);
                ctx.ArcTo(new Point(rect.Right - radius, rect.Bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(rect.Left + radius, rect.Bottom), true, false);
                ctx.ArcTo(new Point(rect.Left, rect.Bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(rect.Left, rect.Top + radius), true, false);
                ctx.ArcTo(new Point(rect.Left + radius, rect.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(ThemeRuntime.Accent, null, geometry);
    }

    private void Step()
    {
        if (Demo)
        {
            Synthesize();
        }
        else if (_source is not null)
        {
            _target = _source() ?? _target;
        }

        var count = Math.Min(_shown.Length, _target.Length);
        for (var i = 0; i < count; i++)
        {
            _shown[i] = _shown[i] * 0.62 + Math.Clamp(_target[i], 0, 1) * 0.38;
        }

        InvalidateVisual();
    }

    private void Synthesize()
    {
        var time = Environment.TickCount64 / 320.0;
        for (var i = 0; i < _target.Length; i++)
        {
            var envelope = Math.Exp(-i * 0.055);
            var wobble = 0.55 + 0.45 * Math.Sin(time * (0.7 + i * 0.045) + i * 0.6);
            _target[i] = Math.Clamp(envelope * wobble + _random.NextDouble() * 0.06, 0.04, 1);
        }
    }
}
