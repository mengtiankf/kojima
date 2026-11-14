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

public enum HomeCardSize
{
    Small,
    Medium,
    Large,
    XLarge,
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

    public string Density { get; set; } = "medium";

    public Dictionary<string, HomeCardSize> HomeCardSizes { get; set; } = new();

    /// <summary>岛体所在显示器（设备名，空 = 主显示器）。</summary>
    public string Monitor { get; set; } = "";

    /// <summary>除主岛外，还要在哪些显示器顶部各放一个岛（设备名列表）。</summary>
    public List<string> ExtraMonitors { get; set; } = [];

    /// <summary>拖拽后的位置（相对显示器工作区左上角的 DIP 偏移，空 = 按 Position 吸附）。</summary>
    public double? DragX { get; set; }
    public double? DragY { get; set; }

    public string Theme { get; set; } = "deck-dark";

    public bool HideFullscreen { get; set; } = true;

    /// <summary>展开方式：hover / always / never。</summary>
    public string ExpandMode { get; set; } = "hover";

    public int HoverExpandMs { get; set; } = 120;

    public int CollapseDelayMs { get; set; } = 320;

    /// <summary>展开 / 收起动画时长（毫秒）。</summary>
    public int AnimationMs { get; set; } = 260;

    /// <summary>导航与功能列表的顺序（页面 key）；为空表示使用内置顺序。</summary>
    public List<string> NavOrder { get; set; } = new();

    /// <summary>收起时显示：clock / pet。</summary>
    public string CompactView { get; set; } = "clock";

    /// <summary>旧版收起态预设：clock / weather / clockWeather / media / none / custom。</summary>
    public string CompactContent { get; set; } = "clockWeather";

    /// <summary>自定义收起态内容及顺序；null 表示读取旧版 CompactContent，空列表表示全部隐藏。</summary>
    public List<string>? CompactItems { get; set; }

    /// <summary>主页卡片及排列顺序；null 使用默认四卡布局，空列表表示全部隐藏。</summary>
    public List<string>? HomeCards { get; set; }

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

    public Dictionary<string, string> Sites { get; set; } = new()
    {
        ["newapi"] = "",
        ["sub2api"] = "",
    };

    /// <summary>每个来源可以分别关掉“本机记录”或“账户额度”，默认都开。</summary>
    public Dictionary<string, UsageToggle> Toggles { get; set; } = new();
}

public sealed class UsageToggle
{
    public bool Local { get; set; } = true;
    public bool Quota { get; set; } = true;

    /// <summary>用量数据取自哪个来源；留空表示本列自身。</summary>
    public string UsageFrom { get; set; } = "";

    /// <summary>额度数据取自哪个来源；留空表示本列自身。</summary>
    public string QuotaFrom { get; set; } = "";
}

public sealed class MonitorSettings
{
    public int IntervalMs { get; set; } = 1500;

    /// <summary>温度/风扇/功耗数据源：auto / internal / lhm / ohm。</summary>
    public string Source { get; set; } = "auto";

    /// <summary>检测到的外部监控软件（只读，由子进程回填）。</summary>
    public List<string> DetectedSources { get; set; } = [];
}

public sealed class StockSettings
{
    /// <summary>自选股代码（腾讯行情格式，如 sh600519、hk00700）。</summary>
    public List<string> Symbols { get; set; } =
    [
        "sh000001", "sz399001", "sh600519", "sz300750", "sz002594", "hk00700",
    ];

    /// <summary>行情源：auto / tencent / sina / eastmoney。</summary>
    public string Source { get; set; } = "auto";

    /// <summary>刷新间隔（秒）。</summary>
    public int RefreshSeconds { get; set; } = 15;
}

public sealed class WeatherSettings
{
    /// <summary>城市名（如“北京”“Tokyo”），留空则按 IP 自动定位。</summary>
    public string Location { get; set; } = "";

    /// <summary>刷新间隔（分钟）。</summary>
    public int RefreshMinutes { get; set; } = 20;
}

