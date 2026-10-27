using InviteMe.Application.Features.Gifts;
using InviteMe.Domain.Gifts;

namespace InviteMe.UnitTests;

/// <summary>Guest08 / Couple08 rules: transfer code, VietQR link, amount limits, statuses and gift book totals.</summary>
public sealed class GiftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TransferCodesAreUniqueLookingAndFoundInBankContent()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => GiftRules.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^MC[A-HJ-NP-Z2-9]{8}$", c));
        Assert.Equal(200, codes.Distinct().Count());
        Assert.Equal("MC7K2Q9PXA", GiftRules.FindCode(" mc7k2q9pxa ", null)); // SePay recognised it
        Assert.Equal("MC7K2Q9PXA", GiftRules.FindCode(null, "chuc mung MC7K2Q9PXA hanh phuc")); // template missing on SePay
        Assert.Null(GiftRules.FindCode(null, "chuc mung hanh phuc"));
        Assert.Null(GiftRules.FindCode("", "XMC7K2Q9PXA")); // part of a longer word
    }

    [Fact]
    public void PresentsAreReceivedWhenRecordedAndCarryNoAmount()
    {
        var present = PlanningGift.ForItem(Guid.NewGuid(), null, "Cô Lan", "Bộ chăn ga", 2, 800_000, null, Guid.NewGuid(), Now, Now);
        Assert.Equal((PlanningGift.Paid, PlanningGift.Manual, (long?)null, false), (present.Status, present.Confirmation, present.AmountReceived, present.AmountMismatch));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningGift.ForItem(Guid.NewGuid(), null, "A", " ", 1, null, null, Guid.NewGuid(), Now, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningGift.ForItem(Guid.NewGuid(), null, "A", "Ly", 0, null, null, Guid.NewGuid(), Now, Now));
        Assert.Throws<InvalidOperationException>(() => present.Cancel(Guid.NewGuid()));

        var validator = new RecordGiftValidator();
        Assert.True(validator.Validate(new RecordGift(PlanningGift.Item, GiverName: "A", ItemName: "Tranh")).IsValid);
        Assert.False(validator.Validate(new RecordGift(PlanningGift.Item, 100_000, GiverName: "A", ItemName: "Tranh")).IsValid);
        Assert.True(validator.Validate(new RecordGift(PlanningGift.Cash, GiverName: "A")).IsValid); // sealed envelope: counted later
        Assert.False(validator.Validate(new RecordGift(PlanningGift.Transfer, GiverName: "A")).IsValid); // a transfer has an amount
        Assert.False(validator.Validate(new RecordGift(PlanningGift.Cash, GiverName: "A", ParticipantId: Guid.NewGuid())).IsValid); // person needs their invitation
        var summary = GiftBook.Summarize([new(Guid.NewGuid(), null, "A", PlanningGift.Item, PlanningGift.Paid, PlanningGift.Manual, null, null, null,
            false, null, false, null, Now, null, Now, "Tranh", 3, 2_000_000)], 0, 0);
        Assert.Equal((0L, 1, 3), (summary.TotalReceived, summary.ItemCount, summary.ItemQuantity));
    }

    [Fact]
    public void VietQrLinkCarriesAmountCodeAndHolderWithoutDiacritics()
    {
        var url = GiftRules.QrImageUrl("970422", "0000000001", "Nguyễn Tuấn  Kiệt", 500_000, "MC7K2Q9PXA");
        Assert.Equal("https://img.vietqr.io/image/970422-0000000001-compact2.png?amount=500000&addInfo=MC7K2Q9PXA&accountName=NGUYEN%20TUAN%20KIET", url);
        Assert.Equal("DANG THI DUNG", GiftRules.Ascii("Đặng thị Dung"));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 5, 24, 6, TimeSpan.Zero), GiftRules.ParseSePayDate("2026-10-22 12:24:06", Now));
        Assert.Equal(Now, GiftRules.ParseSePayDate("bad", Now));
        Assert.Equal("MBBank", GiftBanks.Find("970422")!.Name);
        Assert.Null(GiftBanks.Find("123456"));
    }

    [Fact]
    public void QrGiftLifecycleAndLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewGift(9_999));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewGift(100_000_001));
        var gift = NewGift(500_000);
        Assert.Equal((PlanningGift.Pending, PlanningGift.Pending), (gift.Status, gift.DisplayStatus(Now)));
        Assert.Equal(PlanningGift.Expired, gift.DisplayStatus(Now.AddHours(24)));
        Assert.True(gift.Claim(Now)); Assert.False(gift.Claim(Now));
        gift.MarkPaid(450_000, PlanningGift.Auto, null, Now.AddHours(30)); // late transfer still accepted, less than chosen
        Assert.Equal((PlanningGift.Paid, PlanningGift.Paid, 450_000L, true), (gift.Status, gift.DisplayStatus(Now.AddDays(3)), gift.AmountReceived!.Value, gift.AmountMismatch));
        Assert.Throws<InvalidOperationException>(() => gift.MarkPaid(1, PlanningGift.Manual, Guid.NewGuid(), Now));
        Assert.Throws<InvalidOperationException>(() => gift.Cancel(Guid.NewGuid()));

        var cash = PlanningGift.Recorded(Guid.NewGuid(), null, "Bác Ba", PlanningGift.Cash, 1_000_000, null, Guid.NewGuid(), Now, Now);
        Assert.Equal((PlanningGift.Paid, PlanningGift.Manual, (string?)null, false), (cash.Status, cash.Confirmation, cash.Code, cash.AmountMismatch));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningGift.Recorded(Guid.NewGuid(), null, "X", PlanningGift.Qr, 50_000, null, Guid.NewGuid(), Now, Now));
        Assert.Equal(5_000, PlanningGift.FromBankTransfer(Guid.NewGuid(), null, "X", 5_000, null, Guid.NewGuid(), Now, Now).AmountReceived);
    }

    [Fact]
    public void UnmatchedTransfersCanBeResolvedOnce()
    {
        var t = PlanningGiftTransaction.Create("SEPAY", 40532, "MBBank", "0000000001", null, "chuc mung", "in", 200_000, null, Now, "{}", Now);
        t.Resolve(PlanningGiftTransaction.Unmatched, Guid.NewGuid(), null);
        t.Assign(Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Equal(PlanningGiftTransaction.Matched, t.Status);
        Assert.Throws<InvalidOperationException>(() => t.Dismiss(Guid.NewGuid(), Now));
    }

    [Fact]
    public void GiftBookTotalsAndCsv()
    {
        GiftDto G(string method, string status, long? expected, long? received, string name = "Khách") =>
            new(Guid.NewGuid(), null, name, method, status, null, method == PlanningGift.Qr ? "MCABCDEFGH" : null, expected, received,
                status == PlanningGift.Paid && expected is not null && received != expected, null, false, null, Now, null, received is null ? null : Now);
        var gifts = new[]
        {
            G(PlanningGift.Qr, PlanningGift.Paid, 500_000, 500_000), G(PlanningGift.Qr, PlanningGift.Paid, 500_000, 400_000),
            G(PlanningGift.Cash, PlanningGift.Paid, null, 1_000_000, "=cmd"), G(PlanningGift.Transfer, PlanningGift.Paid, null, 300_000),
            G(PlanningGift.Qr, PlanningGift.Claimed, 200_000, null), G(PlanningGift.Qr, PlanningGift.Expired, 200_000, null)
        };
        var s = GiftBook.Summarize(gifts, needsReview: 2, accounts: 1);
        Assert.Equal((2_200_000L, 4, 900_000L, 2, 1_000_000L, 1, 300_000L, 1), (s.TotalReceived, s.ReceivedCount, s.QrTotal, s.QrCount, s.CashTotal, s.CashCount, s.TransferTotal, s.TransferCount));
        Assert.Equal((1, 1, 1, 1, 2), (s.Waiting, s.Claimed, s.Expired, s.AmountMismatches, s.NeedsReview));
        Assert.Equal(2, GiftBook.Filter(gifts, new GiftLedgerFilter(Status: PlanningGift.Paid, Method: PlanningGift.Qr)).Count);
        var csv = System.Text.Encoding.UTF8.GetString(GiftBook.Csv(gifts)[3..]);
        Assert.StartsWith("Người mừng,Hình thức,", csv);
        Assert.Contains("'=cmd,CASH,PAID", csv);
    }

    private static PlanningGift NewGift(long amount) =>
        PlanningGift.ForQr(Guid.NewGuid(), Guid.NewGuid(), "An", Guid.NewGuid(), "MCABCDEFGH", amount, null, false, "k", Now);
}
