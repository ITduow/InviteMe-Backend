using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationMessagingStore(InviteMeDbContext db, TimeProvider clock) : IInvitationMessagingStore
{
    public async Task<InvitationDraftDto?> GetDraftAsync(Guid weddingId, CancellationToken ct)
    {
        var row = await db.InvitationDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return row is null ? null : Dto(row);
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
            draft = new InvitationDraft { WeddingId = weddingId, Version = 0, SmsContent = InvitationMessageTemplate.DefaultSms(input.Tone), SmsUsedFallback = true };
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
            draft.SmsContent = InvitationMessageTemplate.DefaultSms(input.Tone);
            draft.SmsUsedFallback = true;
        }
        draft.Tone = input.Tone;
        draft.Version++;
        draft.UsedFallback = usedFallback;
        draft.UpdatedBy = actorId;
        draft.UpdatedAt = clock.GetUtcNow();
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "invitation_drafts", weddingId, "INVITATION_DRAFT_SAVED", actorId,
            metadata: JsonSerializer.Serialize(new { draft.Version, draft.Tone, draft.UsedFallback, draft.SmsUsedFallback })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(draft);
    }

    public async Task<InvitationRecipient?> FindRecipientAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        var guest = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId && x.ArchivedAt == null)
            .Select(x => new InvitationRecipient(x.Id, x.RecipientName, x.Email, x.Phone, x.InvitationToken)).SingleOrDefaultAsync(ct);
        return guest is null ? null : guest with { CoupleNames = await CoupleNamesAsync(weddingId, ct) };
    }

    public async Task<InvitationBatchWriteResult> QueueAsync(Guid weddingId, Guid actorId, string idempotencyKey,
        QueueInvitationBatch input, InvitationDraftDto draft, string frontendUrl, string publicApiUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var front) || front.Scheme is not ("http" or "https") ||
            !Uri.TryCreate(publicApiUrl, UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https"))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "INVITATION_LINKS_NOT_CONFIGURED", "Public frontend and API URLs must be configured before queuing invitations.");

        var channels = input.Channels.Order(StringComparer.Ordinal).ToArray();
        var subject = string.IsNullOrWhiteSpace(input.Subject) ? draft.Subject : input.Subject.Trim();
        var emailContent = input.Content ?? draft.Content;
        var smsContent = input.SmsContent ?? (channels.Contains("EMAIL", StringComparer.Ordinal)
            ? draft.SmsContent
            : input.Content ?? draft.SmsContent);
        if (channels.Contains("EMAIL", StringComparer.Ordinal) && !InvitationMessageTemplate.IsValid(emailContent) ||
            channels.Contains("SMS", StringComparer.Ordinal) && !InvitationMessageTemplate.IsValidSms(smsContent))
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

        var guests = await guestsQuery.OrderBy(x => x.Id).Take(181).ToListAsync(ct);
        if (guests.Count == 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "NO_INVITATION_RECIPIENTS", "No guests matched the selection.");
        if (guests.Count > 180)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_BATCH_TOO_LARGE", "A batch can contain at most 180 guests.");
        if (input.GuestIds is { } requested && guests.Count != requested.Distinct().Count())
            throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more guests are unavailable in this wedding.");
        if (guests.Any(x => x.InvitationToken is not { Length: 64 }))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_INVITATION_TOKEN_REQUIRED", "Every selected guest must have an invitation link before sending.");

        var coupleNames = await CoupleNamesAsync(weddingId, ct);

        var missing = guests.SelectMany(g => channels.Where(c => c == "EMAIL" ? string.IsNullOrWhiteSpace(g.Email) : string.IsNullOrWhiteSpace(g.Phone))
            .Select(c => new { GuestId = g.Id, Channel = c })).ToArray();
        if (missing.Length > 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_CONTACT_REQUIRED", "Every selected guest must have contact details for each requested channel.");

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
                var rendered = InvitationMessageTemplate.Render(channelContent, guest.RecipientName, rsvp, channel == "EMAIL" ? qr : "");
                if (channel == "SMS" && !InvitationMessageTemplate.IsSmsLengthSupported(rendered))
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "SMS_CONTENT_TOO_LONG",
                        "SMS content must fit within 3 concatenated Unicode messages (201 UTF-16 code units). Remove extra text or links.");
                db.InvitationSendJobs.Add(new InvitationSendJob
                {
                    Id = Guid.NewGuid(), BatchId = batch.Id, WeddingId = weddingId, PlanningGuestId = guest.Id,
                    Channel = channel, Recipient = channel == "EMAIL" ? guest.Email!.Trim() : guest.Phone!.Trim(),
                    Subject = InvitationMessageTemplate.PersonalizeSubject(subject, guest.RecipientName, coupleNames),
                    Content = rendered, RsvpLink = rsvp, QrLink = qr,
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
                x.AttemptCount, x.SentAt, x.LastErrorCode)).ToListAsync(ct);
        var sent = jobs.Count(x => x.Status == "SENT");
        var failed = jobs.Count(x => x.Status == "FAILED");
        var status = failed > 0 && sent > 0 && sent + failed == jobs.Count ? "PARTIAL_FAILED" :
            sent == jobs.Count ? "SENT" : sent + failed == jobs.Count ? "FAILED" : "PENDING";
        return new(batch.Id, status, jobs.Count, sent, failed, jobs.Count - sent - failed, batch.CreatedAt, jobs);
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

    public async Task<IReadOnlyList<InvitationOutboundMessage>> ClaimDueAsync(Guid leaseId, int limit, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var jobs = await db.InvitationSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.invitation_send_jobs
            WHERE (status = 'PENDING' AND next_attempt_at <= {now})
               OR (status = 'SENDING' AND lease_until <= {now})
            ORDER BY next_attempt_at, created_at, id
            LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Status = "SENDING";
            job.LeaseId = leaseId;
            job.LeaseUntil = now.AddMinutes(2);
            job.AttemptCount++;
        }
        if (jobs.Count > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return jobs.Select(x => new InvitationOutboundMessage(x.Id, x.WeddingId, x.Channel, x.Recipient,
            x.Subject, x.Content, x.RsvpLink, x.QrLink)).ToArray();
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

    private static InvitationDraftDto Dto(InvitationDraft x) => new(x.WeddingId, x.Subject, x.Content, x.Tone, x.Version,
        x.UsedFallback, x.SmsContent, x.SmsUsedFallback);

    private async Task<string> CoupleNamesAsync(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.AsNoTracking().Where(x => x.Id == weddingId)
            .Select(x => new { x.Title, WebsiteJson = db.WeddingSettings.Where(s => s.WeddingId == weddingId)
                .Select(s => s.WebsiteDraftJson).SingleOrDefault() }).SingleAsync(ct);
        if (wedding.WebsiteJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(wedding.WebsiteJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("partnerOne", out var p1) && root.TryGetProperty("partnerTwo", out var p2) &&
                    !string.IsNullOrWhiteSpace(p1.GetString()) && !string.IsNullOrWhiteSpace(p2.GetString()))
                    return $"{p1.GetString()!.Trim()} & {p2.GetString()!.Trim()}";
                if (root.TryGetProperty("nodes", out var nodes) && nodes.TryGetProperty("ROOT", out var frame) &&
                    frame.TryGetProperty("props", out var props) && props.TryGetProperty("cover", out var cover) &&
                    cover.TryGetProperty("names", out var names) && !string.IsNullOrWhiteSpace(names.GetString()))
                    return names.GetString()!.Replace("\r", "").Replace("\n", " ").Trim();
            }
            catch (JsonException) { }
        }
        return wedding.Title;
    }

    private async Task LockWedding(Guid weddingId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE", ct);
}