public sealed class AppSettings
{
    /// <summary>控制页“音乐”按钮要打开的程序；留空则打开系统音乐应用。</summary>
    public string Music { get; set; } = "";
}

public sealed class TranslateSettings
{
    /// <summary>翻译引擎：auto / free / deepseek / baidu / microsoft / custom。</summary>
    public string Engine { get; set; } = "auto";

    /// <summary>百度翻译 AppID。</summary>
    public string BaiduAppId { get; set; } = "";

    /// <summary>微软翻译区域。</summary>
    public string MicrosoftRegion { get; set; } = "";

    /// <summary>自定义大模型接口地址（OpenAI 兼容 /chat/completions）。</summary>
    public string CustomEndpoint { get; set; } = "";

    /// <summary>自定义大模型名称。</summary>
    public string CustomModel { get; set; } = "";

    /// <summary>目标语言：ja / en。</summary>
    public string Target { get; set; } = "ja";

    /// <summary>是否反向（目标语言 → 中文）。</summary>
    public bool Reversed { get; set; }
}

public sealed class LyricsSettings
{
    public bool Enabled { get; set; } = true;

    public List<string> Sources { get; set; } = ["lrclib", "netease", "qq", "kugou"];

    /// <summary>只读本地播放器日志或缓存，不修改播放器安装文件。</summary>
    public bool EnhancedLocal { get; set; }
}

public sealed class AppearanceSettings
{
    /// <summary>manual / system / daylight</summary>
    public string Mode { get; set; } = "manual";

    public string LightTheme { get; set; } = "deck-light";

    public string DarkTheme { get; set; } = "deck-dark";

    public bool FollowHighContrast { get; set; } = true;

    public int DayStartHour { get; set; } = 7;

    public int DayEndHour { get; set; } = 19;
}

public sealed class KojimaConfig
{    public int Version { get; set; } = SettingsMigrations.CurrentVersion;

    public bool StartWithWindows { get; set; }

    public string Language { get; set; } = "zh-CN";

    public AppearanceSettings Appearance { get; set; } = new();

    public IslandSettings Island { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    public UsageSettings Usage { get; set; } = new();

    public MonitorSettings Monitor { get; set; } = new();

    public WeatherSettings Weather { get; set; } = new();

    public AppSettings Apps { get; set; } = new();

    public StockSettings Stocks { get; set; } = new();

    public TranslateSettings Translate { get; set; } = new();

    public LyricsSettings Lyrics { get; set; } = new();

    /// <summary>消息中心（QQ / 微信 / 邮件通知）。</summary>
    public MessageSettings Messages { get; set; } = new();

    public UpdateSettings Update { get; set; } = new();

    public Dictionary<string, FeatureSettings> Features { get; set; } = new()
    {
        ["usage"] = new FeatureSettings(),
        ["monitor"] = new FeatureSettings(),
        ["media"] = new FeatureSettings(),
        ["spectrum"] = new FeatureSettings(),
        ["updates"] = new FeatureSettings(),
        ["weather"] = new FeatureSettings(),
        ["stocks"] = new FeatureSettings(),
        ["clipboard"] = new FeatureSettings { Enabled = false },
        ["translate"] = new FeatureSettings { Enabled = false },
        ["messages"] = new FeatureSettings(),
        ["controls"] = new FeatureSettings(),
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

/// <summary>消息中心设置。</summary>
public sealed class MessageSettings
{
    /// <summary>toast 数据库轮询间隔（毫秒）。</summary>
    public int PollMs { get; set; } = 1500;

    /// <summary>点消息时打开的应用路径（qq / wechat / mail），留空则按进程名激活。</summary>
    public Dictionary<string, string> Open { get; set; } = new()
    {
        ["qq"] = "",
        ["wechat"] = "",
        ["mail"] = "",
    };
}

/// <summary>自动更新设置。</summary>
public sealed class UpdateSettings
{
    /// <summary>启动后静默检查一次（每天最多一次）。</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>上次检查日期（yyyy-MM-dd），用于自动检查去重。</summary>
    public string LastCheck { get; set; } = "";
}
