using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

/// <summary>Import duplicate checks: identical rows are skipped, shared contacts block, look-alikes only warn.</summary>
public sealed class PlanningImportChecksTests
{
    private static readonly Guid FamilyGroup = Guid.NewGuid();
    private static readonly ExistingPlanningContact[] Existing =
    [
        new("Nguyễn Văn An", "0912 345 678", "an@example.test", "Bác", 4, "BRIDE", FamilyGroup, "Họ nhà gái", null),
        new("Trần Thị Bình", null, null, "Chị", 1, "GROOM")
    ];

    [Fact]
    public void ARowIdenticalToAGuestOrToAnEarlierRowIsSkippedAndTheRestIsImported()
    {
        var check = PlanningImportChecks.Analyze(
        [
            new(2, "  nguyễn văn  an ", "bác", 4, "BRIDE", new PlanningGroupInput(ExistingId: FamilyGroup), "+84912345678", Email: "AN@example.test"),
            new(3, "Lê Văn Cường", "Anh", 2, "GROOM"),
            new(4, "Lê Văn Cường", "Anh", 2, "GROOM"),
            new(5, "Trần Thị Bình", "Chị", 1, "GROOM", Note: "ăn chay") // a different note: not the same data
        ], Existing);
        Assert.Equal(new[] { (2, PlanningImportChecks.GuestAlreadyExists), (4, PlanningImportChecks.DuplicateRowInFile) },
            check.Skipped.Select(x => (x.RowNumber, x.Code)).ToArray());
        Assert.Empty(check.Errors);
        Assert.Equal((2, 3), (check.GuestCount, check.PeopleCount)); // rows 3 and 5 are imported
        Assert.Contains(check.Warnings, x => x is { RowNumber: 5, Code: PlanningImportChecks.PossibleDuplicate });
    }

    [Fact]
    public void APhoneOrEmailOfAnotherPersonBlocksTheImport()
    {
        var check = PlanningImportChecks.Analyze(
        [
            new(2, "Phạm Minh", Phone: "+84 912-345-678"),          // same number as Nguyễn Văn An, written differently
            new(3, "Hoàng Lan", Phone: "0988 111 222", Email: "lan@example.test"),
            new(4, "Hoàng Lan Anh", Phone: "0988111222"),
            new(5, "Đỗ Hà", Email: "LAN@example.test ")
        ], Existing);
        Assert.Equal(new[]
            {
                (2, "phone", PlanningImportChecks.PhoneExists), (4, "phone", PlanningImportChecks.DuplicatePhoneInFile),
                (5, "email", PlanningImportChecks.DuplicateEmailInFile)
            },
            check.Errors.Select(x => (x.RowNumber, x.Field, x.Code)).ToArray());
        Assert.Contains("Nguyễn Văn An", check.Errors[0].Message);
        Assert.Contains("dòng 3", check.Errors[1].Message);
    }

    [Fact]
    public void LookAlikesOnlyWarn()
    {
        var check = PlanningImportChecks.Analyze(
        [
            new(2, "Anh Tuấn", "Anh"),
            new(6, "Gia đình ông Nguyễn Văn Đức", ExpectedPeople: 4), // the family label is part of the name: fine without honorific
            new(3, "Mai Hương", ExpectedPeople: 12),
            new(4, "Mai Hương", Phone: "0900000001")
        ], Existing);
        Assert.Empty(check.Errors);
        Assert.Equal(new[]
            {
                (2, PlanningImportChecks.NameHasHonorific), (3, PlanningImportChecks.LargeParty), (4, PlanningImportChecks.DuplicateNameInFile)
            },
            check.Warnings.Select(x => (x.RowNumber, x.Code)).ToArray());
    }

    [Fact]
    public void PhonesCompareWithoutFormattingOrCountryCode()
    {
        Assert.Equal("0912345678", PlanningImportChecks.PhoneKey("+84 912.345.678"));
        Assert.Equal("0912345678", PlanningImportChecks.PhoneKey("(091) 234-5678"));
        Assert.Equal("84123", PlanningImportChecks.PhoneKey("84123")); // too short to be +84
    }
}
