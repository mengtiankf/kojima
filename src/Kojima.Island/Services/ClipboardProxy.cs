using System.Text.Json;
using System.Windows.Threading;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Island.Services;

/// <summary>剪贴板外壳代理：采集与历史都在 clipboard 子进程，这里只缓存给界面看。</summary>
public sealed class ClipboardProxy : IDisposable
{
    private readonly AgentSupervisor _agents;
    private readonly Dispatcher _dispatcher;
    private readonly List<ClipItem> _items = [];
    private bool _started;

    public ClipboardProxy(AgentSupervisor agents, Dispatcher dispatcher)
    {
        _agents = agents;
        _dispatcher = dispatcher;
        _agents.EventReceived += OnEvent;
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
        _ = StartAsync();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _ = _agents.RequestAsync<object>("clipboard", "clipboard.stop", null, 5000)
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Clear() => _ = _agents.RequestAsync<object>("clipboard", "clipboard.clear", null, 5000);

    public bool Copy(string text)
    {
        _ = _agents.RequestAsync<object>("clipboard", "clipboard.copy", new { text }, 8000);
        return true;
    }

    public void Seed(IEnumerable<string> texts) =>
        _ = _agents.RequestAsync<object>("clipboard", "clipboard.seed", new { texts = texts.ToArray() }, 8000);

    private async Task StartAsync()
    {
        try
        {
            await _agents.RequestAsync<JsonElement>("clipboard", "clipboard.start", null, 15000).ConfigureAwait(false);
            var items = await _agents.RequestAsync<List<ClipItem>>("clipboard", "clipboard.list", null, 8000).ConfigureAwait(false);
            Apply(items);
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("启动剪贴板子进程失败：" + ex.Message);
        }
    }

    private void OnEvent(string feature, string name, JsonElement data)
    {
        if (feature != "clipboard" || name != "clipboard")
        {
            return;
        }

        try
        {
            Apply(data.Deserialize<List<ClipItem>>(IpcJson.Options));
        }
        catch
        {
            // 忽略一条坏事件，下一次轮询会补上
        }
    }

    private void Apply(List<ClipItem>? items)
    {
        if (items is null)
        {
            return;
        }

        void Update()
        {
            _items.Clear();
            _items.AddRange(items);
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

    public void Dispose()
    {
        _agents.EventReceived -= OnEvent;
        Stop();
    }
}
