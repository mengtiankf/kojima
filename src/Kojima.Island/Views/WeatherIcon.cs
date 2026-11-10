using System.Windows;
using System.Windows.Media;

namespace Kojima.Island.Views;

/// <summary>
/// 矢量天气图标：Segoe Fluent / MDL2 都缺雨雪雷的码位（E9C4/E9C5/E9C8 是豆腐块），
/// 这里用基础图元自己画，保证任何机器都一样清晰。
/// </summary>
public sealed class WeatherIcon : FrameworkElement
{
    private int _code;

    public int Code
    {
        get => _code;
        set
        {
            if (_code == value)
            {
                return;
            }

            _code = value;
            InvalidateVisual();
        }
    }

    public Brush? Brush { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2)
        {
            return;
        }

        var brush = Brush ?? Theme.ThemeRuntime.Text2;
        var s = size;
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var pen = new Pen(brush, Math.Max(1.1, s * 0.055));
        var kind = KindOf(_code);

        switch (kind)
        {
            case Kind.Sun:
                DrawSun(dc, cx, cy, s, brush, pen);
                break;
            case Kind.Partly:
                DrawSun(dc, cx - s * 0.14, cy - s * 0.14, s * 0.72, brush, pen);
                DrawCloud(dc, cx + s * 0.06, cy + s * 0.10, s * 0.78, brush);
                break;
            case Kind.Cloud:
                DrawCloud(dc, cx, cy, s * 0.86, brush);
                break;
            case Kind.Rain:
                DrawCloud(dc, cx, cy - s * 0.10, s * 0.86, brush);
                for (var i = -1; i <= 1; i++)
                {
                    var x = cx + i * s * 0.20;
                    dc.DrawLine(pen, new Point(x, cy + s * 0.20), new Point(x - s * 0.06, cy + s * 0.36));
                }

                break;
            case Kind.Snow:
                DrawCloud(dc, cx, cy - s * 0.10, s * 0.86, brush);
                for (var i = -1; i <= 1; i++)
                {
                    var x = cx + i * s * 0.20;
                    var y = cy + s * 0.29;
                    var r = s * 0.045;
                    dc.DrawEllipse(brush, null, new Point(x, y), r, r);
                }

                break;
            case Kind.Thunder:
                DrawCloud(dc, cx, cy - s * 0.12, s * 0.86, brush);
                var bolt = new StreamGeometry();
                using (var g = bolt.Open())
                {
                    g.BeginFigure(new Point(cx + s * 0.02, cy + s * 0.10), true, true);
                    g.LineTo(new Point(cx - s * 0.10, cy + s * 0.28), true, false);
                    g.LineTo(new Point(cx + s * 0.00, cy + s * 0.28), true, false);
                    g.LineTo(new Point(cx - s * 0.06, cy + s * 0.44), true, false);
                    g.LineTo(new Point(cx + s * 0.12, cy + s * 0.22), true, false);
                    g.LineTo(new Point(cx + s * 0.02, cy + s * 0.22), true, false);
                }

                bolt.Freeze();
                dc.DrawGeometry(brush, null, bolt);
                break;
            case Kind.Fog:
                DrawCloud(dc, cx, cy - s * 0.14, s * 0.82, brush);
                dc.DrawLine(pen, new Point(cx - s * 0.24, cy + s * 0.24), new Point(cx + s * 0.22, cy + s * 0.24));
                dc.DrawLine(pen, new Point(cx - s * 0.16, cy + s * 0.38), new Point(cx + s * 0.16, cy + s * 0.38));
                break;
        }
    }

    private static void DrawSun(DrawingContext dc, double cx, double cy, double s, Brush brush, Pen pen)
    {
        var r = s * 0.18;
        dc.DrawEllipse(brush, null, new Point(cx, cy), r, r);

        for (var i = 0; i < 8; i++)
        {
            var angle = Math.PI * i / 4;
            var inner = r + s * 0.09;
            var outer = r + s * 0.18;
            dc.DrawLine(
                pen,
                new Point(cx + Math.Cos(angle) * inner, cy + Math.Sin(angle) * inner),
                new Point(cx + Math.Cos(angle) * outer, cy + Math.Sin(angle) * outer));
        }
    }

    private static void DrawCloud(DrawingContext dc, double cx, double cy, double s, Brush brush)
    {
        // 三个圆 + 底部圆角矩形，叠起来就是一朵云
        dc.DrawEllipse(brush, null, new Point(cx - s * 0.18, cy + s * 0.04), s * 0.17, s * 0.17);
        dc.DrawEllipse(brush, null, new Point(cx + s * 0.02, cy - s * 0.08), s * 0.24, s * 0.24);
        dc.DrawEllipse(brush, null, new Point(cx + s * 0.22, cy + s * 0.06), s * 0.15, s * 0.15);

        var baseRect = new Rect(cx - s * 0.30, cy + s * 0.02, s * 0.60, s * 0.20);
        dc.DrawRoundedRectangle(brush, null, baseRect, s * 0.10, s * 0.10);
    }

    private enum Kind
    {
        Sun,
        Partly,
        Cloud,
        Rain,
        Snow,
        Thunder,
        Fog,
    }

    private static Kind KindOf(int code) => code switch
    {
        113 => Kind.Sun,
        116 => Kind.Partly,
        119 or 122 => Kind.Cloud,
        143 or 248 or 260 => Kind.Fog,
        200 or 386 or 389 or 392 or 395 => Kind.Thunder,
        179 or 182 or 185 or 227 or 230 or 323 or 326 or 329 or 332 or 335 or 338 or 368 or 371 or 374 or 377 => Kind.Snow,
        176 or 263 or 266 or 281 or 284 or 293 or 296 or 299 or 302 or 305 or 308 or 311 or 314 or 317 or 320 or 353 or 356 or 359 => Kind.Rain,
        _ => Kind.Cloud,
    };
}
