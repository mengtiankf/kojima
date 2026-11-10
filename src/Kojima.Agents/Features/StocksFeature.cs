using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kojima.Core.Ipc;

namespace Kojima.Agents.Features;

/// <summary>股票行情同样走子进程：GBK 解码与网络请求都不占用界面进程。</summary>
public sealed class StocksFeature : IAgentFeature
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static readonly Encoding Gbk;

    static StocksFeature()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        if (!method.Equals("stocks.read", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("未知方法：" + method);
        }

        var codes = new List<string>();
        if (args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("codes", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var code = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(code))
                {
                    codes.Add(code);
                }
            }
        }

        if (codes.Count == 0)
        {
            return "";
        }

        var url = "https://qt.gtimg.cn/q=" + string.Join(",", codes);
        using var response = await Http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return Gbk.GetString(bytes);
    }
}
