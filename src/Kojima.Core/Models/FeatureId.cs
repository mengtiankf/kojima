namespace Kojima.Core.Models;

/// <summary>可选功能模块。每个模块在独立子进程中运行，未启用时不启动。</summary>
public enum FeatureId
{
    Usage,
    Monitor,
    Media,
    Weather,
    Stocks,
    Clipboard,
    Translate,
    Live,
    Messages,
    Spectrum,
    Updates,
    Controls,
}

public static class FeatureIds
{
    public static string Key(this FeatureId id) => id switch
    {
        FeatureId.Usage => "usage",
        FeatureId.Monitor => "monitor",
        FeatureId.Media => "media",
        FeatureId.Weather => "weather",
        FeatureId.Stocks => "stocks",
        FeatureId.Clipboard => "clipboard",
        FeatureId.Translate => "translate",
        FeatureId.Live => "live",
        FeatureId.Messages => "messages",
        FeatureId.Spectrum => "spectrum",
        FeatureId.Updates => "updates",
        FeatureId.Controls => "controls",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string DisplayName(this FeatureId id) => id switch
    {
        FeatureId.Usage => "AI 用量",
        FeatureId.Monitor => "系统监控",
        FeatureId.Media => "媒体控制",
        FeatureId.Weather => "天气",
        FeatureId.Stocks => "股票",
        FeatureId.Clipboard => "剪贴板",
        FeatureId.Translate => "翻译",
        FeatureId.Live => "实时字幕",
        FeatureId.Messages => "通知中心",
        FeatureId.Spectrum => "音频频谱",
        FeatureId.Updates => "应用更新",
        FeatureId.Controls => "音量与亮度",
        _ => id.ToString(),
    };

    public static bool TryParse(string key, out FeatureId id)
    {
        foreach (var value in Enum.GetValues<FeatureId>())
        {
            if (string.Equals(value.Key(), key, StringComparison.OrdinalIgnoreCase))
            {
                id = value;
                return true;
            }
        }

        id = default;
        return false;
    }
}
