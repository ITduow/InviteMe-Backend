using System.Security.Cryptography;
using System.Text;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Invitations;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Notifications;

public sealed class TextbeeWebhookSignatureVerifier(IOptions<TextbeeOptions> options) : ITextbeeWebhookSignatureVerifier
{
    public bool Verify(string rawBody, string? signature)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature)) return false;
        var supplied = signature.Trim();
        if (supplied.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) supplied = supplied[7..];
        if (supplied.Length != 64) return false;
        byte[] provided;
        try { provided = Convert.FromHexString(supplied); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

public sealed class TextbeeDeliveryStore(InviteMeDbContext db, TimeProvider clock) : ITextbeeDeliveryStore
{
    public async Task<bool> ApplyStatusAsync(string smsBatchId, string recipient, string status, string? errorCode,
        DateTimeOffset? providerOccurredAt, DateTimeOffset? sentAt, DateTimeOffset? deliveredAt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invitation = await db.InvitationSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.invitation_send_jobs
            WHERE channel = 'SMS' AND provider_name = 'TEXTBEE' AND provider_message_id = {smsBatchId} AND recipient = {recipient}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (invitation is not null)
        {
            var now = clock.GetUtcNow();
            if (!CanApply(invitation.Status, status) ||
                (providerOccurredAt is { } occurred && invitation.ProviderStatusUpdatedAt is { } previous && occurred < previous))
            { await tx.CommitAsync(ct); return true; }
            invitation.Status = status;
            invitation.ProviderStatusUpdatedAt = status == "UNKNOWN" ? providerOccurredAt : providerOccurredAt ?? now;
            if (status == "SENT") invitation.SentAt = sentAt ?? providerOccurredAt ?? now;
            if (status == "DELIVERED") { invitation.SentAt ??= sentAt ?? now; invitation.DeliveredAt = deliveredAt ?? providerOccurredAt ?? now; }
            invitation.LastErrorCode = status == "FAILED" ? errorCode ?? "TEXTBEE_SEND_FAILED" : null;
            db.AuditLogs.Add(AuditLog.ForEntity(invitation.WeddingId, "invitation_send_jobs", invitation.Id,
                "INVITATION_SMS_PROVIDER_STATUS_UPDATED", null, metadata: System.Text.Json.JsonSerializer.Serialize(new { status, provider = "TEXTBEE" })));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
        }

        var thankYou = await db.ThankYouSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.thank_you_send_jobs
            WHERE channel = 'SMS' AND provider_name = 'TEXTBEE' AND provider_message_id = {smsBatchId} AND recipient = {recipient}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (thankYou is null) { await tx.CommitAsync(ct); return false; }
        if (!thankYou.ApplyProviderStatus(status, errorCode, providerOccurredAt, sentAt, deliveredAt, clock.GetUtcNow()))
        { await tx.CommitAsync(ct); return true; }
        db.AuditLogs.Add(AuditLog.ForEntity(thankYou.WeddingId, "thank_you_send_jobs", thankYou.Id,
            "THANK_YOU_SMS_PROVIDER_STATUS_UPDATED", null, metadata: System.Text.Json.JsonSerializer.Serialize(new { status, provider = "TEXTBEE" })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<bool> IsAwaitingReceiptAsync(string recipient, CancellationToken ct) =>
        await db.InvitationSendJobs.AnyAsync(x => x.Channel == "SMS" && x.ProviderName == "TEXTBEE" && x.Status == "SENDING" && x.Recipient == recipient, ct) ||
        await db.ThankYouSendJobs.AnyAsync(x => x.Channel == "SMS" && x.ProviderName == "TEXTBEE" && x.Status == ThankYouSendJob.Sending && x.Recipient == recipient, ct);

    private static bool CanApply(string current, string next) => current switch
    {
        "ACCEPTED" => next is "SENT" or "DELIVERED" or "FAILED" or "UNKNOWN",
        "UNKNOWN" => next is "SENT" or "DELIVERED" or "FAILED",
        "SENT" => next is "DELIVERED" or "FAILED",
        _ => false
    };
}
