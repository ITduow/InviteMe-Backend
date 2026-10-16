using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

public sealed class PlanningImportValidationTests
{
    private static IReadOnlyList<ImportIssue> Validate(PlanningImport input) =>
        PlanningImportValidator.Issues(input, new PlanningImportValidator().Validate(input).Errors);

    [Fact]
    public void MissingOptionalValuesAreAllowedWithoutInferringFamilyOrSide() =>
        Assert.Empty(Validate(new([new(2, "An")])));

    [Fact]
    public void InvalidRowsAndAmbiguousGroupsAreRejectedTogether()
    {
        var errors = Validate(new([
            new(2, "An", ExpectedPeople: 0, Side: "Nhà gái"),
            new(2, "Hoa", Group: new(ExistingId: Guid.NewGuid(), NewName: "Họ ngoại", Confirmed: true)),
            new(4, "Nam", Group: new(NewName: "Công ty ABC")), null
        ]));
        Assert.Contains(errors, x => x.Field == "expectedPeople");
        Assert.Contains(errors, x => x.Field == "side");
        Assert.Contains(errors, x => x.Field == "rowNumber");
        Assert.Contains(errors, x => x.Code == "INVALID_GROUP");
        Assert.Contains(errors, x => x.Code == "GROUP_CONFIRMATION_REQUIRED");
        Assert.Contains(errors, x => x.Code == "INVALID_ROW");
    }

    [Fact]
    public void GroupNormalizationPreservesMeaningAndOnlyNormalizesUnicodeSpacingAndCase()
    {
        Assert.Equal(PlanningText.Key("Họ ngoại"), PlanningText.Key("  họ   ngoại  "));
        Assert.NotEqual(PlanningText.Key("Bạn đại học"), PlanningText.Key("Bạn đai học"));
    }

    [Fact]
    public void ValidationErrorsUseSpreadsheetLineRatherThanArrayIndex()
    {
        var errors = Validate(new([new(17, "An", ExpectedPeople: 0), new(17, "Hoa")]));
        Assert.Contains(errors, x => x.RowNumber == 17 && x.Field == "expectedPeople" && x.Code == "INVALID_EXPECTED_PEOPLE");
        Assert.Contains(errors, x => x.RowNumber == 17 && x.Field == "rowNumber" && x.Code == "INVALID_ROW_NUMBER");
        Assert.All(errors, x => Assert.Equal(17, x.RowNumber));
    }

    [Fact]
    public void MissingOrOversizedBatchProducesRootErrorWithoutInspectingRows()
    {
        Assert.Contains(Validate(new(null)), x => x.RowNumber == 0 && x.Field == "rows" && x.Code == "INVALID_ROWS");
        Assert.Single(Validate(new(Enumerable.Repeat<PlanningRow?>(null, 501).ToArray())));
    }
}
