using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Kojima.Core.Models;
using Kojima.Island.Theme;

namespace Kojima.Island.Views;

/// <summary>监控画布：CPU / GPU / 内存 / 网络 / 磁盘 / 进程，自绘。</summary>
public sealed class MonitorSurface : FrameworkElement
{
    private const int ProcRows = 7;
    private const int History = 60;

    private readonly Queue<double> _down = new();
    private readonly Queue<double> _up = new();
    private MonitorSample? _sample;
    private string _sort = "cpu";

    public MonitorSurface()
    {
        AutomationProperties.SetName(this, "系统监控");
        SnapsToDevicePixels = true;
        ThemeRuntime.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    public void Update(MonitorSample sample)
    {
        _sample = sample;
        Push(_down, sample.Net.DownBps);
        Push(_up, sample.Net.UpBps);
        InvalidateVisual();
    }

    public void SetSort(string sort)
    {
        _sort = sort;
        InvalidateVisual();
    }

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > History)
        {
            queue.Dequeue();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var sample = _sample;
        if (sample is null)
        {
            DrawText(dc, "等待采样…", 12, ThemeRuntime.Text3, new Point(4, 6));
            return;
        }

        var left = w * 0.57 - 8;
        var rightX = left + 16;
        var right = w - 4;

        var y = 2.0;
        y = DrawCpu(dc, sample, 0, y, left);
        y = DrawCores(dc, sample, 0, y + 4, left);
        y = DrawGpu(dc, sample, 0, y + 8, left);
        y = DrawMem(dc, sample, 0, y + 6, left);
        y = DrawNet(dc, sample, 0, y + 8, left);
        DrawDisks(dc, sample, 0, y + 4, left, h);

        DrawProcesses(dc, sample, rightX, 0, right - rightX, h);
    }

    // ------------------------------------------------------------------ CPU

    private double DrawCpu(DrawingContext dc, MonitorSample sample, double x, double y, double width)
    {
        var cpu = sample.Cpu;
        DrawRing(dc, new Point(x + 7, y + 12), 6, cpu.Percent / 100.0, ThemeRuntime.AccentColor);
        DrawText(dc, "CPU", 11.5, ThemeRuntime.Text2, new Point(x + 20, y + 3), FontWeights.SemiBold);
        DrawText(dc, $"{cpu.Percent:0}%", 21, ThemeRuntime.Text, new Point(x + 56, y - 3), FontWeights.SemiBold, true);

        var meta = new List<string>();
        if (sample.Temp.Cpu is { } t)
        {
            meta.Add($"{t:0}°C");
        }

        if (cpu.FreqMhz > 0)
        {
            meta.Add($"{cpu.FreqMhz / 1000:0.0}GHz");
        }

        if (cpu.PerCore.Length > 0)
        {
            meta.Add($"{cpu.PerCore.Length} 核");
        }

        DrawText(dc, string.Join(" ", meta), 10.5, ThemeRuntime.Text3, new Point(x + 106, y + 4));

        // 右上角历史折线
        var sparkRect = new Rect(x + width - 96, y + 4, 96, 18);
        DrawSpark(dc, sparkRect, _down, ThemeRuntime.AccentColor, 0);
        return y + 24;
    }

    private double DrawCores(DrawingContext dc, MonitorSample sample, double x, double y, double width)
    {
        var cores = sample.Cpu.PerCore;
        if (cores.Length == 0)
        {
            return y + 66;
        }

        const double height = 66;
        var gap = 3.0;
        var barWidth = Math.Max(4, (width - gap * (cores.Length - 1)) / cores.Length);
        for (var i = 0; i < cores.Length; i++)
        {
            var value = Math.Clamp(cores[i] / 100.0, 0, 1);
            var barX = x + i * (barWidth + gap);
            var rect = new Rect(barX, y, barWidth, height);
            var radius = barWidth / 2;

            dc.DrawRoundedRectangle(ThemeRuntime.Track, null, rect, radius, radius);

            var fillHeight = Math.Max(3, height * value);
            var fill = new Rect(barX, y + height - fillHeight, barWidth, fillHeight);
            var color = value switch
            {
                >= 0.85 => Color.FromRgb(0xFF, 0x9F, 0x0A),
                >= 0.6 => Color.FromRgb(0xFF, 0xD6, 0x0A),
                _ => ThemeRuntime.AccentColor,
            };
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, fill, radius, radius);
        }

