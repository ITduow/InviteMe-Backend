using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;

namespace InviteMe.Application.Ports.Rsvps;

/// <summary>Guest05 reminder persistence (planning_rsvp_reminders + the reminder switch on planning_rsvp_settings).</summary>
public interface IRsvpReminderStore
{
    /// <summary>Everything the reminder rules need for one wedding. Throws WEDDING_NOT_FOUND.</summary>
    Task<RsvpReminderContext> ContextAsync(Guid weddingId, CancellationToken ct);
    Task SetAutoAsync(Guid weddingId, bool enabled, CancellationToken ct);
    /// <summary>Open weddings with automatic reminders on whose RSVP deadline is within the next three days.</summary>
    Task<IReadOnlyList<Guid>> AutoWeddingsAsync(DateTimeOffset now, CancellationToken ct);
    /// <summary>Inserts the reminders that do not exist yet (one per guest and kind) and returns the guests actually queued.</summary>
    Task<IReadOnlyList<Guid>> EnqueueAsync(Guid weddingId, IReadOnlyList<RsvpReminderEnqueue> items, DateTimeOffset now, CancellationToken ct);
    /// <summary>Leases due reminders (FOR UPDATE SKIP LOCKED) so two workers never send the same one.</summary>
    Task<IReadOnlyList<RsvpReminderJob>> ClaimDueAsync(Guid leaseId, DateTimeOffset now, Guid? weddingId, int limit, CancellationToken ct);
    Task MarkSentAsync(Guid reminderId, Guid leaseId, DateTimeOffset now, CancellationToken ct);
    Task MarkFailedAsync(Guid reminderId, Guid leaseId, string code, bool transient, DateTimeOffset now, CancellationToken ct);
    Task PostponeAsync(Guid reminderId, Guid leaseId, DateTimeOffset until, CancellationToken ct);
    Task CancelAsync(Guid reminderId, Guid leaseId, string reason, CancellationToken ct);
    Task<PagedResult<RsvpReminderDto>> HistoryAsync(Guid weddingId, RsvpReminderHistoryFilter filter, PageRequest page, bool contacts, CancellationToken ct);
}

/// <summary>Classifies a delivery exception of the e-mail channel: provider error code and whether a retry may help.</summary>
public interface IRsvpReminderFailureClassifier
{
    (string Code, bool Transient) Classify(Exception exception);
}
