using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using InviteMe.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class AuthCookieSecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("login")]
    [InlineData("google")]
    [InlineData("refresh")]
    [InlineData("logout")]
    public async Task UntrustedOriginsCannotMutateAuthCookies(string endpoint)
    {
        foreach (var origin in new string?[] { null, "null", "https://evil.test", "http://localhost:5173.evil.test", "http://localhost:5173/path", "http://localhost:5174" })
        {
            using var client = factory.CreateHttpsClient();
            client.DefaultRequestHeaders.Remove("Origin");
            if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
            client.DefaultRequestHeaders.Add("Cookie", "refresh_token=existing-session");
            var response = await client.PostAsJsonAsync("/api/auth/" + endpoint,
                new { email = "user@example.test", password = "ValidPassword123!", idToken = "test" });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("CSRF_ORIGIN_DENIED", problem.GetProperty("code").GetString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task TrustedOriginReachesHandlerAndCorsAllowsCredentials()
    {
        using var client = factory.CreateHttpsClient();
        var response = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("INVALID_REFRESH_TOKEN", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task CorsPreflightRemainsAvailable()
    {
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/refresh");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task MultipleOriginsAreRejectedWithoutClearingCookie()
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add("Origin", "https://evil.test");
        var response = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("CSRF_ORIGIN_DENIED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("true", "None", true)]
    [InlineData("false", "Lax", false)]
    public async Task CookieDeletionUsesConfiguredAttributes(string secure, string sameSite, bool expectSecure)
    {
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:RefreshCookie:Secure"] = secure,
                ["Authentication:RefreshCookie:SameSite"] = sameSite
            })));
        using var client = configured.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var response = await client.PostAsync("/api/auth/refresh", null);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("path=/api/auth", cookie);
        Assert.Contains("samesite=" + sameSite.ToLowerInvariant(), cookie);
        Assert.Equal(expectSecure, cookie.Contains("; secure"));
    }

    [Theory]
    [InlineData("Development", "false", "None")]
    [InlineData("Production", "false", "Lax")]
    [InlineData("Development", "true", "Unspecified")]
    public void InvalidCookieConfigurationFailsValidation(string environment, string secure, string sameSite)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:RefreshCookie:Secure"] = secure,
            ["Authentication:RefreshCookie:SameSite"] = sameSite,
            ["Frontend:Url"] = "http://localhost:5173"
        }).Build();
        // Use the real application registrations via the test host.
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddConfiguration(configuration));
        });
        Assert.ThrowsAny<OptionsValidationException>(() => configured.Services.GetRequiredService<IOptions<RefreshCookieOptions>>().Value);
    }

}
