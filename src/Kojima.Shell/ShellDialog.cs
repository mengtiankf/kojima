using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Kojima.Core.Config;
using Kojima.Core.Themes;

namespace Kojima.Shell;

/// <summary>
/// 托盘进程里的小型提示框：无边框、跟随应用主题，避免系统 MessageBox 的复古外观。
/// 只在“找不到灵动岛”这类致命错误时使用。
/// </summary>
internal sealed class ShellDialog : Form
{
    private const int CsDropShadow = 0x20000;

    private readonly Color _hairline;
    private readonly Color _danger;

    private ShellDialog(string title, string message, bool error)
    {
        var palette = LoadPalette();
        var surface = ToColor(palette?.Surface, Color.FromArgb(28, 28, 30));
        var text = ToColor(palette?.Text, Color.FromArgb(245, 245, 247));
        var text2 = ToColor(palette?.Text2, Color.FromArgb(170, 170, 176));
        var accent = ToColor(palette?.Accent, Color.FromArgb(10, 132, 255));
        var onAccent = ToColor(palette?.OnAccent, Color.White);
        _hairline = ToColor(palette?.Hairline, Color.FromArgb(70, 70, 76));
        _danger = ToColor(palette?.Danger, Color.FromArgb(255, 69, 58));

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = surface;
        ForeColor = text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 22, 24, 20),
            BackColor = surface,
        };

        var titleLabel = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12.5f),
            ForeColor = error ? _danger : text,
            Margin = new Padding(0, 0, 0, 10),
        };

        var body = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(392, 0),
            ForeColor = text2,
            Margin = new Padding(0, 0, 0, 18),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = surface,
        };
        var ok = new FlatButton("确定", accent, onAccent) { DialogResult = DialogResult.OK };
        buttons.Controls.Add(ok);

        table.Controls.Add(titleLabel, 0, 0);
        table.Controls.Add(body, 0, 1);
        table.Controls.Add(buttons, 0, 2);
        Controls.Add(table);
        AcceptButton = ok;

        Resize += (_, _) => UpdateRegion();
        Shown += (_, _) => UpdateRegion();
    }

    public static void Show(string title, string message, bool error = false)
    {
        using var dialog = new ShellDialog(title, message, error);
        dialog.ShowDialog();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var pen = new Pen(_hairline, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = RoundedPath(new Rectangle(0, 0, Width, Height), 12);
        Region = new Region(path);
    }

    internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0 || bounds.Width <= d || bounds.Height <= d)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static DeckPalette? LoadPalette()
    {
        try
        {
            return ThemeCatalog.Get(ConfigStore.Load().Island.Theme);
        }
        catch
        {
            return null;
        }
    }

    internal static Color ToColor(Rgba? color, Color fallback) =>
        color is { } value
            ? Color.FromArgb(value.A, value.R, value.G, value.B)
            : fallback;

    private sealed class FlatButton : Button
    {
        private readonly Color _background;
        private readonly Color _foreground;

        public FlatButton(string text, Color background, Color foreground)
        {
            Text = text;
            _background = background;
            _foreground = foreground;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Height = 34;
            Width = 96;
            Cursor = Cursors.Hand;
            BackColor = background;
            ForeColor = foreground;
            Font = new Font("Segoe UI Semibold", 9.5f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(ClientRectangle, 8);
            using var brush = new SolidBrush(_background);
            e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                _foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width > 0 && Height > 0)
            {
                using var path = RoundedPath(ClientRectangle, 8);
                Region = new Region(path);
            }
        }
    }
}
