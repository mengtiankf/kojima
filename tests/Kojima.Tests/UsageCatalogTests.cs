using Kojima.Core.Models;
using Xunit;

namespace Kojima.Tests;

public sealed class UsageCatalogTests
{
    [Fact]
    public void CatalogHasTheThirteenSourcesPromisedByTheReadme()
    {
        Assert.Equal(13, UsageCatalog.All.Count);
        Assert.Equal(13, UsageCatalog.Ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("grokbot", UsageCatalog.Ids);
        Assert.Contains("dsh", UsageCatalog.Ids);
        Assert.Contains("newapi", UsageCatalog.Ids);
        Assert.Contains("sub2api", UsageCatalog.Ids);
    }

    [Fact]
    public void EverySourceCanBeFoundByIdIgnoringCase()
    {
        foreach (var source in UsageCatalog.All)
        {
            Assert.Same(source, UsageCatalog.Find(source.Id.ToUpperInvariant()));
        }

        Assert.Null(UsageCatalog.Find("grokbot"));
    }

    [Fact]
    public void SitesAreOnlyNeededByRelaySources()
    {
        var needsSite = UsageCatalog.All.Where(s => s.NeedsSite).Select(s => s.Id).ToArray();
        Assert.Equal(["newapi", "sub2api"], needsSite);
        Assert.All(UsageCatalog.All.Where(s => s.NeedsSite), s => Assert.True(s.NeedsKey));
    }
}
