using System.Windows.Threading;

namespace Kojima.Shell.Services;

/// <summary>应用内消息中心：天气/监控/股票等规则触发的通知都进这里。</summary>
public sealed class NotifyCenter
{
    private readonly Dispatcher _dispatcher;
    private readonly List<Notice> _items = [];

    public NotifyCenter(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public IReadOnlyList<Notice> Items => _items;

    public event Action? Changed;

    public void Push(string title, string body, string kind = "info")
    {
        if (_dispatcher.CheckAccess())
        {
            PushCore(title, body, kind);
        }
        else
        {
            _dispatcher.BeginInvoke(() => PushCore(title, body, kind));
        }
    }

    private void PushCore(string title, string body, string kind)
    {
        // 同标题 1 分钟内只提醒一次，避免刷屏
        var cutoff = DateTime.Now.AddMinutes(-1);
        if (_items.Any(i => i.Title == title && i.At > cutoff))
        {
            return;
        }

        _items.Insert(0, new Notice(title, body, kind, DateTime.Now));
        while (_items.Count > 60)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Changed?.Invoke();
    }
}

public sealed record Notice(string Title, string Body, string Kind, DateTime At)
{
    public string TimeText => At.ToString("HH:mm");
}
