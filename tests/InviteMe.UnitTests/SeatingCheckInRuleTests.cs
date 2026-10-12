using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Seating;

namespace InviteMe.UnitTests;

public sealed class SeatingCheckInRuleTests
{
    [Theory]
    [InlineData("PRIMARY", "PLANNED", "ACTIVE", true)]
    [InlineData("BACKUP", "BACKUP", "ACTIVE", true)]
    [InlineData("PRIMARY", "ACTIVE", "INACTIVE", true)]
    [InlineData("BACKUP", "BACKUP", "INACTIVE", true)]
    [InlineData("PRIMARY", "INACTIVE", "PLANNED", true)]
    [InlineData("BACKUP", "INACTIVE", "BACKUP", true)]
    [InlineData("PRIMARY", "ACTIVE", "PLANNED", false)]
    [InlineData("BACKUP", "ACTIVE", "BACKUP", false)]
    [InlineData("PRIMARY", "PLANNED", "BACKUP", false)]
    [InlineData("PRIMARY", "INACTIVE", "ACTIVE", false)]
    [InlineData("BACKUP", "INACTIVE", "PLANNED", false)]
    public void TableTransitionsFollowSeat01Matrix(string kind, string from, string to, bool allowed) =>
        Assert.Equal(allowed, TableLifecycle.CanTransition(kind, from, to));

    [Fact]
    public void OnlyActiveTablesSeatPeopleAndKindFollowsTheStartingStatus()
    {
        Assert.True(TableLifecycle.IsSeatable("ACTIVE"));
        Assert.False(TableLifecycle.IsSeatable("PLANNED"));
        Assert.False(TableLifecycle.IsSeatable("BACKUP"));
        Assert.Equal("BACKUP", TableLifecycle.KindFor(null, "BACKUP"));
        Assert.Equal("PRIMARY", TableLifecycle.KindFor(null, "ACTIVE"));
        Assert.False(TableLifecycle.IsAllowed("PRIMARY", "BACKUP"));
        Assert.False(TableLifecycle.IsAllowed("BACKUP", "PLANNED"));
        Assert.Equal("BACKUP", ReceptionTable.Create(Guid.NewGuid(), " B1 ", 10, "BACKUP", "BACKUP").TableKind);
    }

    [Theory]
    [InlineData(0, "EMPTY")]
    [InlineData(6, "UNDERFILLED")]
    [InlineData(7, "NORMAL")]
    [InlineData(10, "FULL")]
    public void FillLevelMatchesBr039(int occupied, string level) =>
        Assert.Equal(level, new TableDto(Guid.NewGuid(), "5", 10, "ACTIVE", 1, null, occupied, []).FillLevel);

    [Fact]
    public async Task CreateTableRejectsKindStatusMismatch()
    {
        var validation = new RequestValidation<CreateTable>([new CreateTableValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new("1", 10, "BACKUP", "PRIMARY"), default));
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new("1", 10, "PLANNED", "BACKUP"), default));
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new("1", 10, "PLANNED", "VIP"), default));
        await validation.ValidateAsync(new("B1", 10, "BACKUP"), default);
    }

    [Theory]
    [InlineData(null, 0, (short)10, true)]
    [InlineData(20, 3, (short)10, true)]
    [InlineData(2, 3, (short)10, false)]
    [InlineData(0, 0, (short)10, false)]
    [InlineData(20, -1, (short)10, false)]
    [InlineData(20, 1, (short)21, false)]
    public async Task SeatingSettingsKeepBackupWithinBookedTables(int? booked, int backup, short size, bool valid)
    {
        var validation = new RequestValidation<SeatingSettings>([new SeatingSettingsValidator()]);
        var call = validation.ValidateAsync(new(booked, backup, size), default);
        if (valid) await call; else await Assert.ThrowsAsync<ValidationException>(() => call);
    }

    [Theory]
    [InlineData("https://inviteme.app/i/AB12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", true)]
    [InlineData("http://localhost:3000/i/ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34/", true)]
    [InlineData("  ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34  ", true)]
    [InlineData("https://inviteme.app/i/not-a-token", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void QrPayloadAcceptsOnlyInvitationTokens(string payload, bool valid)
    {
        Assert.Equal(valid, QrPayload.TryReadToken(payload, out var token));
        if (valid) Assert.Equal("ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", token);
    }

    [Theory]
    [InlineData("0912 345 678", "*******678")]
    [InlineData("+84912345678", "********678")]
    [InlineData("12", "**")]
    [InlineData(null, null)]
    public void PhoneIsMaskedExceptLastThreeDigits(string? phone, string? masked) =>
        Assert.Equal(masked, ReceptionHandler.MaskPhone(phone));

    [Fact]
    public void WalkInCheckInsAreMarkedAndVoidKeepsTheRecord()
    {
        var walk = CheckInRecord.Create(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), "MANUAL");
        Assert.Equal("WALK_IN", walk.Method);
        var guest = CheckInRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "MANUAL");
        guest.Void(Guid.NewGuid(), DateTimeOffset.UtcNow, "Wrong person");
        Assert.Equal(("VOID", "Wrong person"), (guest.Status, guest.VoidReason));
        Assert.Equal(4, new CheckInSummaryDto(3, 2, 2, 0, 1, 2).FinalAttendeeCount);
    }
}
