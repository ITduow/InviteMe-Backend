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

    [Theory]
    [InlineData("user@example.test", "", "Long-enough-Pass1!")]
    [InlineData("user@example.test", "   ", "Long-enough-Pass1!")]
    [InlineData("user@example.test", "CfDJ8ValidResetToken==", "short")]
    [InlineData("bad", "CfDJ8ValidResetToken==", "Long-enough-Pass1!")]
    public async Task ResetPasswordRejectsBadCodeWeakPasswordOrBadEmail(string email, string code, string password)
    {
        var validation = new RequestValidation<ResetPassword>([new ResetPasswordValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(email, code, password), default));
    }

    [Fact]
    public async Task ResetPasswordRejectsTooLongToken()
    {
        var validation = new RequestValidation<ResetPassword>([new ResetPasswordValidator()]);
        var tooLongToken = new string('a', 2049);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new("user@example.test", tooLongToken, "Long-enough-Pass1!"), default));
    }

    [Fact]
    public async Task ResetPasswordAcceptsValidInput()
    {
        await new RequestValidation<ResetPassword>([new ResetPasswordValidator()])
            .ValidateAsync(new("user@example.test", "CfDJ8ValidResetToken+123/abc==", "Long-enough-Pass1!"), default);
    }
}
