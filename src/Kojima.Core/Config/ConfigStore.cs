using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kojima.Core.Config;

/// <summary>配置读写：%LOCALAPPDATA%\Kojima\config.json，写入采用临时文件 + 替换，避免半截文件。</summary>
public static class ConfigStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kojima");

    public static string ConfigPath => Path.Combine(RootDirectory, "config.json");

    public static string ThemesDirectory => Path.Combine(RootDirectory, "themes");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public static KojimaConfig Load()
    {
        EnsureDirectories();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<KojimaConfig>(json, Options);
                if (config is not null)
                {
                    return Normalize(config);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("config load failed: " + ex.Message);
            TryBackupBrokenConfig();
        }

        var fresh = new KojimaConfig();
        Save(fresh);
        return fresh;
    }

    public static void Save(KojimaConfig config)
    {
        EnsureDirectories();
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private static KojimaConfig Normalize(KojimaConfig config)
    {
        config.Island.Height = Math.Clamp(config.Island.Height, 20, 80);
        config.Island.MarginX = Math.Clamp(config.Island.MarginX, 0, 600);
        config.Island.MarginTop = Math.Clamp(config.Island.MarginTop, 0, 60);
        config.Island.Shoulder = Math.Clamp(config.Island.Shoulder, 0, 40);
        config.Island.Corner = Math.Clamp(config.Island.Corner, 0, 40);
        config.Island.PanelWidth = Math.Clamp(config.Island.PanelWidth, 420, 1200);
        config.Island.Opacity = Math.Clamp(config.Island.Opacity, 0.3, 1.0);
        config.Monitor.IntervalMs = Math.Clamp(config.Monitor.IntervalMs, 500, 10000);
        // 兼容旧配置：只允许三种位置
        if (!Enum.IsDefined(config.Island.Position))
        {
            config.Island.Position = IslandPosition.TopCenter;
        }

        return config;
    }

    private static void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            var backup = Path.Combine(RootDirectory, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(ConfigPath, backup, overwrite: true);
        }
        catch
        {
            // 备份失败不影响启动
        }
    }
}
