using InviteMe.Application.Features.Billing;
using InviteMe.Domain.Billing;

namespace InviteMe.UnitTests;

/// <summary>Plan orders: transfer code, partial payments, expiry display and the billing account check.</summary>
public sealed class PlanOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderCodesAreFoundInBankContentAndDifferFromGiftCodes()
    {
        Assert.All(Enumerable.Range(0, 50).Select(_ => PlanOrderRules.NewCode()), c => Assert.Matches("^IM[A-HJ-NP-Z2-9]{8}$", c));
        Assert.Equal("IM7K2Q9PXA", PlanOrderRules.FindCode(null, "chuyen tien im7k2q9pxa nang cap"));
        Assert.Equal("IM7K2Q9PXA", PlanOrderRules.FindCode("IM7K2Q9PXA", null));
        Assert.Null(PlanOrderRules.FindCode(null, "MC7K2Q9PXA mung cuoi"));
        Assert.Null(PlanOrderRules.FindCode(null, "XIM7K2Q9PXA"));
    }

    [Fact]
    public void PaymentsAddUpUntilTheOrderIsPaid()
    {
        var order = PlanOrder.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "IMAAAAAAAA", 299_000, Now);
        Assert.False(order.AddPayment(100_000, Now));
        Assert.Equal((PlanOrder.Underpaid, 100_000L), (order.Status, order.PaidAmount));
        Assert.True(order.AddPayment(250_000, Now)); // overpaid still activates
        Assert.Equal((PlanOrder.Paid, 350_000L, (DateTimeOffset?)Now), (order.Status, order.PaidAmount, order.PaidAt));
        Assert.Throws<InvalidOperationException>(() => order.AddPayment(1, Now));
        Assert.Throws<InvalidOperationException>(() => order.Cancel(null, null, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanOrder.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "IMAAAAAAAA", 0, Now));
    }

    [Fact]
    public void PendingOrdersReadExpiredAfterADayButStayPayable()
    {
        var order = PlanOrder.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "IMAAAAAAAA", 299_000, Now);
        Assert.Equal(PlanOrder.Pending, order.DisplayStatus(Now.AddHours(23)));
        Assert.Equal(PlanOrder.Expired, order.DisplayStatus(Now.AddHours(25)));
        Assert.True(order.AddPayment(299_000, Now.AddDays(2)));
        var cancelled = PlanOrder.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "IMBBBBBBBB", 299_000, Now);
        cancelled.Cancel(Guid.NewGuid(), "x", Now);
        cancelled.Approve(Guid.NewGuid(), "paid late", Now); // admin can still activate a cancelled order
        Assert.Equal(PlanOrder.Paid, cancelled.Status);
    }

    [Fact]
    public void BillingAccountNeedsAKnownBankAndMatchesItsOwnNumber()
    {
        Assert.False(new BillingAccountOptions(null, null, null).Configured);
        Assert.False(new BillingAccountOptions("123456", "0000000002", "InviteMe").Configured);
        var account = new BillingAccountOptions("970422", " 0000000002 ", "InviteMe");
        Assert.True(account.Configured);
        Assert.True(account.Owns("0000000002"));
        Assert.False(account.Owns("0000000001"));
        Assert.False(account.Owns(null));
    }
}
