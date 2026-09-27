using InviteMe.Application.Abstractions.Authentication;

namespace InviteMe.Api.Authentication;

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user
        && Guid.TryParse(user.FindFirst("sub")?.Value, out var id) ? id : null;
}
