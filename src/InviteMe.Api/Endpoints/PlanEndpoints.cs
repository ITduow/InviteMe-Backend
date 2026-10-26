using InviteMe.Application.Features.Billing;

namespace InviteMe.Api.Endpoints;

/// <summary>Plans sold per wedding: the public price list and the plan, limits and usage of one wedding.</summary>
internal static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlans(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plans", (PlanHandler h, CancellationToken ct) => h.PlansAsync(ct))
            .AllowAnonymous().WithTags("Plans").WithName("ListPlans").Produces<IReadOnlyList<PlanDto>>();
        endpoints.MapGet("/api/weddings/{weddingId:guid}/plan", (Guid weddingId, PlanHandler h, CancellationToken ct) => h.WeddingPlanAsync(weddingId, ct))
            .RequireAuthorization().WithTags("Plans").WithName("GetWeddingPlan").Produces<WeddingPlanDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }
}
