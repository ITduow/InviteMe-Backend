namespace InviteMe.Domain.Rsvps;

/// <summary>
/// RSVP of one Guest Planning v2 party (planning_guests row). Attendance of each person lives on
/// planning_participants.AttendanceStatus so the planning guest list and seating read the same answer.
/// Status = the guest's answer: ATTENDING (Có), MAYBE (Phân vân), DECLINED (Không).
/// ConfirmedPartySize = people coming ("đăng ký bao nhiêu tính bấy nhiêu": every person of an ATTENDING answer counts);
/// MaybePartySize = people a MAYBE guest expects to bring (shown, never counted).
/// AcceptedPartySize = the number of people the couple has already seen and accepted for this invitation
/// (a host entering the answer, or "Đã xem" on an over-threshold invitation). Null = never accepted.
/// </summary>
public sealed class PlanningRsvp
{
    public const string Attending = "ATTENDING", Maybe = "MAYBE", Declined = "DECLINED";
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Status { get; private set; } = Attending;
    public int ConfirmedPartySize { get; private set; }
    public int MaybePartySize { get; private set; }
    public int? AcceptedPartySize { get; private set; }
    public string? Note { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static PlanningRsvp Create(Guid weddingId, Guid guestId, DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, SubmittedAt = now, UpdatedAt = now };

    public void Submit(string status, int confirmed, int maybe, DateTimeOffset now)
    {
        var valid = status switch
        {
            Attending => confirmed >= 0 && maybe == 0,
            Maybe => confirmed == 0 && maybe >= 1,
            Declined => confirmed == 0 && maybe == 0,
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid RSVP state/headcount.");
        Status = status; ConfirmedPartySize = confirmed; MaybePartySize = maybe; UpdatedAt = now;
    }

    /// <summary>Two-number form kept for callers outside the RSVP module (reception, tests): no MAYBE people.</summary>
    public void Submit(string status, int confirmed, DateTimeOffset now) => Submit(status, confirmed, 0, now);

    /// <summary>The couple has seen the current number of people: no over-threshold warning until it grows again.</summary>
    public void Accept(DateTimeOffset now) { AcceptedPartySize = ConfirmedPartySize; UpdatedAt = now; }

    /// <summary>
    /// "Vượt ngưỡng": an ATTENDING answer with more people than the invitation's threshold that the couple has not accepted yet.
    /// It never blocks the answer; it only tells the couple to look at it.
    /// </summary>
    public bool OverThreshold(int threshold) => Status == Attending && ConfirmedPartySize > threshold && ConfirmedPartySize > (AcceptedPartySize ?? 0);

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

/// <summary>Dietary note of the RSVP form, stored on the invitation's main person.</summary>
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
/// Wedding-wide RSVP rule set at wedding setup. PlusOneLimit = how many extra people an invitation may bring beyond the people
/// invited on it before the couple is warned (1 = a one-person invitation may come as two, a family of 4 as five).
/// </summary>
public sealed class PlanningRsvpSettings
{
    public const int MaxPlusOneLimit = 20, MaxPartySize = 50;
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

    /// <summary>Threshold ("ngưỡng") of one invitation: the people invited on it + the wedding plus-one limit.</summary>
    public static int Threshold(int invitedPeople, int plusOneLimit) => Math.Max(1, invitedPeople) + plusOneLimit;
}
