namespace InviteMe.Domain.Weddings;

// Names intentionally match the schema's permission codes.
public enum WeddingPermission
{
    WEDDING_VIEW,
    WEDDING_EDIT,
    GUEST_VIEW,
    GUEST_EDIT,
    INVITATION_SEND,
    RSVP_VIEW,
    SEATING_VIEW,
    SEATING_EDIT,
    CHECKIN_MANAGE,
    GIFT_VIEW,
    GIFT_EDIT,
    ANALYTICS_VIEW,
    /// <summary>Co-host at the reception: records cash envelopes and presents for the whole wedding, never sees totals.</summary>
    GIFT_RECORD
}
