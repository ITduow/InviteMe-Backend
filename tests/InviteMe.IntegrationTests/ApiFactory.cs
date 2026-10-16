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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1",
            ["Jwt:Issuer"] = "InviteMe.Tests",
            ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(SigningKey),
            ["Frontend:Url"] = "http://localhost:5173", ["Invitations:DispatchEnabled"] = "false"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountStore>();
            services.AddScoped<IAccountStore, TestAccountStore>();
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private sealed class TestAccountStore : IAccountStore
    {
        public Task<AccountIdentity?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<AccountIdentity?>(new AccountIdentity(id, "Test User", "test@example.com", "USER", DefaultSessionStamp));

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
