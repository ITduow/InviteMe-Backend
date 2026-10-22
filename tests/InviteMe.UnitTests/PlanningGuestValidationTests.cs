using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

public sealed class PlanningGuestValidationTests
{
    [Theory]
    [InlineData("abc12345")]
    [InlineData("123456")]
    [InlineData("1234567890123456")]
    [InlineData("++84900123456")]
    [InlineData("0900\n123456")]
    public void ManualCreateAndUpdateRejectTheSameInvalidPhoneFormats(string phone)
    {
        var create = new CreatePlanningGuest("Khách", Phone: phone);
        var update = new UpdatePlanningGuest(1, "Khách", null, "UNSPECIFIED", null, phone, null, null);

        Assert.Contains(new CreatePlanningGuestValidator().Validate(create).Errors,
            error => error.PropertyName == nameof(create.Phone) && error.ErrorCode == "INVALID_PHONE");
        Assert.Contains(new UpdatePlanningGuestValidator().Validate(update).Errors,
            error => error.PropertyName == nameof(update.Phone) && error.ErrorCode == "INVALID_PHONE");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0900123456")]
    [InlineData("+84 (900) 123-456")]
    public void ManualCreateAndUpdateAcceptTheSameValidOptionalPhoneFormats(string? phone)
    {
        var create = new CreatePlanningGuest("Khách", Phone: phone);
        var update = new UpdatePlanningGuest(1, "Khách", null, "UNSPECIFIED", null, phone, null, null);

        Assert.DoesNotContain(new CreatePlanningGuestValidator().Validate(create).Errors,
            error => error.PropertyName == nameof(create.Phone));
        Assert.DoesNotContain(new UpdatePlanningGuestValidator().Validate(update).Errors,
            error => error.PropertyName == nameof(update.Phone));
    }

    [Fact]
    public void PhoneKeyIgnoresFormattingForDuplicateDetection()
    {
        Assert.Equal(PlanningText.PhoneKey("+84 (900) 123-456"), PlanningText.PhoneKey("84900123456"));
    }
}
