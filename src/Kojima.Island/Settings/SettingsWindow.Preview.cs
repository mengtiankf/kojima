using Kojima.Core.Config;

namespace Kojima.Island.Settings;

public partial class SettingsWindow
{
    private void RefreshPreview()
    {
        if (UiPreview is null)
        {
            return;
        }

        var settings = new IslandSettings
        {
            Shape = ShapeCapsule.IsChecked == true ? IslandShape.Capsule
                : ShapeIndicator.IsChecked == true ? IslandShape.Indicator
                : IslandShape.Notch,
            Position = PosLeft.IsChecked == true ? IslandPosition.TopLeft
                : PosRight.IsChecked == true ? IslandPosition.TopRight
                : IslandPosition.TopCenter,
            Opacity = OpacitySlider.Value,
        };
        UiPreview.Update(
            settings,
            _compactOrder.Where(key => _compactEnabled.Contains(key)).ToArray(),
            _homeOrder.Where(key => _homeEnabled.Contains(key)).ToArray());
    }

    private void OnVisualSettingChanged(object sender, System.Windows.RoutedEventArgs e) => RefreshPreview();
}
