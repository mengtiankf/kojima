using Kojima.Core.Config;
using Kojima.Core.Models;
using Kojima.Island.Island;
using Kojima.Island.Views;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class LayoutInteractionTests : IslandTestBase
{
    [Fact]
    public void CompactWidthGrowsAndShrinksWithContent()
    {
        WithRuntime(config =>
        {
            config.Island.Shape = IslandShape.Capsule;
            config.Island.CapsuleWidth = 196;
            config.Island.CompactItems = ["clock"];
        }, runtime =>
        {
            runtime.Stocks.Seed([new StockQuote("demo", "演示股票", 123.45, 6.78, 120.0)]);
            runtime.Usage.Seed([]);
            var island = new IslandWindow(runtime);
            try
            {
                island.Show();
                var narrow = island.CurrentCompactWidth;
                Assert.True(narrow >= 196);

                runtime.Config.Island.CompactItems = ["date", "clock", "weather", "stocks", "usage"];
                island.RefreshAppearance();
                var wide = island.CurrentCompactWidth;
                Assert.True(wide > narrow);

                runtime.Config.Island.CompactItems = ["clock"];
                island.RefreshAppearance();
                Assert.Equal(narrow, island.CurrentCompactWidth, 0.01);
            }
            finally
            {
                island.Close();
            }
        });
    }

    [Fact]
    public void DirectCardReorderPreservesHiddenFeatures()
    {
        WithRuntime(config =>
        {
            config.Island.HomeCards = ["media", "stocks", "weather", "clock"];
            config.Feature("stocks").Enabled = false;
        }, runtime =>
        {
            var home = new HomeView(runtime);
            home.ReorderCard("clock", "media", after: false);

            Assert.Equal(["clock", "media", "stocks", "weather"], runtime.Config.Island.HomeCards);
            Assert.Equal(["clock", "media", "weather"], home.CardOrder);
            Assert.Equal(["clock", "media", "stocks", "weather"], ConfigStore.Load().Island.HomeCards);
        });
    }
}
