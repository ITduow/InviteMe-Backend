using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationMessagingStore(InviteMeDbContext db, TimeProvider clock, IWeddingEntitlements entitlements) : IInvitationMessagingStore
{
    public async Task<InvitationDraftDto?> GetDraftAsync(Guid weddingId, CancellationToken ct)
    {
        var row = await db.InvitationDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return row is null ? null : Dto(row);
    }

    public async Task<InvitationWeddingContext?> WeddingContextAsync(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.AsNoTracking().Where(x => x.Id == weddingId)
            .Select(x => new
            {
                x.Title,
                x.CoverImageUrl,
                WebsiteJson = db.WeddingSettings.Where(s => s.WeddingId == weddingId)
                    .Select(s => s.WebsiteDraftJson).SingleOrDefault()
            }).SingleOrDefaultAsync(ct);
        if (wedding is null) return null;

        var eventInfo = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.MainEventAt, x.VenueName, x.VenueAddress, x.Timezone }).SingleOrDefaultAsync(ct);
        var coupleNames = CoupleNames(wedding.Title, wedding.WebsiteJson);
        return new(coupleNames, eventInfo?.MainEventAt, eventInfo?.VenueName, eventInfo?.VenueAddress,
            string.IsNullOrWhiteSpace(eventInfo?.Timezone) ? "Asia/Ho_Chi_Minh" : eventInfo.Timezone, wedding.CoverImageUrl);
    }

    public async Task<InvitationDraftDto> SaveDraftAsync(Guid weddingId, Guid actorId, SaveInvitationDraft input,
        bool usedFallback, bool smsUsedFallback, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var draft = await db.InvitationDrafts.SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        var version = draft?.Version ?? 0;
        if (version != input.ExpectedVersion)
            throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Invitation draft changed. Reload it before saving.");
        var isNew = draft is null;
        if (draft is null)
        {
            draft = new InvitationDraft { WeddingId = weddingId, Version = 0, SmsContent = InvitationMessageTemplate.DefaultSms(), SmsUsedFallback = true };
            db.InvitationDrafts.Add(draft);
        }
        draft.Subject = input.Subject.Trim();
        draft.Content = input.Content.Trim();
        if (input.SmsContent is not null)
        {
            draft.SmsContent = input.SmsContent.Trim();
            draft.SmsUsedFallback = smsUsedFallback;
        }
        else if (isNew)
        {
            draft.SmsContent = InvitationMessageTemplate.DefaultSms();
            draft.SmsUsedFallback = true;
        }
        draft.Version++;
        draft.UsedFallback = usedFallback;
        draft.UpdatedBy = actorId;
        draft.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "invitation_drafts", weddingId, "INVITATION_DRAFT_SAVED", actorId,
            metadata: JsonSerializer.Serialize(new { draft.Version, draft.UsedFallback, draft.SmsUsedFallback })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(draft);
    }

    public async Task<InvitationRecipient?> FindRecipientAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        var guest = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId && x.ArchivedAt == null)
            .Select(x => new InvitationRecipient(x.Id, x.RecipientName, x.Email, x.Phone, x.InvitationToken)).SingleOrDefaultAsync(ct);
        return guest;
    }

    public async Task<InvitationBatchWriteResult> QueueAsync(Guid weddingId, Guid actorId, string idempotencyKey,
        QueueInvitationBatch input, InvitationDraftDto draft, string frontendUrl, string publicApiUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var front) || front.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(front.UserInfo) || !string.IsNullOrEmpty(front.Query) || !string.IsNullOrEmpty(front.Fragment) ||
            !Uri.TryCreate(publicApiUrl, UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "INVITATION_LINKS_NOT_CONFIGURED", "Public frontend and API URLs must be configured before queuing invitations.");

        var channels = input.Channels.Order(StringComparer.Ordinal).ToArray();
        var subject = string.IsNullOrWhiteSpace(input.Subject) ? draft.Subject : input.Subject.Trim();
        var emailContent = InvitationMessageTemplate.CleanLegacyCopy(input.Content ?? draft.Content);
        var smsContent = input.SmsContent ?? (channels.Contains("EMAIL", StringComparer.Ordinal)
            ? draft.SmsContent
            : input.Content ?? draft.SmsContent);
        if (channels.Contains("EMAIL", StringComparer.Ordinal) && !InvitationEmailRenderer.IsSafeGeneratedCopy(emailContent) ||
            channels.Contains("SMS", StringComparer.Ordinal) && !InvitationEmailRenderer.IsSafeGeneratedCopy(smsContent))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "INVALID_INVITATION_TEMPLATE", "The invitation template is invalid for the selected channel.");
        var storedContent = channels.Contains("EMAIL", StringComparer.Ordinal) ? emailContent : smsContent;
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { input.GuestIds, input.GroupId, Channels = channels, Subject = subject, EmailContent = emailContent,
                SmsContent = smsContent, draft.Version }))));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var prior = await db.InvitationSendBatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyKey == idempotencyKey, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != requestHash)
                throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "Idempotency-Key was already used with a different request.");
            return new(prior.Id, true);
        }

        var guestsQuery = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (input.GroupId is { } groupId)
        {
            var groupExists = await db.Set<PlanningGroup>().AnyAsync(x => x.Id == groupId && x.WeddingId == weddingId, ct);
            if (!groupExists) throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_GROUP_NOT_FOUND", "Guest group is unavailable in this wedding.");
            guestsQuery = guestsQuery.Where(x => x.GroupId == groupId);
        }
        else
        {
            var guestIds = input.GuestIds!.Distinct().ToArray();
            guestsQuery = guestsQuery.Where(x => guestIds.Contains(x.Id));
        }

        var guests = await guestsQuery.OrderBy(x => x.Id).Take(MessageRecipientLimits.Maximum + 1).ToListAsync(ct);
        if (guests.Count == 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "NO_INVITATION_RECIPIENTS", "No guests matched the selection.");
        if (guests.Count > MessageRecipientLimits.Maximum)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_BATCH_TOO_LARGE", $"A batch can contain at most {MessageRecipientLimits.Maximum} guests.");
        if (input.GuestIds is { } requested && guests.Count != requested.Distinct().Count())
            throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more guests are unavailable in this wedding.");
        if (guests.Any(x => x.InvitationToken is not { Length: 64 }))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_INVITATION_TOKEN_REQUIRED", "Every selected guest must have an invitation link before sending.");

        var weddingContext = await WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");

        var missing = guests.SelectMany(g => channels.Where(c => c == "EMAIL" ? string.IsNullOrWhiteSpace(g.Email) : string.IsNullOrWhiteSpace(g.Phone))
            .Select(c => new { GuestId = g.Id, Channel = c })).ToArray();
        if (missing.Length > 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_CONTACT_REQUIRED", "Every selected guest must have contact details for each requested channel.");
        // Counted under the wedding lock: every queued delivery uses the plan's sending quota of its channel.
        foreach (var channel in channels)
            await entitlements.RequireQuotaAsync(weddingId, channel == "EMAIL" ? PlanQuota.EmailInvites : PlanQuota.SmsInvites, guests.Count, ct);

        var batch = new InvitationSendBatch
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, RequestedBy = actorId, IdempotencyKey = idempotencyKey,
            RequestHash = requestHash, Subject = subject, ContentTemplate = storedContent, Channels = string.Join(',', channels), CreatedAt = clock.GetUtcNow()
        };
        db.InvitationSendBatches.Add(batch);
        var apiBase = api.ToString().TrimEnd('/');
        var frontBase = front.ToString().TrimEnd('/');
        foreach (var guest in guests)
        {
            var rsvp = $"{frontBase}/invite/{guest.InvitationToken}";
            var qr = $"{apiBase}/api/guest-invitations/{guest.InvitationToken}/qr?format=png";
            foreach (var channel in channels)
            {
                var channelContent = channel == "EMAIL" ? emailContent : smsContent;
                var email = channel == "EMAIL"
                    ? InvitationEmailRenderer.Render(weddingId, weddingContext.CoupleNames, guest.RecipientName, emailContent,
                        weddingContext.EventDateTime, weddingContext.VenueName, weddingContext.VenueAddress, weddingContext.TimeZoneId,
                        weddingContext.CoverImageUrl, publicApiUrl, rsvp, qr)
                    : null;
                var rendered = channel == "EMAIL"
                    ? emailContent
                    : InvitationMessageTemplate.RenderSms(channelContent, guest.RecipientName, rsvp);
                if (channel == "SMS" && SmsSegmentCounter.CountSegments(rendered) > 3)
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "SMS_CONTENT_TOO_LONG",
                        "SMS content must fit within 3 concatenated Unicode messages (201 UTF-16 code units). Remove extra text or links.");
                db.InvitationSendJobs.Add(new InvitationSendJob
                {
                    Id = Guid.NewGuid(), BatchId = batch.Id, WeddingId = weddingId, PlanningGuestId = guest.Id,
                    Channel = channel, Recipient = channel == "EMAIL" ? guest.Email!.Trim() : NormalizeSmsPhone(guest.Phone),
                    Subject = InvitationMessageTemplate.PersonalizeSubject(subject, guest.RecipientName, weddingContext.CoupleNames),
                    Content = rendered, RsvpLink = rsvp, QrLink = channel == "EMAIL" ? qr : "",
                    EmailHtmlContent = email?.Html, EmailPlainTextContent = email?.PlainText,
                    Status = "PENDING", NextAttemptAt = clock.GetUtcNow(), CreatedAt = clock.GetUtcNow()
                });
            }
        }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "invitation_send_batches", batch.Id, "INVITATIONS_QUEUED", actorId,
            metadata: JsonSerializer.Serialize(new { guestCount = guests.Count, channelCount = channels.Length, channels })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(batch.Id, false);
    }

    public async Task<InvitationSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    {
        var batch = await db.InvitationSendBatches.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == batchId && x.WeddingId == weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_BATCH_NOT_FOUND", "Invitation batch not found.");
        var jobs = await db.InvitationSendJobs.AsNoTracking().Where(x => x.BatchId == batchId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new InvitationSendJobDto(x.Id, x.PlanningGuestId!.Value, x.Channel, x.Recipient, x.Status,
                x.AttemptCount, x.SentAt, x.LastErrorCode, x.ProviderAcceptedAt, x.DeliveredAt)).ToListAsync(ct);
        var sent = jobs.Count(x => x.Status is "SENT" or "DELIVERED");
        var accepted = jobs.Count(x => x.Status == "ACCEPTED");
        var delivered = jobs.Count(x => x.Status == "DELIVERED");
        var simulated = jobs.Count(x => x.Status == "SIMULATED");
        var unknown = jobs.Count(x => x.Status is "UNKNOWN" or "REVIEW_REQUIRED");
        var failed = jobs.Count(x => x.Status == "FAILED");
        var pending = jobs.Count(x => x.Status is "PENDING" or "SENDING");
        var status = pending + accepted > 0 ? "PENDING" : unknown > 0 ? "NEEDS_REVIEW" :
            failed == jobs.Count ? "FAILED" : failed > 0 ? "PARTIAL_FAILED" :
            simulated == jobs.Count ? "SIMULATED" : simulated > 0 ? "COMPLETED" : "SENT";
        return new(batch.Id, status, jobs.Count, sent, failed, pending, batch.CreatedAt, jobs,
            accepted, delivered, unknown, simulated);
    }

    public async Task<int> RetryFailedAsync(Guid weddingId, Guid batchId, Guid actorId, CancellationToken ct)
    {
        var batch = await db.InvitationSendBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == batchId && x.WeddingId == weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_BATCH_NOT_FOUND", "Invitation batch not found.");
        var rows = await (from job in db.InvitationSendJobs
                          join guest in db.Set<PlanningGuest>() on job.PlanningGuestId equals guest.Id
                          where job.BatchId == batch.Id && job.Status == "FAILED" && guest.ArchivedAt == null
                          select job).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = "PENDING";
            row.NextAttemptAt = clock.GetUtcNow();
            row.LastErrorCode = null;
            row.LeaseId = null;
            row.LeaseUntil = null;
            row.AttemptCount = 0;
            row.ProviderMessageId = null; row.ProviderName = null; row.ProviderAcceptedAt = null;
            row.ProviderStatusUpdatedAt = null; row.DeliveredAt = null;
        }
        if (rows.Count > 0)
        {
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "invitation_send_batches", batchId, "INVITATIONS_RETRY_REQUESTED", actorId,
                metadata: JsonSerializer.Serialize(new { count = rows.Count })));
            await db.SaveChangesAsync(ct);
        }
        return rows.Count;
    }

    public async Task RecordTestSendAsync(Guid weddingId, Guid guestId, Guid actorId, CancellationToken ct)
    {
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guestId, "INVITATION_TEST_SENT", actorId,
            metadata: JsonSerializer.Serialize(new { channel = "EMAIL" })));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<InvitationOutboundMessage>> ClaimDueAsync(Guid leaseId, int limit, CancellationToken ct,
        bool smsEnabled = true, string? smsProviderName = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inviteme.invitation_send_jobs
            SET status = 'REVIEW_REQUIRED', last_error_code = 'SMS_SEND_OUTCOME_UNKNOWN', lease_id = NULL, lease_until = NULL
            WHERE status = 'SENDING' AND channel = 'SMS' AND provider_name = 'TEXTBEE' AND lease_until <= {now}
            """, ct);
        var jobs = await db.InvitationSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.invitation_send_jobs
            WHERE ((status = 'PENDING' AND next_attempt_at <= {now}) OR
                   (status = 'SENDING' AND lease_until <= {now} AND (channel = 'EMAIL' OR provider_name = 'MOCK')))
              AND (channel = 'EMAIL' OR (channel = 'SMS' AND {smsEnabled}))
            ORDER BY next_attempt_at, created_at, id
            LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = "SENDING";
            job.ProviderName = job.Channel == "SMS" ? (smsProviderName ?? "UNKNOWN").ToUpperInvariant() : "SMTP";
            job.LeaseId = leaseId;
            job.LeaseUntil = now.AddMinutes(2);
            job.AttemptCount++;
        }
        if (jobs.Count > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return jobs.Select(x => new InvitationOutboundMessage(x.Id, x.WeddingId, x.Channel, x.Recipient,
            x.Subject, x.Content, x.RsvpLink, x.QrLink, EmailHtmlContent: x.EmailHtmlContent,
            EmailPlainTextContent: x.EmailPlainTextContent)).ToArray();
    }

    public Task MarkAcceptedAsync(Guid jobId, Guid leaseId, string providerName, string providerMessageId, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, "ACCEPTED", providerName, providerMessageId, null, ct);

    public Task MarkSimulatedAsync(Guid jobId, Guid leaseId, string providerMessageId, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, "SIMULATED", "MOCK", providerMessageId, null, ct);

    public Task MarkUnknownAsync(Guid jobId, Guid leaseId, string code, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, "UNKNOWN", null, null, code, ct);

    private async Task UpdateSmsLeaseAsync(Guid jobId, Guid leaseId, string status, string? providerName,
        string? providerMessageId, string? errorCode, CancellationToken ct)
    {
        var job = await db.InvitationSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == "SENDING", ct);
        if (job is null) return;
        var now = clock.GetUtcNow();
        job.Status = status; job.ProviderName = providerName ?? job.ProviderName; job.ProviderMessageId = providerMessageId;
        job.LastErrorCode = errorCode;
        job.ProviderStatusUpdatedAt = status is "ACCEPTED" or "UNKNOWN" ? null : now;
        if (status == "ACCEPTED") job.ProviderAcceptedAt = now;
        job.LeaseId = null; job.LeaseUntil = null;
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "invitation_send_jobs", job.Id,
            status == "ACCEPTED" ? "INVITATION_SMS_ACCEPTED" : status == "SIMULATED" ? "INVITATION_SMS_SIMULATED" : "INVITATION_SMS_OUTCOME_UNKNOWN", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, status, provider = job.ProviderName })));
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerMessageId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.InvitationSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == "SENDING", ct);
        if (job is null) return;
        job.Status = "SENT";
        job.SentAt = clock.GetUtcNow();
        job.ProviderMessageId = providerMessageId;
        job.LeaseId = null;
        job.LeaseUntil = null;
        job.LastErrorCode = null;
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "invitation_send_jobs", job.Id, "INVITATION_SENT", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, job.AttemptCount })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.InvitationSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == "SENDING", ct);
        if (job is null) return;
        var canRetry = retry && job.AttemptCount < 5;
        job.LastErrorCode = code;
        job.LeaseId = null;
        job.LeaseUntil = null;
        if (canRetry)
        {
            job.Status = "PENDING";
            job.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(300, 15 * (1 << Math.Min(job.AttemptCount - 1, 4))));
        }
        else
        {
            job.Status = "FAILED";
        }
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "invitation_send_jobs", job.Id,
            canRetry ? "INVITATION_DELIVERY_RETRY_SCHEDULED" : "INVITATION_DELIVERY_FAILED", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, code, job.AttemptCount })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static InvitationDraftDto Dto(InvitationDraft x) => new(x.WeddingId, x.Subject,
        InvitationMessageTemplate.CleanLegacyCopy(x.Content), x.Version,
        x.UsedFallback, InvitationMessageTemplate.CleanLegacyCopy(x.SmsContent), x.SmsUsedFallback);

    private static string NormalizeSmsPhone(string? value)
    {
        try { return VietnamesePhoneNumber.ToE164(value ?? ""); }
        catch (ArgumentException) { throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_PHONE_INVALID", "A selected guest has an invalid SMS phone number."); }
    }

    private static string CoupleNames(string title, string? websiteJson)
    {
        if (websiteJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(websiteJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("partnerOne", out var p1) && root.TryGetProperty("partnerTwo", out var p2) &&
                    p1.ValueKind == JsonValueKind.String && p2.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(p1.GetString()) && !string.IsNullOrWhiteSpace(p2.GetString()))
                    return $"{p1.GetString()!.Trim()} & {p2.GetString()!.Trim()}";
                if (root.TryGetProperty("nodes", out var nodes) && nodes.TryGetProperty("ROOT", out var frame) &&
                    frame.TryGetProperty("props", out var props) && props.TryGetProperty("cover", out var cover) &&
                    cover.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(names.GetString()))
                    return names.GetString()!.Replace("\r", "").Replace("\n", " ").Trim();
            }
            catch (JsonException) { }
        }
        return title;
    }

    private async Task LockWedding(Guid weddingId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE", ct);
}
