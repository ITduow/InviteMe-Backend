using Microsoft.AspNetCore.Identity;

namespace InviteMe.Infrastructure.Identity;

// Identity's Name is the authorization code (ADMIN/USER), not the display label.
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
