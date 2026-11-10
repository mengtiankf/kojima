using System.Runtime.InteropServices;
using System.Text.Json;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>
/// 剪贴板采集全部在子进程里：轮询剪贴板序列号，保留最近 40 条文本，变化时推给外壳。
/// 用 Win32 剪贴板 API 而不是 WPF Clipboard，避免子进程依赖 UI 线程。
/// </summary>
public sealed class ClipboardFeature : IAgentFeature
{
    private const int MaxItems = 40;
    private readonly List<ClipItem> _items = [];
    private AgentPipeServer? _server;
    private Timer? _timer;
    private int _lastSequence = -1;
    private int _polling;
    private volatile bool _started;

    public bool Busy => _started;

    public void Attach(AgentPipeServer server) => _server = server;

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public Task<object?> HandleAsync(string method, JsonElement? args)
    {
        switch (method.ToLowerInvariant())
        {
            case "clipboard.start":
                Start();
                return Task.FromResult<object?>(Snapshot());
            case "clipboard.list":
                return Task.FromResult<object?>(Snapshot());
            case "clipboard.stop":
                Stop();
                return Task.FromResult<object?>(new { ok = true });
            case "clipboard.clear":
                _items.Clear();
                Push();
                return Task.FromResult<object?>(new { ok = true });
            case "clipboard.seed":
                Seed(args);
                return Task.FromResult<object?>(new { ok = true, items = Snapshot() });
            case "clipboard.copy":
                var text = args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("text", out var value)
                    ? value.GetString() ?? ""
                    : "";
                if (text.Length == 0)
                {
                    return Task.FromResult<object?>(new { ok = false });
                }

                var ok = ClipboardNative.WriteText(text);
                if (ok)
                {
                    Remember(text);
                    Push();
                }

                return Task.FromResult<object?>(new { ok });
            default:
                throw new InvalidOperationException("未知方法：" + method);
        }
    }

    private void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _lastSequence = ClipboardNative.SequenceNumber();
        _timer = new Timer(_ => Poll(), null, 400, 400);
    }

    private void Stop()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        if (!_started || Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var sequence = ClipboardNative.SequenceNumber();
            if (sequence == _lastSequence)
            {
                return;
            }

            _lastSequence = sequence;
            var text = ClipboardNative.ReadText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000)
            {
                return;
            }

            Remember(text);
            Push();
        }
        catch
        {
            // 剪贴板被其它进程占用时忽略，下一轮再试
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void Remember(string text)
    {
        _items.RemoveAll(item => item.Text == text);
        _items.Insert(0, new ClipItem(text.Trim(), DateTime.Now));
        while (_items.Count > MaxItems)
        {
            _items.RemoveAt(_items.Count - 1);
        }
    }

    private void Seed(JsonElement? args)
    {
        _items.Clear();
        if (args is { ValueKind: JsonValueKind.Object } payload &&
            payload.TryGetProperty("texts", out var texts) &&
            texts.ValueKind == JsonValueKind.Array)
        {
            var at = DateTime.Now;
            foreach (var item in texts.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    _items.Add(new ClipItem(text, at));
                    at = at.AddMinutes(-3);
                }
            }
        }

        Push();
    }

    private IReadOnlyList<ClipItem> Snapshot() => _items.ToList();

    private void Push() => _ = _server?.PushAsync("clipboard", Snapshot());
}

internal static class ClipboardNative
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static int SequenceNumber()
    {
        try
        {
            return GetClipboardSequenceNumber();
        }
        catch
        {
            return -1;
        }
    }

    public static string? ReadText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    public static bool WriteText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                var bytes = (text.Length + 1) * 2;
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                if (memory == IntPtr.Zero)
                {
                    return false;
                }

                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                try
                {
                    var chars = (text + "\0").ToCharArray();
                    Marshal.Copy(chars, 0, pointer, chars.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern int GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
