namespace InviteMe.Domain.Weddings;

public sealed record WeddingAccess(
    Guid OwnerId,
    bool IsActiveMember,
    IReadOnlySet<WeddingPermission> Permissions)
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

        return IsActiveMember && Permissions.Contains(permission);
    }
}
