namespace InviteMe.Domain.Rsvps;

/// <summary>
/// RSVP of one Guest Planning v2 party (planning_guests row). Attendance of each person lives on
/// planning_participants.AttendanceStatus so the planning guest list and seating read the same answer.
/// </summary>
public sealed class PlanningRsvp
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Status { get; private set; } = "PENDING";
    public int ConfirmedPartySize { get; private set; }
    public string? Note { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static PlanningRsvp Create(Guid weddingId, Guid guestId, DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, SubmittedAt = now, UpdatedAt = now };

    public void Submit(string status, int size, DateTimeOffset now)
    {
        if (status is not ("PENDING" or "ATTENDING" or "DECLINED") || size < 0 || (status == "ATTENDING" ? size < 1 : size != 0))
            throw new ArgumentException("Invalid RSVP state/headcount.");
        Status = status; ConfirmedPartySize = size; UpdatedAt = now;
    }

    public void SetNote(string? note, DateTimeOffset now) { Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(); UpdatedAt = now; }

    /// <summary>Every saved change advances the version the client must send back as expectedVersion.</summary>
    public void Advance() => Version++;
}

/// <summary>Append-only (DB trigger). Snapshots hold ids and attendance only: no names, notes or tokens.</summary>
public sealed class PlanningRsvpHistory
{
    public Guid Id { get; private set; }
    public Guid RsvpId { get; private set; }
    public string? OldStatus { get; private set; }
    public int? OldConfirmedPartySize { get; private set; }
    public string NewStatus { get; private set; } = "";
    public int NewConfirmedPartySize { get; private set; }
    public string? OldSnapshot { get; private set; }
    public string NewSnapshot { get; private set; } = "";
    public string ActorType { get; private set; } = "";
    public Guid? ChangedBy { get; private set; }
    public Guid? ActorGuestId { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset ChangedAt { get; private set; }

    public static PlanningRsvpHistory Record(Guid rsvpId, string? oldStatus, int? oldSize, string newStatus, int newSize,
        string? oldSnapshot, string newSnapshot, Guid? userId, Guid? guestId, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), RsvpId = rsvpId, OldStatus = oldStatus, OldConfirmedPartySize = oldSize, NewStatus = newStatus,
        NewConfirmedPartySize = newSize, OldSnapshot = oldSnapshot, NewSnapshot = newSnapshot,
        ActorType = userId is not null ? "USER" : "WEDDING_GUEST", ChangedBy = userId, ActorGuestId = userId is null ? guestId : null,
        Reason = reason, ChangedAt = now
    };
}

/// <summary>One party-level entry per planning guest that holds only the places that did not fit.</summary>
public sealed class PlanningWaitlistEntry
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public int RequestedSlots { get; private set; }
    public string Status { get; private set; } = "WAITING";
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static PlanningWaitlistEntry Create(Guid weddingId, Guid guestId, int slots, DateTimeOffset now) => slots > 0
        ? new() { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, RequestedSlots = slots, RequestedAt = now }
        : throw new ArgumentOutOfRangeException(nameof(slots));
    public void Cancel(DateTimeOffset now) { Status = "CANCELLED"; ResolvedAt = now; }
    public void Promote(DateTimeOffset now) { Status = "PROMOTED"; ResolvedAt = now; }
}

/// <summary>Per-person dietary note entered on the RSVP form.</summary>
public sealed class PlanningDietaryNote
{
    public Guid ParticipantId { get; private set; }
    public string Note { get; private set; } = "";
    public static PlanningDietaryNote Create(Guid participantId, string note) => new() { ParticipantId = participantId, Note = note };
    public void Rewrite(string note) => Note = note;
}

/// <summary>
/// A guest's RSVP change received while the RSVP is locked (deadline passed or the hosts locked the guest/group).
/// It does not change any number until a host approves it (committee Q4, Couple06 lock rule).
/// </summary>
public sealed class PlanningRsvpRequest
{
    public const string Pending = "PENDING", Approved = "APPROVED", Rejected = "REJECTED", Superseded = "SUPERSEDED";
    public const string LateReason = "DEADLINE_PASSED", LockedReason = "LOCKED";

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Payload { get; private set; } = "";
    public string Reason { get; private set; } = "";
    public string Status { get; private set; } = Pending;
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }

    public static PlanningRsvpRequest Create(Guid weddingId, Guid guestId, string payload, string reason, DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, Payload = payload, Reason = reason, RequestedAt = now };

    public void Resolve(string status, Guid? actor, DateTimeOffset now)
    {
        if (Status != Pending) throw new InvalidOperationException("Request already resolved.");
        Status = status is Approved or Rejected or Superseded ? status : throw new ArgumentOutOfRangeException(nameof(status));
        ResolvedAt = now; ResolvedBy = actor;
    }
}

/// <summary>Hosts lock one guest (or every guest of a group): afterwards the guest can only send a change request.</summary>
public sealed class PlanningRsvpLock
{
    public Guid GuestId { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid LockedBy { get; private set; }
    public DateTimeOffset LockedAt { get; private set; }
    public static PlanningRsvpLock Create(Guid weddingId, Guid guestId, Guid actor, DateTimeOffset now) => new()
    { WeddingId = weddingId, GuestId = guestId, LockedBy = actor, LockedAt = now };
}

/// <summary>
/// Wedding-wide RSVP rule set at wedding setup. PlusOneLimit = companions every invitation may bring
/// (1 = two people in total including the recipient). An invitation imported with more people (e.g. a family of 4) overrides it.
/// </summary>
public sealed class PlanningRsvpSettings
{
    public const int MaxPlusOneLimit = 20;
    public Guid WeddingId { get; private set; }
    public int PlusOneLimit { get; private set; }
    public int Version { get; private set; }
    /// <summary>Guest05: send the automatic reminders 3 days and 1 day before the RSVP deadline (on by default).</summary>
    public bool AutoRemindersEnabled { get; private set; } = true;
    public static PlanningRsvpSettings Create(Guid weddingId) => new() { WeddingId = weddingId };
    public void SetAutoReminders(bool enabled) => AutoRemindersEnabled = enabled;
    public void Update(int plusOneLimit)
    {
        if (plusOneLimit is < 0 or > MaxPlusOneLimit) throw new ArgumentOutOfRangeException(nameof(plusOneLimit));
        PlusOneLimit = plusOneLimit; Version++;
    }
    /// <summary>People one invitation may confirm directly: its reserved places, or 1 + the wedding plus-one limit if larger.</summary>
    public static int AllowedPeople(int reservedPlaces, int plusOneLimit) => Math.Max(reservedPlaces, 1 + plusOneLimit);
}
