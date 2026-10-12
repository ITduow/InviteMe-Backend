using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.UnitTests;

public sealed class PasswordResetValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task ForgotPasswordRejectsBadEmail(string email)
    {
        var validation = new RequestValidation<ForgotPassword>([new ForgotPasswordValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(email), default));
    }

    [Fact]
    public async Task ForgotPasswordAcceptsValidEmail()
    {
        await new RequestValidation<ForgotPassword>([new ForgotPasswordValidator()])
            .ValidateAsync(new("user@example.test"), default);
    }
}
