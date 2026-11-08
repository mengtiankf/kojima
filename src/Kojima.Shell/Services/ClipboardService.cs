using System.Runtime.InteropServices;
using System.Windows.Interop;
using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>剪贴板历史：监听 WM_CLIPBOARDUPDATE，保留最近若干条文本。</summary>
public sealed class ClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxItems = 40;

    private readonly HwndSource _source;
    private bool _started;

    public ClipboardService()
    {
        var parameters = new HwndSourceParameters("KojimaClipboardHook")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IReadOnlyList<ClipItem> Items => _items;

    public event Action? Changed;

    private readonly List<ClipItem> _items = [];

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (!AddClipboardFormatListener(_source.Handle))
        {
            AppLog.Warn("剪贴板监听注册失败");
        }
    }


    /// <summary>演示数据：只在 KOJIMA_DEMO 截图时使用，不写系统剪贴板。</summary>
    public void Seed(IEnumerable<string> texts)
    {
        _items.Clear();
        var at = DateTime.Now;
        foreach (var text in texts)
        {
            _items.Add(new ClipItem(text, at));
            at = at.AddMinutes(-3);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Changed?.Invoke();
    }

    public bool Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("写剪贴板失败：" + ex.Message);
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return IntPtr.Zero;
            }

            var text = System.Windows.Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 20_000)
            {
                _items.RemoveAll(i => i.Text == text);
                _items.Insert(0, new ClipItem(text, DateTime.Now));
                while (_items.Count > MaxItems)
                {
                    _items.RemoveAt(_items.Count - 1);
                }

                Changed?.Invoke();
            }
        }
        catch
        {
            // 剪贴板被其它进程占用时忽略
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void Dispose()
    {
        if (_started)
        {
            RemoveClipboardFormatListener(_source.Handle);
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

public sealed record ClipItem(string Text, DateTime At)
{
    public string TimeText => At.ToString("HH:mm");

    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Trim() is { Length: > 0 } s
        ? s.Length > 120 ? s[..120] + "…" : s
        : "(空白)";
}
