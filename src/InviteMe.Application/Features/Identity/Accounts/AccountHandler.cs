using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Identity.Accounts;

public sealed record AccountDto(
    Guid Id,
    string DisplayName,
    string Email,
    string Role,
    bool EmailConfirmed = false,
    string? Phone = null,
    string? AvatarUrl = null);

public sealed record TokenDto(
    string AccessToken,
    int ExpiresIn);

public sealed record AuthSessionDto(
    TokenDto Token,
    IssuedRefreshToken IssuedRefresh)
{
    public string AccessToken => Token.AccessToken;
    public int ExpiresIn => Token.ExpiresIn;
    public string RefreshToken => IssuedRefresh.Value;
    public int RefreshExpiresIn => IssuedRefresh.ExpiresIn;
}

public sealed class RegisterAccountValidator : AbstractValidator<RegisterAccount>
{
    public RegisterAccountValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(128);

        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .Must(x => !string.IsNullOrWhiteSpace(x))
            .MaximumLength(150);
    }
}

public sealed class LoginAccountValidator : AbstractValidator<LoginAccount>
{
    public LoginAccountValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(128);
    }
}

public sealed class RefreshSessionValidator : AbstractValidator<RefreshSession>
{
    public RefreshSessionValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty()
            .MaximumLength(256);
    }
}

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPassword>
{
    public ForgotPasswordValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);
    }
}

public sealed class ResetPasswordValidator : AbstractValidator<ResetPassword>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);

        RuleFor(x => x.Code)
            .NotEmpty()
            .Must(x => !string.IsNullOrWhiteSpace(x))
            .MaximumLength(2048);

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(128);
    }
}

public sealed class ConfirmEmailValidator : AbstractValidator<ConfirmEmail>
{
    public ConfirmEmailValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);

        RuleFor(x => x.Code)
            .NotEmpty()
            .Must(x => !string.IsNullOrWhiteSpace(x))
            .MaximumLength(2048);
    }
}

public sealed class ResendConfirmationValidator : AbstractValidator<ResendConfirmation>
{
    public ResendConfirmationValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(255);
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePassword>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(128);

        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("New password must be different from current password.");
    }
}

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfile>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .Must(x => !string.IsNullOrWhiteSpace(x))
            .Must(x => x is not null && x.Trim().Length <= 50)
            .WithMessage("DisplayName must not be empty and cannot exceed 50 characters.");

        When(x => !string.IsNullOrWhiteSpace(x.Phone), () =>
        {
            RuleFor(x => x.Phone!)
                .Must(phone => global::System.Text.RegularExpressions.Regex.IsMatch(phone.Trim(), @"^0\d{9}$"))
                .WithMessage("Phone must start with 0 and contain exactly 10 digits.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.AvatarUrl), () =>
        {
            RuleFor(x => x.AvatarUrl!)
                .MaximumLength(2048)
                .Must(url => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .WithMessage("AvatarUrl must be a valid HTTP or HTTPS URL.");
        });
    }
}

public sealed class GoogleLoginValidator : AbstractValidator<GoogleLogin>
{
    public GoogleLoginValidator()
    {
        RuleFor(x => x.IdToken)
            .NotEmpty()
            .MaximumLength(4096);
    }
}

public sealed class DeleteAccountValidator : AbstractValidator<DeleteAccount>
{
    public DeleteAccountValidator()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.Password) || !string.IsNullOrWhiteSpace(x.GoogleIdToken))
            .WithMessage("Either password or Google ID token is required for account deletion.");

        When(x => !string.IsNullOrWhiteSpace(x.Password), () =>
        {
            RuleFor(x => x.Password)
                .MaximumLength(128);
        });

        When(x => !string.IsNullOrWhiteSpace(x.GoogleIdToken), () =>
        {
            RuleFor(x => x.GoogleIdToken)
                .MaximumLength(4096);
        });
    }
}