        return y + height;
    }

    // ------------------------------------------------------------------ GPU

    private double DrawGpu(DrawingContext dc, MonitorSample sample, double x, double y, double width)
    {
        var gpu = sample.Gpus.FirstOrDefault();
        if (gpu is null)
        {
            DrawText(dc, "GPU  未检测到", 11, ThemeRuntime.Text3, new Point(x + 4, y + 4));
            return y + 24;
        }

        DrawRing(dc, new Point(x + 7, y + 12), 6, gpu.Util / 100.0, ThemeRuntime.AccentColor);
        DrawText(dc, "GPU", 11.5, ThemeRuntime.Text2, new Point(x + 20, y + 3), FontWeights.SemiBold);
        DrawText(dc, $"{gpu.Util:0}%", 21, ThemeRuntime.Text, new Point(x + 56, y - 3), FontWeights.SemiBold, true);

        var parts = new List<string>();
        if (gpu.MemTotal > 0)
        {
            parts.Add($"显存 {gpu.MemUsed / 1073741824.0:0.0}/{gpu.MemTotal / 1073741824.0:0.0} GB");
        }

        if (gpu.Temp is { } temp)
        {
            parts.Add($"{temp:0}°C");
        }

        if (gpu.Power is { } power)
        {
            parts.Add($"{power:0}W");
        }

        DrawText(dc, string.Join(" ", parts), 10.5, ThemeRuntime.Text3, new Point(x + 106, y + 4));
        return y + 24;
    }

    // ------------------------------------------------------------------ 内存

    private double DrawMem(DrawingContext dc, MonitorSample sample, double x, double y, double width)
    {
        var mem = sample.Mem;
        DrawRing(dc, new Point(x + 7, y + 10), 6, mem.Percent / 100.0, ThemeRuntime.AccentColor);
        DrawText(dc, "内存", 11.5, ThemeRuntime.Text2, new Point(x + 20, y + 1), FontWeights.SemiBold);
        DrawText(dc, $"{mem.Percent:0}%", 21, ThemeRuntime.Text, new Point(x + 56, y - 5), FontWeights.SemiBold, true);

        var meta = $"{mem.Used / 1073741824.0:0.0}/{mem.Total / 1073741824.0:0.0} GB";
        if (mem.SwapTotal > 0)
        {
            meta += $" · 页面文件 {mem.SwapPercent:0}%";
        }

        DrawText(dc, meta, 10.5, ThemeRuntime.Text3, new Point(x + 106, y + 2));

        var track = new Rect(x, y + 22, width, 3);
        dc.DrawRoundedRectangle(ThemeRuntime.Track, null, track, 1.5, 1.5);
        var fillWidth = width * Math.Clamp(mem.Percent / 100.0, 0, 1);
        if (fillWidth > 1)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(AccentColor(mem.Percent)), null,
                new Rect(x, y + 22, fillWidth, 3), 1.5, 1.5);
        }

        return y + 26;
    }

    // ------------------------------------------------------------------ 网络

    private double DrawNet(DrawingContext dc, MonitorSample sample, double x, double y, double width)
    {
        var half = (width - 10) / 2;
        DrawText(dc, "↓", 11, ThemeRuntime.Text2, new Point(x, y + 8));
        DrawText(dc, FormatRate(sample.Net.DownBps), 10, ThemeRuntime.Text3, new Point(x + 14, y - 1));
        DrawSpark(dc, new Rect(x + 4, y + 12, half - 16, 22), _down, Color.FromRgb(0x3D, 0xA5, 0xF5), 0);

        var x2 = x + half + 10;
        DrawText(dc, "↑", 11, ThemeRuntime.Text2, new Point(x2, y + 8));
        DrawText(dc, FormatRate(sample.Net.UpBps), 10, ThemeRuntime.Text3, new Point(x2 + 14, y - 1));
        DrawSpark(dc, new Rect(x2 + 4, y + 12, half - 16, 22), _up, Color.FromRgb(0xFF, 0x9F, 0x0A), 0);
        return y + 36;
    }

    // ------------------------------------------------------------------ 磁盘

    private void DrawDisks(DrawingContext dc, MonitorSample sample, double x, double y, double width, double height)
    {
        var disks = sample.Disks.Take(3).ToList();
        y = Math.Max(y, height - 20.0 * disks.Count - 20);
        foreach (var disk in disks)
        {
            var ratio = disk.Total == 0 ? 0 : (double)disk.Used / disk.Total;
            var label = $"{disk.Name}  {disk.Used / 1073741824.0:0}/{disk.Total / 1073741824.0:0} GB";
            DrawText(dc, label, 10, ThemeRuntime.Text3, new Point(x, y));
            var track = new Rect(x, y + 14, width * 0.62, 3);
            dc.DrawRoundedRectangle(ThemeRuntime.Track, null, track, 1.5, 1.5);
            if (ratio > 0)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(AccentColor(ratio * 100)), null,
                    new Rect(x, y + 14, Math.Max(2, track.Width * ratio), 3), 1.5, 1.5);
            }

            y += 20;
        }

        var footer = new List<string>();
        if (sample.Temp.Cpu is { } cpuTemp)
        {
            footer.Add($"CPU {cpuTemp:0}°C");
        }

        if (sample.Temp.Gpu is { } gpuTemp)
        {
            footer.Add($"GPU {gpuTemp:0}°C");
        }

        if (!string.IsNullOrWhiteSpace(sample.Temp.Source)
            && sample.Temp.Source is not ("acpi" or "nvidia"))
        {
            footer.Add(sample.Temp.Source);
        }

        if (sample.Temp.Power is { } tempPower)
        {
            footer.Add($"{tempPower:0} W");
        }

        if (sample.Power.BatteryPercent is { } battery)
        {
            var text = $"电池 {battery}%";
            if (sample.Power.Plugged)
            {
                text += " 充电中";
            }
            else if (sample.Power.SecondsLeft is > 0 and var secondsLeft)
            {
                text += $" 剩余 {FormatSpan(TimeSpan.FromSeconds(secondsLeft))}";
            }

            if (sample.Power.HealthPercent is { } health)
            {
                text += $" · 健康 {health:0}%";
            }

            footer.Add(text);
        }

        footer.Add($"运行 {FormatUptime(sample.UptimeSeconds)}");
        DrawText(dc, string.Join(" · ", footer), 10, ThemeRuntime.Text3, new Point(x, height - 14));
    }

    // ------------------------------------------------------------------ 进程

    private void DrawProcesses(DrawingContext dc, MonitorSample sample, double x, double y, double width, double height)
    {
        DrawText(dc, "进程", 11.5, ThemeRuntime.Text2, new Point(x, y + 2), FontWeights.SemiBold);
        var sortLabel = _sort == "mem" ? "TOP 7 · 内存" : "TOP 7";
        DrawText(dc, sortLabel, 10, ThemeRuntime.Text3, new Point(x, y + 3), FontWeights.Normal, false, width);

        var procs = _sort == "mem"
            ? sample.Processes.OrderByDescending(p => p.Mem).Take(ProcRows).ToList()
            : sample.Processes.OrderByDescending(p => p.Cpu).Take(ProcRows).ToList();

        var max = Math.Max(1, procs.Count == 0 ? 1 : procs.Max(p => _sort == "mem" ? p.Mem : p.Cpu));
        var rowY = y + 24;
        const double rowHeight = 22;

        foreach (var proc in procs)
        {
            var value = _sort == "mem" ? proc.Mem : proc.Cpu;
            var ratio = Math.Clamp(value / max, 0, 1);
            var track = new Rect(x, rowY, width, rowHeight - 4);
            dc.DrawRoundedRectangle(ThemeRuntime.Track, null, track, 7, 7);
            if (ratio > 0.01)
            {
                var fill = new Rect(x, rowY, Math.Max(10, width * ratio), rowHeight - 4);
                var color = _sort == "mem" ? Color.FromRgb(0x30, 0xD1, 0x58) : ThemeRuntime.AccentColor;
                dc.DrawRoundedRectangle(new SolidColorBrush(color) { Opacity = 0.42 }, null, fill, 7, 7);
            }

            DrawText(dc, proc.Name, 11, ThemeRuntime.Text, new Point(x + 8, rowY + 2));
            var valueText = _sort == "mem"
                ? $"{proc.Mem / 1048576.0:0} MB"
                : $"{proc.Cpu:0.0}%";
            DrawText(dc, valueText, 10, ThemeRuntime.Text2, new Point(x, rowY + 3), FontWeights.Normal, false, width - 8);
            rowY += rowHeight;
        }
    }

    // ------------------------------------------------------------------ 绘图助手

    private void DrawRing(DrawingContext dc, Point center, double radius, double value, Color color)
    {
        var pen = new Pen(ThemeRuntime.Track, 2.4);
        dc.DrawEllipse(null, pen, center, radius, radius);

        value = Math.Clamp(value, 0, 1);
        if (value <= 0.001)
        {
            return;
        }

        var accentPen = new Pen(new SolidColorBrush(color), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var start = new Point(center.X, center.Y - radius);
            var endAngle = Math.PI * 2 * value - Math.PI / 2;
            var end = new Point(center.X + radius * Math.Cos(endAngle), center.Y + radius * Math.Sin(endAngle));
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, accentPen, geometry);
    }

    private void DrawSpark(DrawingContext dc, Rect rect, Queue<double> values, Color color, double baseline)
    {
        if (values.Count < 2 || rect.Width < 8)
        {
            return;
        }

        var list = values.ToList();
        var max = Math.Max(1, list.Max());
        var step = rect.Width / Math.Max(1, list.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < list.Count; i++)
            {
                var px = rect.X + i * step;
                var py = rect.Bottom - Math.Clamp(list[i] / max, 0, 1) * (rect.Height - 3) - 1;
                var point = new Point(px, py);
                if (i == 0)
                {
                    ctx.BeginFigure(point, false, false);
                }
                else
                {
                    ctx.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color) { Opacity = 0.9 }, 1.2), geometry);
    }

    private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point origin,
        FontWeight? weight = null, bool numeric = false, double? rightAlignWidth = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var family = numeric ? ThemeRuntime.NumFont : ThemeRuntime.UiFont;
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var x = rightAlignWidth is { } boxWidth ? origin.X + boxWidth - formatted.Width : origin.X;
        dc.DrawText(formatted, new Point(x, origin.Y));
    }

    private static Color AccentColor(double percent)
    {
        var accent = ThemeRuntime.AccentColor;
        return percent switch
        {
            >= 90 => Color.FromRgb(0xFF, 0x45, 0x3A),
            >= 75 => Color.FromRgb(0xFF, 0x9F, 0x0A),
            _ => accent,
        };
    }

    private static string FormatRate(double bytes)
    {
        if (bytes >= 1048576)
        {
            return $"{bytes / 1048576:0.0} MB/s";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024:0} KB/s";
        }

        return $"{bytes:0} B/s";
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        }

        return $"{Math.Max(1, span.Minutes)} 分";
    }

    private static string FormatUptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays} 天 {span.Hours} 小时"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{span.Minutes} 分 {span.Seconds} 秒";
    }
}
