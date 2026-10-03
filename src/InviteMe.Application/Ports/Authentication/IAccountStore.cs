namespace InviteMe.Application.Ports.Authentication;

public sealed record AccountIdentity(Guid Id, string DisplayName, string Email, string Role, string SessionStamp);
public sealed record RegisterAccount(string Email, string Password, string DisplayName);
public sealed record LoginAccount(string Email, string Password);
public sealed record AccessToken(string AccessTokenValue, int ExpiresIn);

public interface IAccountStore
{
    Task<AccountIdentity> RegisterAsync(RegisterAccount input, CancellationToken ct);
    Task<AccountIdentity> LoginAsync(LoginAccount input, CancellationToken ct);
    Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct);
    Task RevokeSessionsAsync(Guid id, CancellationToken ct);
}

public interface IAccessTokenIssuer
{
    AccessToken Issue(AccountIdentity account);
}
