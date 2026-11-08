using System.Globalization;
using System.Text;

namespace Kojima.Core.Geometry;

/// <summary>
/// 岛屿轮廓：BrimDeck IslandBorder.BuildOutline 的移植（顶部缺口 + 两侧肩线 + 四角圆角）。
/// 输出 SVG path 数据（M/L/C/Z），宿主用 Geometry.Parse 直接消费。
/// </summary>
public static class IslandOutline
{
    /// <summary>贝塞尔圆角系数 4/3*(sqrt(2)-1)。</summary>
    public const double Kappa = 0.5522847498307936;

    /// <summary>肩线深度占肩宽的比例（BrimDeck 的 7/16）。</summary>
    public const double ShoulderDepthRatio = 7.0 / 16.0;

    public static string Build(
        double x,
        double y,
        double w,
        double h,
        double shoulderRadius,
        double topLeft,
        double topRight,
        double bottomRight,
        double bottomLeft)
    {
        var s = Math.Clamp(shoulderRadius, 0, Math.Min(w / 4.0, h / 2.0));
        var depth = s * ShoulderDepthRatio;

        // 圆角上限：肩线以下的本体要放得下
        var limit = Math.Min(s > 0 ? h - depth : h / 2.0, Math.Max(0, w - 2 * s) / 2.0);
        limit = Math.Max(0, Math.Min(limit, h / 2.0));
        var br = Math.Clamp(bottomRight, 0, Math.Max(0, limit));
        var bl = Math.Clamp(bottomLeft, 0, Math.Max(0, limit));
        var tr = Math.Clamp(topRight, 0, Math.Min(w / 2.0, h / 2.0));
        var tl = Math.Clamp(topLeft, 0, Math.Min(w / 2.0, h / 2.0));

        var p = new StringBuilder(512);
        if (s > 0)
        {
            Move(p, x, y);
            Line(p, x + w, y);
            // 右侧肩线：顶边沿肩宽收到本体侧壁
            Cubic(p, x + w - s * 0.28, y, x + w - s * 0.64, y, x + w - s * 0.82, y + depth * 0.18);
            Cubic(p, x + w - s, y + depth * 0.36, x + w - s, y + depth * 0.72, x + w - s, y + depth);
            Line(p, x + w - s, y + h - br);
            // 右下角
            Cubic(p, x + w, y + h - br * (1 - Kappa), x + w - br * (1 - Kappa), y + h, x + w - br, y + h);
            Line(p, x + s + bl, y + h);
            // 左下角
            Cubic(p, x + s + bl * (1 - Kappa), y + h, x + s, y + h - bl * (1 - Kappa), x + s, y + h - bl);
            Line(p, x + s, y + depth);
            // 左侧肩线（镜像回来）
            Cubic(p, x + s, y + depth * 0.72, x + s, y + depth * 0.36, x + s * 0.82, y + depth * 0.18);
            Cubic(p, x + s * 0.64, y, x + s * 0.28, y, x, y);
            p.Append('Z');
            return p.ToString();
        }

        Move(p, x + tl, y);
        Line(p, x + w - tr, y);
        Cubic(p, x + w - tr * (1 - Kappa), y, x + w, y + tr * (1 - Kappa), x + w, y + tr);
        Line(p, x + w, y + h - br);
        Cubic(p, x + w, y + h - br * (1 - Kappa), x + w - br * (1 - Kappa), y + h, x + w - br, y + h);
        Line(p, x + bl, y + h);
        Cubic(p, x + bl * (1 - Kappa), y + h, x, y + h - bl * (1 - Kappa), x, y + h - bl);
        Line(p, x, y + tl);
        Cubic(p, x, y + tl * (1 - Kappa), x + tl * (1 - Kappa), y, x + tl, y);
        p.Append('Z');
        return p.ToString();
    }

    /// <summary>纯胶囊/圆角矩形（无肩线）。</summary>
    public static string Rounded(double x, double y, double w, double h, double radius) =>
        Build(x, y, w, h, 0, radius, radius, radius, radius);

    private static void Move(StringBuilder p, double x, double y) =>
        p.Append('M').Append(N(x)).Append(',').Append(N(y));

    private static void Line(StringBuilder p, double x, double y) =>
        p.Append('L').Append(N(x)).Append(',').Append(N(y));

    private static void Cubic(StringBuilder p, double x1, double y1, double x2, double y2, double x3, double y3) =>
        p.Append('C').Append(N(x1)).Append(',').Append(N(y1))
            .Append(' ').Append(N(x2)).Append(',').Append(N(y2))
            .Append(' ').Append(N(x3)).Append(',').Append(N(y3));

    private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
