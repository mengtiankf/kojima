using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Kojima.Core.Ipc;

/// <summary>子进程侧：命名管道服务，按行收发 JSON。</summary>
public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, JsonElement?, Task<JsonElement?>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _loop;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private DateTime _lastActivity = DateTime.UtcNow;

    public AgentPipeServer(string pipeName, Func<string, JsonElement?, Task<JsonElement?>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    /// <summary>最近一次收到请求/连接的时间，供空闲退出判断。</summary>
    public DateTime LastActivity => _lastActivity;

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                _lastActivity = DateTime.UtcNow;

                using var reader = new StreamReader(_pipe, Encoding.UTF8);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    _lastActivity = DateTime.UtcNow;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    _ = HandleLineAsync(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("pipe error: " + ex.Message);
            }
            finally
            {
                try
                {
                    _pipe?.Dispose();
                }
                catch
                {
                    // 忽略
                }

                _pipe = null;
                _writer = null;
            }
        }
    }

    private async Task HandleLineAsync(string line)
    {
        IpcEnvelope? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcEnvelope>(line, IpcJson.Options);
        }
        catch
        {
            return;
        }

        if (request?.Method is null || request.Id is null)
        {
            return;
        }

        IpcEnvelope response = new() { Id = request.Id };
        try
        {
            response.Result = await _handler(request.Method, request.Params).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response.Error = ex.GetType().Name + ": " + ex.Message;
        }

        await SendAsync(response).ConfigureAwait(false);
    }

    public async Task SendAsync(IpcEnvelope envelope)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(envelope, IpcJson.Options);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        catch
        {
            // 主进程断开：忽略，等待重连
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task PushAsync(string name, object? data)
    {
        var element = data is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(data, IpcJson.Options);
        return SendAsync(new IpcEnvelope { Event = name, Data = element });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _pipe?.Dispose();
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
