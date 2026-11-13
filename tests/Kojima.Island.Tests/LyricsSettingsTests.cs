using Kojima.Core.Config;
using Kojima.Island.Settings;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class LyricsSettingsTests : IslandTestBase
{
    [Fact]
    public void SourcesAndEnhancedModeRoundTripThroughSettings()
    {
        WithRuntime(config =>
        {
            config.Lyrics.Sources = ["qq"];
            config.Lyrics.EnhancedLocal = true;
        }, runtime =>
        {
            var window = new SettingsWindow(runtime);
            try
            {
                Assert.True(window.LyricsQq.IsChecked);
                Assert.False(window.LyricsLrclib.IsChecked);
                Assert.True(window.LyricsEnhancedSwitch.IsChecked);

                window.LyricsLrclib.IsChecked = true;
                window.LyricsNetease.IsChecked = true;
                window.LyricsQq.IsChecked = false;
                window.LyricsKugou.IsChecked = false;
                window.LyricsEnhancedSwitch.IsChecked = false;
                window.ApplyForTestAsync().GetAwaiter().GetResult();

                Assert.Equal(["lrclib", "netease"], runtime.Config.Lyrics.Sources);
                Assert.False(runtime.Config.Lyrics.EnhancedLocal);
                Assert.Equal(["lrclib", "netease"], ConfigStore.Load().Lyrics.Sources);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
