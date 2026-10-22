using InviteMe.Domain.AI;

namespace InviteMe.UnitTests;

public sealed class ThankYouMessageTests
{
    [Fact]
    public void Content_requires_exact_supported_placeholders_and_plain_text()
    {
        Assert.True(ThankYouMessageContent.IsValid("Cảm ơn {{GuestName}}!", false));
        Assert.True(ThankYouMessageContent.IsValid("Cảm ơn {{GuestName}} vì {{GiftAmount}}!", true));
        Assert.False(ThankYouMessageContent.IsValid("Cảm ơn {{GuestName}} và {{GuestName}}", false));
        Assert.False(ThankYouMessageContent.IsValid("Cảm ơn {{GuestName}} {{GiftAmount}}", false));
        Assert.False(ThankYouMessageContent.IsValid("<script>{{GuestName}}</script>", false));
        Assert.False(ThankYouMessageContent.IsValid("{{GuestName}} {{Other}}", false));
    }

    [Fact]
    public void Render_uses_paid_gift_amount_only_when_template_contains_opt_in_token()
    {
        Assert.Equal("Cảm ơn Lan!", ThankYouMessageContent.Render("Cảm ơn {{GuestName}}!", "Lan", null));
        Assert.Equal("Cảm ơn Lan vì 1.000.000đ!", ThankYouMessageContent.Render("Cảm ơn {{GuestName}} vì {{GiftAmount}}!", "Lan", 1_000_000));
        Assert.Throws<ArgumentException>(() => ThankYouMessageContent.Render("Cảm ơn {{GuestName}} vì {{GiftAmount}}!", "Lan", null));
    }

    [Fact]
    public void Message_requires_explicit_paid_amount_and_approval_locks_editing()
    {
        var weddingId = Guid.NewGuid(); var guestId = Guid.NewGuid(); var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => ThankYouMessage.Create(weddingId, guestId, actor, "WARM",
            "Cảm ơn {{GuestName}} vì {{GiftAmount}}!", true, null, "model", false, now));
        var draft = ThankYouMessage.Create(weddingId, guestId, actor, "WARM", "Cảm ơn {{GuestName}}!", false, null, "model", true, now);
        draft.Approve(actor, now);
        Assert.Equal("APPROVED", draft.Status);
        Assert.Throws<InvalidOperationException>(() => draft.Edit("Cảm ơn {{GuestName}} thêm lần nữa!", false, null, now));
    }

    [Fact]
    public void Send_job_claim_is_lease_guarded_and_retries_transient_failures()
    {
        var job = ThankYouSendJob.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "EMAIL",
            "guest@example.test", "Cảm ơn", DateTimeOffset.UtcNow);
        var lease = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        Assert.True(job.Claim(lease, now, TimeSpan.FromMinutes(1)));
        Assert.False(job.MarkSent(Guid.NewGuid(), null, now));
        Assert.True(job.MarkFailed(lease, "SMTP_DELIVERY_FAILED", true, now));
        Assert.Equal(ThankYouSendJob.Pending, job.Status);
        Assert.True(job.Claim(lease, now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        Assert.True(job.MarkSent(lease, "provider-id", now.AddMinutes(1)));
        Assert.Equal(ThankYouSendJob.Sent, job.Status);
    }
}
