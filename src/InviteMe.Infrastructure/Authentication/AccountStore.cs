using FluentValidation;
using FluentValidation.Results;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Authentication;

public sealed class AccountStore(
    UserManager<ApplicationUser> users,
    InviteMeDbContext db) : IAccountStore
{
    private const string ResetPurpose = "ResetPassword";
    private const string ConfirmationPurpose = "EmailConfirmation";

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

    public async Task<AccountIdentity> LoginWithGoogleAsync(GoogleUserPayload payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Look up by Google sub in user_logins
        var user = await users.FindByLoginAsync("Google", payload.Subject);
        if (user is not null)
        {
            if (user.Status != "ACTIVE" || await users.IsLockedOutAsync(user))
            {
                throw InvalidLogin();
            }

            if (string.IsNullOrEmpty(user.AvatarUrl) && !string.IsNullOrWhiteSpace(payload.Picture))
            {
                user.AvatarUrl = payload.Picture;
                await users.UpdateAsync(user);
            }

            return await DescribeAsync(user);
        }

        // 2. Look up by normalized email
        var normalizedEmail = payload.Email.Trim();
        user = await users.FindByEmailAsync(normalizedEmail);

        if (user is not null)
        {
            if (user.Status != "ACTIVE" || await users.IsLockedOutAsync(user))
            {
                throw InvalidLogin();
            }

            var existingLogins = await users.GetLoginsAsync(user);
            var existingGoogleLogin = existingLogins.FirstOrDefault(l => l.LoginProvider == "Google");
            if (existingGoogleLogin is not null && existingGoogleLogin.ProviderKey != payload.Subject)
            {
                throw new ApplicationProblemException(
                    ProblemKind.Conflict,
                    "GOOGLE_ACCOUNT_CONFLICT",
                    "This account is already linked to a different Google account.");
            }

            if (!user.EmailConfirmed)
            {
                await using var unconfirmedTx = await db.Database.BeginTransactionAsync(ct);

                // Verify whether the unconfirmed account has existing business data inside the transaction
                var hasWeddings = await db.Weddings.AnyAsync(w => w.OwnerUserId == user.Id, ct);
                var hasMembers = await db.WeddingMembers.AnyAsync(m => m.UserId == user.Id, ct);
                var hasAudit = await db.AuditLogs.AnyAsync(a => a.ActorUserId == user.Id, ct);

                if (hasWeddings || hasMembers || hasAudit)
                {
                    throw new ApplicationProblemException(
                        ProblemKind.Conflict,
                        "UNCONFIRMED_ACCOUNT_HAS_DATA",
                        "Account has unconfirmed email and existing data. Please verify email before linking Google.");
                }

                // Empty unconfirmed account: safe linking (wipe password to prevent pre-hijacking)
                user.PasswordHash = null;
                user.EmailConfirmed = true;
                EnsureSuccess(await users.UpdateSecurityStampAsync(user));

                var now = DateTimeOffset.UtcNow;
                await db.Set<RefreshToken>()
                    .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now), ct);

                if (existingGoogleLogin is null)
                {
                    EnsureSuccess(await users.AddLoginAsync(user, new UserLoginInfo("Google", payload.Subject, "Google")));
                }

                if (string.IsNullOrEmpty(user.AvatarUrl) && !string.IsNullOrWhiteSpace(payload.Picture))
                {
                    user.AvatarUrl = payload.Picture;
                }

                EnsureSuccess(await users.UpdateAsync(user));
                await unconfirmedTx.CommitAsync(ct);
                return await DescribeAsync(user);
            }

            // Confirmed account: link Google
            if (existingGoogleLogin is null)
            {
                EnsureSuccess(await users.AddLoginAsync(user, new UserLoginInfo("Google", payload.Subject, "Google")));
            }

            var updated = false;
            if (string.IsNullOrEmpty(user.AvatarUrl) && !string.IsNullOrWhiteSpace(payload.Picture))
            {
                user.AvatarUrl = payload.Picture;
                updated = true;
            }

            if (updated)
            {
                await users.UpdateAsync(user);
            }

            return await DescribeAsync(user);
        }

        // 3. New user
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var displayName = !string.IsNullOrWhiteSpace(payload.Name)
            ? payload.Name.Trim()
            : normalizedEmail.Split('@')[0];

        user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = normalizedEmail,
            Email = normalizedEmail,
            FullName = displayName.Length > 150 ? displayName[..150] : displayName,
            AvatarUrl = payload.Picture,
            EmailConfirmed = true,
            Status = "ACTIVE"
        };

        EnsureSuccess(await users.CreateAsync(user));
        EnsureSuccess(await users.AddToRoleAsync(user, "USER"));
        EnsureSuccess(await users.AddLoginAsync(user, new UserLoginInfo("Google", payload.Subject, "Google")));

        await transaction.CommitAsync(ct);
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

    public async Task<PasswordResetRequest?> CreatePasswordResetAsync(string email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { Status: "ACTIVE" })
        {
            return null;
        }

        var code = await users.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ResetPurpose);
        return new PasswordResetRequest(user.Id, user.Email!, user.FullName, code);
    }

    public async Task<Guid> ResetPasswordAsync(ResetPassword input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user))
        {
            throw InvalidResetCode();
        }

        if (!await users.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ResetPurpose, input.Code))
        {
            // Wrong codes count toward the normal lockout so a 6-digit code cannot be brute-forced.
            EnsureSuccess(await users.AccessFailedAsync(user));
            throw InvalidResetCode();
        }

        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        EnsureSuccess(await users.ResetPasswordAsync(user, resetToken, input.NewPassword));

        // A successful reset also lifts any lockout caused by earlier failed sign-ins.
        EnsureSuccess(await users.SetLockoutEndDateAsync(user, null));
        EnsureSuccess(await users.ResetAccessFailedCountAsync(user));
        return user.Id;
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePassword input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user))
        {
            throw InvalidLogin();
        }

        var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
            {
                throw InvalidCurrentPassword();
            }

            EnsureSuccess(result);
        }

        EnsureSuccess(await users.UpdateSecurityStampAsync(user));
    }

    public async Task<EmailConfirmationRequest?> CreateEmailConfirmationAsync(string email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user) || user.EmailConfirmed)
        {
            return null;
        }

        var code = await users.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ConfirmationPurpose);
        return new EmailConfirmationRequest(user.Id, user.Email!, user.FullName, code);
    }

    public async Task ConfirmEmailAsync(ConfirmEmail input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user))
        {
            throw InvalidConfirmationCode();
        }

        if (user.EmailConfirmed)
        {
            return;
        }

        if (!await users.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ConfirmationPurpose, input.Code))
        {
            EnsureSuccess(await users.AccessFailedAsync(user));
            throw InvalidConfirmationCode();
        }

        user.EmailConfirmed = true;
        EnsureSuccess(await users.UpdateAsync(user));
        EnsureSuccess(await users.ResetAccessFailedCountAsync(user));
    }

    public async Task DeleteAccountAsync(Guid userId, DeleteAccount input, GoogleUserPayload? googlePayload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not { Status: "ACTIVE" } || await users.IsLockedOutAsync(user))
        {
            throw InvalidLogin();
        }

        // Bước 1: Re-authentication (D1)
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            if (string.IsNullOrWhiteSpace(input.Password))
            {
                throw new ApplicationProblemException(
                    ProblemKind.BusinessRule,
                    "PASSWORD_REQUIRED",
                    "Password is required to delete this account.");
            }

            if (!await users.CheckPasswordAsync(user, input.Password))
            {
                EnsureSuccess(await users.AccessFailedAsync(user));
                throw InvalidLogin();
            }
        }
        else
        {
            // Pure Google account
            if (googlePayload is null)
            {
                throw new ApplicationProblemException(
                    ProblemKind.BusinessRule,
                    "GOOGLE_TOKEN_REQUIRED",
                    "Google ID token is required to delete this account.");
            }

            var logins = await users.GetLoginsAsync(user);
            var googleLogin = logins.FirstOrDefault(l => l.LoginProvider == "Google");
            if (googleLogin is null || googleLogin.ProviderKey != googlePayload.Subject)
            {
                throw InvalidLogin();
            }
        }

        // Bước 2: Pre-condition guards (D2)
        if (await users.IsInRoleAsync(user, "ADMIN"))
        {
            throw new ApplicationProblemException(
                ProblemKind.Forbidden,
                "ADMIN_CANNOT_SELF_DELETE",
                "Administrator accounts cannot be deleted through this endpoint.");
        }

        // Bước 3 & 4: Atomic Execution in 1 Database Transaction (D3)
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var currentUser = await db.Set<ApplicationUser>().SingleOrDefaultAsync(u => u.Id == user.Id, ct);
        if (currentUser is null || currentUser.Status != "ACTIVE")
        {
            throw InvalidLogin();
        }

        var hasActiveWeddings = await db.Weddings.AnyAsync(
            w => w.OwnerUserId == user.Id && (w.Status == "DRAFT" || w.Status == "PUBLISHED"),
            ct);

        if (hasActiveWeddings)
        {
            throw new ApplicationProblemException(
                ProblemKind.Conflict,
                "ACTIVE_WEDDING_OWNED",
                "Cannot delete account while owning active weddings (DRAFT or PUBLISHED). Please close or transfer your weddings first.");
        }

        // Thu hồi toàn bộ WeddingMembers của user (status = 'REVOKED')
        var memberships = await db.WeddingMembers
            .Where(m => m.UserId == user.Id && m.Status != "REVOKED")
            .ToListAsync(ct);

        foreach (var member in memberships)
        {
            member.Revoke();

            var memberPermissions = await db.WeddingMemberPermissions
                .Where(p => p.MemberId == member.Id)
                .ToListAsync(ct);
            db.WeddingMemberPermissions.RemoveRange(memberPermissions);

            db.AuditLogs.Add(AuditLog.ForEntity(
                member.WeddingId,
                "wedding_members",
                member.Id,
                "COHOST_REVOKED",
                actorUserId: user.Id,
                metadata: "{\"reason\":\"USER_ACCOUNT_DELETED\"}"));
        }

        // Xóa logins & tokens
        var userTokens = await db.UserTokens.Where(t => t.UserId == user.Id).ToListAsync(ct);
        db.UserTokens.RemoveRange(userTokens);

        var userLogins = await db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync(ct);
        db.UserLogins.RemoveRange(userLogins);

        // Đặt credentials = null
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.AvatarUrl = null;

        // Anonymize
        var anonymizedEmail = $"deleted-{user.Id:N}@deleted.inviteme.local";
        user.Email = anonymizedEmail;
        user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
        user.UserName = anonymizedEmail;
        user.NormalizedUserName = anonymizedEmail.ToUpperInvariant();
        user.FullName = "Deleted User";

        // Deactivate & Revoke
        user.Status = "DISABLED";
        user.EmailConfirmed = false;
        user.SecurityStamp = Guid.NewGuid().ToString();

        EnsureSuccess(await users.UpdateAsync(user));

        // Revoke all refresh tokens on the same transaction
        var now = DateTimeOffset.UtcNow;
        await db.Set<RefreshToken>()
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now), ct);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static ValidationException InvalidCurrentPassword()
    {
        return new ValidationException([new ValidationFailure("CurrentPassword", "Current password is incorrect.")]);
    }

    private async Task<AccountIdentity> DescribeAsync(ApplicationUser user)
    {
        var role = await users.IsInRoleAsync(user, "ADMIN") ? "ADMIN" : "USER";
        return new AccountIdentity(
            user.Id,
            user.FullName,
            user.Email!,
            role,
            user.SecurityStamp!,
            user.EmailConfirmed);
    }

    private static ApplicationProblemException InvalidLogin()
    {
        return new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "INVALID_CREDENTIALS",
            "Email, password or account is unavailable.");
    }

    private static ValidationException InvalidResetCode()
    {
        return new ValidationException([new ValidationFailure("Code", "The code is invalid or has expired.")]);
    }

    private static ValidationException InvalidConfirmationCode()
    {
        return new ValidationException([new ValidationFailure("Code", "The confirmation code is invalid or has expired.")]);
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

        if (result.Errors.Any(x => x.Code.Equals("ConcurrencyFailure", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApplicationProblemException(
                ProblemKind.Conflict,
                "CONCURRENCY_CONFLICT",
                "The account was modified by another operation.");
        }

        var failures = result.Errors.Select(x =>
            new ValidationFailure(
                x.Code.StartsWith("Password", StringComparison.Ordinal) ? "Password" : "Email",
                x.Description));

        throw new ValidationException(failures);
    }
}
