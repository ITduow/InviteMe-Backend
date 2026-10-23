using System.Net;
using System.Collections.Concurrent;
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

public sealed class EmailConfirmationApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RegistrationSendsConfirmationEmailAndMeReflectsConfirmedState() => WithApi(async (sender, client) =>
    {
        var email = "register-confirm@example.test";
        var regResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Confirm User"));
        Assert.Equal(HttpStatusCode.Created, regResponse.StatusCode);
        await WaitForConfirmationsAsync(sender, 1);

        // Confirmation email should be sent with a non-empty signed token
        var sent = Assert.Single(sender.ConfirmationsSent);
        Assert.Equal(email, sent.Email);
        Assert.False(string.IsNullOrWhiteSpace(sent.Code));
        Assert.True(sent.Code.Length > 20);

        // Login before confirmation is rejected with 403 Forbidden (EMAIL_NOT_CONFIRMED)
        var loginBeforeResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.Forbidden, loginBeforeResponse.StatusCode);

        // Confirm email with the token
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, sent.Code));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        // Login after confirmation succeeds and /api/auth/me reflects EmailConfirmed = true
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var token = (await loginResponse.Content.ReadFromJsonAsync<TokenDto>())!;

        using var meAfterReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meAfterReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var meAfter = await client.SendAsync(meAfterReq);
        Assert.Equal(HttpStatusCode.OK, meAfter.StatusCode);
        var accountAfter = (await meAfter.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.True(accountAfter.EmailConfirmed);

        // Idempotent: calling confirm again with the same token or when already confirmed returns NoContent
        var reConfirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, sent.Code));
        Assert.Equal(HttpStatusCode.NoContent, reConfirmResponse.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WrongCodeOrUnknownEmailIsRejected() => WithApi(async (sender, client) =>
    {
        var email = "wrong-code@example.test";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Wrong User"));
        await WaitForConfirmationsAsync(sender, 1);
        var sent = Assert.Single(sender.ConfirmationsSent);

        // Wrong token
        var wrongCode = sent.Code + "-tampered";
        var badResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, wrongCode));
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        // Unknown email
        var unknownResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail("nobody@example.test", sent.Code));
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ResendConfirmationSendsNewCodeAndIgnoresUnknownOrConfirmedAccounts() => WithApi(async (sender, client) =>
    {
        // Unknown email returns Accepted without sending email
        var unknownResend = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation("nobody@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, unknownResend.StatusCode);
        Assert.Empty(sender.ConfirmationsSent);

        // Register user
        var email = "resend-user@example.test";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Resend User"));
        await WaitForConfirmationsAsync(sender, 1);
        Assert.Single(sender.ConfirmationsSent);

        // Resend confirmation invalidates the previous token
        var firstCode = sender.ConfirmationsSent.First().Code;
        var resendResponse = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation(email));
        Assert.Equal(HttpStatusCode.Accepted, resendResponse.StatusCode);
        await WaitForConfirmationsAsync(sender, 2);
        Assert.Equal(2, sender.ConfirmationsSent.Count);

        var oldTokenAttempt = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, firstCode));
        Assert.Equal(HttpStatusCode.BadRequest, oldTokenAttempt.StatusCode);

        var latestCode = Uri.EscapeDataString(sender.ConfirmationsSent.Last().Code);
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, latestCode));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        // Once confirmed, further resend requests do not send email
        var afterConfirmedResend = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation(email));
        Assert.Equal(HttpStatusCode.Accepted, afterConfirmedResend.StatusCode);
        Assert.Equal(2, sender.ConfirmationsSent.Count);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExpiredConfirmationTokenIsRejectedAfterWaitingForLifespan() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new CapturingEmailSender();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!, testAsyncConfirmation: true);

        // Verify default configured lifespan is 24 hours
        var defaultLifespan = baseFactory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.DataProtectionTokenProviderOptions>>()
            .Value.TokenLifespan;
        Assert.Equal(TimeSpan.FromHours(24), defaultLifespan);

        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
            services.Configure<Microsoft.AspNetCore.Identity.DataProtectionTokenProviderOptions>(options =>
                options.TokenLifespan = TimeSpan.FromSeconds(1));
        }));

        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var email = "expired-confirm@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Expired User"))).StatusCode);
        await WaitForConfirmationsAsync(sender, 1);
        var sent = Assert.Single(sender.ConfirmationsSent);

        // Wait in real time for the token to expire
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var expiredConfirm = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, sent.Code));
        Assert.Equal(HttpStatusCode.BadRequest, expiredConfirm.StatusCode);

        // Account remains unconfirmed and cannot log in
        var loginAttempt = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.Forbidden, loginAttempt.StatusCode);
    });

    private static Task WithApi(Func<CapturingEmailSender, HttpClient, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new CapturingEmailSender();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!, testAsyncConfirmation: true);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await action(sender, client);
    });

    private static async Task WaitForConfirmationsAsync(CapturingEmailSender sender, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (sender.ConfirmationsSent.Count < count && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(sender.ConfirmationsSent.Count >= count, "Confirmation worker did not deliver the queued email.");
    }

    private sealed class CapturingEmailSender : IAccountEmailSender
    {
        public List<PasswordResetRequest> ResetSent { get; } = [];
        public ConcurrentQueue<EmailConfirmationRequest> ConfirmationsSent { get; } = new();

        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
        {
            ResetSent.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct)
        {
            ConfirmationsSent.Enqueue(request);
            return Task.CompletedTask;
        }
    }
}
