namespace Kojima.Core.Models;

/// <summary>
/// 用量来源元数据。README 承诺 13 个来源，界面的顺序、说明与密钥开关都从这里生成，
/// 避免设置页和子进程各维护一份列表而逐渐漂移。
/// </summary>
/// <param name="Id">稳定标识，同时用作 <c>usage.&lt;id&gt;</c> 密钥名。</param>
/// <param name="Name">显示名。</param>
/// <param name="Group">分组：本机 / 在线。</param>
/// <param name="Description">一句话说明数据从哪里来。</param>
/// <param name="NeedsKey">是否需要用户填写 API Key。</param>
/// <param name="NeedsSite">是否需要站点地址（NewAPI / Sub2API）。</param>
/// <param name="SupportsQuota">是否会尝试读取套餐额度或余额。</param>
/// <param name="SupportsLocal">是否有可读取的本机日志 / 数据库。</param>
public sealed record UsageSourceInfo(
    string Id,
    string Name,
    string Group,
    string Description,
    bool NeedsKey = false,
    bool NeedsSite = false,
    bool SupportsQuota = false,
    bool SupportsLocal = false);

/// <summary>13 个用量来源的目录。</summary>
public static class UsageCatalog
{
    public static readonly IReadOnlyList<UsageSourceInfo> All =
    [
        new("claude", "Claude", "本机", "Claude Code 会话日志 + Claude 账户额度", SupportsQuota: true, SupportsLocal: true),
        new("codex", "Codex", "本机", "Codex CLI 会话日志 + ChatGPT 账户额度", SupportsQuota: true, SupportsLocal: true),
        new("cursor", "Cursor", "在线", "Cursor 账期额度（Token 明细来自账户接口）", SupportsQuota: true),
        new("antigravity", "Antigravity", "本机", "读取 Antigravity 本机会话数据库中的模型用量", SupportsLocal: true),
        new("dsh", "DSH", "本机", "DeepSeek Harness 本机会话", SupportsLocal: true),
        new("kimi", "Kimi", "本机", "Kimi Code 会话日志 + 套餐额度", SupportsQuota: true, SupportsLocal: true),
        new("opencode", "OpenCode", "本机", "OpenCode 本机数据库", SupportsLocal: true),
        new("zcode", "ZCode", "本机", "ZCode 本机数据库", SupportsLocal: true),
        new("deepseek", "DeepSeek", "在线", "DeepSeek 账户余额", NeedsKey: true, SupportsQuota: true),
        new("openrouter", "OpenRouter", "在线", "OpenRouter 额度与累计花费", NeedsKey: true, SupportsQuota: true),
        new("glm", "GLM 智谱", "在线", "智谱 / Z.ai 套餐额度", NeedsKey: true, SupportsQuota: true),
        new("newapi", "NewAPI", "在线", "NewAPI 站点令牌额度", NeedsKey: true, NeedsSite: true, SupportsQuota: true),
        new("sub2api", "Sub2API", "在线", "Sub2API 站点订阅额度", NeedsKey: true, NeedsSite: true, SupportsQuota: true),
    ];

    public static IReadOnlyList<string> Ids { get; } = All.Select(s => s.Id).ToArray();

    public static UsageSourceInfo? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
