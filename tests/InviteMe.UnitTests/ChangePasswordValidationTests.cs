using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.UnitTests;

public sealed class ChangePasswordValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChangePasswordRejectsEmptyCurrentPassword(string currentPassword)
    {
        var validation = new RequestValidation<ChangePassword>([new ChangePasswordValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() =>
            validation.ValidateAsync(new(currentPassword, "ValidNewPassword123!"), default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("12345678901")] // 11 chars
    public async Task ChangePasswordRejectsShortNewPassword(string newPassword)
    {
        var validation = new RequestValidation<ChangePassword>([new ChangePasswordValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() =>
            validation.ValidateAsync(new("ValidCurrentPassword123!", newPassword), default));
    }

    [Fact]
    public async Task ChangePasswordRejectsIdenticalCurrentAndNewPassword()
    {
        var validation = new RequestValidation<ChangePassword>([new ChangePasswordValidator()]);
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            validation.ValidateAsync(new("SamePassword123!", "SamePassword123!"), default));

        Assert.Contains(ex.Errors, e => e.ErrorMessage.Contains("different from current password"));
    }

    [Fact]
    public async Task ChangePasswordAcceptsValidInput()
    {
        var validation = new RequestValidation<ChangePassword>([new ChangePasswordValidator()]);
        await validation.ValidateAsync(new("OldPassword123!", "NewStrongPassword123!"), default);
    }
}