public sealed class AccountHandler(
    IAccountStore store,
    IRefreshTokenStore refreshTokens,
    IAccessTokenIssuer issuer,
    ICurrentUser current,
    RequestValidation<RegisterAccount> registerValidation,
    RequestValidation<LoginAccount> loginValidation,
    RequestValidation<RefreshSession> refreshValidation,
    IAccountEmailSender emailSender,
    RequestValidation<ForgotPassword> forgotValidation,
    RequestValidation<ResetPassword> resetValidation,
    RequestValidation<ConfirmEmail> confirmValidation,
    RequestValidation<ResendConfirmation> resendValidation,
    RequestValidation<ChangePassword> changeValidation,
    IGoogleTokenValidator googleValidator,
    RequestValidation<GoogleLogin> googleValidation,
    RequestValidation<DeleteAccount> deleteValidation,
    RequestValidation<UpdateProfile> updateProfileValidation)
{
    private readonly RequestValidation<RefreshSession> _ = refreshValidation;

    public async Task<AccountDto> RegisterAsync(RegisterAccount input, CancellationToken ct)
    {
        await registerValidation.ValidateAsync(input, ct);
        var account = await store.RegisterAsync(input, ct);
        var confirmation = await store.CreateEmailConfirmationAsync(account.Email, ct);
        if (confirmation is not null)
        {
            await emailSender.SendEmailConfirmationAsync(confirmation, ct);
        }
        return MapToDto(account);
    }

    public async Task<AuthSessionDto> LoginAsync(LoginAccount input, CancellationToken ct)
    {
        await loginValidation.ValidateAsync(input, ct);
        var account = await store.LoginAsync(input, ct);
        var refresh = await refreshTokens.IssueAsync(account.Id, issuer.RefreshLifetime, ct);
        return MapToSessionDto(issuer.Issue(account), refresh);
    }

    public async Task<AuthSessionDto> LoginWithGoogleAsync(GoogleLogin input, CancellationToken ct)
    {
        await googleValidation.ValidateAsync(input, ct);

        var payload = await googleValidator.ValidateAsync(input.IdToken, ct)
            ?? throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "INVALID_GOOGLE_TOKEN",
                "Google authentication token is invalid or expired.");

        if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Email))
        {
            throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "UNVERIFIED_GOOGLE_EMAIL",
                "Google email is not verified.");
        }

        var account = await store.LoginWithGoogleAsync(payload, ct);
        var refresh = await refreshTokens.IssueAsync(account.Id, issuer.RefreshLifetime, ct);
        return MapToSessionDto(issuer.Issue(account), refresh);
    }

    public async Task<AuthSessionDto> RefreshAsync(RefreshSession input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.RefreshToken) || input.RefreshToken.Length > 256)
        {
            throw InvalidRefreshToken();
        }

        var rotation = await refreshTokens.RotateAsync(input.RefreshToken, issuer.RefreshLifetime, ct)
            ?? throw InvalidRefreshToken();

        var account = await store.GetAsync(rotation.UserId, ct);
        if (account is null)
        {
            await refreshTokens.RevokeAllAsync(rotation.UserId, ct);
            throw InvalidRefreshToken();
        }

        return MapToSessionDto(issuer.Issue(account), rotation.Token);
    }

    public async Task ForgotPasswordAsync(ForgotPassword input, CancellationToken ct)
    {
        await forgotValidation.ValidateAsync(input, ct);

        // Same outcome whether or not the email exists, so callers cannot probe for accounts.
        var request = await store.CreatePasswordResetAsync(input.Email, ct);
        if (request is not null)
        {
            await emailSender.SendPasswordResetAsync(request, ct);
        }
    }

    public async Task ResetPasswordAsync(ResetPassword input, CancellationToken ct)
    {
        await resetValidation.ValidateAsync(input, ct);
        var userId = await store.ResetPasswordAsync(input, ct);
        await refreshTokens.RevokeAllAsync(userId, ct);
    }

    public async Task ConfirmEmailAsync(ConfirmEmail input, CancellationToken ct)
    {
        await confirmValidation.ValidateAsync(input, ct);
        await store.ConfirmEmailAsync(input, ct);
    }

    public async Task ResendConfirmationAsync(ResendConfirmation input, CancellationToken ct)
    {
        await resendValidation.ValidateAsync(input, ct);

        // Same outcome whether or not the email exists, so callers cannot probe for accounts.
        var confirmation = await store.CreateEmailConfirmationAsync(input.Email, ct);
        if (confirmation is not null)
        {
            await emailSender.SendEmailConfirmationAsync(confirmation, ct);
        }
    }

    public async Task ChangePasswordAsync(ChangePassword input, CancellationToken ct)
    {
        await changeValidation.ValidateAsync(input, ct);
        var userId = GetCurrentUserId();
        await store.ChangePasswordAsync(userId, input, ct);
        await refreshTokens.RevokeAllAsync(userId, ct);
    }

    public async Task<AccountDto> MeAsync(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var account = await store.GetAsync(userId, ct);

        if (account is null)
        {
            throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "SESSION_REVOKED",
                "Sign in again.");
        }

        return MapToDto(account);
    }

    public async Task<AccountDto> UpdateProfileAsync(UpdateProfile input, CancellationToken ct)
    {
        await updateProfileValidation.ValidateAsync(input, ct);
        var userId = GetCurrentUserId();
        var account = await store.UpdateProfileAsync(userId, input, ct);
        return MapToDto(account);
    }

    public Task LogoutAsync(CancellationToken ct) => LogoutAsync(null, null, ct);

    public async Task LogoutAsync(string? bearerToken, string? refreshToken, CancellationToken ct)
    {
        Guid userId;
        if (current.UserId is { } authenticatedId && authenticatedId != Guid.Empty)
        {
            userId = authenticatedId;
        }
        else if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var principal = issuer.ValidateIgnoringLifetime(bearerToken)
                ?? throw new ApplicationProblemException(
                    ProblemKind.Unauthenticated,
                    "INVALID_ACCESS_TOKEN",
                    "Access token is invalid.");

            var account = await store.GetAsync(principal.UserId, ct);
            if (account is null || !string.Equals(account.SessionStamp, principal.SessionStamp, StringComparison.Ordinal))
            {
                throw new ApplicationProblemException(
                    ProblemKind.Unauthenticated,
                    "SESSION_REVOKED",
                    "Sign in again.");
            }

            userId = account.Id;
        }
        else if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var tokenUserId = await refreshTokens.FindUserIdByActiveTokenAsync(refreshToken, ct)
                ?? throw InvalidRefreshToken();

            var account = await store.GetAsync(tokenUserId, ct)
                ?? throw InvalidRefreshToken();

            userId = account.Id;
        }
        else
        {
            throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "AUTHENTICATION_REQUIRED",
                "Sign in first.");
        }

        await store.RevokeSessionsAsync(userId, ct);
        await refreshTokens.RevokeAllAsync(userId, ct);
    }

    public async Task DeleteAccountAsync(DeleteAccount input, CancellationToken ct)
    {
        await deleteValidation.ValidateAsync(input, ct);
        var userId = GetCurrentUserId();

        GoogleUserPayload? googlePayload = null;
        if (!string.IsNullOrWhiteSpace(input.GoogleIdToken))
        {
            googlePayload = await googleValidator.ValidateAsync(input.GoogleIdToken, ct)
                ?? throw new ApplicationProblemException(
                    ProblemKind.Unauthenticated,
                    "INVALID_GOOGLE_TOKEN",
                    "Google authentication token is invalid or expired.");

            if (!googlePayload.EmailVerified || string.IsNullOrWhiteSpace(googlePayload.Email))
            {
                throw new ApplicationProblemException(
                    ProblemKind.Unauthenticated,
                    "UNVERIFIED_GOOGLE_EMAIL",
                    "Google email is not verified.");
            }
        }

        await store.DeleteAccountAsync(userId, input, googlePayload, ct);
        await refreshTokens.RevokeAllAsync(userId, ct);
    }

    private Guid GetCurrentUserId()
    {
        return current.UserId ?? throw new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "AUTHENTICATION_REQUIRED",
            "Sign in first.");
    }

    private static ApplicationProblemException InvalidRefreshToken()
    {
        return new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "INVALID_REFRESH_TOKEN",
            "Sign in again.");
    }

    private static AuthSessionDto MapToSessionDto(AccessToken accessToken, IssuedRefreshToken refreshToken)
    {
        return new AuthSessionDto(
            new TokenDto(accessToken.AccessTokenValue, accessToken.ExpiresIn),
            refreshToken);
    }

    private static AccountDto MapToDto(AccountIdentity account)
    {
        return new AccountDto(
            account.Id,
            account.DisplayName,
            account.Email,
            account.Role,
            account.EmailConfirmed,
            account.Phone,
            account.AvatarUrl);
    }
}
