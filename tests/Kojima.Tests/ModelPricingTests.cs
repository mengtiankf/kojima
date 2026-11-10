using Kojima.Core.Models;
using Xunit;

namespace Kojima.Tests;

public sealed class ModelPricingTests
{
    [Fact]
    public void EstimatesKnownModelFromListPrice()
    {
        var cost = ModelPricing.Estimate("claude-sonnet-4-5", 1_000_000, 0, 0, 0);
        Assert.Equal(3.0, cost);
    }

    [Fact]
    public void UnknownModelReturnsNull()
    {
        Assert.Null(ModelPricing.Estimate("some-private-model", 1000, 0, 0, 1000));
    }

    [Fact]
    public void FillKeepsExistingCost()
    {
        var entries = new[]
        {
            new TokenEntry { Model = "claude-sonnet-4", Input = 1_000_000, Cost = 1.23 },
            new TokenEntry { Model = "unknown-model", Input = 1_000_000 },
        };

        ModelPricing.Fill(entries);

        Assert.Equal(1.23, entries[0].Cost);
        Assert.Null(entries[1].Cost);
    }
}
