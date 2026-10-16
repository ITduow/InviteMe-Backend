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
    [InlineData("user@example.test", "12345")]
    [InlineData("user@example.test", "1234567")]
    [InlineData("user@example.test", "12a456")]
    [InlineData("not-an-email", "123456")]
    [InlineData("", "123456")]
    public async Task ConfirmEmailRejectsBadCodeOrBadEmail(string email, string code)
    {
        var validation = new RequestValidation<ConfirmEmail>([new ConfirmEmailValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(email, code), default));
    }

    [Fact]
    public async Task ConfirmEmailAcceptsValidInput()
    {
        await new RequestValidation<ConfirmEmail>([new ConfirmEmailValidator()])
            .ValidateAsync(new("user@example.test", "123456"), default);
    }
}
