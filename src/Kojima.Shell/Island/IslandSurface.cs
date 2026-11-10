using System.Windows;
using System.Windows.Media;
using Kojima.Core.Config;
using Kojima.Core.Geometry;
using Kojima.Shell.Theme;

namespace Kojima.Shell.Island;

/// <summary>岛屿底板：按 BrimDeck 的轮廓算法绘制表面渐变、描边、扫描线。</summary>
public sealed class IslandSurface : FrameworkElement
{
    private Geometry? _geometry;

    public IslandShape Shape { get; set; } = IslandShape.Notch;

    public bool Expanded { get; set; }

    public double NotchWidth { get; set; } = 238;

    public double CapsuleWidth { get; set; } = 196;

    public double IndicatorWidth { get; set; } = 56;

    public double IndicatorThickness { get; set; } = 4;

    public new double Height { get; set; } = 34;

    public double Shoulder { get; set; } = 16;

    public double Corner { get; set; } = 12;

    public double PanelRadius { get; set; } = 20;

    public bool ShowShadow { get; set; } = true;

    public IslandSurface()
    {
        // 不使用投影：阴影会在圆角处露出半透明直角，用户明确不要
    }

    public Geometry Geometry
    {
        get
        {
            var w = Math.Max(1, ActualWidth);
            var h = Math.Max(1, ActualHeight);
            _geometry ??= BuildGeometry(w, h);
            return _geometry;
        }
    }

    /// <summary>尺寸变化后调用，重建轮廓。</summary>
    public void Refresh()
    {
        _geometry = null;
        InvalidateVisual();
    }

    private Geometry BuildGeometry(double w, double h)
    {
        string data;
        if (Expanded)
        {
            // 展开：贴着屏幕顶边，上方两角为直角，下方圆角
            data = IslandOutline.Build(0, 0, w, h, 0, 0, 0, PanelRadius, PanelRadius);
        }
        else
        {
            data = Shape switch
            {
                IslandShape.Capsule => IslandOutline.Rounded(0, 0, w, h, h / 2),
                IslandShape.Indicator => IslandOutline.Rounded(0, 0, w, h, h / 2),
                // 刘海：贴顶；两侧肩线按 BrimDeck 的 7/16 深度曲线过渡，底部圆角
                _ => IslandOutline.Build(0, 0, w, h, Math.Max(0, Shoulder), 0, 0, Corner, Corner),
            };
        }

        var geometry = System.Windows.Media.Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        if (_geometry is null)
        {
            _geometry = BuildGeometry(w, h);
        }

        var palette = ThemeRuntime.Palette;
        var outline = _geometry;

        // 表面：竖向渐变（顶部略亮）
        var surface = new LinearGradientBrush(palette.SurfaceTop.ToColor(), palette.SurfaceBottom.ToColor(), 90);
        surface.Freeze();
        dc.DrawGeometry(surface, null, outline);

        // 扫描线（像素风格主题）
        if (palette.Effects.Scanlines > 0.001)
        {
            dc.PushClip(outline);
            var lineBrush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(palette.Effects.Scanlines * 255, 0, 255), 255, 255, 255));
            lineBrush.Freeze();
            var pen = new Pen(lineBrush, 1);
            pen.Freeze();
            for (var y = 2.0; y < h; y += 3)
            {
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }

            dc.Pop();
        }

        // 顶部内侧高光（材质感）
        if (palette.Dark)
        {
            var sheen = new LinearGradientBrush(
                Color.FromArgb(26, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                90)
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
            };
            sheen.Freeze();
            var sheenRect = new Rect(0, 0, w, Math.Min(h, Expanded ? 46 : h * 0.7));
            dc.PushClip(outline);
            dc.DrawRectangle(sheen, null, sheenRect);
            dc.Pop();
        }

        // 描边：主题 hairline，贴边内缩 0.5 px
        var rimWidth = Math.Max(0.5, palette.Effects.RimWidth);
        var rim = new Pen(new SolidColorBrush(palette.Hairline.ToColor()), rimWidth);
        rim.Freeze();
        dc.DrawGeometry(null, rim, outline);
    }
}
