using System.Text.Json;
using Kojima.Core.Config;
using Kojima.Core.Ipc;
using Kojima.Core.Services;

namespace Kojima.Agents.Features;

/// <summary>翻译全部在子进程里完成：外壳只负责把文本送进来、把结果画出来。</summary>
public sealed class TranslateFeature : IAgentFeature
{
    public bool Busy => false;

    public void Attach(AgentPipeServer server)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<object?> HandleAsync(string method, JsonElement? args)
    {
        if (!method.Equals("translate.run", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("未知方法：" + method);
        }

        var text = args is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("text", out var value)
            ? value.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var config = ConfigStore.Load();
        return await TranslationService.TranslateAsync(text, config.Translate).ConfigureAwait(false);
    }
}
