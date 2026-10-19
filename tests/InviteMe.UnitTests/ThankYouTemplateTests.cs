using System.Net;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Ports.AI;
using InviteMe.Domain.AI;
using InviteMe.Infrastructure.AI;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class ThankYouTemplateTests
{
    [Theory]
    [InlineData("FORMAL", true)]
    [InlineData("WARM", true)]
    [InlineData("FRIENDLY", true)]
    [InlineData("warm", false)]
    [InlineData("OTHER", false)]
    public async Task GenerationValidatesTone(string tone, bool valid) =>
        Assert.Equal(valid, (await new GenerateThankYouTemplateValidator().ValidateAsync(new GenerateThankYouTemplate(tone))).IsValid);

    [Fact]
    public async Task GenerationRejectsUnsupportedLanguageAndLongInstructions()
    {
        var validator = new GenerateThankYouTemplateValidator();
        Assert.False((await validator.ValidateAsync(new GenerateThankYouTemplate(Language: "en"))).IsValid);
        Assert.False((await validator.ValidateAsync(new GenerateThankYouTemplate(AdditionalInstructions: new string('x', 501)))).IsValid);
    }

    [Theory]
    [InlineData("Nguyễn Văn Minh")]
    [InlineData("Cô Mai & Chú Nam")]
    [InlineData("O'Connor")]
    public void RenderingReplacesEveryOccurrenceWithoutChangingOriginal(string name)
    {
        const string template = "{{GuestName}}, cảm ơn {{GuestName}}!";
        var renderer = new ThankYouTemplateRenderer();
        Assert.Equal($"{name}, cảm ơn {name}!", renderer.Render(template, name));
        Assert.Equal("{{GuestName}}, cảm ơn {{GuestName}}!", template);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{{GuestName}}")]
    [InlineData("<script>bad</script>")]
    [InlineData("Tên\nkhác")]
    public void RenderingRejectsInvalidGuestNames(string name) =>
        Assert.Throws<ApplicationProblemException>(() => new ThankYouTemplateRenderer().Render("Cảm ơn {{GuestName}}", name));

    [Theory]
    [InlineData("Cảm ơn bạn")]
    [InlineData("Cảm ơn {{guestName}}")]
    [InlineData("{{GuestName}} {{Another}}")]
    [InlineData("<b>{{GuestName}}</b>")]
    public async Task MissingMalformedOrUnresolvedPlaceholderIsRejected(string content)
    {
        Assert.False(ThankYouContent.IsValid(content));
        Assert.False((await new UpdateThankYouTemplateValidator().ValidateAsync(new UpdateThankYouTemplate(content, 1))).IsValid);
        Assert.Throws<ApplicationProblemException>(() => new ThankYouTemplateRenderer().Render(content, "Minh"));
    }

    [Theory]
    [InlineData("FORMAL")]
    [InlineData("WARM")]
    [InlineData("FRIENDLY")]
    public void DeterministicFallbackIsValidAndPreservesPlaceholder(string tone) =>
        Assert.True(ThankYouGenerationPolicy.IsUsable(ThankYouGenerationPolicy.Fallback(tone)));

    [Fact]
    public void ApprovedContentCannotBeEditedOrApprovedAgain()
    {
        var template = ThankYouTemplate.Create(Guid.NewGuid(), Guid.NewGuid(), "Cảm ơn {{GuestName}}", "WARM", "gpt-5-nano", 15, 20, false, DateTimeOffset.UtcNow);
        template.Edit("Xin cảm ơn {{GuestName}}", DateTimeOffset.UtcNow);
        Assert.Equal(2, template.Version);
        var owner = Guid.NewGuid(); template.Approve(owner, DateTimeOffset.UtcNow);
        Assert.Equal("APPROVED", template.Status); Assert.Equal(owner, template.ApprovedBy); Assert.Equal(3, template.Version);
        Assert.Throws<InvalidOperationException>(() => template.Edit("{{GuestName}}", DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => template.Approve(owner, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ThankYouTemplate.Create(Guid.NewGuid(), owner, "missing", "WARM", "gpt-5-nano", null, null, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ResponsesAdapterUsesOneSupportedRequestAndMapsUsage()
    {
        string? body = null;
        var transport = new FakeTransport(async (request, ct) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
            body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Response(200, GoodResponse);
        });
        var result = await Generator(transport).GenerateAsync(new("WARM", "vi", null), default);
        Assert.Equal(1, transport.Calls); Assert.Equal("Xin cảm ơn {{GuestName}}", result.Text);
        Assert.Equal(123L, result.InputTokens); Assert.Equal(45L, result.OutputTokens);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("gpt-5-nano", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, json.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(json.RootElement.GetProperty("store").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        Assert.Contains("{{GuestName}}", json.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task InvitationPurposeUsesSeparateEmailAndSmsPrompt()
    {
        string? instructions = null;
        var responseBody = """
            {"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"subject\":\"Mời dự cưới\",\"emailContent\":\"{{GuestName}} {{RsvpLink}} {{QrLink}}\",\"smsContent\":\"{{GuestName}} {{RsvpLink}}\"}"}]}]}
            """;
        var transport = new FakeTransport(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            instructions = payload.RootElement.GetProperty("instructions").GetString();
            return Response(200, responseBody);
        });

        var result = await Generator(transport).GenerateAsync(new("WARM", "vi", null, "WEDDING_INVITATION"), default);

        Assert.Contains("emailContent", instructions);
        Assert.Contains("smsContent", instructions);
        Assert.Contains("never include QR", instructions);
        Assert.Contains("\"smsContent\":", result.Text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"completed\",\"output\":[]}")]
    [InlineData("{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("not-json")]
    public async Task EmptyOrUnexpectedProviderOutputIsNotFakeSuccess(string body)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(Response(200, body)));
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Generator(transport).GenerateAsync(new("WARM", "vi", null), default));
        Assert.Equal("AI_INVALID_RESPONSE", error.Code); Assert.Equal(ProblemKind.BadGateway, error.Kind); Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task InvalidProviderRequestsAndCredentialsAreNeverRetried(int status)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(Response(status, "secret-provider-details")));
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Generator(transport).GenerateAsync(new("WARM", "vi", null), default));
        Assert.Equal(1, transport.Calls); Assert.Equal("AI_PROVIDER_REJECTED", error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task TransientProviderFailureIsRetriedWithBoundedAttempts()
    {
        var calls = 0;
        var transport = new FakeTransport((_, _) => Task.FromResult(++calls == 1 ? Response(503, "unavailable") : Response(200, GoodResponse)));
        await Generator(transport).GenerateAsync(new("WARM", "vi", null), default);
        Assert.Equal(2, transport.Calls);
        var limited = new FakeTransport((_, _) => Task.FromResult(Response(429, "limit")));
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Generator(limited).GenerateAsync(new("WARM", "vi", null), default));
        Assert.Equal(3, limited.Calls); Assert.Equal(ProblemKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task ProviderTimeoutAndCallerCancellationAreDistinct()
    {
        var transport = new FakeTransport((_, _) => throw new TaskCanceledException());
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Generator(transport).GenerateAsync(new("WARM", "vi", null), default));
        Assert.Equal(ProblemKind.Timeout, error.Kind); Assert.Equal(3, transport.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator(transport).GenerateAsync(new("WARM", "vi", null), cancelled.Token));
    }

    [Fact]
    public async Task MissingApiKeyDoesNotCallProvider()
    {
        var transport = new FakeTransport((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(transport);
        var generator = new OpenAiTextGenerator(client, Options.Create(new OpenAiOptions()));
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => generator.GenerateAsync(new("WARM", "vi", null), default));
        Assert.Equal("AI_NOT_CONFIGURED", error.Code); Assert.Equal(0, transport.Calls);
    }

    private const string GoodResponse = """
        {"status":"completed","model":"gpt-5-nano","output":[{"type":"reasoning"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Xin cảm ơn {{GuestName}}"}]}],"usage":{"input_tokens":123,"output_tokens":45}}
        """;
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static OpenAiTextGenerator Generator(FakeTransport transport) => new(new HttpClient(transport), Options.Create(new OpenAiOptions { ApiKey = "test-not-a-real-key" }));
    private sealed class FakeTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
