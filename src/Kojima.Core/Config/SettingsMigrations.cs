using System.Text.Json.Nodes;

namespace Kojima.Core.Config;

/// <summary>
/// 配置版本迁移：在反序列化之前对原始 JSON 做有序处理，旧字段可以改名/删除，
/// 新字段由 <see cref="KojimaConfig"/> 的默认值补齐。迁移失败时调用方回退默认配置。
/// </summary>
public static class SettingsMigrations
{
    public const int CurrentVersion = 2;

    private static readonly (int Version, Action<JsonObject> Step)[] Steps =
    {
        (2, V2ThemeIds),
    };

    /// <summary>返回是否改动了 JSON（需要回写）。</summary>
    public static bool Apply(JsonObject root)
    {
        var version = root["version"]?.GetValue<int>() ?? 1;
        if (version >= CurrentVersion)
        {
            return false;
        }

        foreach (var (target, step) in Steps)
        {
            if (version >= target)
            {
                continue;
            }

            step(root);
            version = target;
        }

        root["version"] = CurrentVersion;
        return true;
    }

    /// <summary>v1 的主题字段允许写不带 deck- 前缀的 id，统一成目录里的真实 id。</summary>
    private static void V2ThemeIds(JsonObject root)
    {
        if (root["island"] is not JsonObject island)
        {
            return;
        }

        if (island["theme"] is not JsonValue value || !value.TryGetValue<string>(out var theme) ||
            string.IsNullOrWhiteSpace(theme))
        {
            return;
        }

        theme = theme.Trim();
        if (!theme.StartsWith("deck-", StringComparison.OrdinalIgnoreCase))
        {
            island["theme"] = "deck-" + theme;
        }
    }
}
