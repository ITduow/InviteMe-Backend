using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;

public sealed class PlanningImportChecksTests
{
    private static PlanningRow Row(int number = 2) => new(number, "Nguyễn Văn An", "Bác", 4, "BRIDE", PlanningGroupKind.RELATIVES,
        "0912 345 678", "Có trẻ nhỏ", "An@Example.com");
    private static ImportGuestSnapshot Existing(PlanningRow? row = null) => PlanningImportChecks.FromRow(row ?? Row()) with { Id = Guid.NewGuid(), RowNumber = null };
    private static PlanningImportRowCheck Check(PlanningRow row, params ImportGuestSnapshot[] existing) => Assert.Single(PlanningImportChecks.Match([row], existing));

    [Fact]
    public void D01_ExactDuplicateInFileRequiresExplicitDecision()
    {
        var rows = PlanningImportChecks.Match([Row(), Row(8)], []);
        Assert.Equal("NEW", rows[0].Status);
        Assert.Equal("NEED_DECISION", rows[1].Status);
        Assert.Contains("EXACT_DUPLICATE_IN_FILE", rows[1].Reasons);
        Assert.Equal(2, Assert.Single(rows[1].Matches).SourceRowNumber);
    }
    [Fact]
    public void D02_NormalizedExactDuplicateStillRequiresDecision()
    {
        var result = Check(Row() with { RecipientName = " NGUYỄN  VĂN AN ", Phone = "+84912345678", Email = "an@example.com" }, Existing());
        Assert.Equal("NEED_DECISION", result.Status);
        Assert.Contains("EXACT_DUPLICATE_IN_WEDDING", result.Reasons);
        Assert.Empty(Assert.Single(result.Matches).Differences);
    }
    [Fact]
    public void D03_SameNameContactShowsTheExactChangedFields()
    {
        var result = Check(Row() with { ExpectedPeople = 5, Note = "Ngồi gần cửa" }, Existing());
        Assert.Equal("NEED_DECISION", result.Status);
        Assert.Contains("SAME_NAME_AND_CONTACT_CHANGED", result.Reasons);
        Assert.Equal(new[] { "expectedPeople", "note" }, result.Matches[0].Differences.Select(x => x.Field));
    }
    [Fact]
    public void D04_SameNameDifferentContactIsOnlyWarning()
    {
        var result = Check(Row() with { Phone = "0902222222", Email = "other@example.com" }, Existing());
        Assert.Equal("NEED_DECISION", result.Status); Assert.Contains("SAME_NAME_DIFFERENT_CONTACT", result.Reasons);
    }
    [Fact]
    public void D05_SameNameWithoutContactWithDifferentDetailsIsOnlyWarning()
    {
        var result = Check(Row() with { Phone = null, Email = null, Note = "Khác" }, Existing(Row() with { Phone = null, Email = null }));
        Assert.Equal("NEED_DECISION", result.Status); Assert.Contains("SAME_NAME_NO_CONTACT", result.Reasons);
    }
    [Theory]
    [InlineData("phone", "SHARED_PHONE")]
    [InlineData("email", "SHARED_EMAIL")]
    public void D06_D07_SharedContactNeverForcesMerge(string field, string reason)
    {
        var row = Row() with { RecipientName = "Trần Thị Bình", Phone = field == "phone" ? "+84912345678" : null, Email = field == "email" ? "an@example.COM" : null };
        var result = Check(row, Existing());
        Assert.Equal("NEED_DECISION", result.Status); Assert.Contains(reason, result.Reasons);
    }
    [Fact]
    public void D09_SameNameDifferentSideGroupWithoutSharedContactIsWarning()
    {
        var result = Check(Row() with { Side = "GROOM", Group = PlanningGroupKind.FRIENDS, Phone = null, Email = null }, Existing());
        Assert.Equal("NEED_DECISION", result.Status); Assert.Contains("SAME_NAME_OTHER_SIDE_GROUP", result.Reasons);
    }
    [Fact]
    public void D11_AccentDifferencesAreNotAutomaticallyMatched()
    {
        var result = Check(Row() with { RecipientName = "Nguyen Van An", Phone = null, Email = null }, Existing());
        Assert.Equal("NEW", result.Status);
    }
    [Fact]
    public void BadExistingContactsAreIgnoredAndNeverChanged()
    {
        var old = Existing() with { Phone = "bad old phone", Email = "invalid" };
        var result = Check(Row() with { RecipientName = "Khách mới" }, old);
        Assert.Equal("NEW", result.Status); Assert.Equal("bad old phone", old.Phone);
    }
    [Fact]
    public void DefaultsAreVisibleAndDoNotInferFamily()
    {
        var row = new PlanningRow(17, "An", ExpectedPeople: 4);
        var result = Check(row);
        Assert.Contains("DEFAULTED_SIDE", result.Reasons); Assert.Contains("DEFAULTED_GROUP", result.Reasons);
    }
    [Fact]
    public void SnapshotIsOrderIndependentButDetectsChanges()
    {
        var a = Existing(); var b = Existing(Row() with { RecipientName = "Bình" });
        Assert.Equal(PlanningImportChecks.SnapshotHash([a, b], 10), PlanningImportChecks.SnapshotHash([b, a], 10));
        Assert.NotEqual(PlanningImportChecks.SnapshotHash([a], 10), PlanningImportChecks.SnapshotHash([a with { Note = "changed" }], 10));
        Assert.NotEqual(PlanningImportChecks.SnapshotHash([a], 10), PlanningImportChecks.SnapshotHash([a], 9));
    }
}
