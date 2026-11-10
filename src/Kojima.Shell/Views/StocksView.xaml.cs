using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Shell.Theme;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kojima.Shell.Views;

/// <summary>股票页：新浪/腾讯行情，15 秒轮询。</summary>
public partial class StocksView : UserControl
{
    public StocksView(AppRuntime runtime)
    {
        InitializeComponent();
        // 功能开关只影响自动刷新，手动刷新按钮仍可用。
        Surface.AutoRefresh = runtime.FeatureEnabled("stocks");
    }
}

/// <summary>股票画布：标题、状态、自绘行情行。</summary>
public sealed class StocksSurface : FrameworkElement
{
    private const double PageWidth = 640, PageHeight = 300, HeaderH = 30, HeaderGap = 10, RowH = 40,
        CapsuleW = 64, CapsuleH = 20, RefreshW = 28;

    private static readonly (string Code, string Name)[] Watch =
    [
        ("sh000001", "上证指数"),
        ("sz399001", "深证成指"),
        ("sh600519", "贵州茅台"),
        ("sz300750", "宁德时代"),
        ("sz002594", "比亚迪"),
        ("hk00700", "腾讯控股"),
    ];
    private static readonly Encoding Gbk;
    private static readonly Brush Rise = new SolidColorBrush(ThemeRuntime.ColorOf("#FF453A"));
    private static readonly Brush Fall = new SolidColorBrush(ThemeRuntime.ColorOf("#30D158"));
    private static readonly Brush RisePill = Pill(ThemeRuntime.ColorOf("#FF453A"));
    private static readonly Brush FallPill = Pill(ThemeRuntime.ColorOf("#30D158"));

    private readonly List<Row> _rows = [];
    private readonly DispatcherTimer _timer;
    private DateTimeOffset? _updatedAt;
    private string _status = "尚未更新";
    private bool _error, _busy, _loaded;
    private double _dpi = 1.0, _contentW = PageWidth, _x0;
    private Rect _refreshRect = Rect.Empty;

