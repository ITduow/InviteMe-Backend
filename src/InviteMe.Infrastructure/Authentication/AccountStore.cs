using FluentValidation;
using FluentValidation.Results;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace InviteMe.Infrastructure.Authentication;

public sealed class AccountStore(
    UserManager<ApplicationUser> users,
    InviteMeDbContext db) : IAccountStore
{
    public async Task<AccountIdentity> RegisterAsync(RegisterAccount input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = input.Email.Trim(),
            Email = input.Email.Trim(),
            FullName = input.DisplayName.Trim()
        };

        EnsureSuccess(await users.CreateAsync(user, input.Password));
        EnsureSuccess(await users.AddToRoleAsync(user, "USER"));

        await transaction.CommitAsync(ct);
        return await DescribeAsync(user);
    }

    public async Task<AccountIdentity> LoginAsync(LoginAccount input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedEmail = input.Email.Trim();
        var user = await users.FindByEmailAsync(normalizedEmail);

        if (user is null || user.Status != "ACTIVE" || await users.IsLockedOutAsync(user))
        {
            throw InvalidLogin();
        }

        if (!await users.CheckPasswordAsync(user, input.Password))
        {
            EnsureSuccess(await users.AccessFailedAsync(user));
            throw InvalidLogin();
        }

        EnsureSuccess(await users.ResetAccessFailedCountAsync(user));
        return await DescribeAsync(user);
    }

    public async Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByIdAsync(id.ToString());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user))
        {
            return null;
        }

        return await DescribeAsync(user);
    }

    public async Task RevokeSessionsAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByIdAsync(id.ToString()) ?? throw InvalidLogin();
        EnsureSuccess(await users.UpdateSecurityStampAsync(user));
    }

    private async Task<AccountIdentity> DescribeAsync(ApplicationUser user)
    {
        var role = await users.IsInRoleAsync(user, "ADMIN") ? "ADMIN" : "USER";
        return new AccountIdentity(
            user.Id,
            user.FullName,
            user.Email!,
            role,
            user.SecurityStamp!);
    }

    private static ApplicationProblemException InvalidLogin()
    {
        return new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "INVALID_CREDENTIALS",
            "Email, password or account is unavailable.");
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        if (result.Errors.Any(x => x.Code.StartsWith("Duplicate", StringComparison.Ordinal)))
        {
            throw new ApplicationProblemException(
                ProblemKind.Conflict,
                "DUPLICATE_ACCOUNT",
                "Account already exists.");
        }

        var failures = result.Errors.Select(x =>
            new ValidationFailure(
                x.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email",
                x.Description));

        throw new ValidationException(failures);
    }
}
