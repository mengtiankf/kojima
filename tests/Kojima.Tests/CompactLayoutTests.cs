using Kojima.Core.Config;
using Xunit;

namespace Kojima.Tests;

public sealed class CompactLayoutTests
{
    [Fact]
    public void LegacyPresetsRemainCompatible()
    {
        Assert.Equal(["clock", "weather"], CompactLayout.Resolve(new IslandSettings { CompactContent = "clockWeather" }));
        Assert.Equal(["media"], CompactLayout.Resolve(new IslandSettings { CompactContent = "media" }));
        Assert.Empty(CompactLayout.Resolve(new IslandSettings { CompactContent = "none" }));
    }

    [Fact]
    public void CustomItemsKeepOrderAndRemoveInvalidDuplicates()
    {
        var settings = new IslandSettings
        {
            CompactItems = ["media", "clock", "invalid", "media", "date"],
        };

        Assert.Equal(["media", "clock", "date"], CompactLayout.Resolve(settings));
        Assert.Equal("custom", CompactLayout.LegacyContent(CompactLayout.Resolve(settings)));
    }

    [Fact]
    public void EmptyCustomSelectionDoesNotFallBackToLegacyPreset()
    {
        var settings = new IslandSettings
        {
            CompactContent = "clockWeather",
            CompactItems = [],
        };

        Assert.Empty(CompactLayout.Resolve(settings));
        Assert.Equal("none", CompactLayout.LegacyContent([]));
    }
}
