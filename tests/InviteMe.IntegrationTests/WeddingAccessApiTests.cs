using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.IntegrationTests;

public sealed class WeddingAccessApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("cohost", HttpStatusCode.OK)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("outsider", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    public async Task ReturnsOnlyCurrentUsersEffectivePermissions(string scenario, HttpStatusCode expected)
    {
        var userId = Guid.NewGuid();
        var weddingId = Guid.NewGuid();
        var reader = new Reader(scenario == "missing" ? null : new WeddingAccess(
            scenario == "owner" ? userId : Guid.NewGuid(),
            scenario == "cohost",
            new HashSet<WeddingPermission> { WeddingPermission.GUEST_VIEW }));
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWeddingAccessReader>();
            services.AddSingleton<IWeddingAccessReader>(reader);
        }));
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        if (scenario != "anonymous")
        {
            var token = new JwtSecurityToken("InviteMe.Tests", "InviteMe.Tests.Web",
                [new Claim("sub", userId.ToString()), new Claim("role", "USER"),
                 new Claim("session_stamp", ApiFactory.DefaultSessionStamp)],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(ApiFactory.SigningKey),
                    SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                new JwtSecurityTokenHandler().WriteToken(token));
        }
        var response = await client.GetAsync($"/api/weddings/{weddingId}/access");
        Assert.Equal(expected, response.StatusCode);
        if (scenario == "anonymous") Assert.Null(reader.LastRequest);
        else Assert.Equal((weddingId, userId), reader.LastRequest);
        if (expected != HttpStatusCode.OK) return;

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(weddingId, body.GetProperty("weddingId").GetGuid());
        var permissions = body.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (scenario == "owner")
            Assert.Equal(Enum.GetValues<WeddingPermission>().Select(x => x.ToString()), permissions);
        else Assert.Equal(["WEDDING_VIEW", "GUEST_VIEW"], permissions); // WEDDING_VIEW comes with an active membership
    }

    private sealed class Reader(WeddingAccess? access) : IWeddingAccessReader
    {
        public (Guid, Guid)? LastRequest { get; private set; }
        public Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken ct)
        {
            LastRequest = (weddingId, userId);
            return Task.FromResult(access);
        }
    }
}
