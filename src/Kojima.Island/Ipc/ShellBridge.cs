using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Kojima.Island.Ipc;

/// <summary>灵动岛到 Shell 监督器的长连接；Shell 负责托盘、重启和更新后的收尾。</summary>
internal sealed class ShellBridge : IDisposable
{
    private readonly string _pipeName;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cancellation;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    public ShellBridge(string pipeName) => _pipeName = pipeName;

    public event Action<string>? CommandReceived;

    public bool Connected
    {
        get
        {
            lock (_writeLock)
            {
                return _writer is not null;
            }
        }
    }

    public void Start()
    {
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cancellation.Token));
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(15000, cancellationToken).ConfigureAwait(false);

                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                lock (_writeLock)
                {
                    _pipe = pipe;
                    _writer = writer;
                }

                Send("ready");
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                string? line;
                while (!cancellationToken.IsCancellationRequested &&
                       (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                {
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("Shell 通信失败：" + ex.Message);
            }
            finally
            {
                lock (_writeLock)
                {
                    _writer = null;
                    _pipe = null;
                }

                pipe?.Dispose();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void Dispatch(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("cmd", out var value) &&
                value.GetString() is { } command)
            {
                CommandReceived?.Invoke(command);
            }
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("解析 Shell 命令失败：" + ex.Message);
        }
    }

    public void Send(string evt)
    {
        lock (_writeLock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new { @event = evt }));
                _writer.Flush();
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("向 Shell 发送事件失败：" + ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        lock (_writeLock)
        {
            _writer = null;
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
