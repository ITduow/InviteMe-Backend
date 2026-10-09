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
    int ExpiresIn);

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

public sealed class AccountHandler(
    IAccountStore store,
    IAccessTokenIssuer issuer,
    ICurrentUser current,
    RequestValidation<RegisterAccount> registerValidation,
    RequestValidation<LoginAccount> loginValidation)
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
        var token = issuer.Issue(account);
        return new TokenDto(token.AccessTokenValue, token.ExpiresIn);
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
    }

    private Guid GetCurrentUserId()
    {
        return current.UserId ?? throw new ApplicationProblemException(
            ProblemKind.Unauthenticated,
            "SESSION_REQUIRED",
            "Sign in first.");
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
