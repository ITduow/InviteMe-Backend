namespace InviteMe.Domain.Rsvps;

/// <summary>Who changed an RSVP. Mirrors the audit_logs actor shape (RSVP-02).</summary>
public readonly record struct RsvpActor(string Type, Guid? UserId, Guid? GuestId)
{
    public static RsvpActor User(Guid userId) => new("USER", userId, null);
    public static RsvpActor WeddingGuest(Guid guestId) => new("WEDDING_GUEST", null, guestId);
    public static RsvpActor System => new("SYSTEM", null, null);
}

/// <summary>
/// One immutable RSVP change. Rows are only ever inserted: there is no mutator, and the database rejects
/// UPDATE/DELETE/TRUNCATE on rsvp_history (trigger trg_rsvp_history_append_only).
/// </summary>
public sealed class RsvpHistory
{
    public Guid Id { get; private set; }
    public Guid RsvpId { get; private set; }
    public string? OldStatus { get; private set; }
    public string NewStatus { get; private set; } = "";
    public int? OldConfirmedPartySize { get; private set; }
    public int NewConfirmedPartySize { get; private set; }
    public string? OldSnapshot { get; private set; }
    public string? NewSnapshot { get; private set; }
    public string ActorType { get; private set; } = "SYSTEM";
    public Guid? ChangedBy { get; private set; }
    public Guid? ActorGuestId { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    /// <param name="oldStatus">Null for the first submission (no previous state).</param>
    public static RsvpHistory Record(Guid rsvpId, string? oldStatus, int? oldSize, string newStatus, int newSize,
        string? oldSnapshot, string newSnapshot, RsvpActor actor, string reason)
    {
        if ((oldStatus is null) != (oldSize is null)) throw new ArgumentException("Previous status and party size must both be present or both absent.");
        return new()
        {
            Id = Guid.NewGuid(), RsvpId = rsvpId, OldStatus = oldStatus, OldConfirmedPartySize = oldSize, NewStatus = newStatus,
            NewConfirmedPartySize = newSize, OldSnapshot = oldSnapshot, NewSnapshot = newSnapshot,
            ActorType = actor.Type, ChangedBy = actor.UserId, ActorGuestId = actor.GuestId, Reason = reason
        };
    }
}
