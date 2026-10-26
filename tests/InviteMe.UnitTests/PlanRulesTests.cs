using InviteMe.Application.Features.Billing;

namespace InviteMe.UnitTests;

/// <summary>Plan limits: JSON defaults, the public link window and the cheapest plan to offer.</summary>
public sealed class PlanRulesTests
{
    private static readonly DateTimeOffset Wedding = new(2026, 12, 20, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingLimitsAreClosedAndNullMeansUnlimited()
    {
        var limits = PlanLimits.Parse("""{"guestPlaces":300,"customSlug":true,"reminderRounds":null}""");
        Assert.Equal((300, true, (int?)null), (limits.GuestPlaces, limits.CustomSlug, limits.ReminderRounds));
        Assert.Equal((0, false, true), (limits.SmsInvites, limits.Checkin, limits.BrandingBadge)); // not listed = locked
        Assert.Equal(new PlanLimits(), PlanLimits.Parse(null));
        Assert.Null(PlanLimits.Unlimited.GuestPlaces);
        Assert.Equal(limits, PlanLimits.Parse(limits.ToJson()));
    }

    [Fact]
    public void PublicLinkClosesTheGivenDaysAfterTheWedding()
    {
        var free = new PlanLimits(PublicLinkDaysAfterWedding: 7);
        Assert.Equal(Wedding.AddDays(7), PlanRules.PublicLinkUntil(free, Wedding));
        Assert.True(PlanRules.PublicLinkOpen(free, Wedding, Wedding.AddDays(7)));
        Assert.False(PlanRules.PublicLinkOpen(free, Wedding, Wedding.AddDays(7).AddSeconds(1)));
        Assert.True(PlanRules.PublicLinkOpen(free, null, Wedding.AddYears(5))); // no date yet
        Assert.True(PlanRules.PublicLinkOpen(PlanLimits.Unlimited, Wedding, Wedding.AddYears(5)));
    }

    [Fact]
    public void TheCheapestPlanThatAllowsTheRequestIsOffered()
    {
        PlanDto Plan(string code, decimal price, int? guests) => new(code, code, price, "VND", new(GuestPlaces: guests));
        var plans = new[] { Plan("PREMIUM", 699_000, 1000), Plan("FREE", 0, 50), Plan("BASIC", 299_000, 300) };
        Assert.Equal("BASIC", PlanRules.RequiredPlan(plans, x => x.GuestPlaces >= 51));
        Assert.Equal("PREMIUM", PlanRules.RequiredPlan(plans, x => x.GuestPlaces >= 301));
        Assert.Null(PlanRules.RequiredPlan(plans, x => x.GuestPlaces >= 5000));
        var error = PlanRules.Limit("guestPlaces", 50, 45, 6, "BASIC", "msg");
        Assert.Equal((PlanRules.LimitReached, (object?)"BASIC"), (error.Code, error.Extensions!["requiredPlan"]));
    }
}
