using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

public sealed class SeatingSnapshotTests
{
    [Fact]
    public void FingerprintIsCanonicalAndDetectsPlaceAndLayoutChanges()
    {
        var a = new PlanningSeatingPlace(Guid.NewGuid(), Guid.NewGuid(), 1, null);
        var b = new PlanningSeatingPlace(Guid.NewGuid(), Guid.NewGuid(), 1, null);
        var source = new PlanningSeatingDto(Guid.NewGuid(), 0, [], [], [a, b], []);
        Assert.Equal(SeatingSnapshot.Fingerprint(source), SeatingSnapshot.Fingerprint(source with { Places = [b, a] }));
        Assert.NotEqual(SeatingSnapshot.Fingerprint(source), SeatingSnapshot.Fingerprint(source with { Places = [a] }));
        Assert.NotEqual(SeatingSnapshot.Fingerprint(source), SeatingSnapshot.Fingerprint(source with { BrideTablesPerRow = 3 }));
    }

    [Theory]
    [InlineData("CREATE_TABLE")]
    [InlineData("CREATE_TABLES")]
    public void CreationAcceptsAnExplicitSlotOrAutomaticFirstFreeSlot(string action)
    {
        var input = new ChangePlanningSeating(0, action, Name: "Table", Capacity: 10, TableCount: 2, Side: "BRIDE");
        var validator = new ChangePlanningSeatingValidator();
        Assert.True(validator.Validate(input).IsValid);
        Assert.True(validator.Validate(input with { Row = 1, Column = 2 }).IsValid);
        Assert.False(validator.Validate(input with { Row = 1 }).IsValid);
        Assert.False(validator.Validate(input with { Column = 1 }).IsValid);
        Assert.False(validator.Validate(input with { Row = 0, Column = 1 }).IsValid);
    }
}
