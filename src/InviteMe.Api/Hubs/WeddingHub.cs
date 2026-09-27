using InviteMe.Application.Abstractions.Authorization;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

[Authorize]
public sealed class WeddingHub(IWeddingPermissionService permissions) : Hub
{
    public async Task JoinWedding(Guid weddingId)
    {
        try
        {
            await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, Context.ConnectionAborted);
        }
        catch (ApplicationProblemException)
        {
            throw new HubException("WEDDING_ACCESS_DENIED");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(weddingId), Context.ConnectionAborted);
    }

    public Task LeaveWedding(Guid weddingId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(weddingId), Context.ConnectionAborted);

    internal static string GroupName(Guid weddingId) => $"wedding:{weddingId:D}";
}
