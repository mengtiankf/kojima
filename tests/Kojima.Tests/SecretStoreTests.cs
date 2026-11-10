using System.Text.Json;
using Kojima.Core.Config;
using Xunit;

namespace Kojima.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kojima-tests", Guid.NewGuid().ToString("N"));

    public SecretStoreTests()
    {
        Directory.CreateDirectory(_root);
        ConfigStore.UseRoot(_root);
        SecretStore.Invalidate();
    }

    public void Dispose()
    {
        ConfigStore.UseRoot(null);
        SecretStore.Invalidate();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响测试结论
        }
    }

    [Fact]
    public void StoresSecretsEncryptedAndReadsThemBack()
    {
        SecretStore.Set("usage.deepseek", "sk-very-secret");

        Assert.True(File.Exists(SecretStore.FilePath));
        var raw = File.ReadAllText(SecretStore.FilePath);
        Assert.StartsWith("KJ1:", raw);
        Assert.DoesNotContain("sk-very-secret", raw);

        SecretStore.Invalidate();
        Assert.Equal("sk-very-secret", SecretStore.Get("usage.deepseek"));
        Assert.True(SecretStore.Has("usage.deepseek"));
    }

    [Fact]
    public void EmptyValueRemovesTheSecret()
    {
        SecretStore.Set("usage.kimi", "kimi-key");
        SecretStore.Set("usage.kimi", "");

        Assert.False(SecretStore.Has("usage.kimi"));
        Assert.DoesNotContain("kimi-key", File.ReadAllText(SecretStore.FilePath));
    }

    [Fact]
    public void LegacyPlaintextSecretsFileIsEncryptedInPlace()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["usage.claude"] = "claude-plain",
        });
        File.WriteAllText(SecretStore.FilePath, json);

        SecretStore.Invalidate();
        Assert.Equal("claude-plain", SecretStore.Get("usage.claude"));
        Assert.StartsWith("KJ1:", File.ReadAllText(SecretStore.FilePath));
    }

    [Fact]
    public void CorruptFileIsBackedUpAndTreatedAsEmpty()
    {
        File.WriteAllText(SecretStore.FilePath, "not-a-secret-file");

        SecretStore.Invalidate();
        Assert.Equal("", SecretStore.Get("usage.deepseek"));
        Assert.NotEmpty(Directory.GetFiles(_root, "secrets.broken-*.dat"));
    }
}

public sealed class SecretMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kojima-tests", Guid.NewGuid().ToString("N"));

    public SecretMigrationTests()
    {
        Directory.CreateDirectory(_root);
        ConfigStore.UseRoot(_root);
        SecretStore.Invalidate();
    }

    public void Dispose()
    {
        ConfigStore.UseRoot(null);
        SecretStore.Invalidate();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void LegacyPlaintextKeysMoveToSecretStoreAndLeaveTheConfig()
    {
        var legacy = """
        {
          "version": 1,
          "island": { "theme": "dracula" },
          "usage": {
            "sources": [ "deepseek", "kimi" ],
            "keys": { "deepseek": "sk-deepseek-plain", "glm": "" }
          },
          "translate": {
            "engine": "baidu",
            "baiduAppId": "app-id",
            "baiduKey": "baidu-plain",
            "microsoftKey": "microsoft-plain",
            "customKey": "custom-plain"
          }
        }
        """;
        File.WriteAllText(ConfigStore.ConfigPath, legacy);

        var config = ConfigStore.Load();

        Assert.Equal("deck-dracula", config.Island.Theme);
        Assert.Equal("sk-deepseek-plain", SecretStore.Get(SecretNames.DeepSeek));
        Assert.Equal("baidu-plain", SecretStore.Get(SecretNames.TranslateBaiduKey));
        Assert.Equal("microsoft-plain", SecretStore.Get(SecretNames.TranslateMicrosoftKey));
        Assert.Equal("custom-plain", SecretStore.Get(SecretNames.TranslateCustomKey));

        var saved = File.ReadAllText(ConfigStore.ConfigPath);
        Assert.DoesNotContain("sk-deepseek-plain", saved);
        Assert.DoesNotContain("baidu-plain", saved);
        Assert.DoesNotContain("microsoft-plain", saved);
        Assert.DoesNotContain("custom-plain", saved);
        Assert.DoesNotContain("\"keys\"", saved);
        Assert.DoesNotContain("\"baiduKey\"", saved);
        Assert.Equal("app-id", config.Translate.BaiduAppId);
    }

    [Fact]
    public void CleanConfigIsNotRewritten()
    {
        var fresh = new KojimaConfig();
        ConfigStore.Save(fresh);
        var before = File.ReadAllText(ConfigStore.ConfigPath);

        ConfigStore.Load();

        Assert.Equal(before, File.ReadAllText(ConfigStore.ConfigPath));
    }

    [Fact]
    public void DefaultThemeExistsInCatalog()
    {
        Assert.Equal("deck-dark", new KojimaConfig().Island.Theme);
        Assert.Equal("deck-dark", Kojima.Core.Themes.ThemeCatalog.Get("no-such-theme").Id);
    }

    [Fact]
    public void Version1ConfigMigratesThemeIdAndKeepsBackup()
    {
        File.WriteAllText(ConfigStore.ConfigPath,
            """{"version":1,"island":{"theme":"dark","position":"topCenter"}}""");

        var cfg = ConfigStore.Load();

        Assert.Equal("deck-dark", cfg.Island.Theme);
        Assert.Equal(SettingsMigrations.CurrentVersion, cfg.Version);
        Assert.True(File.Exists(Path.Combine(_root, "config.v1.bak")));
        Assert.Contains("\"deck-dark\"", File.ReadAllText(ConfigStore.ConfigPath));
    }

    [Fact]
    public void NewerConfigVersionIsLeftAlone()
    {
        File.WriteAllText(ConfigStore.ConfigPath,
            """{"version":99,"island":{"theme":"custom-theme"}}""");

        var cfg = ConfigStore.Load();

        Assert.Equal("custom-theme", cfg.Island.Theme);
    }
}
