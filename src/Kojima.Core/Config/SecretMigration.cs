using System.Runtime.Versioning;
using System.Text.Json;

namespace Kojima.Core.Config;

/// <summary>
/// 旧配置迁移：把 1.2.7 及更早版本写在 config.json 里的明文密钥
/// 搬进加密的 secrets.dat，并在下一次保存配置时把这些字段从 json 中移除。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretMigration
{
    /// <summary>
    /// 扫描配置文本；发现旧密钥就导入密钥库。
    /// 返回值表示“旧结构存在”，调用方据此重启保存配置以清除明文。
    /// </summary>
    public static bool ExtractFromConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            var values = new List<KeyValuePair<string, string>>();

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
            {
                found = true;
                foreach (var property in keys.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        values.Add(new KeyValuePair<string, string>(
                            SecretNames.Usage(property.Name), property.Value.GetString() ?? ""));
                    }
                }
            }

            if (root.TryGetProperty("translate", out var translate) && translate.ValueKind == JsonValueKind.Object)
            {
                Add(translate, "baiduKey", SecretNames.TranslateBaiduKey);
                Add(translate, "microsoftKey", SecretNames.TranslateMicrosoftKey);
                Add(translate, "customKey", SecretNames.TranslateCustomKey);
            }

            if (found)
            {
                SecretStore.ImportLegacy(values);
            }

            return found;

            void Add(JsonElement owner, string property, string name)
            {
                if (!owner.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                found = true;
                values.Add(new KeyValuePair<string, string>(name, value.GetString() ?? ""));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("旧密钥迁移失败：" + ex.Message);
            return false;
        }
    }
}