    static StocksSurface()
    {
        // .NET 10 默认不带 GBK，需要显式注册代码页。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public StocksSurface()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;

        var cfg = AppRuntime.Current?.Config.Stocks;
        var symbols = cfg?.Symbols is { Count: > 0 } list ? list : Watch.Select(w => w.Code).ToList();
        foreach (var code in symbols)
        {
            var known = Watch.FirstOrDefault(w => w.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            _rows.Add(new Row { Code = code, Name = known.Code is null ? code.ToUpperInvariant() : known.Name });
        }

        var seconds = Math.Clamp(cfg?.RefreshSeconds ?? 15, 5, 600);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>是否随页面显示自动轮询。</summary>
    public bool AutoRefresh { get; set; } = true;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) { ThemeRuntime.Changed += OnThemeChanged; }
        _loaded = true;
        _timer.Start();
        if (AutoRefresh) { _ = RefreshAsync(); }
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_loaded) { ThemeRuntime.Changed -= OnThemeChanged; }
        _loaded = false;
    }

    private void OnThemeChanged()
    {
        if (Dispatcher.CheckAccess()) { InvalidateVisual(); } else { Dispatcher.BeginInvoke(new Action(InvalidateVisual)); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? PageWidth : Math.Max(0, availableSize.Width);
        return new Size(Math.Min(width, PageWidth), PageHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth > 0 ? ActualWidth : PageWidth;
        var height = ActualHeight > 0 ? ActualHeight : PageHeight;
        _contentW = Math.Min(PageWidth, width);
        _x0 = Math.Max(0, (width - _contentW) / 2);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(width, 1), Math.Max(height, 1)));
        DrawHeader(dc);
        DrawRows(dc);
    }

    // ── 绘制 ────────────────────────────────────────────────────────────────

    private void DrawHeader(DrawingContext dc)
    {
        var title = Make("股票", 12, ThemeRuntime.Text, FontWeights.SemiBold);
        dc.DrawText(title, new Point(_x0, (HeaderH - title.Height) / 2));

        _refreshRect = new Rect(_x0 + _contentW - RefreshW, 0, RefreshW, HeaderH);
        var statusMax = Math.Max(60, _refreshRect.Left - 10 - (_x0 + title.WidthIncludingTrailingWhitespace) - 12);
        var status = Make(_status, 11, _error ? ThemeRuntime.Danger : ThemeRuntime.Text2, FontWeights.Normal, false,
            statusMax);
        dc.DrawText(status, new Point(_refreshRect.Left - 10 - Math.Min(status.WidthIncludingTrailingWhitespace, statusMax),
            (HeaderH - status.Height) / 2));
        DrawGlyph(dc, "\uE72C", 13.5, _busy ? ThemeRuntime.Text3 : ThemeRuntime.Text2, _refreshRect);
    }

    private void DrawRows(DrawingContext dc)
    {
        var y = HeaderH + HeaderGap;
        var right = _x0 + _contentW;
        for (var i = 0; i < _rows.Count; i++)
        {
            DrawRow(dc, _rows[i], y);
            if (i < _rows.Count - 1)
            {
                dc.DrawRectangle(ThemeRuntime.Hairline, null, new Rect(_x0, y + RowH - 1, _contentW, 1));
            }

            y += RowH;
        }
    }

    private void DrawRow(DrawingContext dc, Row row, double y)
    {
        var name = Make(row.Name, 13, ThemeRuntime.Text, FontWeights.Normal, false, _contentW * 0.45);
        dc.DrawText(name, new Point(_x0 + 2, y + 4));
        dc.DrawText(Make(row.Code.ToUpperInvariant(), 11, ThemeRuntime.Text3, FontWeights.Normal),
            new Point(_x0 + 2, y + 21));

        var pill = new Rect(_x0 + _contentW - CapsuleW - 2, y + (RowH - CapsuleH) / 2, CapsuleW, CapsuleH);
        var (pillText, pillBack) = PillBrushes(row.Percent);
        dc.DrawRoundedRectangle(pillBack, null, pill, 6, 6);
        var percent = Make(PercentText(row.Percent), 11, pillText, FontWeights.SemiBold, true);
        dc.DrawText(percent, new Point(pill.Left + (pill.Width - percent.WidthIncludingTrailingWhitespace) / 2,
            pill.Top + (pill.Height - percent.Height) / 2));

        var price = Make(PriceText(row.Price), 15, ThemeRuntime.Text, FontWeights.SemiBold, true);
        dc.DrawText(price, new Point(pill.Left - 12 - price.WidthIncludingTrailingWhitespace,
            y + (RowH - price.Height) / 2 - 1));
    }

    private void DrawGlyph(DrawingContext dc, string glyph, double size, Brush brush, Rect area)
    {
        var text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(ThemeRuntime.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, _dpi);
        dc.DrawText(text, new Point(area.Left + (area.Width - text.Width) / 2,
            area.Top + (area.Height - text.Height) / 2));
    }

    private FormattedText Make(string text, double size, Brush brush, FontWeight weight, bool numeric = false,
        double maxWidth = 0)
    {
        var formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(numeric ? ThemeRuntime.NumFont : ThemeRuntime.UiFont, FontStyles.Normal, weight,
                FontStretches.Normal), size, brush, _dpi);
        if (maxWidth > 0 && formatted.WidthIncludingTrailingWhitespace > maxWidth)
        {
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.MaxTextHeight = size * 1.8;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        return formatted;
    }

    private static (Brush Text, Brush Pill) PillBrushes(double? percent)
    {
        if (percent is not { } value || value == 0)
        {
            var color = (ThemeRuntime.Text2 as SolidColorBrush)?.Color ?? Colors.Transparent;
            return (ThemeRuntime.Text2, Pill(color));
        }

        return value > 0 ? (Rise, RisePill) : (Fall, FallPill);
    }

    private static Brush Pill(Color color) =>
        new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B));

    private static string PriceText(double? price) => price is { } value ? value.ToString("0.00##", CultureInfo.InvariantCulture) : "--";

    private static string PercentText(double? percent)
    {
        if (percent is not { } value) { return "--"; }
        if (value > 0) { return "+" + value.ToString("0.00", CultureInfo.InvariantCulture) + "%"; }
        if (value < 0) { return value.ToString("0.00", CultureInfo.InvariantCulture) + "%"; }
        return "0.00%";
    }

    // ── 数据 ────────────────────────────────────────────────────────────────

    /// <summary>拉取并解析行情；失败只更新状态文字。</summary>
    public async Task RefreshAsync()
    {
        if (_busy) { return; }
        _busy = true;
        _status = "刷新中…";
        InvalidateVisual();
        try
        {
            var agents = AppRuntime.Current?.Agents
                         ?? throw new InvalidOperationException("运行时尚未就绪");
            var text = await agents.RequestAsync<string>("stocks", "stocks.read",
                new { codes = _rows.Select(r => r.Code).ToArray() }, 20000);
            var quotes = Parse(text ?? "");
            if (quotes.Count == 0) { throw new InvalidOperationException("行情返回为空"); }
            foreach (var row in _rows)
            {
                if (!quotes.TryGetValue(row.Code, out var quote))
                {
                    row.Price = null;
                    row.Percent = null;
                    continue;
                }

                row.Price = quote.Price;
                row.Percent = quote.Percent;
                if (quote.Name.Length > 0) { row.Name = quote.Name; }
            }

            _updatedAt = DateTimeOffset.Now;
            _error = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("行情获取失败：" + ex.Message);
            _error = true;
        }
        finally
        {
            _busy = false;
            _status = _error ? "行情获取失败"
                : _updatedAt is { } updated ? $"更新于 {updated.ToLocalTime():HH:mm}"
                : "尚未更新";
            InvalidateVisual();
        }
    }

    /// <summary>解析 v_xxx="1~名称~代码~现价~昨收~今开~…~涨跌~涨跌幅~最高~最低~…";</summary>
    private static Dictionary<string, Quote> Parse(string text)
    {
        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = line.IndexOf('"');
            var end = line.LastIndexOf('"');
            if (start <= 0 || end <= start) { continue; }
            var code = line[..start].Trim();
            if (code.StartsWith("v_", StringComparison.OrdinalIgnoreCase)) { code = code[2..]; }
            if (code.EndsWith("=", StringComparison.Ordinal)) { code = code[..^1].Trim(); }
            var fields = line[(start + 1)..end].Split('~');
            if (fields.Length < 6) { continue; }

            var price = Num(fields[3]);
            var previous = Num(fields[4]);
            var percent = fields.Length > 32 ? Num(fields[32]) : null;
            if (percent is null && price is { } current && previous is { } close && close != 0)
            {
                percent = (current - close) / close * 100;
            }

            quotes[code] = new Quote(fields[1].Trim(), price, percent);
        }

        return quotes;
    }

    private static double? Num(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    protected override void OnMouseMove(MouseEventArgs e) =>
        Cursor = _refreshRect.Contains(e.GetPosition(this)) ? Cursors.Hand : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_refreshRect.Contains(e.GetPosition(this))) { return; }
        e.Handled = true;
        _ = RefreshAsync();
    }

    private readonly record struct Quote(string Name, double? Price, double? Percent);

    private sealed class Row
    {
        public string Code = "";
        public string Name = "";
        public double? Price;
        public double? Percent;
    }
}
