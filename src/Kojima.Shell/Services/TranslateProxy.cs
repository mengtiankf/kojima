using Kojima.Core.Config;

namespace Kojima.Shell.Services;

/// <summary>翻译外壳代理：真正的引擎、密钥读取与 HTTP 都在 translate 子进程里。</summary>
public sealed class TranslateProxy(AgentSupervisor agents, KojimaConfig config)
{
    public async Task<string> TranslateAsync(string text, CancellationToken ct = default)
    {
        if (!config.Feature("translate").Enabled)
        {
            throw new InvalidOperationException("翻译功能未开启：设置 → 功能 → 翻译");
        }

        return await agents.RequestAsync<string>(
            "translate",
            "translate.run",
            new { text },
            60000,
            ct).ConfigureAwait(false) ?? "";
    }
}
