using Kojima.Core.Config;
using Kojima.Core.Themes;
using Xunit;

namespace Kojima.Tests;

public sealed class ThemeDerivationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kojima-tests", Guid.NewGuid().ToString("N"));

    public ThemeDerivationTests()
    {
        Directory.CreateDirectory(_root);
        ConfigStore.UseRoot(_root);
        ThemeCatalog.Reload();
    }

    public void Dispose()
    {
        ConfigStore.UseRoot(null);
        ThemeCatalog.Reload();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [Fact]
    public void DarkSeedsDeriveDarkPalette()
    {
        var doc = ThemeDerivation.FromSeeds("测试", "#101014", "#F5F5F7", "#0A84FF");
        Assert.Equal("dark", doc.Colors["mode"]);
        Assert.StartsWith("deck-", doc.Id);

        var path = ThemeDerivation.Save(doc);
        Assert.True(File.Exists(path));
        ThemeCatalog.Reload();

        var palette = ThemeCatalog.Get(doc.Id);
        Assert.Equal(doc.Id, palette.Id);
        Assert.True(palette.Dark);
        Assert.Equal(10, palette.Accent.R);
        Assert.Equal(132, palette.Accent.G);
    }

    [Fact]
    public void LightSeedsDeriveLightPaletteAndWhiteOnAccent()
    {
        var doc = ThemeDerivation.FromSeeds("light-test", "#FAFAFA", "#1C1C1E", "#FFD60A");
        Assert.Equal("light", doc.Colors["mode"]);
        Assert.Equal("#FF000000", doc.Colors["onAccent"]);
        Assert.Equal("deck-custom-light-test", doc.Id);
    }

    [Fact]
    public void InvalidColorThrows()
    {
        Assert.Throws<ArgumentException>(() => ThemeDerivation.FromSeeds("x", "not-a-color", "#FFFFFF", "#000000"));
    }
}
