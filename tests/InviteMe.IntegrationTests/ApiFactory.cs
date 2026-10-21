using System.Collections.Concurrent;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly byte[] SigningKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    public const string DefaultSessionStamp = "test-session-stamp";
    private readonly ConcurrentDictionary<Guid, AccountIdentity> _profiles = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1",
            ["Jwt:Issuer"] = "InviteMe.Tests",
            ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(SigningKey),
            ["Frontend:Url"] = "http://localhost:5173", ["Database:ApplyMigrationsOnStartup"] = "false"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountStore>();
            services.AddScoped<IAccountStore>(_ => new TestAccountStore(_profiles));
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
    });

    private sealed class TestAccountStore(ConcurrentDictionary<Guid, AccountIdentity> profiles) : IAccountStore
    {
        public Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct)
        {
            var identity = profiles.GetOrAdd(id,
                uid => new AccountIdentity(uid, "Test User", "test@example.com", "USER", DefaultSessionStamp, true));
            return Task.FromResult<AccountIdentity?>(identity);
        }

        public Task<AccountIdentity> UpdateProfileAsync(Guid userId, UpdateProfile input, CancellationToken ct)
        {
            var updated = new AccountIdentity(
                userId,
                input.DisplayName.Trim(),
                "test@example.com",
                "USER",
                DefaultSessionStamp,
                true,
                string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim(),
                string.IsNullOrWhiteSpace(input.AvatarUrl) ? null : input.AvatarUrl.Trim());
            profiles[userId] = updated;
            return Task.FromResult(updated);
        }

        public Task<AccountIdentity> RegisterAsync(RegisterAccount input, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountIdentity> LoginAsync(LoginAccount input, CancellationToken ct) => throw new NotImplementedException();
        public Task<AccountIdentity> LoginWithGoogleAsync(GoogleUserPayload payload, CancellationToken ct) => throw new NotImplementedException();
        public Task RevokeSessionsAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public Task<PasswordResetRequest?> CreatePasswordResetAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<Guid> ResetPasswordAsync(ResetPassword input, CancellationToken ct) => throw new NotImplementedException();
        public Task ChangePasswordAsync(Guid userId, ChangePassword input, CancellationToken ct) => throw new NotImplementedException();
        public Task<EmailConfirmationRequest?> CreateEmailConfirmationAsync(string email, CancellationToken ct) => Task.FromResult<EmailConfirmationRequest?>(null);
        public Task ConfirmEmailAsync(ConfirmEmail input, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAccountAsync(Guid userId, DeleteAccount input, GoogleUserPayload? googlePayload, CancellationToken ct) => Task.CompletedTask;
    }
}
