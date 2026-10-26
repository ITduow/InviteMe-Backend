using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Billing;
using InviteMe.Domain.Billing;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

/// <summary>
/// Reads the ACTIVE subscription of a wedding. New weddings get a FREE row when created; a wedding without any row
/// predates plans and is treated as unlimited (legacy) so nobody loses features they already used.
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
        return new(weddingId, code, name, legacy, limits, new(await GuestPlaces(weddingId, ct)), PlanRules.PublicLinkUntil(limits, weddingAt));
    }

    public async Task RequireGuestPlacesAsync(Guid weddingId, int adding, CancellationToken ct)
    {
        if (adding <= 0) return;
        var limits = (await Current(weddingId, ct)).Limits;
        if (limits.GuestPlaces is not { } limit) return;
        var used = await GuestPlaces(weddingId, ct);
        if (used + adding <= limit) return;
        var required = PlanRules.RequiredPlan(await PlansAsync(ct), x => x.GuestPlaces is null || x.GuestPlaces >= used + adding);
        throw PlanRules.Limit("guestPlaces", limit, used, adding, required,
            $"Gói hiện tại cho tối đa {limit} khách mời (đang có {used}). Nâng cấp gói để thêm {adding} người.");
    }

    public async Task RequireCustomSlugAsync(Guid weddingId, CancellationToken ct)
    {
        if ((await Current(weddingId, ct)).Limits.CustomSlug) return;
        var required = PlanRules.RequiredPlan(await PlansAsync(ct), x => x.CustomSlug);
        throw PlanRules.Limit("customSlug", null, 0, 1, required, "Gói hiện tại không cho đổi đường dẫn website cưới.");
    }

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

    private Task<int> GuestPlaces(Guid weddingId, CancellationToken ct) =>
        db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null).SumAsync(x => x.ExpectedPeople, ct);

    private Task<DateTimeOffset?> WeddingAt(Guid weddingId, CancellationToken ct) =>
        db.WeddingEvents.AsNoTracking().Where(x => x.WeddingId == weddingId && x.IsMain).Select(x => (DateTimeOffset?)x.StartAt).FirstOrDefaultAsync(ct);
}
