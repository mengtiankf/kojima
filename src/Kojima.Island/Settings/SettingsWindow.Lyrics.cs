using Kojima.Core.Config;

namespace Kojima.Island.Settings;

public partial class SettingsWindow
{
    private void LoadLyricsSettings(LyricsSettings settings)
    {
        LyricsEnabledSwitch.IsChecked = settings.Enabled;
        var sources = settings.Sources ?? [];
        LyricsLrclib.IsChecked = sources.Contains("lrclib");
        LyricsNetease.IsChecked = sources.Contains("netease");
        LyricsQq.IsChecked = sources.Contains("qq");
        LyricsKugou.IsChecked = sources.Contains("kugou");
        LyricsEnhancedSwitch.IsChecked = settings.EnhancedLocal;
    }

    private void ApplyLyricsSettings(LyricsSettings settings)
    {
        settings.Enabled = LyricsEnabledSwitch.IsChecked == true;
        settings.Sources = new List<string>();
        if (LyricsLrclib.IsChecked == true) settings.Sources.Add("lrclib");
        if (LyricsNetease.IsChecked == true) settings.Sources.Add("netease");
        if (LyricsQq.IsChecked == true) settings.Sources.Add("qq");
        if (LyricsKugou.IsChecked == true) settings.Sources.Add("kugou");
        settings.EnhancedLocal = LyricsEnhancedSwitch.IsChecked == true;
    }
}
