using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Billing;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingDeletionStore(InviteMeDbContext db, TimeProvider clock) : IWeddingDeletionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string RequestedAction = "WEDDING_DELETION_REQUESTED";

    public async Task<WeddingDeletionAccepted> RequestAsync(Guid weddingId, Guid ownerId, DeleteWeddingRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw Missing();
        if (wedding.OwnerUserId != ownerId)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_DELETE_OWNER_REQUIRED", "Only the wedding owner can delete this wedding.");

        if (wedding.Status == "ARCHIVED" && await HasDeletionRequest(weddingId, ct))
            return new(weddingId, "DELETION_PENDING", await RequestedAt(weddingId, ct) ?? clock.GetUtcNow());
        if (wedding.Status == "ARCHIVED")
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_ALREADY_ARCHIVED", "This wedding is already archived.");
        if (!string.Equals(request.ConfirmationSlug, wedding.Slug, StringComparison.Ordinal))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_DELETE_CONFIRMATION_MISMATCH", "Enter the exact wedding URL to confirm deletion.");
        if (wedding.Version != request.ExpectedVersion)
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_CONCURRENCY_CONFLICT", "Wedding changed. Reload it before confirming deletion.");

        var invitationJobs = await db.InvitationSendJobs.FromSqlInterpolated($"""
            SELECT * FROM inviteme.invitation_send_jobs
            WHERE wedding_id = {weddingId} AND status IN ('PENDING','SENDING') ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var thankYouJobs = await db.Set<ThankYouSendJob>().FromSqlInterpolated($"""
            SELECT * FROM inviteme.thank_you_send_jobs
            WHERE wedding_id = {weddingId} AND status IN ('PENDING','SENDING') ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        var reminders = await db.Set<PlanningRsvpReminder>().FromSqlInterpolated($"""
            SELECT * FROM inviteme.planning_rsvp_reminders
            WHERE wedding_id = {weddingId} AND status IN ('PENDING','SENDING') ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        if (invitationJobs.Any(x => x.Status == "SENDING") || thankYouJobs.Any(x => x.Status == ThankYouSendJob.Sending) ||
            reminders.Any(x => x.Status == PlanningRsvpReminder.Sending))
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_DELIVERY_IN_PROGRESS",
                "A message or RSVP reminder is currently being sent. Wait for it to finish, then retry deletion.");

        var openOrders = await db.Set<PlanOrder>().FromSqlInterpolated($"""
            SELECT * FROM inviteme.plan_orders WHERE wedding_id = {weddingId}
              AND status <> 'PAID' ORDER BY id FOR UPDATE
            """).ToListAsync(ct);
        if (openOrders.Any(x => x.PaidAmount > 0 || x.Status == PlanOrder.Underpaid))
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_PLAN_PAYMENT_REVIEW_REQUIRED",
                "A plan order has received money and is not settled. Contact InviteMe to reconcile it before deleting this wedding.");

        var activePlan = await (from subscription in db.Set<Subscription>().AsNoTracking()
                                join plan in db.Set<Plan>().AsNoTracking() on subscription.PlanId equals plan.Id
                                where subscription.WeddingId == weddingId && subscription.Status == Subscription.Active
                                select plan.Code).SingleOrDefaultAsync(ct);
        var now = clock.GetUtcNow();

        foreach (var job in invitationJobs.Where(x => x.Status == "PENDING"))
        {
            job.Status = "FAILED";
            job.LastErrorCode = "WEDDING_DELETION_PENDING";
            job.LeaseId = null;
            job.LeaseUntil = null;
        }
        foreach (var job in thankYouJobs.Where(x => x.Status == ThankYouSendJob.Pending))
            job.CancelForWeddingDeletion();
        foreach (var reminder in reminders.Where(x => x.Status == PlanningRsvpReminder.Pending))
        {
            reminder.Cancel("WEDDING_DELETION_PENDING");
        }
        foreach (var order in openOrders.Where(x => x.Open))
        {
            order.Cancel(ownerId, "Wedding owner requested deletion", now);
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "plan_orders", order.Id, "PLAN_ORDER_CANCELLED_BY_WEDDING_DELETION", ownerId,
                metadata: JsonSerializer.Serialize(new { order.PaidAmount }, Json)));
        }

        wedding.ArchiveForDeletion();
        foreach (var subscription in await db.Set<Subscription>()
                     .Where(x => x.WeddingId == weddingId && x.Status == Subscription.Active).ToListAsync(ct))
            subscription.Cancel();
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "weddings", weddingId, RequestedAction, ownerId,
            metadata: JsonSerializer.Serialize(new { packageBenefitsRevoked = true, activePlanCode = activePlan }, Json)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(weddingId, "DELETION_PENDING", now);
    }

    public async Task<IReadOnlyList<Guid>> PendingAsync(int limit, CancellationToken ct) => await db.Weddings.AsNoTracking()
        .Where(w => w.Status == "ARCHIVED" && db.AuditLogs.Any(a => a.WeddingId == w.Id && a.Action == RequestedAction))
        .OrderBy(w => w.UpdatedAt).Select(w => w.Id).Take(limit).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GiftProofKeysAsync(Guid weddingId, CancellationToken ct) =>
        await db.Set<InviteMe.Domain.Gifts.PlanningGiftProof>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId).Select(x => x.StorageKey).ToListAsync(ct);

    public async Task PurgeAsync(Guid weddingId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (wedding is null || wedding.Status != "ARCHIVED" || !await HasDeletionRequest(weddingId, ct))
        {
            await tx.RollbackAsync(ct);
            return;
        }

        // RSVP history is normally immutable. This transaction-local flag is set only by the owner-confirmed aggregate purge.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL inviteme.allow_wedding_purge = 'on'", ct);
        await db.Set<ThankYouSendJob>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<ThankYouMessage>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<ThankYouTemplate>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<CoHostInvitationGrant>().Where(x => db.Set<CoHostInvitation>()
            .Any(invitation => invitation.WeddingId == weddingId && invitation.Id == x.InvitationId)).ExecuteDeleteAsync(ct);
        await db.Set<PlanningSeatAssignment>().Where(x => db.Set<PlanningTable>().Any(t => t.WeddingId == weddingId &&
            (t.Id == x.TableId || t.Id == x.PlannedTableId))).ExecuteDeleteAsync(ct);
        await db.Set<PlanningPlace>().Where(x => db.Set<PlanningGuest>().Any(g => g.WeddingId == weddingId && g.Id == x.GuestId)).ExecuteDeleteAsync(ct);
        await db.Set<PlanningDietaryNote>().Where(x => db.Set<PlanningParticipant>().Any(p =>
            db.Set<PlanningGuest>().Any(g => g.WeddingId == weddingId && g.Id == p.GuestId) && p.Id == x.ParticipantId)).ExecuteDeleteAsync(ct);
        await db.Set<PlanningRsvpHistory>().Where(x => db.Set<PlanningRsvp>().Any(r => r.WeddingId == weddingId && r.Id == x.RsvpId)).ExecuteDeleteAsync(ct);
        await db.Set<PlanningRsvp>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<PlanningParticipant>().Where(x => db.Set<PlanningGuest>().Any(g => g.WeddingId == weddingId && g.Id == x.GuestId)).ExecuteDeleteAsync(ct);
        await db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);
        await db.Set<PlanningGroup>().Where(x => x.WeddingId == weddingId).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(AuditLog.ForPlatform(wedding.OwnerUserId, "weddings", weddingId, "WEDDING_DELETED"));
        await db.SaveChangesAsync(ct);
        await db.Weddings.Where(x => x.Id == weddingId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private Task<bool> HasDeletionRequest(Guid weddingId, CancellationToken ct) => db.AuditLogs.AsNoTracking()
        .AnyAsync(x => x.WeddingId == weddingId && x.Action == RequestedAction, ct);

    private Task<DateTimeOffset?> RequestedAt(Guid weddingId, CancellationToken ct) => db.AuditLogs.AsNoTracking()
        .Where(x => x.WeddingId == weddingId && x.Action == RequestedAction).OrderBy(x => x.CreatedAt)
        .Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(ct);

    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
}
