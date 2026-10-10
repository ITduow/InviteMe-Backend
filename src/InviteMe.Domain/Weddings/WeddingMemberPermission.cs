namespace InviteMe.Domain.Weddings;

public sealed class WeddingMemberPermission
{
    public Guid MemberId { get; private set; }
    public Guid PermissionId { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }

    public static WeddingMemberPermission Create(Guid memberId, Guid permissionId) => new()
    {
        MemberId = memberId,
        PermissionId = permissionId,
        GrantedAt = DateTimeOffset.UtcNow
    };
}
