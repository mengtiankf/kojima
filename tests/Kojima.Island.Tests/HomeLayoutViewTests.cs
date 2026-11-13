using Kojima.Core.Config;
using Kojima.Island.Island;
using Kojima.Island.Settings;
using Kojima.Island.Views;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class HomeLayoutViewTests : IslandTestBase
{
    [Fact]
    public void DefaultHomeKeepsExistingLayout()
    {
        WithRuntime(null, runtime =>
        {
            var home = new HomeView(runtime);
            Assert.Equal(["media", "weather", "clock", "actions"], home.CardOrder);
        });
    }

    [Fact]
    public void HomeCardsCanBeReorderedHiddenAndPersisted()
    {
        WithRuntime(config => config.Island.HomeCards = ["clock", "media", "weather"], runtime =>
        {
            var island = new IslandWindow(runtime);
            runtime.Island = island;
            island.Show();
            var settings = new SettingsWindow(runtime);
            try
            {
                var initialHome = island.HomeViewForTest;
                Assert.NotNull(initialHome);
                Assert.Equal(["clock", "media", "weather"], initialHome.CardOrder);

                settings.SetHomeItemsForTest(["weather", "clock"]);
                settings.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal(["weather", "clock"], runtime.Config.Island.HomeCards);
                var updatedHome = island.HomeViewForTest;
                Assert.NotNull(updatedHome);
                Assert.Equal(["weather", "clock"], updatedHome.CardOrder);
                Assert.Equal(["weather", "clock"], ConfigStore.Load().Island.HomeCards);
            }
            finally
            {
                settings.Close();
                island.Close();
            }
        });
    }
}
