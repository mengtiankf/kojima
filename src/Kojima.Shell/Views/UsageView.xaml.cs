using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kojima.Core.Models;
using Kojima.Shell.Theme;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Kojima.Shell.Views;

public partial class UsageView : UserControl
{
    public UsageView()
    {
        InitializeComponent();
        Canvas.PeriodChanged += period => PeriodChanged?.Invoke(period);
        Canvas.RefreshRequested += () => RefreshRequested?.Invoke();
    }
    public string Period
    {
        get => Canvas.Period;
        set => Canvas.Period = value;
    }
    public event Action<string>? PeriodChanged;
    public event Action? RefreshRequested;
    public void SetBusy(bool busy) => Canvas.SetBusy(busy);
    public void Update(IReadOnlyList<UsageSnapshot> snapshots, DateTimeOffset? updatedAt) =>
        Canvas.Update(snapshots, updatedAt);
}

public sealed class UsageCanvas : FrameworkElement
{
    private const string Today = "today", Week = "week", Month = "month";
    private const double PageWidth = 616, Gutter = 8, PadX = 14, PadY = 12, TitleH = 20, TitleGap = 9,
        RowGap = 6, FooterGap = 8, FooterH = 14, HeaderH = 28, HeaderGap = 12, SegW = 56, OthersH = 32,
        RefreshW = 28;
    private static readonly string[] PeriodIds = [Today, Week, Month];
    private static readonly string[] PeriodLabels = ["今日", "本周", "本月"];
    private static readonly Brush Warn = new SolidColorBrush(ThemeRuntime.ColorOf("#FF9F0A"));
    private readonly List<Card> _cards = [];
    private readonly List<UsageSnapshot> _others = [];
    private readonly Rect[] _segBounds = [Rect.Empty, Rect.Empty, Rect.Empty];
    private readonly DispatcherTimer _spinTimer;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private DateTimeOffset? _updatedAt;
    private string _period = Today;
    private bool _busy, _dirty = true, _loaded;
    private double _spin, _dpi = 1.0, _layoutWidth = -1, _contentW, _x0, _contentH = 260;
    private Rect _refreshRect = Rect.Empty, _othersRect = Rect.Empty;

