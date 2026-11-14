using System.Reflection;
using Kojima.Core.Config;
using Kojima.Island.Island;
using Kojima.Island.Settings;
using Kojima.Island.Views;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class SizingAndDensityTests : IslandTestBase
{
    [Fact]
    public void DensityOptionsChangeExpandedWidth()
    {
        WithRuntime(null, runtime =>
        {
            var island = new IslandWindow(runtime);
            try
            {
                island.Show();
                runtime.Config.Island.Density = "compact";
                var compact = island.CurrentExpandedWidth;
                runtime.Config.Island.Density = "loose";
                var loose = island.CurrentExpandedWidth;
                Assert.True(compact < loose);
            }
            finally
            {
                island.Close();
            }
        });
    }

    [Fact]
    public void CardSizesPersistAndReduceMediaContent()
    {
        WithRuntime(null, runtime =>
        {
            var editor = new HomeEditorView(runtime, () => { }, () => { });
            editor.SetSizeForTest("media", HomeCardSize.Small);
            editor.SetSizeForTest("weather", HomeCardSize.XLarge);

            Assert.Equal(HomeCardSize.Small, runtime.Config.Island.HomeCardSizes["media"]);
            Assert.Equal(HomeCardSize.XLarge, runtime.Config.Island.HomeCardSizes["weather"]);
            Assert.Equal(HomeCardSize.Small, ConfigStore.Load().Island.HomeCardSizes["media"]);

            var media = new MediaWidget { CardSize = HomeCardSize.Small };
            Assert.Equal(48, media.Height);
        });
    }

    [Fact]
    public void InlineEditorSupportsAllCardsAndSettingsHasNoHomeSection()
    {
        WithRuntime(null, runtime =>
        {
            var editor = new HomeEditorView(runtime, () => { }, () => { });
            editor.SetHomeItemsForTest(HomeLayout.Available);
            editor.DropHomeForTest("monitor", 0);

            Assert.Equal(HomeLayout.Available.Count, runtime.Config.Island.HomeCards!.Count);
            Assert.Equal("monitor", runtime.Config.Island.HomeCards[0]);
            Assert.Null(typeof(SettingsWindow).GetField("HomeItemsPanel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        });
    }
}
