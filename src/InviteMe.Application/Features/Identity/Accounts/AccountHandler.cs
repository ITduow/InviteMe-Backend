using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Identity.Accounts;

public sealed record AccountDto(Guid Id, string DisplayName, string Email, string Role);
public sealed record TokenDto(string AccessToken, int ExpiresIn);

public sealed class RegisterAccountValidator : AbstractValidator<RegisterAccount>
{
    public RegisterAccountValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
        RuleFor(x => x.DisplayName).NotEmpty().Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(150);
    }
}

public sealed class LoginAccountValidator : AbstractValidator<LoginAccount>
{
    public LoginAccountValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class AccountHandler(IAccountStore store, IAccessTokenIssuer issuer, ICurrentUser current,
    RequestValidation<RegisterAccount> registerValidation, RequestValidation<LoginAccount> loginValidation)
{
    public async Task<AccountDto> RegisterAsync(RegisterAccount input, CancellationToken ct)
    {
        await registerValidation.ValidateAsync(input, ct);
        return Map(await store.RegisterAsync(input, ct));
    }
    public async Task<TokenDto> LoginAsync(LoginAccount input, CancellationToken ct)
    {
        await loginValidation.ValidateAsync(input, ct);
        var token = issuer.Issue(await store.LoginAsync(input, ct));
        return new(token.AccessTokenValue, token.ExpiresIn);
    }
    public async Task<AccountDto> MeAsync(CancellationToken ct) => Map(await store.GetAsync(UserId(), ct)
        ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in again."));
    public Task LogoutAsync(CancellationToken ct) => store.RevokeSessionsAsync(UserId(), ct);
    private Guid UserId() => current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
    private static AccountDto Map(AccountIdentity account) => new(account.Id, account.DisplayName, account.Email, account.Role);
}
