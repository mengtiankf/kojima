using Kojima.Core.Config;
using Xunit;

namespace Kojima.Tests;

public sealed class HomeLayoutTests
{
    [Fact]
    public void DefaultHomeKeepsExistingCardOrder()
    {
        Assert.Equal(["media", "weather", "clock", "actions"], HomeLayout.Resolve(new IslandSettings()));
    }

    [Fact]
    public void CustomHomeKeepsOrderAndRemovesInvalidItems()
    {
        var settings = new IslandSettings
        {
            HomeCards = ["clock", "media", "invalid", "clock"],
        };
        Assert.Equal(["clock", "media"], HomeLayout.Resolve(settings));
    }

    [Fact]
    public void EmptyHomeIsAllowed()
    {
        Assert.Empty(HomeLayout.Resolve(new IslandSettings { HomeCards = [] }));
    }
}
