using System.Net;
using System.Text;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class InvitationMessagingTests
{
    [Theory]
    [InlineData("{{GuestName}} {{RsvpLink}} {{QrLink}}", true)]
    [InlineData("{{GuestName}} {{RsvpLink}}", false)]
    [InlineData("{{GuestName}} {{RsvpLink}} {{QrLink}} {{Unknown}}", false)]
    public void Invitation_template_requires_only_supported_personalization_tokens(string template, bool valid) =>
        Assert.Equal(valid, InvitationMessageTemplate.IsValid(template));

    [Fact]
    public void Invitation_template_renders_guest_specific_values()
    {
        var rendered = InvitationMessageTemplate.Render("Hi {{GuestName}} {{RsvpLink}} {{QrLink}}", "Linh",
            "https://invite.test/a", "https://api.test/qr/a");

        Assert.Equal("Hi Linh https://invite.test/a https://api.test/qr/a", rendered);
    }

    [Theory]
    [InlineData("Chào {{GuestName}}, RSVP {{RsvpLink}}", true)]
    [InlineData("Chào {{GuestName}}, RSVP {{RsvpLink}} QR {{QrLink}}", false)]
    [InlineData("Chào {{GuestName}}, RSVP {{RsvpLink}} {{Unknown}}", false)]
    public void Sms_template_requires_guest_and_rsvp_but_rejects_qr(string template, bool valid) =>
        Assert.Equal(valid, InvitationMessageTemplate.IsValidSms(template));

    [Fact]
    public void Sms_template_respects_three_concatenated_unicode_message_limit()
    {
        Assert.True(InvitationMessageTemplate.IsValidSms(InvitationMessageTemplate.DefaultSms()));
        Assert.True(InvitationMessageTemplate.IsSmsLengthSupported(new string('a', InvitationMessageTemplate.MaxSmsCodeUnits)));
        Assert.False(InvitationMessageTemplate.IsSmsLengthSupported(new string('a', InvitationMessageTemplate.MaxSmsCodeUnits + 1)));
    }

    [Fact]
    public async Task SpeedSms_sends_one_recipient_with_basic_token_and_returns_transaction_id()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"success\",\"code\":\"00\",\"data\":{\"tranId\":12345}}", Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        {
            AccessToken = "secret-token", Sender = "INVITEME", SmsType = 3
        }));

        var receipt = await provider.SendAsync(new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS",
            "+84901234567", "Invite", "Please RSVP", "https://invite.test/a", "https://api.test/qr/a"), default);

        Assert.True(provider.IsConfigured);
        Assert.Equal("12345", receipt.ProviderMessageId);
        Assert.Equal("https://api.speedsms.vn/index.php/sms/send", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Basic", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token:x", Encoding.UTF8.GetString(Convert.FromBase64String(handler.Request.Headers.Authorization.Parameter!)));
        Assert.Contains("\\u002B84901234567", handler.Body);
        Assert.Contains("INVITEME", handler.Body);
    }

    [Fact]
    public async Task SpeedSms_rejects_provider_level_error_without_exposing_provider_text()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"error\",\"code\":\"300\",\"message\":\"private provider detail\"}", Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        {
            AccessToken = "secret-token", Sender = "INVITEME"
        }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "bad-number", "Invite", "Hi", "https://invite.test/a", "https://api.test/qr/a"), default));

        Assert.Equal("SPEEDSMS_INSUFFICIENT_BALANCE", error.Code);
        Assert.DoesNotContain("private provider detail", error.Message);
    }

    [Fact]
    public async Task SpeedSms_maps_invalid_phone_to_a_safe_actionable_error()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"error\",\"code\":105,\"message\":\"invalid phone\"}", Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        {
            AccessToken = "secret-token", SmsType = 2
        }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "bad-number", "Invite", "Hi", "https://invite.test/a", "https://api.test/qr/a"), default));

        Assert.Equal("SPEEDSMS_INVALID_PHONE", error.Code);
        Assert.Equal("105", error.ProviderCode);
        Assert.DoesNotContain("invalid phone", error.Message);
    }

    [Theory]
    [InlineData("999", "999")]
    [InlineData("0388506852", null)]
    [InlineData("private provider detail", null)]
    public async Task SpeedSms_preserves_only_short_numeric_unknown_codes_for_diagnostics(string code, string? expected)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            { status = "error", code, message = "private provider detail" }), Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        { AccessToken = "secret-token" }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "test-recipient", "Invite", "Hi", "https://invite.test/a", ""), default));

        Assert.Equal("SPEEDSMS_REQUEST_REJECTED", error.Code);
        Assert.Equal(expected, error.ProviderCode);
        Assert.DoesNotContain("private provider detail", error.Message);
    }

    [Fact]
    public void SpeedSms_cskh_type_does_not_require_a_brand_sender_but_brandname_type_does()
    {
        var cskh = new SpeedSmsOptions { AccessToken = "token", SmsType = 2, Sender = "" };
        var brandnameWithoutApproval = new SpeedSmsOptions { AccessToken = "token", SmsType = 3, Sender = "" };
        var approvedBrandname = new SpeedSmsOptions { AccessToken = "token", SmsType = 3, Sender = "APPROVED" };

        Assert.True(cskh.IsConfigured);
        Assert.False(brandnameWithoutApproval.IsConfigured);
        Assert.True(approvedBrandname.IsConfigured);
    }

    [Fact]
    public async Task SpeedSms_missing_code_keeps_reason_in_diagnostic_and_redacts_sensitive_values()
    {
        const string reason = "SMS type is not enabled: secret-token 0388506852 someone@example.com https://invite.test/token";
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            { status = "error", message = reason }), Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        { AccessToken = "secret-token" }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "0388506852", "Invite", "Hello guest",
                "https://invite.test/token", ""), default));

        Assert.Equal("SPEEDSMS_REQUEST_REJECTED", error.Code);
        Assert.Null(error.ProviderCode);
        Assert.Contains("code=<missing>", error.ProviderDiagnostic);
        Assert.Contains("SMS type is not enabled", error.ProviderDiagnostic);
        foreach (var sensitive in new[] { "secret-token", "0388506852", "someone@example.com", "https://invite.test/token" })
            Assert.DoesNotContain(sensitive, error.ProviderDiagnostic);
        Assert.DoesNotContain(reason, error.Message);
    }

    [Theory]
    [InlineData("sender not found")]
    [InlineData(" Sender Not Found ")]
    public async Task SpeedSms_live_error_without_code_maps_missing_sender_to_permanent_configuration_failure(string message)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            { status = "error", message }), Encoding.UTF8, "application/json")
        });
        var provider = new SpeedSmsChannelProvider(new HttpClient(handler), Options.Create(new SpeedSmsOptions
        { AccessToken = "test-token", SmsType = 2, Sender = "" }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "test-recipient", "Invite", "Hello guest",
                "https://invite.test/token", ""), default));

        Assert.Equal("SPEEDSMS_SENDER_NOT_FOUND", error.Code);
        Assert.False(error.Transient);
        Assert.Null(error.ProviderCode);
    }

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
