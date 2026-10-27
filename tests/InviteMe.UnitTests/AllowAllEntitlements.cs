using InviteMe.Application.Features.Billing;

namespace InviteMe.UnitTests;

/// <summary>Test double: every plan check passes (handler tests are not about plans).</summary>
internal sealed class AllowAllEntitlements : IWeddingEntitlements
{
    public Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlanDto>>([]);
    public Task<WeddingPlanDto> GetAsync(Guid weddingId, CancellationToken ct) =>
        Task.FromResult(new WeddingPlanDto(weddingId, "PREMIUM", "Test", true, PlanLimits.Unlimited, new(0), null));
    public Task RequireQuotaAsync(Guid weddingId, PlanQuota quota, int adding, CancellationToken ct) => Task.CompletedTask;
    public Task RequireFeatureAsync(Guid weddingId, PlanFeature feature, CancellationToken ct) => Task.CompletedTask;
    public Task<PlanLimits> LimitsAsync(Guid weddingId, CancellationToken ct) => Task.FromResult(PlanLimits.Unlimited);
    public Task<bool> BrandingBadgeForSlugAsync(string slug, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> BrandingBadgeForGuestAsync(string invitationToken, CancellationToken ct) => Task.FromResult(false);
    public Task<bool?> PublicLinkOpenAsync(string slug, CancellationToken ct) => Task.FromResult<bool?>(true);
}
