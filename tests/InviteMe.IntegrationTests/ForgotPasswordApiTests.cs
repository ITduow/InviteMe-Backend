using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class ForgotPasswordApiTests
{
    private const string Password = "Test-only-Pass123!";
    private const string NewPassword = "Brand-new-Pass456!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UnknownEmailGetsTheSameAcceptedAnswerAndNoEmailIsSent() => WithApi(async (sender, client) =>
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("nobody@example.test"))).StatusCode);
        Assert.Empty(sender.Sent);

        await Register(sender, client, "known");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("known@example.test"))).StatusCode);
        Assert.Single(sender.Sent);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ResetWithTheEmailedCodeChangesThePasswordAndEndsOldSessions() => WithApi(async (sender, client) =>
    {
        var (session, refreshCookie) = await RegisterWithCookie(sender, client, "reset");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("reset@example.test"));
        var code = sender.Sent.Single().Code;

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("reset@example.test", code, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        AuthRefreshApiTests.AssertCookieDeleted(reset);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("reset@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("reset@example.test", NewPassword))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthRefreshApiTests.SendRefresh(client, refreshCookie)).StatusCode);
        using var old = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        old.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(old)).StatusCode);

        // A reset code works only once.
        var reuse = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("reset@example.test", code, "Another-Pass789!"));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WrongCodeUnknownEmailAndWeakPasswordAreRejected() => WithApi(async (sender, client) =>
    {
        await Register(sender, client, "reject");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("reject@example.test"));
        var code = sender.Sent.Single().Code;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("reject@example.test", code + "-tampered", NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("nobody@example.test", code, NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("reject@example.test", code, "short"))).StatusCode);

        // None of the failures changed the password.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("reject@example.test", Password))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RepeatedWrongCodesLockTheResetEvenForTheCorrectCode() => WithApi(async (sender, client) =>
    {
        await Register(sender, client, "brute");
        await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("brute@example.test"));
        var code = sender.Sent.Single().Code;
        var wrong = code + "-tampered";

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("brute@example.test", wrong, NewPassword))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("brute@example.test", code, NewPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("brute@example.test", NewPassword))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExpiredResetTokenIsRejectedAfterWaitingForLifespan() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new CapturingEmailSender();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);

        // Verify default configured lifespan for PasswordReset is 15 minutes
        var defaultLifespan = baseFactory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<InviteMe.Infrastructure.PasswordResetTokenProviderOptions>>()
            .Value.TokenLifespan;
        Assert.Equal(TimeSpan.FromMinutes(15), defaultLifespan);

        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
            services.Configure<InviteMe.Infrastructure.PasswordResetTokenProviderOptions>(options =>
                options.TokenLifespan = TimeSpan.FromSeconds(1));
        }));

        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await Register(sender, client, "expiredreset");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("expiredreset@example.test"))).StatusCode);
        var code = sender.Sent.Single().Code;

        // Wait in real time for the password reset token to expire
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var expiredReset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPassword("expiredreset@example.test", code, NewPassword));
        Assert.Equal(HttpStatusCode.BadRequest, expiredReset.StatusCode);

        // Old password still works; new password does not
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("expiredreset@example.test", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("expiredreset@example.test", NewPassword))).StatusCode);
    });

    private static Task WithApi(Func<CapturingEmailSender, HttpClient, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new CapturingEmailSender();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        await action(sender, client);
    });

    private static async Task<TokenDto> Register(CapturingEmailSender sender, HttpClient client, string name) =>
        (await RegisterWithCookie(sender, client, name)).Token;

    private static async Task<(TokenDto Token, string RefreshCookie)> RegisterWithCookie(CapturingEmailSender sender, HttpClient client, string name)
    {
        var email = $"{name}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
        var confirmCode = sender.ConfirmationsSent.Last(x => x.Email == email).Code;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, confirmCode))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return ((await login.Content.ReadFromJsonAsync<TokenDto>())!, AuthRefreshApiTests.ExtractRefreshCookie(login));
    }

    private sealed class CapturingEmailSender : IAccountEmailSender
    {
        public List<PasswordResetRequest> Sent { get; } = [];
        public List<EmailConfirmationRequest> ConfirmationsSent { get; } = [];

        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct)
        {
            ConfirmationsSent.Add(request);
            return Task.CompletedTask;
        }
    }
}
