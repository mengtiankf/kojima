using Kojima.Core.Geometry;
using Xunit;

namespace Kojima.Tests;

public sealed class IslandGeometryTests
{
    [Fact]
    public void ShoulderChangesTheNotchOutline()
    {
        var flat = IslandOutline.Build(0, 0, 238, 34, 0, 0, 0, 12, 12);
        var shouldered = IslandOutline.Build(0, 0, 238, 34, 16, 0, 0, 12, 12);

        Assert.NotEqual(flat, shouldered);
        Assert.Contains("C", shouldered);
        Assert.StartsWith("M0,0", shouldered);
        Assert.EndsWith("Z", shouldered);
    }

    [Fact]
    public void ShoulderIsClampedToTheAvailableWidth()
    {
        var huge = IslandOutline.Build(0, 0, 100, 34, 900, 0, 0, 12, 12);
        var clamped = IslandOutline.Build(0, 0, 100, 34, 17, 0, 0, 12, 12);

        Assert.Equal(clamped, huge);
    }

    [Fact]
    public void RoundedFallsBackToPlainCorners()
    {
        var rounded = IslandOutline.Rounded(0, 0, 56, 34, 17);
        Assert.StartsWith("M17,0", rounded);
        Assert.EndsWith("Z", rounded);
    }
}
