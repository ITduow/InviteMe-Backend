using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class GoogleLoginValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<your-google-client-id>")]
    public async Task GoogleTokenValidatorRejectsUnconfiguredOrPlaceholderClientId(string? clientId)
    {
        var options = Options.Create(new GoogleAuthOptions { ClientId = clientId });
        var validator = new GoogleTokenValidator(options, NullLogger<GoogleTokenValidator>.Instance);

        var result = await validator.ValidateAsync("any-token", default);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GoogleLoginRejectsEmptyToken(string idToken)
    {
        var validation = new RequestValidation<GoogleLogin>([new GoogleLoginValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(idToken), default));
    }

    [Fact]
    public async Task GoogleLoginRejectsExcessivelyLongToken()
    {
        var validation = new RequestValidation<GoogleLogin>([new GoogleLoginValidator()]);
        var longToken = new string('a', 4097);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(longToken), default));
    }

    [Fact]
    public async Task GoogleLoginAcceptsValidToken()
    {
        var validation = new RequestValidation<GoogleLogin>([new GoogleLoginValidator()]);
        await validation.ValidateAsync(new("valid-google-id-token"), default);
    }

    [Fact]
    public async Task HandlerRejectsInvalidGoogleToken()
    {
        var (handler, _) = CreateHandler(new MockGoogleTokenValidator(null));

        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(
            () => handler.LoginWithGoogleAsync(new("invalid-token"), default));

        Assert.Equal("INVALID_GOOGLE_TOKEN", ex.Code);
        Assert.Equal(ProblemKind.Unauthenticated, ex.Kind);
    }

    [Fact]
    public async Task HandlerRejectsUnverifiedEmail()
    {
        var payload = new GoogleUserPayload("sub-123", "user@example.test", false, "Test User", null);
        var (handler, _) = CreateHandler(new MockGoogleTokenValidator(payload));

        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(
            () => handler.LoginWithGoogleAsync(new("token-unverified"), default));

        Assert.Equal("UNVERIFIED_GOOGLE_EMAIL", ex.Code);
        Assert.Equal(ProblemKind.Unauthenticated, ex.Kind);
    }

    [Fact]
    public async Task HandlerRejectsBlankEmail()
    {
        var payload = new GoogleUserPayload("sub-123", "", true, "Test User", null);
        var (handler, _) = CreateHandler(new MockGoogleTokenValidator(payload));

        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(
            () => handler.LoginWithGoogleAsync(new("token-blank-email"), default));

        Assert.Equal("UNVERIFIED_GOOGLE_EMAIL", ex.Code);
        Assert.Equal(ProblemKind.Unauthenticated, ex.Kind);
    }

    [Fact]
    public async Task HandlerSuccessfullyLogsInAndIssuesTokens()
    {
        var payload = new GoogleUserPayload("sub-123", "googleuser@example.test", true, "Google User", "https://avatar.test/pic.png");
        var (handler, store) = CreateHandler(new MockGoogleTokenValidator(payload));

        var result = await handler.LoginWithGoogleAsync(new("valid-token"), default);

        Assert.NotNull(result);
        Assert.Equal("issued-access-token", result.AccessToken);
        Assert.Equal("issued-refresh-token", result.RefreshToken);
        Assert.True(store.GoogleLoginCalled);
    }

    private static (AccountHandler Handler, FakeAccountStore Store) CreateHandler(IGoogleTokenValidator validator)
    {
        var store = new FakeAccountStore();
        var refreshTokens = new FakeRefreshTokenStore();
        var issuer = new FakeAccessTokenIssuer();
        var current = new FakeCurrentUser();
        var googleValidation = new RequestValidation<GoogleLogin>([new GoogleLoginValidator()]);

        var handler = new AccountHandler(
            store,
            refreshTokens,
            issuer,
            current,
            new RequestValidation<RegisterAccount>([]),
            new RequestValidation<LoginAccount>([]),
            new RequestValidation<RefreshSession>([]),
            new FakeEmailSender(),
            new RequestValidation<ForgotPassword>([]),
            new RequestValidation<ResetPassword>([]),
            new RequestValidation<ChangePassword>([]),
            validator,
            googleValidation);

        return (handler, store);
    }

    private sealed class MockGoogleTokenValidator(GoogleUserPayload? payload) : IGoogleTokenValidator
    {
        public Task<GoogleUserPayload?> ValidateAsync(string idToken, CancellationToken ct) => Task.FromResult(payload);
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public bool GoogleLoginCalled { get; private set; }

        public Task<AccountIdentity> LoginWithGoogleAsync(GoogleUserPayload payload, CancellationToken ct)
        {
            GoogleLoginCalled = true;
            return Task.FromResult(new AccountIdentity(Guid.NewGuid(), payload.Name ?? "User", payload.Email, "USER", "stamp"));
        }

        public Task<AccountIdentity> RegisterAsync(RegisterAccount input, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountIdentity> LoginAsync(LoginAccount input, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task RevokeSessionsAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<PasswordResetRequest?> CreatePasswordResetAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> ResetPasswordAsync(ResetPassword input, CancellationToken ct) => throw new NotImplementedException();
        public Task ChangePasswordAsync(Guid userId, ChangePassword input, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeRefreshTokenStore : IRefreshTokenStore
    {
        public Task<IssuedRefreshToken> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken ct)
            => Task.FromResult(new IssuedRefreshToken("issued-refresh-token", (int)lifetime.TotalSeconds));
        public Task<RefreshRotation?> RotateAsync(string refreshToken, TimeSpan lifetime, CancellationToken ct) => throw new NotImplementedException();
        public Task RevokeAllAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
    {
        public TimeSpan RefreshLifetime => TimeSpan.FromDays(7);
        public AccessToken Issue(AccountIdentity account) => new("issued-access-token", 900);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserId => Guid.NewGuid();
        public string? Email => "user@example.test";
        public string? Role => "USER";
        public bool IsAdmin => false;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeEmailSender : IAccountEmailSender
    {
        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct) => Task.CompletedTask;
    }
}
