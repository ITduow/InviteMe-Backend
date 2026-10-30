using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.AI.ThankYouMessages;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ThankYouMessageStore(InviteMeDbContext db, TimeProvider clock) : IThankYouMessageStore
{
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "THANK_YOU_MESSAGE_NOT_FOUND", "Thank-you message is unavailable in this wedding.");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<IReadOnlyList<ThankYouGuestContext>> GuestsAsync(Guid weddingId, IReadOnlyList<Guid> guestIds, CancellationToken ct)
    {
        var guests = await db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.ArchivedAt == null && guestIds.Contains(x.Id))
            .Select(x => new { x.Id, x.RecipientName, x.Email, x.Phone, x.GroupId, x.Side }).ToListAsync(ct);
        var ids = guests.Select(x => x.Id).ToArray();
        var groups = await db.Set<PlanningGroup>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id != Guid.Empty)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var rsvps = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.WeddingId == weddingId && ids.Contains(x.GuestId))
            .ToDictionaryAsync(x => x.GuestId, ct);
        var participantCounts = await db.Set<PlanningParticipant>().AsNoTracking().Where(x => ids.Contains(x.GuestId) && x.AttendanceStatus == "ATTENDING")
            .GroupBy(x => x.GuestId).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var gifts = await db.Set<PlanningGift>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.GuestId != null && ids.Contains(x.GuestId.Value))
            .Select(x => new { GuestId = x.GuestId!.Value, x.Message, x.MessagePublic, x.Status, x.AmountReceived }).ToListAsync(ct);
        return guests.Select(g =>
        {
            rsvps.TryGetValue(g.Id, out var rsvp);
            var publicWish = gifts.Where(x => x.GuestId == g.Id && x.MessagePublic && !string.IsNullOrWhiteSpace(x.Message))
                .OrderByDescending(x => x.Status == PlanningGift.Paid).Select(x => x.Message).FirstOrDefault();
            var paidAmount = gifts.Where(x => x.GuestId == g.Id && x.Status == PlanningGift.Paid).Sum(x => x.AmountReceived ?? 0);
            return new ThankYouGuestContext(g.Id, g.RecipientName, g.Email, g.Phone,
                g.GroupId is { } groupId && groups.TryGetValue(groupId, out var name) ? name : null,
                g.Side, rsvp?.Status ?? "PENDING", rsvp?.ConfirmedPartySize ?? participantCounts.GetValueOrDefault(g.Id, 0),
                publicWish, paidAmount > 0 ? paidAmount : null);
        }).ToArray();
    }

    public async Task<ThankYouMessageDto> CreateAsync(ThankYouMessage message, CancellationToken ct)
    {
        db.ThankYouMessages.Add(message);
        db.AuditLogs.Add(AuditLog.ForEntity(message.WeddingId, "thank_you_messages", message.Id, "THANK_YOU_MESSAGE_GENERATED", message.CreatedBy,
            metadata: JsonSerializer.Serialize(new { message.GuestId, message.Tone, message.Model, message.UsedFallback, message.IncludesGiftAmount })));
        await db.SaveChangesAsync(ct);
        return await DtoAsync(message, ct);
    }

    public async Task<IReadOnlyList<ThankYouMessageDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var rows = await (from message in db.ThankYouMessages.AsNoTracking()
                          join guest in db.Set<PlanningGuest>().AsNoTracking() on message.GuestId equals guest.Id
                          where message.WeddingId == weddingId
                          orderby message.CreatedAt descending
                          select new { message, guest.RecipientName }).Take(500).ToListAsync(ct);
        return rows.Select(x => Dto(x.message, x.RecipientName)).ToArray();
    }

    public async Task<ThankYouMessageDto> GetAsync(Guid weddingId, Guid messageId, CancellationToken ct)
    {
        var row = await (from message in db.ThankYouMessages.AsNoTracking()
                         join guest in db.Set<PlanningGuest>().AsNoTracking() on message.GuestId equals guest.Id
                         where message.WeddingId == weddingId && message.Id == messageId
                         select new { message, guest.RecipientName }).SingleOrDefaultAsync(ct) ?? throw Missing();
        return Dto(row.message, row.RecipientName);
    }

    public async Task<ThankYouMessageDto> EditAsync(Guid weddingId, Guid messageId, Guid actor, EditThankYouMessage input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, actor, ct);
        var entity = await db.ThankYouMessages.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == messageId, ct) ?? throw Missing();
        if (entity.Version != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT", "Message changed. Reload it before saving.");
        try { entity.Edit(input.Content.Trim(), input.IncludeGiftAmount, input.IncludeGiftAmount ? entity.GiftAmount : null, clock.GetUtcNow()); }
        catch (ArgumentException) { throw new FluentValidation.ValidationException("Invalid thank-you message content or gift amount option."); }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "thank_you_messages", entity.Id, "THANK_YOU_MESSAGE_EDITED", actor,
            metadata: JsonSerializer.Serialize(new { entity.Version, entity.IncludesGiftAmount })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetAsync(weddingId, messageId, ct);
    }

    public async Task<ThankYouMessageDto> ApproveAsync(Guid weddingId, Guid messageId, Guid actor, int expectedVersion, CancellationToken ct)
    {
        if (expectedVersion <= 0) throw new FluentValidation.ValidationException("ExpectedVersion must be positive.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, actor, ct);
        var entity = await db.ThankYouMessages.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == messageId, ct) ?? throw Missing();
        if (entity.Version != expectedVersion) throw Conflict("CONCURRENCY_CONFLICT", "Message changed. Reload it before approving.");
        try { entity.Approve(actor, clock.GetUtcNow()); }
        catch (InvalidOperationException) { throw Conflict("THANK_YOU_MESSAGE_LOCKED", "Only a draft can be approved."); }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "thank_you_messages", entity.Id, "THANK_YOU_MESSAGE_APPROVED", actor,
            metadata: JsonSerializer.Serialize(new { entity.Version, entity.IncludesGiftAmount })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await GetAsync(weddingId, messageId, ct);
    }

    public async Task<ThankYouBatchQueued> QueueAsync(Guid weddingId, Guid actor, string key, IReadOnlyList<Guid> messageIds,
        IReadOnlyList<string> channels, string frontendUrl, CancellationToken ct)
    {
        var ids = messageIds.Order().ToArray();
        var canonical = JsonSerializer.Serialize(new { ids, channels = channels.Order(StringComparer.Ordinal).ToArray() });
        var requestHash = Hash(canonical); var idempotencyHash = Hash(key);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, actor, ct);
        var prior = await db.ThankYouSendBatches.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyHash == idempotencyHash, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != requestHash) throw Conflict("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for another request.");
            await tx.CommitAsync(ct); return new(prior.Id, true);
        }
        var rows = await (from message in db.ThankYouMessages
                          join guest in db.Set<PlanningGuest>() on message.GuestId equals guest.Id
                          where message.WeddingId == weddingId && ids.Contains(message.Id) && message.Status == "APPROVED" && guest.ArchivedAt == null
                          select new { Message = message, Guest = guest }).ToListAsync(ct);
        if (rows.Count != ids.Length) throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_APPROVAL_REQUIRED", "Every selected message must be approved and belong to this wedding.");
        if (rows.Any(x => x.Guest.InvitationToken is not { Length: 64 }))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_INVITATION_TOKEN_REQUIRED", "Every guest must have an active personal link before sending.");
        if (channels.Contains("EMAIL") && rows.Any(x => string.IsNullOrWhiteSpace(x.Guest.Email)) ||
            channels.Contains("SMS") && rows.Any(x => string.IsNullOrWhiteSpace(x.Guest.Phone)))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_CONTACT_REQUIRED", "Every selected guest needs contact details for the chosen channels.");

        var weddingTitle = await db.Weddings.Where(x => x.Id == weddingId).Select(x => x.Title).SingleAsync(ct);
        var batch = ThankYouSendBatch.Create(weddingId, idempotencyHash, requestHash, clock.GetUtcNow());
        db.ThankYouSendBatches.Add(batch);
        foreach (var row in rows)
        {
            var rendered = ThankYouMessageContent.Render(row.Message.Content, row.Guest.RecipientName, row.Message.GiftAmount);
            var link = $"{frontendUrl.TrimEnd('/')}/thank-you/{row.Guest.InvitationToken}";
            foreach (var channel in channels)
            {
                var content = channel == "EMAIL" ? rendered : $"Xin cảm ơn {Short(row.Guest.RecipientName, 32)} vì lời chúc và tình cảm của bạn! Xem thư: {link}";
                if (channel == "SMS" && SmsSegmentCounter.CountSegments(content) > 3)
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_SMS_TOO_LONG", "The thank-you SMS and personal link exceed three SMS segments.");
                db.ThankYouSendJobs.Add(ThankYouSendJob.Create(batch.Id, weddingId, row.Message.Id, row.Guest.Id, channel,
                    channel == "EMAIL" ? row.Guest.Email!.Trim() : NormalizeSmsPhone(row.Guest.Phone), content, clock.GetUtcNow()));
            }
        }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "thank_you_send_batches", batch.Id, "THANK_YOU_MESSAGES_QUEUED", actor,
            metadata: JsonSerializer.Serialize(new { guestCount = rows.Count, channels })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(batch.Id, false);
    }

    public async Task<ThankYouBatchQueued> QueueTemplateAsync(Guid weddingId, Guid actor, string key, Guid templateId,
        IReadOnlyList<Guid> guestIds, IReadOnlyList<string> channels, string frontendUrl, CancellationToken ct)
    {
        var ids = guestIds.Order().ToArray();
        var canonical = JsonSerializer.Serialize(new { templateId, guestIds = ids, channels = channels.Order(StringComparer.Ordinal).ToArray() });
        var requestHash = Hash(canonical); var idempotencyHash = Hash(key);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, actor, ct);
        var prior = await db.ThankYouSendBatches.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyHash == idempotencyHash, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != requestHash) throw Conflict("IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used for another request.");
            await tx.CommitAsync(ct); return new(prior.Id, true);
        }

        var template = await db.Set<ThankYouTemplate>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == templateId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "THANK_YOU_TEMPLATE_NOT_FOUND", "Template is unavailable in this wedding.");
        if (template.Status != "APPROVED")
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_APPROVAL_REQUIRED", "Approve the shared template before sending.");
        var guests = await db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.RecipientName, x.Email, x.Phone, x.InvitationToken }).ToListAsync(ct);
        if (guests.Count != ids.Length)
            throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more selected guests are unavailable in this wedding.");
        if (guests.Any(x => x.InvitationToken is not { Length: 64 }))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_INVITATION_TOKEN_REQUIRED", "Every guest must have an active personal link before sending.");
        if (channels.Contains("EMAIL") && guests.Any(x => string.IsNullOrWhiteSpace(x.Email)) ||
            channels.Contains("SMS") && guests.Any(x => string.IsNullOrWhiteSpace(x.Phone)))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_CONTACT_REQUIRED", "Every selected guest needs contact details for the chosen channels.");

        var paidAmounts = await db.Set<PlanningGift>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.GuestId != null && ids.Contains(x.GuestId.Value) && x.Status == PlanningGift.Paid)
            .GroupBy(x => x.GuestId!.Value)
            .Select(x => new { GuestId = x.Key, Amount = x.Sum(g => g.AmountReceived ?? 0) })
            .ToDictionaryAsync(x => x.GuestId, x => x.Amount, ct);
        var batch = ThankYouSendBatch.Create(weddingId, idempotencyHash, requestHash, clock.GetUtcNow());
        db.ThankYouSendBatches.Add(batch);
        foreach (var guest in guests)
        {
            var paidAmount = paidAmounts.GetValueOrDefault(guest.Id);
            var letter = ThankYouContent.Render(template.Content, guest.RecipientName,
                template.IncludesGiftAmount && paidAmount > 0 ? paidAmount : null);
            var publicLink = $"{frontendUrl.TrimEnd('/')}/thank-you/{guest.InvitationToken}";
            foreach (var channel in channels)
            {
                var content = channel == "EMAIL" ? letter : $"Cảm ơn {Short(guest.RecipientName, 32)}! Xem thư cảm ơn: {publicLink}";
                if (channel == "SMS" && SmsSegmentCounter.CountSegments(content) > 3)
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "THANK_YOU_SMS_TOO_LONG", "The thank-you SMS and personal link exceed three SMS segments.");
                db.ThankYouSendJobs.Add(ThankYouSendJob.CreateForTemplate(batch.Id, weddingId, template.Id, guest.Id,
                    channel, channel == "EMAIL" ? guest.Email!.Trim() : NormalizeSmsPhone(guest.Phone), content, letter, clock.GetUtcNow()));
            }
        }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "thank_you_send_batches", batch.Id, "THANK_YOU_MESSAGES_QUEUED", actor,
            metadata: JsonSerializer.Serialize(new { templateId, guestCount = guests.Count, channels, includeGiftAmount = template.IncludesGiftAmount })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(batch.Id, false);
    }

    public async Task<ThankYouSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    {
        var batch = await db.ThankYouSendBatches.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == batchId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "THANK_YOU_BATCH_NOT_FOUND", "Send batch is unavailable in this wedding.");
        var jobs = await db.ThankYouSendJobs.AsNoTracking().Where(x => x.BatchId == batchId)
            .OrderBy(x => x.Id).Select(x => new ThankYouSendJobDto(x.Id, x.MessageId, x.TemplateId, x.GuestId, x.Channel,
                x.Recipient, x.Status, x.AttemptCount, x.SentAt, x.ErrorCode, x.ProviderAcceptedAt, x.DeliveredAt)).ToListAsync(ct);
        var sent = jobs.Count(x => x.Status is "SENT" or "DELIVERED"); var failed = jobs.Count(x => x.Status == "FAILED");
        var accepted = jobs.Count(x => x.Status == "ACCEPTED"); var delivered = jobs.Count(x => x.Status == "DELIVERED");
        var unknown = jobs.Count(x => x.Status is "UNKNOWN" or "REVIEW_REQUIRED"); var simulated = jobs.Count(x => x.Status == "SIMULATED");
        var pending = jobs.Count(x => x.Status is "PENDING" or "SENDING");
        var status = pending + accepted > 0 ? "PENDING" : unknown > 0 ? "NEEDS_REVIEW" : failed == jobs.Count ? "FAILED" :
            failed > 0 ? "PARTIAL_FAILED" : simulated == jobs.Count ? "SIMULATED" : simulated > 0 ? "COMPLETED" : "SENT";
        return new(batch.Id, status, jobs.Count, sent, failed, pending, batch.CreatedAt, jobs, accepted, delivered, unknown, simulated);
    }

    public async Task<int> RetryAsync(Guid weddingId, Guid batchId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, actor, ct);
        var batch = await db.ThankYouSendBatches.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == batchId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "THANK_YOU_BATCH_NOT_FOUND", "Send batch is unavailable in this wedding.");
        var jobs = await db.ThankYouSendJobs.Where(x => x.BatchId == batch.Id && x.Status == ThankYouSendJob.Failed).ToListAsync(ct);
        foreach (var job in jobs) job.Retry(clock.GetUtcNow());
        if (jobs.Count > 0)
        {
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "thank_you_send_batches", batchId, "THANK_YOU_MESSAGES_RETRY_REQUESTED", actor,
                metadata: JsonSerializer.Serialize(new { count = jobs.Count })));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct); return jobs.Count;
    }

    public async Task<ThankYouPublicLetterDto?> PublicLetterAsync(string token, CancellationToken ct)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        var shared = await (from guest in db.Set<PlanningGuest>().AsNoTracking()
                            join wedding in db.Weddings.AsNoTracking() on guest.WeddingId equals wedding.Id
                            join owner in db.Users.AsNoTracking() on wedding.OwnerUserId equals owner.Id
                            join job in db.ThankYouSendJobs.AsNoTracking() on guest.Id equals job.GuestId
                            join template in db.Set<ThankYouTemplate>().AsNoTracking() on job.TemplateId equals template.Id
                            where guest.InvitationToken == token && guest.ArchivedAt == null && owner.Status == "ACTIVE" &&
                                  template.Status == "APPROVED" && (job.Status == ThankYouSendJob.Sent || job.Status == "DELIVERED") && job.LetterSnapshot != null
                            orderby job.SentAt descending, job.Channel
                            select new ThankYouPublicLetterDto(guest.RecipientName, wedding.Title, job.LetterSnapshot!, job.SentAt!.Value))
            .FirstOrDefaultAsync(ct);
        if (shared is not null) return shared;
        return await (from guest in db.Set<PlanningGuest>().AsNoTracking()
                      join wedding in db.Weddings.AsNoTracking() on guest.WeddingId equals wedding.Id
                      join owner in db.Users.AsNoTracking() on wedding.OwnerUserId equals owner.Id
                      join message in db.ThankYouMessages.AsNoTracking() on guest.Id equals message.GuestId
                      join job in db.ThankYouSendJobs.AsNoTracking() on message.Id equals job.MessageId
                      where guest.InvitationToken == token && guest.ArchivedAt == null && owner.Status == "ACTIVE" &&
                            message.Status == "APPROVED" && (job.Status == ThankYouSendJob.Sent || job.Status == "DELIVERED")
                      orderby job.SentAt descending, job.Channel
                      select new ThankYouPublicLetterDto(guest.RecipientName, wedding.Title,
                          ThankYouMessageContent.Render(message.Content, guest.RecipientName, message.GiftAmount), job.SentAt!.Value))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ThankYouDeliveryMessage>> ClaimDueAsync(Guid leaseId, int limit, string frontendUrl, CancellationToken ct,
        bool smsEnabled = true, string? smsProviderName = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inviteme.thank_you_send_jobs
            SET status = 'REVIEW_REQUIRED', error_code = 'SMS_SEND_OUTCOME_UNKNOWN', lease_id = NULL, lease_expires_at = NULL
            WHERE status = 'SENDING' AND channel = 'SMS' AND provider_name = 'TEXTBEE' AND lease_expires_at <= {now}
            """, ct);
        var jobs = await db.ThankYouSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.thank_you_send_jobs
            WHERE ((status = 'PENDING' AND next_attempt_at <= {now}) OR
                   (status = 'SENDING' AND lease_expires_at <= {now} AND (channel = 'EMAIL' OR provider_name = 'MOCK')))
              AND (channel = 'EMAIL' OR (channel = 'SMS' AND {smsEnabled}))
            ORDER BY next_attempt_at, id LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.Claim(leaseId, now, TimeSpan.FromMinutes(2));
            job.SetProviderNameForClaim(job.Channel == "SMS" ? (smsProviderName ?? "UNKNOWN") : "SMTP");
        }
        if (jobs.Count > 0) await db.SaveChangesAsync(ct);
        var rows = await (from job in db.ThankYouSendJobs.AsNoTracking()
                          join guest in db.Set<PlanningGuest>().AsNoTracking() on job.GuestId equals guest.Id
                          join wedding in db.Weddings.AsNoTracking() on job.WeddingId equals wedding.Id
                          where jobs.Select(x => x.Id).Contains(job.Id)
                          select new { job, guest.InvitationToken, wedding.Title }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(x => new ThankYouDeliveryMessage(x.job.Id, x.job.WeddingId, x.job.MessageId, x.job.TemplateId, x.job.GuestId,
            x.job.Channel, x.job.Recipient, x.job.ContentSnapshot,
            $"Lời cảm ơn từ {Short(x.Title, 120)}", $"{frontendUrl.TrimEnd('/')}/thank-you/{x.InvitationToken}" )).ToArray();
    }

    public async Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerId, CancellationToken ct)
    {
        var job = await db.ThankYouSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == ThankYouSendJob.Sending, ct);
        if (job is null) return;
        job.MarkSent(leaseId, providerId, clock.GetUtcNow());
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "thank_you_send_jobs", job.Id, "THANK_YOU_MESSAGE_SENT", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, job.AttemptCount })));
        await db.SaveChangesAsync(ct);
    }

    public Task MarkAcceptedAsync(Guid jobId, Guid leaseId, string providerName, string providerId, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, status: "ACCEPTED", providerName, providerId, null, ct);

    public Task MarkSimulatedAsync(Guid jobId, Guid leaseId, string providerId, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, status: "SIMULATED", "MOCK", providerId, null, ct);

    public Task MarkUnknownAsync(Guid jobId, Guid leaseId, string code, CancellationToken ct) =>
        UpdateSmsLeaseAsync(jobId, leaseId, status: "UNKNOWN", null, null, code, ct);

    private async Task UpdateSmsLeaseAsync(Guid jobId, Guid leaseId, string status, string? providerName,
        string? providerId, string? errorCode, CancellationToken ct)
    {
        var job = await db.ThankYouSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == ThankYouSendJob.Sending, ct);
        if (job is null) return;
        var now = clock.GetUtcNow();
        if (status == "ACCEPTED") job.MarkAccepted(leaseId, providerName!, providerId!, now);
        else if (status == "SIMULATED") job.MarkSimulated(leaseId, providerId!, now);
        else job.MarkUnknown(leaseId, errorCode ?? "SMS_SEND_OUTCOME_UNKNOWN", now);
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "thank_you_send_jobs", job.Id,
            status == "ACCEPTED" ? "THANK_YOU_SMS_ACCEPTED" : status == "SIMULATED" ? "THANK_YOU_SMS_SIMULATED" : "THANK_YOU_SMS_OUTCOME_UNKNOWN", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, status, provider = job.ProviderName })));
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct)
    {
        var job = await db.ThankYouSendJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.LeaseId == leaseId && x.Status == ThankYouSendJob.Sending, ct);
        if (job is null) return;
        job.MarkFailed(leaseId, code, retry, clock.GetUtcNow());
        db.AuditLogs.Add(AuditLog.ForEntity(job.WeddingId, "thank_you_send_jobs", job.Id, "THANK_YOU_MESSAGE_DELIVERY_FAILED", null,
            metadata: JsonSerializer.Serialize(new { job.Channel, job.AttemptCount, errorCode = code })));
        await db.SaveChangesAsync(ct);
    }

    private async Task LockWedding(Guid weddingId, Guid actor, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wedding is null || wedding.OwnerUserId != actor)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage thank-you messages.");
        if (!wedding.CanEdit) throw new ApplicationProblemException(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding is closed.");
    }

    private async Task<ThankYouMessageDto> DtoAsync(ThankYouMessage message, CancellationToken ct)
    {
        var name = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.Id == message.GuestId && x.WeddingId == message.WeddingId)
            .Select(x => x.RecipientName).SingleOrDefaultAsync(ct) ?? "Guest";
        return Dto(message, name);
    }
    private static ThankYouMessageDto Dto(ThankYouMessage x, string name) => new(x.Id, x.WeddingId, x.GuestId, name, x.Tone,
        x.Content, x.IncludesGiftAmount, x.GiftAmount, x.Status, x.Model, x.UsedFallback, x.Version, x.CreatedAt, x.UpdatedAt, x.ApprovedAt);
    private static string Short(string value, int max) => value.Length <= max ? value : value[..max];
    private static ApplicationProblemException Conflict(string code, string detail) => new(ProblemKind.Conflict, code, detail);
    private static string NormalizeSmsPhone(string? value)
    {
        try { return VietnamesePhoneNumber.ToE164(value ?? ""); }
        catch (ArgumentException) { throw new ApplicationProblemException(ProblemKind.BusinessRule, "GUEST_PHONE_INVALID", "A selected guest has an invalid SMS phone number."); }
    }
}
