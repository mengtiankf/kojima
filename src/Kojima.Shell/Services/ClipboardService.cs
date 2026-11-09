using System.Runtime.InteropServices;
using System.Windows.Threading;
using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>
/// 剪贴板历史：轮询 GetClipboardSequenceNumber（0.4 s），保留最近若干条文本。
/// 用轮询而不是 WM_CLIPBOARDUPDATE 监听，是因为消息窗口在部分进程/时机收不到该消息。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int MaxItems = 40;

    private readonly DispatcherTimer _timer;
    private readonly List<ClipItem> _items = [];
    private int _lastSequence = -1;
    private bool _started;

    public ClipboardService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public IReadOnlyList<ClipItem> Items => _items;

    public event Action? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _lastSequence = GetClipboardSequenceNumber();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _started = false;
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
            _lastSequence = GetClipboardSequenceNumber();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("写剪贴板失败：" + ex.Message);
            return false;
        }
    }

    private void Poll()
    {
        try
        {
            var sequence = GetClipboardSequenceNumber();
            if (sequence == _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;
            if (!System.Windows.Clipboard.ContainsText())
            {
                return;
            }

            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000)
            {
                return;
            }

            _items.RemoveAll(i => i.Text == text);
            _items.Insert(0, new ClipItem(text.Trim(), DateTime.Now));
            while (_items.Count > MaxItems)
            {
                _items.RemoveAt(_items.Count - 1);
            }

            Changed?.Invoke();
        }
        catch
        {
            // 剪贴板被其它进程占用时忽略，下一轮再试
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetClipboardSequenceNumber();

    public void Dispose()
    {
        _timer.Stop();
    }
}

public sealed record ClipItem(string Text, DateTime At)
{
    public string TimeText => At.ToString("HH:mm");

    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Trim() is { Length: > 0 } s
        ? s.Length > 120 ? s[..120] + "…" : s
        : "(空白)";
}
