using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Kojima.Core.Ipc;

/// <summary>主进程侧：连接某个功能子进程的命名管道。</summary>
public sealed class AgentPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _nextId;
    private Task? _loop;
    private int _closed;

    private AgentPipeClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _reader = new StreamReader(pipe, Encoding.UTF8);
    }

    public bool Connected => Volatile.Read(ref _closed) == 0 && _pipe.IsConnected;

    public event Action<string, JsonElement>? EventReceived;

    public event Action? Closed;

    public static async Task<AgentPipeClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var left = (int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds);
                await pipe.ConnectAsync(Math.Min(left, 250), ct).ConfigureAwait(false);
                var client = new AgentPipeClient(pipe);
                client._loop = Task.Run(client.ReadLoopAsync);
                return client;
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                pipe.Dispose();
                await Task.Delay(60, ct).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"连接子进程管道超时：{pipeName}（{last?.Message}）");
    }

    public async Task<JsonElement?> RequestAsync(string method, object? args = null, int timeoutMs = 15000)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var envelope = new IpcEnvelope
            {
                Id = id,
                Method = method,
                Params = args is null ? null : JsonSerializer.SerializeToElement(args, IpcJson.Options),
            };
            await SendAsync(envelope).ConfigureAwait(false);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (done != tcs.Task)
            {
                throw new TimeoutException($"子进程无响应：{method}");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T?> RequestAsync<T>(string method, object? args = null, int timeoutMs = 15000)
    {
        var element = await RequestAsync(method, args, timeoutMs).ConfigureAwait(false);
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return element.Value.Deserialize<T>(IpcJson.Options);
    }

    private async Task SendAsync(IpcEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope, IpcJson.Options);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                IpcEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
                }
                catch
                {
                    continue;
                }

                if (envelope is null)
                {
                    continue;
                }

                if (envelope.Id is { } id && _pending.TryRemove(id, out var tcs))
                {
                    if (envelope.Error is { Length: > 0 } error)
                    {
                        tcs.TrySetException(new InvalidOperationException(error));
                    }
                    else
                    {
                        tcs.TrySetResult(envelope.Result);
                    }
                }
                else if (!string.IsNullOrEmpty(envelope.Event) && envelope.Data is { } data)
                {
                    try
                    {
                        EventReceived?.Invoke(envelope.Event!, data);
                    }
                    catch
                    {
                        // 订阅方异常不影响读循环
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception)
        {
            // 管道断开
        }
        finally
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                foreach (var pair in _pending)
                {
                    pair.Value.TrySetException(new IOException("子进程已断开"));
                }

                _pending.Clear();
                try
                {
                    Closed?.Invoke();
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _writer.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _pipe.Dispose();
        }
        catch
        {
            // 忽略
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // 忽略
            }
        }

        _cts.Dispose();
        _writeGate.Dispose();
    }
}
