using Kojima.Core.Updates;
using Xunit;

namespace Kojima.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.8", "1.2.8")]
    [InlineData("1.2.8", "1.2.8")]
    [InlineData("v2.0", "2.0")]
    [InlineData("1.3.0-beta.2", "1.3.0")]
    public void NormalizesReleaseTags(string tag, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeVersion(tag));
    }

    [Fact]
    public void ParsesFourPartVersionSoEqualReleasesAreNotNewer()
    {
        Assert.True(UpdateService.TryParseVersion("1.2.7", out var parsed));
        Assert.Equal(new Version(1, 2, 7, 0), parsed);
        Assert.False(parsed > new Version(1, 2, 7, 0));
        Assert.True(UpdateService.TryParseVersion("1.2.8", out var newer));
        Assert.True(newer > new Version(1, 2, 7, 0));
    }

    [Fact]
    public void RejectsMalformedVersions()
    {
        Assert.False(UpdateService.TryParseVersion("nightly", out _));
        Assert.False(UpdateService.TryParseVersion("1", out _));
    }
}
