using Kojima.Core.Config;
using Kojima.Island.Island;
using Kojima.Island.Settings;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class CompactLayoutViewTests : IslandTestBase
{
    [Fact]
    public void CustomCompactOrderIsRenderedAndPersisted()
    {
        WithRuntime(config =>
        {
            config.Island.CompactItems = ["media", "clock", "date", "weather"];
            config.Feature("media").Enabled = true;
            config.Feature("weather").Enabled = true;
        }, runtime =>
        {
            var island = new IslandWindow(runtime);
            runtime.Island = island;
            island.Show();
            var settings = new SettingsWindow(runtime);
            try
            {
                Assert.Equal(["media", "clock", "date", "weather"], island.CompactItemKeys);

                settings.SetCompactItemsForTest(["date", "media", "clock"]);
                settings.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal(["date", "media", "clock"], runtime.Config.Island.CompactItems);
                Assert.Equal(["date", "media", "clock"], island.CompactItemKeys);
                Assert.Equal(["date", "media", "clock"], ConfigStore.Load().Island.CompactItems);
            }
            finally
            {
                settings.Close();
                island.Close();
            }
        });
    }
}
