namespace Kojima.Core.Config;

/// <summary>
/// 密钥名称表：secret 名称只描述“谁的密钥”，与存储实现无关。
/// config.json 里不再出现任何密钥；所有密钥都在 secrets.dat 中按名字加密保存。
/// </summary>
public static class SecretNames
{
    /// <summary>百度翻译密钥（AppID 不是密钥，仍留在配置里）。</summary>
    public const string TranslateBaiduKey = "translate.baidu-key";

    /// <summary>微软翻译（Azure Translator）密钥。</summary>
    public const string TranslateMicrosoftKey = "translate.microsoft-key";

    /// <summary>自定义 OpenAI 兼容端点密钥。</summary>
    public const string TranslateCustomKey = "translate.custom-key";

    /// <summary>某个用量来源的密钥，例如 usage.deepseek。</summary>
    public static string Usage(string source) => "usage." + Normalize(source);

    /// <summary>DeepSeek 的用量/翻译共用密钥。</summary>
    public static string DeepSeek => Usage("deepseek");

    /// <summary>统一来源名：小写、去空白。</summary>
    public static string Normalize(string? source) => (source ?? "").Trim().ToLowerInvariant();
}
