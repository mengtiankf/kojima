namespace Kojima.Core.Config;

public enum IslandShape
{
    Notch,
    Capsule,
    Indicator,
}

public enum IslandPosition
{
    TopCenter,
    TopLeft,
    TopRight,
}

public sealed class IslandSettings
{
    public IslandShape Shape { get; set; } = IslandShape.Notch;
    public IslandPosition Position { get; set; } = IslandPosition.TopCenter;

    /// <summary>上靠左 / 上靠右时与屏幕左右边缘的间距。</summary>
    public double MarginX { get; set; } = 24;

    /// <summary>与屏幕上边缘的间距（0 = 完全贴边）。</summary>
    public double MarginTop { get; set; }

    public double Height { get; set; } = 34;
    public double NotchWidth { get; set; } = 238;
    public double CapsuleWidth { get; set; } = 196;
    public double IndicatorWidth { get; set; } = 56;
    public double IndicatorThickness { get; set; } = 4;

    /// <summary>缺口两侧肩线宽度。</summary>
    public double Shoulder { get; set; } = 16;

    /// <summary>缺口下缘圆角。</summary>
    public double Corner { get; set; } = 12;

    /// <summary>展开面板的圆角。</summary>
    public double PanelRadius { get; set; } = 20;

    public double PanelWidth { get; set; } = 640;

    public string Theme { get; set; } = "deck-dark";

    public bool HideFullscreen { get; set; } = true;

    /// <summary>展开方式：hover / always / never。</summary>
    public string ExpandMode { get; set; } = "hover";

    public int HoverExpandMs { get; set; } = 120;

    public int CollapseDelayMs { get; set; } = 320;

    /// <summary>收起时显示：clock / pet。</summary>
    public string CompactView { get; set; } = "clock";

    public bool ShowWeather { get; set; } = true;

    public double Opacity { get; set; } = 1.0;
}

public sealed class HotkeySettings
{
    public string Toggle { get; set; } = "Ctrl+Alt+K";
    public string Settings { get; set; } = "Ctrl+Alt+S";
    public string Monitor { get; set; } = "Ctrl+Alt+M";
}

public sealed class FeatureSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>子进程空闲多久后退出（秒）。0 = 立即退出。</summary>
    public int IdleExitSeconds { get; set; } = 90;
}

public sealed class UsageSettings
{
    public List<string> Sources { get; set; } =
    [
        "claude", "codex", "cursor", "antigravity", "dsh", "kimi", "opencode", "zcode",
        "deepseek", "openrouter", "glm", "newapi", "sub2api",
    ];

    public string Period { get; set; } = "today";

    public int RefreshSeconds { get; set; } = 300;

    public string Home { get; set; } = "";

    public Dictionary<string, string> Keys { get; set; } = new()
    {
        ["claude"] = "",
        ["cursor"] = "",
        ["zcode"] = "",
        ["kimi"] = "",
        ["deepseek"] = "",
        ["openrouter"] = "",
        ["glm"] = "",
        ["glm-global"] = "",
        ["newapi"] = "",
        ["sub2api"] = "",
    };

    public Dictionary<string, string> Sites { get; set; } = new()
    {
        ["newapi"] = "",
        ["sub2api"] = "",
    };
}

public sealed class MonitorSettings
{
    public int IntervalMs { get; set; } = 1500;
}

public sealed class KojimaConfig
{
    public int Version { get; set; } = 1;

    public bool StartWithWindows { get; set; }

    public string Language { get; set; } = "zh-CN";

    public IslandSettings Island { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    public UsageSettings Usage { get; set; } = new();

    public MonitorSettings Monitor { get; set; } = new();

    public Dictionary<string, FeatureSettings> Features { get; set; } = new()
    {
        ["usage"] = new FeatureSettings(),
        ["monitor"] = new FeatureSettings(),
        ["media"] = new FeatureSettings(),
        ["weather"] = new FeatureSettings(),
        ["stocks"] = new FeatureSettings(),
        ["clipboard"] = new FeatureSettings { Enabled = false },
        ["translate"] = new FeatureSettings { Enabled = false },
        ["live"] = new FeatureSettings { Enabled = false },
    };

    public FeatureSettings Feature(string key)
    {
        if (!Features.TryGetValue(key, out var settings))
        {
            settings = new FeatureSettings();
            Features[key] = settings;
        }

        return settings;
    }
}
