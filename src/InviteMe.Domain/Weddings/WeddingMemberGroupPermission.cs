namespace InviteMe.Domain.Weddings;

/// <summary>A permission granted to one co-host for one group in one wedding.</summary>
public sealed class WeddingMemberGroupPermission
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid MemberId { get; set; }
    public Guid PermissionId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
}