    public UsageCanvas()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _spinTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(24) };
        _spinTimer.Tick += (_, _) => { _spin = (_spin + 9) % 360; InvalidateVisual(); };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event Action<string>? PeriodChanged;
    public event Action? RefreshRequested;
    public string Period
    {
        get => _period;
        set
        {
            var next = Normalize(value);
            if (next == _period) { return; }
            _period = next; _dirty = true; InvalidateMeasure(); InvalidateVisual();
        }
    }
    public void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => SetBusy(busy))); return; }
        if (_busy == busy) { return; }
        _busy = busy; if (busy && _loaded) { _spinTimer.Start(); } else { _spinTimer.Stop(); }
        InvalidateVisual();
    }
    public void Update(IReadOnlyList<UsageSnapshot> snapshots, DateTimeOffset? updatedAt)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Update(snapshots, updatedAt))); return; }
        _snapshots = snapshots ?? Array.Empty<UsageSnapshot>();
        _updatedAt = updatedAt; _dirty = true; InvalidateMeasure(); InvalidateVisual();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) { ThemeRuntime.Changed += OnThemeChanged; }
        _loaded = true; if (_busy) { _spinTimer.Start(); } InvalidateVisual();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) { ThemeRuntime.Changed -= OnThemeChanged; } _loaded = false; _spinTimer.Stop();
    }
    private void OnThemeChanged()
    {
        if (Dispatcher.CheckAccess()) { InvalidateVisual(); } else { Dispatcher.BeginInvoke(new Action(InvalidateVisual)); }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? PageWidth : Math.Max(0, availableSize.Width);
        if (_dirty || Math.Abs(width - _layoutWidth) > 0.5) { Build(width); }
        return new Size(Math.Min(width, PageWidth), _contentH);
    }
    protected override void OnRender(DrawingContext dc)
    {
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth > 0 ? ActualWidth : Math.Max(_layoutWidth, 0);
        if (_dirty || Math.Abs(width - _layoutWidth) > 0.5) { Build(width); }
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, Math.Max(width, 1), Math.Max(ActualHeight, _contentH)));
        DrawHeader(dc);
        if (_cards.Count == 0 && _others.Count == 0) { DrawEmpty(dc, Math.Max(ActualHeight, _contentH)); return; }
        foreach (var card in _cards) { DrawCard(dc, card); }
        if (!_othersRect.IsEmpty) { DrawOthers(dc); }
    }

    // ── 布局 ────────────────────────────────────────────────────────────────

    private void Build(double hostWidth)
    {
        _layoutWidth = hostWidth;
        _dirty = false;
        _cards.Clear(); _others.Clear();
        _contentW = Math.Min(PageWidth, Math.Max(0, hostWidth)); _x0 = Math.Max(0, (hostWidth - _contentW) / 2);
        var filled = new List<Card>();
        foreach (var snap in _snapshots)
        {
            if (snap is null) { continue; }
            if (HasData(snap)) { filled.Add(BuildCard(snap)); } else { _others.Add(snap); }
        }

        var columnW = Math.Max(0, (_contentW - Gutter) / 2);
        var y = HeaderH + HeaderGap;
        for (var i = 0; i < filled.Count; i += 2)
        {
            var first = filled[i];
            var second = i + 1 < filled.Count ? filled[i + 1] : null;
            var rowH = Math.Max(first.Height, second?.Height ?? 0);
            first.Bounds = new Rect(_x0, y, columnW, rowH);
            _cards.Add(first);
            if (second is not null)
            {
                second.Bounds = new Rect(_x0 + columnW + Gutter, y, columnW, rowH);
                _cards.Add(second);
            }

            y += rowH + Gutter;
        }

        _othersRect = Rect.Empty;
        if (_others.Count > 0)
        {
            _othersRect = new Rect(_x0, y, _contentW, OthersH);
            y += OthersH + Gutter;
        }

        _contentH = Math.Max(260, y);
    }
    private Card BuildCard(UsageSnapshot snap)
    {
        var card = new Card { Snap = snap, Error = IsError(snap), Tag = TagOf(snap) };
        if (snap.Quotas.Count > 0)
        {
            foreach (var quota in snap.Quotas.Take(3))
            {
                var used = double.IsFinite(quota.UsedPercent) ? Math.Clamp(quota.UsedPercent, 0, 100) : 0;
                card.Rows.Add(new CardRow
                {
                    Label = quota.Label,
                    Value = $"{used:0.#}%",
                    Suffix = " used",
                    Side = SpanText(UsageFormat.Span(quota.ResetAt, quota.Minutes, DateTimeOffset.Now)),
                    Percent = used,
                    Height = 26,
                });
            }
        }
        else if (snap.Metrics.Count > 0)
        {
            foreach (var metric in snap.Metrics) { card.Rows.Add(new CardRow { Label = metric.Label, Value = MetricText(metric) }); }
        }
        else if (!card.Error)
        {
            card.Rows.Add(new CardRow { Label = PeriodLabel(_period), Value = UsageFormat.Tokens(snap.Totals(Since(_period)).Tokens) });
            card.Rows.Add(new CardRow { Label = "累计", Value = UsageFormat.Tokens(snap.Totals(null).Tokens) });
        }

        // 额度 / 余额卡片也带上当前周期的 Token，满足「今日 / 本周 / 本月 Token 与额度」。
        if (snap.Entries.Count > 0 && (snap.Quotas.Count > 0 || snap.Metrics.Count > 0))
        {
            card.Rows.Add(new CardRow
            {
                Label = PeriodLabel(_period),
                Value = UsageFormat.Tokens(snap.Totals(Since(_period)).Tokens),
                Suffix = " tokens",
            });
        }

        var footer = card.Error ? snap.Error ?? snap.Note : snap.Note;
        footer ??= SourceText(snap.Source);
        card.Footer = string.IsNullOrWhiteSpace(footer) ? null : footer.Trim();
        card.Height = PadY * 2 + TitleH
            + (card.Rows.Count > 0 ? TitleGap + card.Rows.Sum(row => row.Height) + (card.Rows.Count - 1) * RowGap : 0)
            + (card.Footer is null ? 0 : FooterGap + FooterH);
        return card;
    }
    private static bool HasData(UsageSnapshot snap) =>
        snap.Entries.Count > 0 || snap.Quotas.Count > 0 || snap.Metrics.Count > 0 || IsError(snap);
    private static bool IsError(UsageSnapshot snap) =>
        string.Equals(snap.Status, "error", StringComparison.OrdinalIgnoreCase);
    private static string TagOf(UsageSnapshot snap) =>
        !string.IsNullOrWhiteSpace(snap.Plan) ? snap.Plan.Trim()
        : !string.IsNullOrWhiteSpace(snap.Account) ? snap.Account.Trim()
        : SourceText(snap.Source) ?? "未配置";
    private static string? SourceText(string? source) =>
        string.IsNullOrWhiteSpace(source) ? null
        : source.Trim().ToLowerInvariant() is "local" or "device" or "本机" ? "本机" : source.Trim();
    private static string SpanText(string span) =>
        span.Length == 0 || span == "已重置" ? span : "in " + span;
    private static string NameOf(UsageSnapshot snap) =>
        !string.IsNullOrWhiteSpace(snap.Name) ? snap.Name.Trim()
        : !string.IsNullOrWhiteSpace(snap.Pid) ? snap.Pid.Trim()
        : "未知";
    private static string MetricText(Metric metric) => metric.Kind switch
    {
        MetricKind.Percent => PercentText(metric),
        MetricKind.Balance or MetricKind.Spend => metric.Unlimited
            ? "无限"
            : UsageFormat.Money(metric.Amount ?? metric.Used ?? 0, metric.Currency ?? "USD"),
        MetricKind.Count => metric.Unlimited ? "无限" : NumberText(metric.Amount ?? metric.Used ?? 0),
        MetricKind.Text => TextText(metric),
        _ => "—",
    };
    private static string PercentText(Metric metric) =>
        (metric.Percent ?? (metric.Total is > 0 ? (metric.Used ?? 0) * 100.0 / metric.Total.Value : metric.Used)) is { } value
            ? $"{value:0.#}%" : "—";
    private static string NumberText(double value) => value.ToString("#,0.##", CultureInfo.CurrentCulture);
    private static string TextText(Metric metric) =>
        (((metric.Amount is { } amount ? NumberText(amount) : "") + (metric.Unit ?? "")).Trim() is { Length: > 0 } text) ? text
            : metric.Percent is { } percent ? $"{percent:0.#}%" : "—";
    private static DateTimeOffset Since(string period) =>
        new(period switch
        {
            Week => DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7)),
            Month => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            _ => DateTime.Today,
        });
    private static string PeriodLabel(string period) => period switch { Week => "本周", Month => "本月", _ => "今日" };
    private static string Normalize(string? period)
    {
        var value = (period ?? "").Trim().ToLowerInvariant();
        return value is Week or Month ? value : Today;
    }

    // ── 绘制 ────────────────────────────────────────────────────────────────

    private void DrawHeader(DrawingContext dc)
    {
        var segW = SegW * PeriodIds.Length;
        dc.DrawRoundedRectangle(ThemeRuntime.Track, null, new Rect(_x0, 0, segW, HeaderH), 8, 8);
        for (var i = 0; i < PeriodIds.Length; i++)
        {
            var slot = new Rect(_x0 + i * SegW + 2, 2, SegW - 4, HeaderH - 4);
            _segBounds[i] = slot;
            var selected = PeriodIds[i] == _period;
            if (selected) { dc.DrawRoundedRectangle(ThemeRuntime.Card, new Pen(ThemeRuntime.Hairline, 1), slot, 6, 6); }
            var label = Make(PeriodLabels[i], 11.5, selected ? ThemeRuntime.Text : ThemeRuntime.Text2,
                selected ? FontWeights.SemiBold : FontWeights.Normal, false);
            dc.DrawText(label, new Point(slot.Left + (slot.Width - label.Width) / 2,
                slot.Top + (slot.Height - label.Height) / 2));
        }

        _refreshRect = new Rect(_x0 + _contentW - RefreshW, 0, RefreshW, HeaderH);
        var metaMax = Math.Max(60, _refreshRect.Left - 10 - (_x0 + segW) - 16);
        var meta = Make(Meta(), 11, ThemeRuntime.Text3, FontWeights.Normal, false, metaMax);
        dc.DrawText(meta, new Point(_refreshRect.Left - 10 - Math.Min(meta.WidthIncludingTrailingWhitespace, metaMax),
            (HeaderH - meta.Height) / 2));
        if (_busy) { DrawSpinner(dc); return; }
        DrawGlyph(dc, "\uE72C", 13.5, ThemeRuntime.Text2, _refreshRect);
    }
    private string Meta()
    {
        if (_busy) { return "刷新中…"; }
        if (_updatedAt is not { } updated) { return "尚未更新"; }
        var text = $"更新于 {updated.ToLocalTime():HH:mm}";
        return _others.Count > 0 ? $"{text} · {_others.Count} 个无数据" : text;
    }
    private void DrawSpinner(DrawingContext dc)
    {
        var center = new Point(_refreshRect.Left + RefreshW / 2, HeaderH / 2);
        const double radius = 6;
        var angle = _spin * Math.PI / 180;
        dc.DrawEllipse(null, new Pen(ThemeRuntime.Track, 1.5), center, radius, radius);
        dc.DrawEllipse(ThemeRuntime.Accent, null,
            new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), 1.8, 1.8);
    }
    private void DrawGlyph(DrawingContext dc, string glyph, double size, Brush brush, Rect area)
    {
        var text = new FormattedText(glyph, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(ThemeRuntime.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush, _dpi);
        dc.DrawText(text, new Point(area.Left + (area.Width - text.Width) / 2,
            area.Top + (area.Height - text.Height) / 2));
    }
    private void DrawCard(DrawingContext dc, Card card)
    {
        var rect = card.Bounds;
        if (rect.Width <= 1 || rect.Height <= 1) { return; }
        dc.DrawRoundedRectangle(ThemeRuntime.Card, new Pen(ThemeRuntime.Hairline, 1), rect, 10, 10);
        var left = rect.Left + PadX;
        var right = rect.Right - PadX;
        var top = rect.Top + PadY;
        var tagMax = (right - left) * 0.45;
        var tag = Make(card.Error ? "读取失败" : card.Tag, 11, card.Error ? ThemeRuntime.Danger : ThemeRuntime.Text3,
            FontWeights.Normal, false, tagMax);
        var tagW = Math.Min(tag.WidthIncludingTrailingWhitespace, tagMax);
        dc.DrawText(tag, new Point(right - tagW, top + 3));
        dc.DrawText(Make(NameOf(card.Snap), 15, ThemeRuntime.Text, FontWeights.SemiBold, false,
            Math.Max(24, right - left - tagW - 10)), new Point(left, top));

        var y = top + TitleH + TitleGap;
        foreach (var row in card.Rows)
        {
            DrawRow(dc, row, left, right, y);
            y += row.Height + RowGap;
        }

        if (card.Footer is { } footer)
        {
            dc.DrawText(Make(footer, 11, ThemeRuntime.Text3, FontWeights.Normal, false, right - left),
                new Point(left, rect.Bottom - PadY - FooterH + 1));
        }
    }
    private void DrawRow(DrawingContext dc, CardRow row, double left, double right, double y)
    {
        if (row.Percent < 0)
        {
            var value = Make(row.Value, 12, ThemeRuntime.Text, FontWeights.SemiBold, true);
            var valueW = value.WidthIncludingTrailingWhitespace;
            dc.DrawText(value, new Point(right - valueW, y));
            dc.DrawText(Make(row.Label, 12, ThemeRuntime.Text2, FontWeights.Normal, false,
                Math.Max(20, right - left - valueW - 12)), new Point(left, y));
            return;
        }

        var number = Make(row.Value, 12, ThemeRuntime.Text, FontWeights.SemiBold, true);
        var used = Make(row.Suffix ?? "", 11, ThemeRuntime.Text3, FontWeights.Normal, false);
        var side = Make(row.Side ?? "", 11, ThemeRuntime.Text3, FontWeights.Normal, false);
        var sideW = side.WidthIncludingTrailingWhitespace;
        var usedRight = sideW > 0 ? right - sideW - 8 : right;
        var usedW = used.WidthIncludingTrailingWhitespace;
        var numberX = usedRight - usedW - number.WidthIncludingTrailingWhitespace;
        if (sideW > 0) { dc.DrawText(side, new Point(right - sideW, y + 1)); }
        dc.DrawText(used, new Point(usedRight - usedW, y + 1));
        dc.DrawText(number, new Point(numberX, y));
        dc.DrawText(Make(row.Label, 12, ThemeRuntime.Text2, FontWeights.Normal, false,
            Math.Max(20, numberX - left - 10)), new Point(left, y));
        var barW = Math.Max(0, right - left);
        dc.DrawRoundedRectangle(ThemeRuntime.Track, null, new Rect(left, y + 19, barW, 4), 2, 2);
        var filled = barW * row.Percent / 100.0;
        if (filled > 0)
        {
            dc.DrawRoundedRectangle(PercentBrush(row.Percent), null,
                new Rect(left, y + 19, Math.Min(Math.Max(filled, 3), barW), 4), 2, 2);
        }
    }
    private static Brush PercentBrush(double percent) =>
        percent >= 90 ? ThemeRuntime.Danger : percent >= 75 ? Warn : ThemeRuntime.Accent;
    private void DrawOthers(DrawingContext dc)
    {
        var rect = _othersRect;
        dc.DrawRoundedRectangle(ThemeRuntime.Card, null, rect, 8, 8);
        var text = Make("其它来源：" + string.Join(" · ", _others.Select(NameOf)), 11, ThemeRuntime.Text3,
            FontWeights.Normal, false, rect.Width - 24);
        dc.DrawText(text, new Point(rect.Left + 12, rect.Top + (rect.Height - text.Height) / 2));
    }
    private void DrawEmpty(DrawingContext dc, double height)
    {
        var top = HeaderH + HeaderGap;
        var center = top + Math.Max(80, height - top) / 2;
        DrawGlyph(dc, "\uE9D9", 24, ThemeRuntime.Text3, new Rect(_x0, center - 46, _contentW, 30));
        var title = Make("暂无用量数据", 12.5, ThemeRuntime.Text2, FontWeights.SemiBold, false);
        dc.DrawText(title, new Point(_x0 + (_contentW - title.Width) / 2, center - 6));
    }
    private FormattedText Make(string text, double size, Brush brush, FontWeight weight, bool numeric,
        double maxWidth = 0)
    {
        var formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
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
    // ── 交互 ────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        Cursor = _refreshRect.Contains(point) || _segBounds.Any(slot => slot.Contains(point)) ? Cursors.Hand : null;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (!_busy && _refreshRect.Contains(point)) { RefreshRequested?.Invoke(); return; }
        for (var i = 0; i < _segBounds.Length; i++)
        {
            if (!_segBounds[i].Contains(point) || PeriodIds[i] == _period) { continue; }
            _period = PeriodIds[i];
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
            PeriodChanged?.Invoke(_period);
            return;
        }
    }

    private sealed class Card
    {
        public UsageSnapshot Snap = null!;
        public string Tag = "";
        public string? Footer;
        public bool Error;
        public double Height;
        public Rect Bounds;
        public readonly List<CardRow> Rows = [];
    }
    private sealed class CardRow
    {
        public string Label = "";
        public string Value = "";
        public string? Suffix;
        public string? Side;
        public double Percent = -1;
        public double Height = 18;
    }
}
