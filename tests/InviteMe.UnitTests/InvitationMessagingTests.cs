using System.Net;
using System.Net.Sockets;
using System.Text;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class InvitationMessagingTests
{
    [Theory]
    [InlineData("Chúng mình mong được đón bạn trong ngày vui.", true)]
    [InlineData("Thân gửi {{GuestName}}. RSVP {{RsvpLink}}", false)]
    [InlineData("Hãy xem link bên dưới.", false)]
    [InlineData("QR: {{QrLink}}", false)]
    [InlineData("<b>Thân mời</b>", false)]
    [InlineData("https://invite.example/token", false)]
    public void Invitation_copy_must_be_plain_natural_copy_without_technical_tokens(string content, bool valid) =>
        Assert.Equal(valid, InvitationEmailRenderer.IsSafeGeneratedCopy(content));

    [Fact]
    public void Invitation_subject_names_the_couple_and_the_guest()
    {
        Assert.Equal("Thiệp mời — Minh & Lan mời Huỳnh Tấn Dương",
            InvitationMessageTemplate.PersonalizeSubject("Thiệp mời {{GuestName}}", "Huỳnh Tấn Dương", "Minh & Lan"));
        Assert.Equal("Mời dự cưới — Minh & Lan mời Huỳnh Tấn Dương",
            InvitationMessageTemplate.PersonalizeSubject("Mời dự cưới", "Huỳnh Tấn Dương", "Minh & Lan"));
        Assert.DoesNotContain("\r", InvitationMessageTemplate.PersonalizeSubject("Thiệp mời\r\nBcc: attacker@example.com", "An", "Minh & Lan"));
        Assert.DoesNotContain("\n", InvitationMessageTemplate.PersonalizeSubject("Thiệp mời\r\nBcc: attacker@example.com", "An", "Minh & Lan"));
    }

    [Fact]
    public void Email_renderer_builds_branded_personal_email_and_uses_only_a_secondary_qr_link()
    {
        var weddingId = Guid.NewGuid();
        var cover = $"/media/wedding-covers/{weddingId:N}/{new string('a', 48)}.webp";
        var rendered = InvitationEmailRenderer.Render(weddingId, "Minh & Lan", "Huỳnh Tấn Dương",
            "Chúng mình rất mong được đón bạn đến chung vui.", new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
            "Nhà hàng Hoa Sen", "12 Đường Hoa", "Asia/Ho_Chi_Minh", cover, "https://api.inviteme.asia",
            "https://www.inviteme.asia/invite/private-token", "https://api.inviteme.asia/api/guest-invitations/private-token/qr?format=png");

        var visibleHtml = WebUtility.HtmlDecode(rendered.Html);
        Assert.Contains("Thân gửi Huỳnh Tấn Dương", visibleHtml);
        Assert.Contains("Minh &amp; Lan", rendered.Html);
        Assert.Contains("Nhà hàng Hoa Sen", visibleHtml);
        Assert.Contains("Xác nhận tham dự", visibleHtml);
        Assert.Contains("Mở mã QR cá nhân", visibleHtml);
        Assert.DoesNotContain("cid:invitation-qr", rendered.Html);
        Assert.DoesNotContain("width=\"220\"", rendered.Html);
        Assert.Contains("https://api.inviteme.asia/media/wedding-covers/", rendered.Html);
        Assert.Contains("https://www.inviteme.asia/invite/private-token", rendered.Html);
        Assert.Contains("Thời gian:", rendered.PlainText);
        Assert.Equal(rendered.ImageUrl, InvitationEmailRenderer.ResolvePublicImageUrl(weddingId, cover, "https://api.inviteme.asia"));
    }

    [Fact]
    public void Missing_or_non_public_cover_uses_image_free_email_and_invalid_rsvp_is_not_rendered_as_a_link()
    {
        var weddingId = Guid.NewGuid();
        var imagePath = $"/media/wedding-covers/{weddingId:N}/{new string('b', 48)}.jpg";
        var missing = InvitationEmailRenderer.Render(weddingId, "Minh & Lan", "An", "Mời bạn đến chung vui.", null,
            null, null, "Asia/Ho_Chi_Minh", imagePath, "http://localhost:5000", "javascript:alert(1)", "not-a-url");
        var wrongWedding = InvitationEmailRenderer.ResolvePublicImageUrl(Guid.NewGuid(), imagePath, "https://api.inviteme.asia");

        Assert.Null(missing.ImageUrl);
        Assert.DoesNotContain("<img", missing.Html);
        Assert.DoesNotContain("href=\"javascript:", missing.Html);
        Assert.DoesNotContain("href=\"not-a-url", missing.Html);
        Assert.False(InvitationEmailRenderer.IsValidRsvpLink("http://public.example/invite/token"));
        Assert.True(InvitationEmailRenderer.IsValidRsvpLink("http://localhost:3000/invite/token"));
        Assert.Null(wrongWedding);
    }

    [Fact]
    public void Email_renderer_escapes_guest_copy_and_venue_before_writing_html()
    {
        var email = InvitationEmailRenderer.Render(Guid.NewGuid(), "Minh & Lan", "<img src=x onerror=alert(1)>",
            "Lời mời & niềm vui", null, "<script>alert(1)</script>", null, "Asia/Ho_Chi_Minh", null,
            "https://api.inviteme.asia", "https://invite.inviteme.asia/guest/token", "");

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", email.Html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", email.Html);
        Assert.DoesNotContain("<script>alert(1)</script>", email.Html);
        Assert.DoesNotContain("onerror=alert(1)>", email.Html);
    }

    [Fact]
    public void Legacy_draft_tokens_and_qr_text_are_removed_without_guessing_a_guest_honorific()
    {
        var clean = InvitationMessageTemplate.CleanLegacyCopy(
            "{{GuestName}} thân mến, chúng mình mong bạn đến. RSVP tại {{RsvpLink}}. QR thiệp: {{QrLink}}");


        var sms = InvitationMessageTemplate.RenderSms("{{GuestName}} thân mến, nhớ đến chung vui nhé! RSVP tại {{RsvpLink}}",
            "Nguyễn An", "https://www.inviteme.asia/invite/token");
        Assert.StartsWith("Chào Nguyễn An,", sms);
        Assert.EndsWith(" RSVP: https://www.inviteme.asia/invite/token", sms);
        Assert.DoesNotContain("<", sms);
        Assert.DoesNotContain("QR", sms, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chúng mình mong bạn đến", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{", clean);
        Assert.DoesNotContain("QR", clean, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Email_sender_uses_the_reviewed_html_and_text_snapshot_verbatim()
    {
        const string html = "<!doctype html><html><body>Reviewed invitation</body></html>";
        const string plain = "Reviewed invitation text";
        var message = new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "EMAIL", "guest@example.com",
            "Minh & Lan mời An", "copy", "https://www.inviteme.asia/invite/token", "", EmailHtmlContent: html,
            EmailPlainTextContent: plain);

        Assert.Equal(html, InvitationEmailFormatter.BuildHtml(message));
        Assert.Equal(plain, InvitationEmailFormatter.BuildPlainText(message));
    }

    [Fact]
    public async Task Smtp_provider_sends_the_saved_html_snapshot_and_plain_text_alternative()
    {
        await using var smtp = new LoopbackSmtpServer();
        var provider = new SmtpInvitationChannelProvider(Options.Create(new SmtpOptions
        {
            Host = "127.0.0.1", Port = smtp.Port, EnableSsl = false, From = "sender@example.test"
        }));
        const string html = "<!doctype html><html><body>Exact reviewed email snapshot</body></html>";
        const string plain = "Exact reviewed plain-text snapshot";

        var receipt = await provider.SendAsync(new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "EMAIL",
            "guest@example.test", "Reviewed invitation subject", "copy", "https://www.inviteme.asia/invite/token", "",
            EmailHtmlContent: html, EmailPlainTextContent: plain), default);

        var mime = await smtp.Message;
        Assert.Null(receipt.ProviderMessageId);
        Assert.Contains("Subject: Reviewed invitation subject", mime);
        Assert.Contains("Content-Type: text/plain", mime);
        Assert.Contains("Content-Type: text/html", mime);
        var foldedMime = mime.Replace("\r\n", "", StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(html)), foldedMime);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)), foldedMime);
    }

    [Fact]
    public void Sms_template_respects_three_concatenated_unicode_message_limit()
    {
        Assert.True(InvitationEmailRenderer.IsSafeGeneratedCopy(InvitationMessageTemplate.DefaultSms()));
        Assert.True(InvitationMessageTemplate.DefaultSms().Length <= InvitationMessageTemplate.MaxSmsCopyCodeUnits);
        Assert.True(InvitationMessageTemplate.IsSmsLengthSupported(new string('a', InvitationMessageTemplate.MaxSmsCodeUnits)));
        Assert.False(InvitationMessageTemplate.IsSmsLengthSupported(new string('a', InvitationMessageTemplate.MaxSmsCodeUnits + 1)));
    }

    [Fact]
    public void Sms_shortens_existing_long_copy_and_keeps_the_entire_personal_rsvp_link()
    {
        const string guest = "Huỳnh Tấn Dương";
        var rsvp = "https://www.inviteme.asia/invite/" + new string('a', 64);
        var sms = InvitationMessageTemplate.RenderSms(
            "Chúng tôi rất hạnh phúc chia sẻ ngày đặc biệt của mình và mong được đón bạn đến tham dự lễ cưới.", guest, rsvp);

        Assert.StartsWith($"Chào {guest}, ", sms);
        Assert.EndsWith($" RSVP: {rsvp}", sms);
        Assert.True(InvitationMessageTemplate.IsSmsLengthSupported(sms));
        var body = sms[($"Chào {guest}, ".Length)..^($" RSVP: {rsvp}".Length)];
        Assert.True(body.Length <= InvitationMessageTemplate.MaxSmsCopyCodeUnits);
        Assert.EndsWith("…", body);
        Assert.Equal(1, sms.Split(rsvp, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Sms_budget_includes_the_actual_guest_name_and_link_and_preserves_unicode_text_elements()
    {
        const string guest = "Nguyễn An";
        const string emoji = "👩‍❤️‍👩";
        var rsvp = "https://invite.test/" + new string('b', 140);
        var copy = string.Concat(Enumerable.Repeat(emoji, 12));
        var sms = InvitationMessageTemplate.RenderSms(copy, guest, rsvp);
        var body = sms[($"Chào {guest}, ".Length)..^($" RSVP: {rsvp}".Length)];

        Assert.True(InvitationMessageTemplate.IsSmsLengthSupported(sms));
        Assert.EndsWith($" RSVP: {rsvp}", sms);
        Assert.EndsWith("…", body);
        Assert.Equal("", body[..^1].Replace(emoji, "", StringComparison.Ordinal));
    }

    [Fact]
    public void Sms_short_copy_is_sent_without_rewriting_or_shortening_the_rsvp_url()
    {
        const string copy = "Mời bạn dự lễ cưới cùng chúng mình.";
        const string rsvp = "http://localhost:3000/invite/";
        Assert.Equal($"Chào An, {copy} RSVP: {rsvp}", InvitationMessageTemplate.RenderSms(copy, "An", rsvp));
    }

    [Fact]
    public void Sms_keeps_the_over_limit_guard_when_guest_metadata_cannot_fit()
    {
        var rsvp = "https://www.inviteme.asia/invite/" + new string('a', 64);
        var sms = InvitationMessageTemplate.RenderSms(InvitationMessageTemplate.DefaultSms(), new string('N', 210), rsvp);
        Assert.False(InvitationMessageTemplate.IsSmsLengthSupported(sms));
        Assert.EndsWith($" RSVP: {rsvp}", sms);
    }

    [Fact]
    public async Task Textbee_sends_one_e164_recipient_and_returns_accepted_batch()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"success\":true,\"smsBatchId\":\"batch-123\",\"recipientCount\":1}}", Encoding.UTF8, "application/json")
        });
        var provider = new TextbeeSmsChannelProvider(new HttpClient(handler), Options.Create(new TextbeeOptions
        {
            ApiKey = "secret-token", DeviceId = "device-1", WebhookSecret = new string('s', 24)
        }));

        var receipt = await provider.SendAsync(new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS",
            "0901234567", "Invite", "Please RSVP", "https://invite.test/a", ""), default);

        Assert.True(provider.IsConfigured);
        Assert.Equal("batch-123", receipt.ProviderMessageId);
        Assert.Equal("Textbee", receipt.ProviderName);
        Assert.Equal("ACCEPTED", receipt.Status);
        Assert.Equal("https://api.textbee.dev/api/v1/gateway/send-sms", handler.Request!.RequestUri!.ToString());
        Assert.Equal("secret-token", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Contains("+84901234567", handler.Body);
        Assert.Contains("device-1", handler.Body);
    }

    [Fact]
    public async Task Textbee_rejects_provider_request_without_exposing_provider_text()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"success\":false,\"error\":\"private provider detail\"}}", Encoding.UTF8, "application/json")
        });
        var provider = new TextbeeSmsChannelProvider(new HttpClient(handler), Options.Create(new TextbeeOptions
        {
            ApiKey = "secret-token", DeviceId = "device-1", WebhookSecret = new string('s', 24)
        }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "0901234567", "Invite", "Hi", "https://invite.test/a", ""), default));

        Assert.Equal("TEXTBEE_OUTCOME_UNKNOWN", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.DoesNotContain("private provider detail", error.Message);
    }

    [Fact]
    public async Task Textbee_maps_invalid_phone_to_a_safe_actionable_error()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new TextbeeSmsChannelProvider(new HttpClient(handler), Options.Create(new TextbeeOptions
        {
            ApiKey = "secret-token", DeviceId = "device-1", WebhookSecret = new string('s', 24)
        }));

        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "not a phone", "Invite", "Hi", "https://invite.test/a", ""), default));

        Assert.Equal("SMS_PHONE_INVALID", error.Code);
        Assert.Null(handler.Request);
    }

    [Theory]
    [InlineData("0901234567", "+84901234567")]
    [InlineData("84901234567", "+84901234567")]
    [InlineData("+84901234567", "+84901234567")]
    public void Vietnamese_phone_numbers_are_normalized_to_e164(string input, string expected)
    {
        Assert.Equal(expected, VietnamesePhoneNumber.ToE164(input));
    }

    [Fact]
    public void Sms_segment_counter_accounts_for_gsm_extensions_and_unicode_parts()
    {
        Assert.Equal(1, SmsSegmentCounter.CountSegments(new string('a', 160)));
        Assert.Equal(2, SmsSegmentCounter.CountSegments(new string('a', 161)));
        Assert.Equal(2, SmsSegmentCounter.CountSegments(new string('^', 81)));
        Assert.Equal(2, SmsSegmentCounter.CountSegments(new string('ế', 71)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "TEXTBEE_AUTH_FAILED")]
    [InlineData(HttpStatusCode.Forbidden, "TEXTBEE_ACCESS_DENIED")]
    [InlineData(HttpStatusCode.NotFound, "TEXTBEE_DEVICE_NOT_FOUND")]
    [InlineData(HttpStatusCode.TooManyRequests, "TEXTBEE_RATE_LIMITED")]
    [InlineData(HttpStatusCode.BadRequest, "TEXTBEE_REQUEST_REJECTED")]
    public async Task Textbee_maps_provider_statuses_to_safe_error_codes(HttpStatusCode status, string code)
    {
        var handler = new CaptureHandler(new HttpResponseMessage(status));
        var provider = new TextbeeSmsChannelProvider(new HttpClient(handler),
            Options.Create(new TextbeeOptions { ApiKey = "test-token", DeviceId = "device-1", WebhookSecret = new string('s', 24) }));
        var error = await Assert.ThrowsAsync<InvitationDeliveryException>(() => provider.SendAsync(
            new InvitationOutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "SMS", "0901234567",
                "Invite", "Hello", "https://invite.test/a", ""), default));
        Assert.Equal(code, error.Code);
        Assert.False(error.OutcomeUnknown);
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

    private sealed class LoopbackSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;

        public LoopbackSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        public int Port { get; }
        public Task<string> Message => _message.Task;

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost InviteMe test SMTP");
            var message = new StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 SIZE 10000000");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
                    while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".") message.AppendLine(dataLine);
                    _message.TrySetResult(message.ToString());
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 goodbye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("250 ok");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _serve.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
