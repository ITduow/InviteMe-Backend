using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Identity.Accounts;

public sealed record AccountDto(
    Guid Id,
    string DisplayName,
    string Email,
    string Role);

public sealed record TokenDto(
    string AccessToken,
    int ExpiresIn,
    string RefreshToken,
    int RefreshExpiresIn);

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
            .Matches("^[0-9]{6}$");

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(12)
            .MaximumLength(128);
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
    IGoogleTokenValidator googleValidator,
    RequestValidation<GoogleLogin> googleValidation)
{
    public async Task<AccountDto> RegisterAsync(RegisterAccount input, CancellationToken ct)
    {
        await registerValidation.ValidateAsync(input, ct);
        var account = await store.RegisterAsync(input, ct);
        return MapToDto(account);
    }

    public async Task<TokenDto> LoginAsync(LoginAccount input, CancellationToken ct)
    {
        await loginValidation.ValidateAsync(input, ct);
        var account = await store.LoginAsync(input, ct);
        var refresh = await refreshTokens.IssueAsync(account.Id, issuer.RefreshLifetime, ct);
        return MapToTokenDto(issuer.Issue(account), refresh);
    }

    public async Task<TokenDto> LoginWithGoogleAsync(GoogleLogin input, CancellationToken ct)
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
        return MapToTokenDto(issuer.Issue(account), refresh);
    }

    public async Task<TokenDto> RefreshAsync(RefreshSession input, CancellationToken ct)
    {
        await refreshValidation.ValidateAsync(input, ct);
        var rotation = await refreshTokens.RotateAsync(input.RefreshToken, issuer.RefreshLifetime, ct)
            ?? throw InvalidRefreshToken();

        var account = await store.GetAsync(rotation.UserId, ct);
        if (account is null)
        {
            await refreshTokens.RevokeAllAsync(rotation.UserId, ct);
            throw InvalidRefreshToken();
        }

        return MapToTokenDto(issuer.Issue(account), rotation.Token);
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

    public async Task<AccountDto> MeAsync(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var account = await store.GetAsync(userId, ct);

        if (account is null)
        {
            throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "SESSION_REQUIRED",
                "Sign in again.");
        }

        return MapToDto(account);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        await store.RevokeSessionsAsync(userId, ct);
        await refreshTokens.RevokeAllAsync(userId, ct);
    }

    private Guid GetCurrentUserId()
    {
        return current.UserId ?? throw new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "SESSION_REQUIRED",
            "Sign in first.");
    }

    private static ApplicationProblemException InvalidRefreshToken()
    {
        return new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "INVALID_REFRESH_TOKEN",
            "Sign in again.");
    }

    private static TokenDto MapToTokenDto(AccessToken accessToken, IssuedRefreshToken refreshToken)
    {
        return new TokenDto(
            accessToken.AccessTokenValue,
            accessToken.ExpiresIn,
            refreshToken.Value,
            refreshToken.ExpiresIn);
    }

    private static AccountDto MapToDto(AccountIdentity account)
    {
        return new AccountDto(
            account.Id,
            account.DisplayName,
            account.Email,
            account.Role);
    }
}
