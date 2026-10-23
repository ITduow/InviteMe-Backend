namespace InviteMe.Application.Ports.Authentication;

public sealed record AccountIdentity(
    Guid Id,
    string DisplayName,
    string Email,
    string Role,
    string SessionStamp,
    bool EmailConfirmed = false,
    string? Phone = null,
    string? AvatarUrl = null);

public sealed record RegisterAccount(
    string Email,
    string Password,
    string DisplayName);

public sealed record LoginAccount(
    string Email,
    string Password);

public sealed record RefreshSession(string RefreshToken);

public sealed record ForgotPassword(string Email);

public sealed record ResetPassword(
    string Email,
    string Code,
    string NewPassword);

public sealed record ConfirmEmail(
    string Email,
    string Code);

public sealed record ResendConfirmation(
    string Email);

public sealed record ChangePassword(
    string CurrentPassword,
    string NewPassword);

public sealed record UpdateProfile(
    string DisplayName,
    string? Phone = null,
    string? AvatarUrl = null);

public sealed record DeleteAccount(
    string? Password = null,
    string? GoogleIdToken = null);

public sealed record PasswordResetRequest(
    Guid UserId,
    string Email,
    string DisplayName,
    string Code);

public sealed record EmailConfirmationRequest(
    Guid UserId,
    string Email,
    string DisplayName,
    string Code);

public sealed record AccessToken(
    string AccessTokenValue,
    int ExpiresIn);

public sealed record IssuedRefreshToken(
    string Value,
    int ExpiresIn);

public sealed record RefreshRotation(
    Guid UserId,
    IssuedRefreshToken Token);

public sealed record GoogleLogin(string IdToken);

public sealed record GoogleUserPayload(
    string Subject,
    string Email,
    bool EmailVerified,
    string? Name,
    string? Picture);

public interface IGoogleTokenValidator
{
    Task<GoogleUserPayload?> ValidateAsync(string idToken, CancellationToken ct);
}

public interface IAccountStore
{
    Task<AccountIdentity> RegisterAsync(RegisterAccount input, CancellationToken ct);
    Task<AccountIdentity> LoginAsync(LoginAccount input, CancellationToken ct);
    Task<AccountIdentity> LoginAdminAsync(LoginAccount input, CancellationToken ct);
    Task<AccountIdentity> LoginWithGoogleAsync(GoogleUserPayload payload, CancellationToken ct);
    Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct);
    Task<AccountIdentity> UpdateProfileAsync(Guid userId, UpdateProfile input, CancellationToken ct);
    Task RevokeSessionsAsync(Guid id, CancellationToken ct);
    Task<PasswordResetRequest?> CreatePasswordResetAsync(string email, CancellationToken ct);
    Task<Guid> ResetPasswordAsync(ResetPassword input, CancellationToken ct);
    Task ChangePasswordAsync(Guid userId, ChangePassword input, CancellationToken ct);
    Task<EmailConfirmationRequest?> CreateEmailConfirmationAsync(string email, CancellationToken ct);
    Task ConfirmEmailAsync(ConfirmEmail input, CancellationToken ct);
    Task DeleteAccountAsync(Guid userId, DeleteAccount input, GoogleUserPayload? googlePayload, CancellationToken ct);
}

public interface IAccountEmailSender
{
    Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct);
    Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct);
}

public sealed record AccessTokenPrincipal(
    Guid UserId,
    string SessionStamp);

public interface IRefreshTokenStore
{
    Task<IssuedRefreshToken> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken ct);
    Task<RefreshRotation?> RotateAsync(string refreshToken, TimeSpan lifetime, CancellationToken ct);
    Task<Guid?> FindUserIdByActiveTokenAsync(string refreshToken, CancellationToken ct);
    Task RevokeAllAsync(Guid userId, CancellationToken ct);
}

public interface IAccessTokenIssuer
{
    TimeSpan RefreshLifetime { get; }
    AccessToken Issue(AccountIdentity account);
    AccessTokenPrincipal? ValidateIgnoringLifetime(string token);
}
