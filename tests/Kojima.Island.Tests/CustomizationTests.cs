using Kojima.Core.Config;
using Kojima.Island.Island;
using Kojima.Island.Settings;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class CustomizationTests : IslandTestBase
{
    [Fact]
    public void DropTargetsReorderAllThreeLists()
    {
        WithRuntime(config =>
        {
            config.Island.NavOrder = ["usage", "weather", "monitor"];
            config.Island.CompactItems = ["clock", "weather", "media"];
            config.Island.HomeCards = ["media", "weather", "clock"];
        }, runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                window.DropFeatureForTest("weather", 0);
                window.DropCompactForTest("media", 0);
                window.DropHomeForTest("clock", 0);
                window.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal("weather", runtime.Config.Island.NavOrder[0]);
                Assert.Equal(["media", "clock", "weather"], runtime.Config.Island.CompactItems);
                Assert.Equal(["clock", "media", "weather"], runtime.Config.Island.HomeCards);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StocksAndUsageCanAppearInHomeAndCompact()
    {
        WithRuntime(config =>
        {
            config.Island.HomeCards = ["stocks", "usage"];
            config.Island.CompactItems = ["stocks", "usage"];
        }, runtime =>
        {
            runtime.Usage.Seed([]);
            runtime.Stocks.Seed([]);
            var island = new IslandWindow(runtime);
            try
            {
                island.Show();
                var home = island.HomeViewForTest;
                Assert.NotNull(home);
                Assert.Equal(["stocks", "usage"], home.CardOrder);
                Assert.Equal(["stocks", "usage"], island.CompactItemKeys);
            }
            finally
            {
                island.Close();
            }
        });
    }

    [Fact]
    public void DemoQuotesAreNotOverwrittenByRefresh()
    {
        WithRuntime(null, runtime =>
        {
            var expected = new Kojima.Core.Models.StockQuote("demo", "演示股票", 123.45, 6.78, 120.0);
            runtime.Stocks.Seed([expected]);
            runtime.Stocks.RefreshAsync().GetAwaiter().GetResult();
            Assert.Equal(expected, Assert.Single(runtime.Stocks.Quotes));
        });
    }

    [Fact]
    public void MonitorStopsOnlyAfterLastSubscriberLeaves()
    {
        WithRuntime(null, runtime =>
        {
            runtime.Monitor.AddSubscriber();
            runtime.Monitor.AddSubscriber();
            runtime.Monitor.StopAsync().GetAwaiter().GetResult();
            Assert.True(runtime.Monitor.Streaming);
            runtime.Monitor.StopAsync().GetAwaiter().GetResult();
            Assert.False(runtime.Monitor.Streaming);
        });
    }

    [Fact]
    public void PreviewSupportsMoreThanFourCards()
    {
        WithRuntime(null, runtime =>
        {
            var preview = new SettingsPreview();
            preview.Update(new IslandSettings(), CompactLayout.Available, HomeLayout.Available);

            Assert.NotNull(preview.Content);
            Assert.Contains("stocks", HomeLayout.Available);
            Assert.Contains("usage", HomeLayout.Available);
            Assert.Contains("monitor", HomeLayout.Available);
            Assert.Equal(["media", "weather", "clock", "actions"], HomeLayout.Default);
        });
    }
}
