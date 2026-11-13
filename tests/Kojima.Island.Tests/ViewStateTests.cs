using System.Windows;
using Kojima.Core.Config;
using Kojima.Island.Views;
using Xunit;

namespace Kojima.Island.Tests;

public sealed class ViewStateTests : IslandTestBase
{
    [Fact]
    public void TranslateDirectionRoundTripsThroughConfig()
    {
        WithRuntime(config =>
        {
            config.Translate.Target = "en";
            config.Translate.Reversed = true;
        }, runtime =>
        {
            var view = new TranslateView(runtime);
            var host = new Window { Content = view, Width = 640, Height = 340, ShowInTaskbar = false };
            try
            {
                host.Show();
                Assert.Equal("英", view.Target);
                Assert.True(view.Reversed);

                view.SelectLanguage(0);
                view.ToggleDirection();

                Assert.Equal("ja", runtime.Config.Translate.Target);
                Assert.False(runtime.Config.Translate.Reversed);
                var saved = ConfigStore.Load();
                Assert.Equal("ja", saved.Translate.Target);
                Assert.False(saved.Translate.Reversed);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void CustomMusicProgramIsUsedWhenItExists()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "");
        try
        {
            Assert.Equal(path, ControlsView.ResolveMusicTarget(path));
            Assert.Equal("ms-music:", ControlsView.ResolveMusicTarget(path + ".missing"));
            Assert.Equal("ms-music:", ControlsView.ResolveMusicTarget(""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
