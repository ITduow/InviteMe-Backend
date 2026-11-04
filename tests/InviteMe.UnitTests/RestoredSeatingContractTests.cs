using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

public sealed class RestoredSeatingContractTests
{
    [Theory]
    [InlineData("MOVE_PARTY")]
    [InlineData("SWAP_PLACES")]
    [InlineData("SWAP_UNATTENDED")]
    [InlineData("OPERATIONAL_MOVE")]
    [InlineData("AUTO_PLACE_TABLES")]
    [InlineData("TAG_TABLES")]
    [InlineData("RENUMBER_TABLES")]
    [InlineData("START_EVENT")]
    [InlineData("LOCK_PLAN")]
    [InlineData("UNLOCK_PLAN")]
    [InlineData("RELEASE_DECLINED_SEATS")]
    public void RemovedActionsAreRejectedBeforeReachingTheStore(string action)
    {
        var result = new ChangePlanningSeatingValidator().Validate(new ChangePlanningSeating(0, action));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(ChangePlanningSeating.Action));
    }

    [Fact]
    public void OriginalTableAndPlaceOperationsRemainAvailable()
    {
        var validator = new ChangePlanningSeatingValidator();
        Assert.True(validator.Validate(new ChangePlanningSeating(0, "CREATE_TABLE", Name: "Bàn 1", Capacity: 4)).IsValid);
        Assert.True(validator.Validate(new ChangePlanningSeating(0, "MOVE_PLACES", PlaceIds: [Guid.NewGuid()])).IsValid);
    }

    [Fact]
    public void ImportValidatesExpectedPeople()
    {
        var row = new PlanningRow(2, "Nguyễn Văn An", "Bác", 4, "BRIDE");
        Assert.True(new PlanningImportValidator().Validate(new PlanningImport([row])).IsValid);
        Assert.False(new PlanningImportValidator().Validate(new PlanningImport([row with { ExpectedPeople = 0 }])).IsValid);
    }
}
