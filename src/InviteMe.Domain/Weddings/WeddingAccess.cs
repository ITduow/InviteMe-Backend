namespace InviteMe.Domain.Weddings;

public sealed record WeddingAccess(
    Guid OwnerId,
    bool IsActiveMember,
    IReadOnlySet<WeddingPermission> Permissions,
    IReadOnlySet<WeddingPermission>? WeddingWidePermissions = null,
    IReadOnlyDictionary<WeddingPermission, IReadOnlySet<Guid>>? GroupPermissions = null)
{
    public bool IsOwner(Guid userId)
    {
        return userId != Guid.Empty && OwnerId == userId;
    }

    public bool IsActiveCohost(Guid userId)
    {
        return userId != Guid.Empty && IsActiveMember;
    }

    public bool Allows(Guid userId, WeddingPermission permission)
    {
        if (userId == Guid.Empty || !Enum.IsDefined(permission))
        {
            return false;
        }

        if (OwnerId == userId)
        {
            return true;
        }

        if (!IsActiveMember) return false;

        // Reception access is deliberately wedding-wide. Even legacy group grants must not authorize check-in,
        // counter lookup, walk-ins, or voids for only part of the guest list.
        if (permission == WeddingPermission.CHECKIN_MANAGE)
            return IsWeddingWide(userId, permission);

        return IsWeddingWide(userId, permission) || GroupPermissions?.ContainsKey(permission) == true;
    }

    // Every active co-host sees the wedding itself (setup, media, wedding list, realtime channel): WEDDING_VIEW is not
    // delegable, it comes with the membership. Guest data stays behind the guest, RSVP and seating permissions and scopes.
    public bool IsWeddingWide(Guid userId, WeddingPermission permission) =>
        userId != Guid.Empty && (OwnerId == userId || (permission == WeddingPermission.WEDDING_VIEW && IsActiveMember)
                                 || (WeddingWidePermissions ?? Permissions).Contains(permission));

    public IReadOnlySet<Guid> GroupsFor(WeddingPermission permission) =>
        GroupPermissions?.GetValueOrDefault(permission) ?? new HashSet<Guid>();
}
