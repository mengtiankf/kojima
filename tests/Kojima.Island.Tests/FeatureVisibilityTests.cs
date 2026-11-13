using Kojima.Core.Models;
using Kojima.Island.Island;
using Kojima.Island.Settings;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class FeatureVisibilityTests : IslandTestBase
{
    [Fact]
    public void ConfigurableFeaturesMatchIslandCardsExactly()
    {
        Assert.Equal(
            FeatureIds.CardFeatures.Order(),
            SettingsWindow.ConfigurableFeatureKeys.Order());
        Assert.DoesNotContain("spectrum", SettingsWindow.ConfigurableFeatureKeys);
        Assert.DoesNotContain("updates", SettingsWindow.ConfigurableFeatureKeys);
        Assert.All(FeatureIds.CardFeatures, key => Assert.NotNull(FeatureIds.PageKey(key)));
        Assert.Equal("media", FeatureIds.PageKey("media"));
        Assert.Null(FeatureIds.PageKey("spectrum"));
        Assert.Null(FeatureIds.PageKey("updates"));
    }

    [Fact]
    public void MediaHasDedicatedPageWithoutRemovingHomeCard()
    {
        WithRuntime(config => config.Feature("media").Enabled = true, runtime =>
        {
            var island = new IslandWindow(runtime);
            try
            {
                island.Show();
                Assert.Contains("media", island.LoadedPageKeys);
                Assert.NotNull(island.HomeViewForTest);
                Assert.True(island.HomeViewForTest.MediaCardVisible);
            }
            finally
            {
                island.Close();
            }
        });
    }

    [Fact]
    public void ApplyingSettingsRemovesDisabledPagesAndHomeCards()
    {
        WithRuntime(config =>
        {
            foreach (var key in FeatureIds.CardFeatures)
            {
                config.Feature(key).Enabled = true;
            }
        }, runtime =>
        {
            var island = new IslandWindow(runtime);
            runtime.Island = island;
            island.Show();
            var settings = new SettingsWindow(runtime);
            try
            {
                Assert.Contains("stocks", island.LoadedPageKeys);
                Assert.Contains("clip", island.LoadedPageKeys);
                Assert.Contains("translate", island.LoadedPageKeys);
                Assert.Contains("msg", island.LoadedPageKeys);

                foreach (var key in new[] { "stocks", "clipboard", "translate", "messages", "media", "weather", "controls" })
                {
                    settings.SetFeatureForTest(key, false);
                }

                settings.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal(["home", "usage", "monitor"], island.LoadedPageKeys);
                var home = island.HomeViewForTest;
                Assert.NotNull(home);
                Assert.False(home.MediaCardVisible);
                Assert.False(home.WeatherCardVisible);
                Assert.False(home.ControlsCardVisible);
                Assert.DoesNotContain("stocks", island.LoadedPageKeys);
                Assert.DoesNotContain("clip", island.LoadedPageKeys);
                Assert.DoesNotContain("translate", island.LoadedPageKeys);
                Assert.DoesNotContain("msg", island.LoadedPageKeys);
                Assert.DoesNotContain("ctl", island.LoadedPageKeys);
                Assert.DoesNotContain("weather", island.LoadedPageKeys);
            }
            finally
            {
                settings.Close();
                island.Close();
            }
        });
    }
}
