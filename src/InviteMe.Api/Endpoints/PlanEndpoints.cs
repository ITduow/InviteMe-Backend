using InviteMe.Api.Authorization;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Billing;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// Plans sold per wedding: the public price list, the plan of a wedding, orders paid by bank transfer (confirmed by the
/// SePay webhook) and the admin back office (prices, limits, orders, payments, manual grants, revenue).
/// </summary>
internal static class PlanEndpoints
{
    private sealed record SimulatePlanPayment(long? Amount = null);

    public static IEndpointRouteBuilder MapPlans(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plans", (PlanHandler h, CancellationToken ct) => h.PlansAsync(ct))
            .AllowAnonymous().WithTags("Plans").WithName("ListPlans").Produces<IReadOnlyList<PlanDto>>();
        endpoints.MapGet("/api/weddings/{weddingId:guid}/plan", (Guid weddingId, PlanHandler h, CancellationToken ct) => h.WeddingPlanAsync(weddingId, ct))
            .RequireAuthorization().WithTags("Plans").WithName("GetWeddingPlan").Produces<WeddingPlanDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        // Wedding owner: buy or upgrade, then pay the QR (content = order code).
        var orders = endpoints.MapGroup("/api/weddings/{weddingId:guid}/plan-orders").RequireAuthorization().WithTags("Plans");
        orders.MapPost("", async (Guid weddingId, CreatePlanOrder input, PlanOrderHandler h, CancellationToken ct) =>
            {
                var result = await h.CreateAsync(weddingId, input, ct);
                return Results.Json(result.Order, statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .WithName("CreatePlanOrder").Produces<PlanOrderDto>(StatusCodes.Status201Created).Produces<PlanOrderDto>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        orders.MapGet("", (Guid weddingId, PlanOrderHandler h, CancellationToken ct) => h.ListAsync(weddingId, ct))
            .WithName("ListPlanOrders").Produces<IReadOnlyList<PlanOrderDto>>().ProducesProblem(StatusCodes.Status403Forbidden);
        orders.MapGet("/{orderId:guid}", (Guid weddingId, Guid orderId, PlanOrderHandler h, CancellationToken ct) => h.GetAsync(weddingId, orderId, ct))
            .WithName("GetPlanOrder").Produces<PlanOrderDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        orders.MapPost("/{orderId:guid}/cancel", (Guid weddingId, Guid orderId, PlanOrderHandler h, CancellationToken ct) => h.CancelAsync(weddingId, orderId, ct))
            .WithName("CancelPlanOrder").Produces<PlanOrderDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        if (endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            // Development only: the SePay webhook of the owner's transfer, without a bank.
            orders.MapPost("/{orderId:guid}/dev/simulate-payment", (Guid weddingId, Guid orderId, SimulatePlanPayment? input, PlanOrderHandler h,
                    SePayWebhookDispatcher webhook, TimeProvider clock, CancellationToken ct) =>
                    h.SimulatePaymentAsync(weddingId, orderId, input?.Amount, webhook, clock, ct))
                .WithName("SimulatePlanPayment").Produces<PlanPaymentResult>();

        // Platform admin.
        var admin = endpoints.MapGroup("/api/admin").RequireAuthorization("PlatformAdmin").RequireAuthorization(AdminPolicyNames.PlansManage)
            .WithTags("Plans (Admin)");
        admin.MapGet("/plans", (AdminPlanHandler h, CancellationToken ct) => h.PlansAsync(ct))
            .WithName("ListAdminPlans").Produces<IReadOnlyList<AdminPlanDto>>();
        admin.MapPut("/plans/{code}", (string code, UpdatePlan input, AdminPlanHandler h, CancellationToken ct) => h.UpdatePlanAsync(code, input, ct))
            .WithName("UpdateAdminPlan").Produces<AdminPlanDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        admin.MapGet("/plan-orders", (AdminPlanHandler h, CancellationToken ct, string? status = null, string? search = null, int page = 1,
                int pageSize = 25) => h.OrdersAsync(status, search, new PageRequest(page, pageSize), ct))
            .WithName("ListAdminPlanOrders").Produces<PagedResult<AdminPlanOrderDto>>().ProducesValidationProblem();
        admin.MapPost("/plan-orders/{orderId:guid}/approve", (Guid orderId, ResolvePlanOrder? input, AdminPlanHandler h, CancellationToken ct) =>
                h.ApproveOrderAsync(orderId, input ?? new(), ct))
            .WithName("ApproveAdminPlanOrder").Produces<AdminPlanOrderDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        admin.MapPost("/plan-orders/{orderId:guid}/cancel", (Guid orderId, ResolvePlanOrder? input, AdminPlanHandler h, CancellationToken ct) =>
                h.CancelOrderAsync(orderId, input ?? new(), ct))
            .WithName("CancelAdminPlanOrder").Produces<AdminPlanOrderDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        admin.MapGet("/plan-payments", (AdminPlanHandler h, CancellationToken ct, string? status = null, int page = 1, int pageSize = 25) =>
                h.PaymentsAsync(status, new PageRequest(page, pageSize), ct))
            .WithName("ListAdminPlanPayments").Produces<PagedResult<AdminPlanPaymentDto>>().ProducesValidationProblem();
        admin.MapPut("/weddings/{weddingId:guid}/plan", (Guid weddingId, GrantPlan input, AdminPlanHandler h, CancellationToken ct) =>
                h.GrantAsync(weddingId, input, ct))
            .WithName("GrantAdminWeddingPlan").Produces<WeddingPlanDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        admin.MapGet("/plan-revenue", (AdminPlanHandler h, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
                h.RevenueAsync(from, to, ct))
            .WithName("GetAdminPlanRevenue").Produces<PlanRevenueDto>().ProducesValidationProblem();
        return endpoints;
    }
}
