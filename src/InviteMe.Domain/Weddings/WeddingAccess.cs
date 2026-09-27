namespace InviteMe.Domain.Weddings;

public sealed record WeddingAccess(
    Guid OwnerId,
    bool IsActiveMember,
    IReadOnlySet<WeddingPermission> Permissions)
{
    public bool Allows(Guid userId, WeddingPermission permission) =>
        userId != Guid.Empty && Enum.IsDefined(permission) &&
        (OwnerId == userId || (IsActiveMember && Permissions.Contains(permission)));
}
