using System.Text.Json;
using System.Threading.RateLimiting;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Gifts;
using Microsoft.AspNetCore.RateLimiting;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// Gift module: Guest08 (guest sends a gift through a VietQR code to the couple's account), Couple08 (gift book and
/// reconciliation, wedding owner only) and the SePay bank webhook (System04, idempotent by SePay transaction id).
/// </summary>
internal static class GiftEndpoints
{
    public const string GuestRateLimitPolicy = "gift-guest";
    private static readonly JsonSerializerOptions WebhookJson = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddGiftApi(this IServiceCollection services)
    {
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(GuestRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return services;
    }

    public static IEndpointRouteBuilder MapGifts(this IEndpointRouteBuilder endpoints)
    {
        // Guest (opens the personal invitation link; same token as /invite/{token} and RSVP).
        var guest = endpoints.MapGroup("/api/gifts/{token}").AllowAnonymous().RequireRateLimiting(GuestRateLimitPolicy).WithTags("Gifts (Guest)");
        guest.MapGet("", (string token, GiftGuestHandler h, CancellationToken ct) => h.PageAsync(token, ct))
            .WithName("GetMyGiftPage").Produces<GuestGiftPageDto>().ProducesProblem(StatusCodes.Status404NotFound);
        guest.MapPost("", async (string token, CreateGuestGift input, [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                GiftGuestHandler h, CancellationToken ct) =>
            {
                var result = await h.CreateAsync(token, idempotencyKey, input, ct);
                return Results.Json(result.Gift, statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .WithName("CreateMyGift").Produces<GuestGiftDto>(StatusCodes.Status201Created).Produces<GuestGiftDto>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        guest.MapGet("/{giftId:guid}", (string token, Guid giftId, GiftGuestHandler h, CancellationToken ct) => h.GetAsync(token, giftId, ct))
            .WithName("GetMyGift").Produces<GuestGiftDto>().ProducesProblem(StatusCodes.Status404NotFound);
        // "Đã chuyển" with the photo of the bank receipt (camera or file): the couple checks it before confirming the money.
        guest.MapPost("/{giftId:guid}/proof", async (string token, Guid giftId, Microsoft.AspNetCore.Http.IFormFile? image, GiftGuestHandler h,
                CancellationToken ct) =>
            {
                if (image is null) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                    InviteMe.Application.Common.Errors.ProblemKind.BadRequest, "GIFT_PROOF_REQUIRED", "An image file is required.");
                await using var stream = image.OpenReadStream();
                return await h.SendProofAsync(token, giftId, stream, image.Length, ct);
            })
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InviteMe.Domain.Weddings.UploadLimits.MaxImageRequestBytes))
            .WithName("SendMyGiftProof").Produces<GuestGiftDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        guest.MapPost("/{giftId:guid}/claim", (string token, Guid giftId, GiftGuestHandler h, CancellationToken ct) => h.ClaimAsync(token, giftId, ct))
            .WithName("ClaimMyGiftTransferred").Produces<GuestGiftDto>().ProducesProblem(StatusCodes.Status404NotFound);

        // Wedding owner (GIFT_VIEW / GIFT_EDIT, wedding-wide).
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}/gifts").RequireAuthorization().WithTags("Gifts (Host)");
        host.MapGet("/banks", (GiftHostHandler h) => h.Banks()).WithName("ListGiftBanks").Produces<IReadOnlyList<GiftBanks.Bank>>();
        host.MapGet("/accounts", (Guid weddingId, GiftHostHandler h, CancellationToken ct) => h.AccountsAsync(weddingId, ct))
            .WithName("ListGiftAccounts").Produces<IReadOnlyList<GiftBankAccountDto>>().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/accounts", async (Guid weddingId, SaveGiftAccount input, GiftHostHandler h, CancellationToken ct) =>
                Results.Json(await h.AddAccountAsync(weddingId, input, ct), statusCode: StatusCodes.Status201Created))
            .WithName("AddGiftAccount").Produces<GiftBankAccountDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapDelete("/accounts/{accountId:guid}", async (Guid weddingId, Guid accountId, GiftHostHandler h, CancellationToken ct) =>
            { await h.RemoveAccountAsync(weddingId, accountId, ct); return Results.NoContent(); })
            .WithName("RemoveGiftAccount").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        // SePay Bank Hub: the couple links their account in SePay's page (iframe), then the app syncs the linked accounts.
        host.MapPost("/bank-hub/link", (Guid weddingId, GiftHostHandler h, CancellationToken ct) => h.LinkBankHubAsync(weddingId, ct))
            .WithName("LinkGiftBankHub").Produces<BankHubLinkDto>().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        host.MapPost("/bank-hub/sync", (Guid weddingId, GiftHostHandler h, CancellationToken ct) => h.SyncBankHubAsync(weddingId, ct))
            .WithName("SyncGiftBankHub").Produces<BankHubSyncDto>().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status502BadGateway).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        host.MapPost("/accounts/{accountId:guid}/bank-hub/unlink", (Guid weddingId, Guid accountId, GiftHostHandler h, CancellationToken ct) =>
                h.UnlinkBankHubAsync(weddingId, accountId, ct))
            .WithName("UnlinkGiftBankHub").Produces<BankHubLinkDto>().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        host.MapGet("", (Guid weddingId, GiftHostHandler h, CancellationToken ct, string? status = null, string? method = null, string? search = null,
                Guid? guestId = null, int page = 1, int pageSize = 25) =>
                h.LedgerAsync(weddingId, new GiftLedgerFilter(status, method, search, guestId), new PageRequest(page, pageSize), ct))
            .WithName("ListGifts").Produces<PagedResult<GiftDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        // Reception desk: photo of a guest's transfer screen (camera or file), then RecordGift { method: TRANSFER, proofId }.
        host.MapPost("/proofs", async (Guid weddingId, Microsoft.AspNetCore.Http.IFormFile? image, GiftHostHandler h, CancellationToken ct) =>
            {
                if (image is null) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                    InviteMe.Application.Common.Errors.ProblemKind.BadRequest, "GIFT_PROOF_REQUIRED", "An image file is required.");
                await using var stream = image.OpenReadStream();
                return Results.Json(await h.UploadProofAsync(weddingId, stream, image.Length, ct), statusCode: StatusCodes.Status201Created);
            })
            .DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InviteMe.Domain.Weddings.UploadLimits.MaxImageRequestBytes))
            .WithName("UploadGiftProof").Produces<GiftProofDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        host.MapGet("/{giftId:guid}/proof", async (Guid weddingId, Guid giftId, GiftHostHandler h, HttpContext http, CancellationToken ct) =>
            {
                var file = await h.ProofAsync(weddingId, giftId, ct);
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(file.Content, file.ContentType);
            })
            .WithName("GetGiftProof").Produces(StatusCodes.Status200OK, contentType: "image/jpeg").ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        // Reception desk (owner or co-host with GIFT_RECORD): cash envelopes, presents and transfers shown at the wedding.
        host.MapGet("/reception", (Guid weddingId, GiftHostHandler h, CancellationToken ct) => h.ReceptionAsync(weddingId, ct))
            .WithName("ListReceptionGifts").Produces<IReadOnlyList<GiftDto>>().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapGet("/summary", (Guid weddingId, GiftHostHandler h, CancellationToken ct) => h.SummaryAsync(weddingId, ct))
            .WithName("GetGiftSummary").Produces<GiftSummaryDto>().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapGet("/export", async (Guid weddingId, GiftHostHandler h, HttpContext http, CancellationToken ct, string? status = null,
                string? method = null, string? search = null, Guid? guestId = null) =>
            {
                var file = await h.ExportAsync(weddingId, new GiftLedgerFilter(status, method, search, guestId), ct);
                http.Response.Headers.CacheControl = "no-store";
                // Not text/*: Swagger UI reads text responses as a string and drops the UTF-8 BOM, so Excel shows broken Vietnamese.
                return Results.File(file.Content, "application/octet-stream", file.FileName);
            })
            .WithName("ExportGifts").Produces(StatusCodes.Status200OK, contentType: "application/octet-stream").ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("", async (Guid weddingId, RecordGift input, [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                GiftHostHandler h, CancellationToken ct) =>
                Results.Json(await h.RecordAsync(weddingId, input, idempotencyKey, ct), statusCode: StatusCodes.Status201Created))
            .WithName("RecordGift").Produces<GiftDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        host.MapPost("/{giftId:guid}/confirm", (Guid weddingId, Guid giftId, ConfirmGift? input, GiftHostHandler h, CancellationToken ct) =>
                h.ConfirmAsync(weddingId, giftId, input ?? new ConfirmGift(), ct))
            .WithName("ConfirmGift").Produces<GiftDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapPost("/{giftId:guid}/cancel", (Guid weddingId, Guid giftId, GiftHostHandler h, CancellationToken ct) => h.CancelAsync(weddingId, giftId, ct))
            .WithName("CancelGift").Produces<GiftDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapGet("/transactions", (Guid weddingId, GiftHostHandler h, CancellationToken ct, string? status = null) => h.TransactionsAsync(weddingId, status, ct))
            .WithName("ListGiftTransactions").Produces<IReadOnlyList<GiftTransactionDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/transactions/{transactionId:guid}/assign", (Guid weddingId, Guid transactionId, AssignGiftTransaction input, GiftHostHandler h,
                CancellationToken ct) => h.AssignAsync(weddingId, transactionId, input, ct))
            .WithName("AssignGiftTransaction").Produces<GiftDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapPost("/transactions/{transactionId:guid}/dismiss", async (Guid weddingId, Guid transactionId, GiftHostHandler h, CancellationToken ct) =>
            { await h.DismissAsync(weddingId, transactionId, ct); return Results.NoContent(); })
            .WithName("DismissGiftTransaction").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        if (endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
        {
            // Development only: sends the SePay webhook body of a transfer into the couple's account (no SePay account needed).
            host.MapPost("/dev/simulate-sepay", (Guid weddingId, SimulateSePayTransfer input, GiftHostHandler h, GiftWebhookHandler webhook,
                    CancellationToken ct) => h.SimulateAsync(weddingId, input, webhook, ct))
                .WithName("SimulateSePayGiftTransfer").Produces<GiftWebhookResult>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
            // Development only: the same transfer as a Bank Hub IPN (the Bank Hub sandbox cannot create transactions).
            host.MapPost("/dev/simulate-bankhub", (Guid weddingId, SimulateSePayTransfer input, GiftHostHandler h, BankHubWebhookHandler webhook,
                    CancellationToken ct) => h.SimulateBankHubAsync(weddingId, input, webhook, ct))
                .WithName("SimulateBankHubGiftTransfer").Produces<GiftWebhookResult>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        }

        // SePay webhook: "Authorization: Apikey <SePay:WebhookApiKey>"; SePay needs 200 + {"success": true} within 30 s.
        // Plan payments (content with an IM... order code) share this endpoint; SePay sends every transfer of the account here.
        endpoints.MapPost("/api/webhooks/sepay", async (HttpRequest request, GiftWebhookHandler h,
            InviteMe.Application.Features.Billing.SePayWebhookDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!h.Configured) return Results.Json(new { success = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
                if (!h.Authorized(request.Headers.Authorization)) return Results.Json(new { success = false }, statusCode: StatusCodes.Status401Unauthorized);
                using var reader = new StreamReader(request.Body);
                var raw = await reader.ReadToEndAsync(ct);
                SePayWebhookPayload? payload;
                try { payload = JsonSerializer.Deserialize<SePayWebhookPayload>(raw, WebhookJson); }
                catch (JsonException) { payload = null; }
                if (payload is null || payload.Id <= 0) return Results.Json(new { success = false }, statusCode: StatusCodes.Status400BadRequest);
                await dispatcher.ProcessAsync(payload, raw, ct);
                return Results.Json(new { success = true });
            })
            .AllowAnonymous().WithTags("Gifts (SePay webhook)").WithName("SePayWebhook")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024))
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized);

        // SePay Bank Hub IPN: transfers of the accounts couples linked through Bank Hub (the "Notify URL" of our Bank Hub merchant).
        // "Authorization: Apikey <SePay:BankHub:NotifyApiKey>"; SePay waits 8 s for 200 + {"success": true}.
        endpoints.MapPost("/api/webhooks/sepay-bankhub", async (HttpRequest request, BankHubWebhookHandler h, CancellationToken ct) =>
            {
                if (!h.Configured) return Results.Json(new { success = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
                if (!h.Authorized(request.Headers.Authorization)) return Results.Json(new { success = false }, statusCode: StatusCodes.Status401Unauthorized);
                using var reader = new StreamReader(request.Body);
                return await h.ProcessAsync(await reader.ReadToEndAsync(ct), ct) is null
                    ? Results.Json(new { success = false }, statusCode: StatusCodes.Status400BadRequest)
                    : Results.Json(new { success = true });
            })
            .AllowAnonymous().WithTags("Gifts (SePay webhook)").WithName("SePayBankHubWebhook")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024))
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized);
        return endpoints;
    }
}
