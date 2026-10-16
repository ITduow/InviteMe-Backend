using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

public sealed class PlanningImportValidationTests
{
    [Fact]
    public void MissingOptionalValuesAreAllowedWithoutInferringFamilyOrSide() =>
        Assert.Empty(PlanningValidation.Validate(new([new(2, "An")])));

    [Fact]
    public void InvalidRowsAndAmbiguousGroupsAreRejectedTogether()
    {
        var errors = PlanningValidation.Validate(new([
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
        Assert.Equal(PlanningValidation.Key("Họ ngoại"), PlanningValidation.Key("  họ   ngoại  "));
        Assert.NotEqual(PlanningValidation.Key("Bạn đại học"), PlanningValidation.Key("Bạn đai học"));
    }
}
