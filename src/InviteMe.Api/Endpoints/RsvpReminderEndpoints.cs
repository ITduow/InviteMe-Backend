using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// Guest05 RSVP reminders (e-mail). Automatic: 3 days and 1 day before the RSVP deadline; manual: once per guest.
/// At most three reminders per guest, 24 hours apart, sent between 08:00 and 21:00 wedding time.
/// </summary>
internal static class RsvpReminderEndpoints
{
    public static void MapRsvpReminders(this RouteGroupBuilder host, bool development)
    {
        var reminders = host.MapGroup("/rsvps/reminders").WithTags("RSVP (Host)");
        reminders.MapGet("/summary", (Guid weddingId, RsvpReminderHandler h, CancellationToken ct) => h.SummaryAsync(weddingId, ct))
            .WithName("GetRsvpReminderSummary")
            .Produces<RsvpReminderSummaryDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        reminders.MapPut("/settings", (Guid weddingId, SaveRsvpReminderSettings input, RsvpReminderHandler h, CancellationToken ct) =>
                h.SaveSettingsAsync(weddingId, input, ct))
            .WithName("SaveRsvpReminderSettings")
            .Produces<RsvpReminderSummaryDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        reminders.MapGet("/preview", (Guid weddingId, RsvpReminderHandler h, CancellationToken ct) => h.PreviewAsync(weddingId, ct))
            .WithName("PreviewRsvpReminders")
            .Produces<RsvpReminderPreviewDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        // 202: the e-mails are queued and sent by the background worker (right away, or at 08:00 when it is late in the evening).
        reminders.MapPost("/send", async (Guid weddingId, SendRsvpReminders? input, RsvpReminderHandler h, CancellationToken ct) =>
                Results.Json(await h.SendAsync(weddingId, input ?? new SendRsvpReminders(), ct), statusCode: StatusCodes.Status202Accepted))
            .WithName("SendRsvpReminders")
            .Produces<RsvpReminderSendResult>(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        reminders.MapGet("", (Guid weddingId, RsvpReminderHandler h, CancellationToken ct, Guid? guestId = null, string? status = null,
                string? kind = null, int page = 1, int pageSize = 25) =>
                h.HistoryAsync(weddingId, new RsvpReminderHistoryFilter(guestId, status, kind), new PageRequest(page, pageSize), ct))
            .WithName("ListRsvpReminders")
            .Produces<PagedResult<RsvpReminderDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        if (development)
            // Development only: run the automatic round + delivery now, optionally "as if" at a given time (e.g. 2 days before the deadline).
            reminders.MapPost("/run-now", (Guid weddingId, RsvpReminderHandler h, CancellationToken ct, DateTimeOffset? at = null) =>
                    h.RunNowAsync(weddingId, at, ct))
                .WithName("RunRsvpRemindersNow")
                .Produces<RsvpReminderRunResult>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
    }
}
