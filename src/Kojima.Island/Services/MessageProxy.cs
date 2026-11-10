using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>消息中心外壳代理：toast 数据库轮询在 messages 子进程，这里负责展示与打开来源应用。</summary>
public sealed class MessageProxy : IDisposable
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private readonly KojimaConfig _config;
    private readonly List<MessageItem> _items = [];
    private Task? _starting;

    public MessageProxy(AgentSupervisor agents, Dispatcher dispatcher, KojimaConfig config)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _config = config;
        _agents.EventReceived += OnEvent;
    }

    public IReadOnlyList<MessageItem> Items => _items;

    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    public event Action? Changed;

    public void Start() => _starting ??= StartAsync();

    public void Stop() => _ = _agents.RequestAsync<object>("messages", "messages.stop", null, 5000);

    public void Clear()
    {
        _items.Clear();
        Recount();
        Changed?.Invoke();
        _ = _agents.RequestAsync<object>("messages", "messages.clear", null, 5000);
    }

    public void Seed(IEnumerable<MessageItem> items) =>
        _ = _agents.RequestAsync<List<MessageItem>>("messages", "messages.seed", new { items = items.ToArray() }, 8000);

    /// <summary>打开消息来源应用：优先用配置里的路径，其次按进程名尝试。</summary>
    public void Open(string kind)
    {
        var path = _config.Messages.Open.GetValueOrDefault(kind, "");
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            var name = kind switch { "qq" => "QQ", "wechat" => "Weixin", "mail" => "thunderbird", _ => kind };
            var running = Process.GetProcessesByName(name).FirstOrDefault();
            if (running is not null)
            {
                ActivateWindow(running.Id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开消息应用失败：" + ex.Message);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            var items = await _agents.RequestAsync<List<MessageItem>>("messages", "messages.start", null, 20000).ConfigureAwait(false);
            Apply(items);
        }
        catch (Exception ex)
        {
            AppLog.Warn("启动消息子进程失败：" + ex.Message);
        }
    }

    private void OnEvent(string feature, string name, JsonElement data)
    {
        if (feature != "messages" || name != "messages")
        {
            return;
        }

        try
        {
            Apply(data.Deserialize<List<MessageItem>>(IpcJson.Options));
        }
        catch
        {
            // 忽略坏事件，下一轮补上
        }
    }

    private void Apply(List<MessageItem>? items)
    {
        if (items is null)
        {
            return;
        }

        void Update()
        {
            _items.Clear();
            _items.AddRange(items);
            Recount();
            Changed?.Invoke();
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void Recount() =>
        Counts = _items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    private static void ActivateWindow(int pid)
    {
        try
        {
            var handle = Process.GetProcessById(pid).MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
        catch
        {
            // 应用刚退出时忽略
        }
    }

    public void Dispose()
    {
        _agents.EventReceived -= OnEvent;
        Stop();
    }
}
