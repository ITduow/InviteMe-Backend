using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InviteMe.Application.Features.System.GetApplicationHealth;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.IntegrationTests;

public sealed class HttpFoundationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DevelopmentSwaggerAndOpenApiAreAccessibleWithoutSigningIn()
    {
        using var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task ApplicationSliceAndLivenessWorkWithoutDatabase()
    {
        using var client = factory.CreateHttpsClient();
        var health = await client.GetFromJsonAsync<ApplicationHealthResponse>("/api/health/application");
        Assert.Equal("InviteMe", health!.Application);
        Assert.Equal("ok", health.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task ReadinessReturnsUnavailableWhenDatabaseIsOffline()
    {
        using var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task AnonymousHubNegotiationReturnsProblemDetailsAndCorrelationId()
    {
        using var client = factory.CreateHttpsClient();
        var response = await client.PostAsync("/hubs/weddings/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHENTICATION_REQUIRED", problem.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), problem.GetProperty("traceId").GetString());
    }

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("issuer", HttpStatusCode.Unauthorized)]
    [InlineData("audience", HttpStatusCode.Unauthorized)]
    [InlineData("signature", HttpStatusCode.Unauthorized)]
    [InlineData("subject", HttpStatusCode.Unauthorized)]
    public async Task HubNegotiationValidatesJwt(string scenario, HttpStatusCode expected)
    {
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(scenario));
        var response = await client.PostAsync("/hubs/weddings/negotiate?negotiateVersion=1", null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task QueryTokenIsNotAcceptedOnOrdinaryHttpRequests()
    {
        using var client = factory.CreateHttpsClient();
        var response = await client.PostAsync("/hubs/weddings/negotiate?access_token=" + CreateToken("valid"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiContainsExampleAndBearerScheme()
    {
        using var client = factory.CreateHttpsClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/health/application", out _));
        Assert.Equal("bearer", document.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());
    }

    private static string CreateToken(string scenario)
    {
        var key = scenario == "signature" ? new byte[32] : ApiFactory.SigningKey;
        var token = new JwtSecurityToken(
            issuer: scenario == "issuer" ? "wrong" : "InviteMe.Tests",
            audience: scenario == "audience" ? "wrong" : "InviteMe.Tests.Web",
            claims: [new Claim("sub", scenario == "subject" ? "not-a-uuid" : Guid.NewGuid().ToString()), new Claim("role", "USER")],
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: scenario == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
