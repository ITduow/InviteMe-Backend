using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

public sealed class PlanningImportValidationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "  ")]
    [InlineData("0900123456", "nam@example.com")]
    [InlineData("+84 900-123-456", "nam+invite@example.com")]
    [InlineData("(invalid)", "wrong")]
    public void OptionalContactsAreValidated(string? phone, string? email)
    {
        var errors = Validate(new([new(12, "Nam", Phone: phone, Email: email)]));
        if (phone == "(invalid)")
        {
            Assert.Contains(errors, x => x.RowNumber == 12 && x.Field == "phone" && x.Code == "INVALID_PHONE");
            Assert.Contains(errors, x => x.RowNumber == 12 && x.Field == "email" && x.Code == "INVALID_EMAIL");
        }
        else Assert.Empty(errors);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123456")]
    [InlineData("0900abc123")]
    [InlineData("++84900123456")]
    [InlineData("=1234567")]
    [InlineData("0900\n123456")]
    public void InvalidPhonesAreRejected(string value) =>
        Assert.Contains(Validate(new([new(2, "Nam", Phone: value)])), x => x.Code == "INVALID_PHONE");

    [Theory]
    [InlineData("nam")]
    [InlineData("nam@")]
    [InlineData("nam@@example.com")]
    [InlineData("@example.com")]
    [InlineData("=nam@example.com")]
    public void InvalidEmailsAreRejected(string value) =>
        Assert.Contains(Validate(new([new(2, "Nam", Email: value)])), x => x.Code == "INVALID_EMAIL");

    [Fact]
    public void OversizedEmailIsRejected() =>
        Assert.Contains(Validate(new([new(2, "Nam", Email: new string('a', 250) + "@example.com")])), x => x.Code == "INVALID_EMAIL");
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
