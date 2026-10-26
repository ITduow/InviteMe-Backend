using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Billing;
using InviteMe.Domain.AI;
using InviteMe.Domain.Billing;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

/// <summary>
/// Reads the ACTIVE subscription of a wedding. New weddings get a FREE row when created; a wedding without any row
/// predates plans and is treated as unlimited (legacy) so nobody loses features they already used.
/// Usage is counted from the data itself (no counters to drift): guests, media, sent invitations, reminder rounds,
/// co-hosts, tables and AI runs.
/// </summary>
public sealed class WeddingEntitlements(InviteMeDbContext db, TimeProvider clock) : IWeddingEntitlements
{
    private const string LegacyName = "Đám cưới trước khi có gói";

    public async Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct) =>
        (await db.Set<Plan>().AsNoTracking().Where(x => x.Status == Plan.Active).OrderBy(x => x.Price).ToListAsync(ct))
        .Select(x => new PlanDto(x.Code, x.Name, x.Price, x.Currency, PlanLimits.Parse(x.LimitsJson))).ToList();

    public async Task<WeddingPlanDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        var (code, name, legacy, limits) = await Current(weddingId, ct);
        var weddingAt = await WeddingAt(weddingId, ct);
        var usage = new WeddingPlanUsage(await Used(weddingId, PlanQuota.GuestPlaces, ct), await Used(weddingId, PlanQuota.MediaImages, ct),
            await Used(weddingId, PlanQuota.EmailInvites, ct), await Used(weddingId, PlanQuota.SmsInvites, ct),
            await Used(weddingId, PlanQuota.ReminderRounds, ct), await Used(weddingId, PlanQuota.CoHosts, ct),
            await Used(weddingId, PlanQuota.SeatingTables, ct), await Used(weddingId, PlanQuota.AiThankYouRuns, ct));
        return new(weddingId, code, name, legacy, limits, usage, PlanRules.PublicLinkUntil(limits, weddingAt));
    }

    public async Task<PlanLimits> LimitsAsync(Guid weddingId, CancellationToken ct) => (await Current(weddingId, ct)).Limits;

    public async Task RequireQuotaAsync(Guid weddingId, PlanQuota quota, int adding, CancellationToken ct)
    {
        if (adding <= 0) return;
        if (PlanRules.Limit(await LimitsAsync(weddingId, ct), quota) is not { } limit) return;
        var used = await Used(weddingId, quota, ct);
        if (used + adding <= limit) return;
        var required = PlanRules.RequiredPlan(await PlansAsync(ct), x => PlanRules.Limit(x, quota) is not { } other || other >= used + adding);
        throw PlanRules.Limit(PlanRules.Name(quota), limit, used, adding, required, limit == 0
            ? $"Gói hiện tại chưa có {PlanRules.Label(quota)}. Nâng cấp gói để dùng tính năng này."
            : $"Gói hiện tại cho tối đa {limit} {PlanRules.Label(quota)} (đã dùng {used}). Nâng cấp gói để thêm {adding}.");
    }

    public async Task RequireFeatureAsync(Guid weddingId, PlanFeature feature, CancellationToken ct)
    {
        if (PlanRules.Allows(await LimitsAsync(weddingId, ct), feature)) return;
        var required = PlanRules.RequiredPlan(await PlansAsync(ct), x => PlanRules.Allows(x, feature));
        throw PlanRules.Limit(PlanRules.Name(feature), null, 0, 1, required,
            $"Gói hiện tại không có {PlanRules.Label(feature)}. Nâng cấp gói để dùng tính năng này.");
    }

    public async Task<bool> BrandingBadgeForSlugAsync(string slug, CancellationToken ct) =>
        await db.Weddings.AsNoTracking().Where(x => x.Slug.ToLower() == slug.ToLower()).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct) is { } id
        && (await LimitsAsync(id, ct)).BrandingBadge;

    public async Task<bool> BrandingBadgeForGuestAsync(string invitationToken, CancellationToken ct) =>
        await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.InvitationToken == invitationToken).Select(x => (Guid?)x.WeddingId)
            .SingleOrDefaultAsync(ct) is { } id && (await LimitsAsync(id, ct)).BrandingBadge;

    public async Task<bool?> PublicLinkOpenAsync(string slug, CancellationToken ct)
    {
        var weddingId = await db.Weddings.AsNoTracking().Where(x => x.Slug.ToLower() == slug.ToLower()).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (weddingId is not { } id) return null;
        return PlanRules.PublicLinkOpen((await Current(id, ct)).Limits, await WeddingAt(id, ct), clock.GetUtcNow());
    }

    private async Task<(string Code, string Name, bool Legacy, PlanLimits Limits)> Current(Guid weddingId, CancellationToken ct)
    {
        if (!await db.Weddings.AnyAsync(x => x.Id == weddingId, ct))
            throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
        var plan = await (from s in db.Set<Subscription>().AsNoTracking()
                          join p in db.Set<Plan>().AsNoTracking() on s.PlanId equals p.Id
                          where s.WeddingId == weddingId && s.Status == Subscription.Active
                          select new { p.Code, p.Name, p.LimitsJson }).SingleOrDefaultAsync(ct);
        if (plan is not null) return (plan.Code, plan.Name, false, PlanLimits.Parse(plan.LimitsJson));
        if (await db.Set<Subscription>().AnyAsync(x => x.WeddingId == weddingId, ct))
        {
            // Had a plan that is no longer active (cancelled/suspended): back to FREE, never back to legacy.
            var free = await db.Set<Plan>().AsNoTracking().SingleAsync(x => x.Code == Plan.Free, ct);
            return (free.Code, free.Name, false, PlanLimits.Parse(free.LimitsJson));
        }
        return (Plan.Premium, LegacyName, true, PlanLimits.Unlimited);
    }

    private async Task<int> Used(Guid weddingId, PlanQuota quota, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return quota switch
        {
            PlanQuota.GuestPlaces => await db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null).SumAsync(x => x.ExpectedPeople, ct),
            PlanQuota.MediaImages => await db.WeddingMedia.CountAsync(x => x.WeddingId == weddingId, ct),
            // Every queued delivery counts, also failed ones: the provider may already have been paid for them.
            PlanQuota.EmailInvites => await db.InvitationSendJobs.CountAsync(x => x.WeddingId == weddingId && x.Channel == "EMAIL", ct),
            PlanQuota.SmsInvites => await db.InvitationSendJobs.CountAsync(x => x.WeddingId == weddingId && x.Channel == "SMS", ct),
            // One manual "send reminders" action queues all its reminders with the same creation time.
            PlanQuota.ReminderRounds => await db.Set<PlanningRsvpReminder>().Where(x => x.WeddingId == weddingId && x.Kind == PlanningRsvpReminder.Manual)
                .Select(x => x.CreatedAt).Distinct().CountAsync(ct),
            PlanQuota.CoHosts => await db.WeddingMembers.CountAsync(x => x.WeddingId == weddingId && x.Status == "ACTIVE" && x.MemberRole == "CO_HOST", ct)
                + await db.CoHostInvitations.CountAsync(x => x.WeddingId == weddingId && (x.Status == "PENDING" || x.Status == "SENT") && x.RevokedAt == null && x.ExpiresAt > now, ct),
            PlanQuota.SeatingTables => await db.Set<PlanningTable>().CountAsync(x => x.WeddingId == weddingId, ct),
            // The options of one generation share their creation time.
            PlanQuota.AiThankYouRuns => await db.Set<ThankYouTemplate>().Where(x => x.WeddingId == weddingId).Select(x => x.CreatedAt).Distinct().CountAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(quota))
        };
    }

    private Task<DateTimeOffset?> WeddingAt(Guid weddingId, CancellationToken ct) =>
        db.WeddingEvents.AsNoTracking().Where(x => x.WeddingId == weddingId && x.IsMain).Select(x => (DateTimeOffset?)x.StartAt).FirstOrDefaultAsync(ct);
}
