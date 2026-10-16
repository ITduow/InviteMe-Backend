using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.UnitTests;

public sealed class EmailConfirmationValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task ResendConfirmationRejectsBadEmail(string email)
    {
        var validation = new RequestValidation<ResendConfirmation>([new ResendConfirmationValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(email), default));
    }

    [Fact]
    public async Task ResendConfirmationAcceptsValidEmail()
    {
        await new RequestValidation<ResendConfirmation>([new ResendConfirmationValidator()])
            .ValidateAsync(new("user@example.test"), default);
    }

    [Theory]
    [InlineData("user@example.test", "")]
    [InlineData("user@example.test", "   ")]
    [InlineData("not-an-email", "CfDJ8ValidTokenString==")]
    [InlineData("", "CfDJ8ValidTokenString==")]
    public async Task ConfirmEmailRejectsBadCodeOrBadEmail(string email, string code)
    {
        var validation = new RequestValidation<ConfirmEmail>([new ConfirmEmailValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(email, code), default));
    }

    [Fact]
    public async Task ConfirmEmailRejectsTooLongToken()
    {
        var validation = new RequestValidation<ConfirmEmail>([new ConfirmEmailValidator()]);
        var tooLongToken = new string('a', 2049);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new("user@example.test", tooLongToken), default));
    }

    [Fact]
    public async Task ConfirmEmailAcceptsValidInput()
    {
        await new RequestValidation<ConfirmEmail>([new ConfirmEmailValidator()])
            .ValidateAsync(new("user@example.test", "CfDJ8ValidConfirmationTokenString+123/abc=="), default);
    }
}
