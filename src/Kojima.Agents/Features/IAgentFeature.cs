using System.Text.Json;
using Kojima.Core.Ipc;

namespace Kojima.Agents.Features;

/// <summary>一个功能子进程里的实现模块。</summary>
public interface IAgentFeature : IAsyncDisposable
{
    /// <summary>绑定管道，用于主动推送（订阅类功能）。</summary>
    void Attach(AgentPipeServer pipe);

    /// <summary>是否有持续任务（订阅中），供空闲退出判断。</summary>
    bool Busy { get; }

    /// <summary>处理一条请求，返回值序列化为 JSON result。</summary>
    Task<object?> HandleAsync(string method, JsonElement? args);
}

/// <summary>占位实现：用于尚未移植的功能，避免子进程直接退出。</summary>
public sealed class NoopFeature : IAgentFeature
{
    public void Attach(AgentPipeServer pipe)
    {
    }

    public bool Busy => false;

    public Task<object?> HandleAsync(string method, JsonElement? args) =>
        Task.FromResult<object?>(new { ok = true, method, note = "该功能尚未实现" });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
