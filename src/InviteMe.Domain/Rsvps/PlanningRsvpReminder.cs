namespace InviteMe.Domain.Rsvps;

/// <summary>
/// Guest05: one reminder e-mail to a planning guest who has not answered the RSVP yet.
/// A guest gets at most one reminder of each kind (unique guest + kind): two automatic ones (3 days and 1 day before the
/// RSVP deadline) and one sent by the hosts, so never more than three in total.
/// </summary>
public sealed class PlanningRsvpReminder
{
    public const string Auto3Days = "AUTO_3D", Auto1Day = "AUTO_1D", Manual = "MANUAL";
    public const string Pending = "PENDING", Sending = "SENDING", Sent = "SENT", Failed = "FAILED", Cancelled = "CANCELLED";
    public const string Email = "EMAIL";
    public const int MaxAttempts = 3;
    public static readonly IReadOnlyList<string> Kinds = [Auto3Days, Auto1Day, Manual];
    public static readonly IReadOnlyList<string> Statuses = [Pending, Sending, Sent, Failed, Cancelled];

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Kind { get; private set; } = "";
    public string Channel { get; private set; } = Email;
    public string Recipient { get; private set; } = "";
    public string Status { get; private set; } = Pending;
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Guid? RequestedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public static PlanningRsvpReminder Create(Guid weddingId, Guid guestId, string kind, string recipient, DateTimeOffset sendAt,
        Guid? requestedBy, DateTimeOffset now)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(recipient)) throw new ArgumentException("Recipient is required.", nameof(recipient));
        if ((kind == Manual) != (requestedBy is not null)) throw new ArgumentException("Only a manual reminder has a requesting user.");
        return new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, Kind = kind, Recipient = recipient.Trim(),
            NextAttemptAt = sendAt, RequestedBy = requestedBy, CreatedAt = now
        };
    }

    public void Claim(Guid leaseId, DateTimeOffset until)
    {
        Status = Sending; LeaseId = leaseId; LeaseUntil = until; AttemptCount++;
    }

    public void MarkSent(DateTimeOffset now) { Status = Sent; SentAt = now; LeaseId = null; LeaseUntil = null; LastErrorCode = null; }

    /// <summary>Transient errors are retried later (up to <see cref="MaxAttempts"/>); others fail at once.</summary>
    public void MarkFailed(string code, bool transient, DateTimeOffset now)
    {
        LeaseId = null; LeaseUntil = null; LastErrorCode = code;
        if (transient && AttemptCount < MaxAttempts) { Status = Pending; NextAttemptAt = now.AddMinutes(5 * AttemptCount); }
        else Status = Failed;
    }

    /// <summary>Outside the allowed sending hours: wait without using up an attempt.</summary>
    public void Postpone(DateTimeOffset until)
    {
        Status = Pending; NextAttemptAt = until; LeaseId = null; LeaseUntil = null;
        if (AttemptCount > 0) AttemptCount--;
    }

    public void Cancel(string reason) { Status = Cancelled; LastErrorCode = reason; LeaseId = null; LeaseUntil = null; }
}
