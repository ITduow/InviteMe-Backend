namespace InviteMe.Domain.Identity;

public sealed class Permission
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public string Scope { get; private set; } = "WEDDING";
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class RolePermission
{
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
