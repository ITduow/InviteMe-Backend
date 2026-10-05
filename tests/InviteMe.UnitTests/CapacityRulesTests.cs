using InviteMe.Domain.Rsvps;

namespace InviteMe.UnitTests;

public sealed class CapacityRulesTests
{
    [Theory]
    [InlineData(null, 0, 1, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(3, 2, 1, true)]
    [InlineData(3, 2, 2, false)]
    [InlineData(3, 3, 0, true)]
    [InlineData(3, -1, 1, false)]
    [InlineData(3, 0, -1, false)]
    [InlineData(3, long.MaxValue, long.MaxValue, false)]
    public void WholePartyMustFitConfiguredCapacity(int? capacity, long occupied, long additional, bool fits) =>
        Assert.Equal(fits, CapacityRules.Fits(capacity, occupied, additional));
}
