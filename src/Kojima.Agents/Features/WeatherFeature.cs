using System.Net.Http;
using System.Text.Json;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>天气请求与提醒规则放在子进程里，外壳只负责解析与显示。</summary>
public sealed class WeatherFeature : IAgentFeature
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private AgentPipeServer? _server;
    private DateTime _lastAlert = DateTime.MinValue;

    public bool Busy => false;

    public void Attach(AgentPipeServer server) => _server = server;

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
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        await MaybeAlertAsync(body).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return WeatherParser.Parse(document.RootElement);
    }

    /// <summary>雨雪提醒：3 小时内最多一次，判定用 wttr 的中文/英文天气描述。</summary>
    private async Task MaybeAlertAsync(string body)
    {
        if (_server is null || DateTime.Now - _lastAlert < TimeSpan.FromHours(3))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var description = "";
            var current = document.RootElement.GetProperty("current_condition")[0];
            if (current.TryGetProperty("lang_zh", out var zh) && zh.GetArrayLength() > 0)
            {
                description = zh[0].GetProperty("value").GetString() ?? "";
            }

            if (description.Length == 0 && current.TryGetProperty("weatherDesc", out var desc) && desc.GetArrayLength() > 0)
            {
                description = desc[0].GetProperty("value").GetString() ?? "";
            }

            if (description.IndexOf('雨') < 0 && description.IndexOf('雪') < 0 &&
                description.IndexOf("rain", StringComparison.OrdinalIgnoreCase) < 0 &&
                description.IndexOf("snow", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            var temperature = current.TryGetProperty("temp_C", out var temp) ? temp.GetString() ?? "" : "";
            _lastAlert = DateTime.Now;
            await _server.PushAsync("notice", new
            {
                title = "天气提醒",
                body = $"{description}，{temperature}°C，出门记得带伞。",
                kind = "weather",
            }).ConfigureAwait(false);
        }
        catch
        {
            // 天气提醒失败不影响正常取数
        }
    }
}
