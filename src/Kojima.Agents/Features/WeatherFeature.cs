using System.Net.Http;
using System.Text.Json;
using Kojima.Core.Ipc;

namespace Kojima.Agents.Features;

/// <summary>天气请求放在子进程里，外壳只负责解析与显示。</summary>
public sealed class WeatherFeature : IAgentFeature
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        if (!method.Equals("weather.read", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("未知方法：" + method);
        }

        var location = args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("location", out var value)
            ? value.GetString()?.Trim() ?? ""
            : "";
        var target = location.Length == 0 ? "" : Uri.EscapeDataString(location);
        var url = $"https://wttr.in/{target}?format=j1&lang=zh-cn";
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }
}
