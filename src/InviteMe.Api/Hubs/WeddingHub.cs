using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;

namespace InviteMe.Api.Hubs;

[Authorize]
public sealed class WeddingHub(IWeddingPermissionService permissions, ISeatingDragLeaseService leases,
    IPlanningSeatingStore seating, IAccountStore accounts, IWeddingAccessReader access,
    BuilderPresenceRegistry builderPresence) : Hub
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

    public async Task LeaveWedding(Guid weddingId)
    {
        leases.Disconnect(Context.ConnectionId, weddingId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(weddingId), Context.ConnectionAborted);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SeatingGroupName(weddingId), Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<BuilderPresenceDto>> JoinBuilder(Guid weddingId, Guid sessionId, string? displayName,
        string? pageId, string? sectionId)
    {
        await RequireBuilderOwner(weddingId);
        if (sessionId == Guid.Empty) throw new HubException("BUILDER_SESSION_INVALID");
        var location = new BuilderPresenceLocation(CleanToken(pageId, 100), CleanToken(sectionId, 100), null, "viewing");
        var group = GroupName(weddingId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        var snapshot = builderPresence.Join(weddingId, sessionId, UserId, Context.ConnectionId,
            CleanDisplayName(displayName), location);
        var own = snapshot.Single(item => item.SessionId == sessionId);
        await Clients.Caller.SendAsync("builderPresenceSnapshot", snapshot, Context.ConnectionAborted);
        await Clients.OthersInGroup(group).SendAsync("builderPresenceChanged", own, Context.ConnectionAborted);
        return snapshot;
    }

    public async Task UpdateBuilderPresence(Guid weddingId, Guid sessionId, string? displayName,
        string? pageId, string? sectionId, string? fieldPath, string? status)
    {
        await RequireBuilderOwner(weddingId);
        if (!builderPresence.AllowLocationUpdate(Context.ConnectionId)) return;
        var location = new BuilderPresenceLocation(CleanToken(pageId, 100), CleanToken(sectionId, 100),
            CleanFieldPath(fieldPath), CleanStatus(status));
        var update = builderPresence.Update(weddingId, sessionId, UserId, Context.ConnectionId,
            CleanDisplayName(displayName), location);
        if (!update.Changed || update.Presence is null) return;
        await Clients.OthersInGroup(GroupName(weddingId))
            .SendAsync("builderPresenceChanged", update.Presence, Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<BuilderPresenceDto>> HeartbeatBuilder(Guid weddingId, Guid sessionId)
    {
        await RequireBuilderOwner(weddingId);
        if (!builderPresence.Heartbeat(weddingId, sessionId, UserId, Context.ConnectionId))
            throw new HubException("BUILDER_SESSION_EXPIRED");
        return builderPresence.Snapshot(weddingId);
    }

    public async Task LeaveBuilder(Guid weddingId, Guid sessionId)
    {
        await RequireBuilderOwner(weddingId);
        if (!builderPresence.Leave(weddingId, sessionId, UserId, Context.ConnectionId)) return;
        await Clients.OthersInGroup(GroupName(weddingId))
            .SendAsync("builderPresenceLeft", sessionId, Context.ConnectionAborted);
    }

    public async Task<SeatingDragSnapshot> GetSeatingDrags(Guid weddingId)
    {
        await RequireSeating(weddingId, false);
        await Groups.AddToGroupAsync(Context.ConnectionId, SeatingGroupName(weddingId), Context.ConnectionAborted);
        return leases.Snapshot(weddingId);
    }

    public async Task<SeatingDragLease> StartSeatingDrag(Guid weddingId, IReadOnlyList<Guid> placeIds)
    {
        await RequireSeating(weddingId, true);
        if (placeIds.Count is < 1 or > 500) throw new HubException("INVALID_DRAG_PLACES");
        // Only places inside the caller's seating edit scope can be locked: a group co-host cannot hold other groups' guests.
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_EDIT, Context.ConnectionAborted);
        var plan = await seating.GetScopedAsync(weddingId, Context.ConnectionAborted, scope.IsWeddingWide ? null : scope.GroupIds);
        if (placeIds.Any(id => !plan.Places.Any(p => p.Id == id)))
        {
            if (!scope.IsWeddingWide)
            {
                var fullPlan = await seating.GetAsync(weddingId, Context.ConnectionAborted);
                if (placeIds.All(id => fullPlan.Places.Any(p => p.Id == id)))
                    throw new HubException("SEATING_SCOPE_DENIED");
            }
            throw new HubException("PLACE_NOT_FOUND");
        }
        var account = await accounts.GetAsync(UserId, Context.ConnectionAborted);
        return Lease(() => leases.Acquire(weddingId, UserId, Context.ConnectionId, account?.DisplayName ?? "Cohost", placeIds));
    }

    public async Task<SeatingDragLease> RenewSeatingDrag(Guid weddingId, Guid token)
    {
        await RequireSeating(weddingId, true);
        return Lease(() => leases.Renew(weddingId, token, UserId, Context.ConnectionId));
    }

    public async Task TargetSeatingDrag(Guid weddingId, Guid token, Guid? tableId, int? seatNumber)
    {
        await RequireSeating(weddingId, true);
        if (tableId == Guid.Empty || seatNumber is < 1 or > 20 || (seatNumber != null && tableId == null))
            throw new HubException("INVALID_DRAG_TARGET");
        Lease(() => { leases.Target(weddingId, token, UserId, Context.ConnectionId, tableId, seatNumber); return true; });
    }

    public Task EndSeatingDrag(Guid weddingId, Guid token)
    {
        Lease(() => { leases.Release(weddingId, token, UserId, Context.ConnectionId); return true; });
        return Task.CompletedTask;
    }

    public Task MoveSeatingPointer(Guid weddingId, Guid token, string anchor, double x, double y, long sequence)
        => QueuePointer(weddingId, token, anchor, x, y, sequence, null, null, false);

    public Task MoveSeatingPointerWithTarget(Guid weddingId, Guid token, string anchor, double x, double y, long sequence,
        Guid? tableId, int? seatNumber)
        => QueuePointer(weddingId, token, anchor, x, y, sequence, tableId, seatNumber, true);

    private Task QueuePointer(Guid weddingId, Guid token, string anchor, double x, double y, long sequence,
        Guid? tableId, int? seatNumber, bool hasTarget)
    {
        if (anchor is null || !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 || sequence < 0 ||
            !(anchor is "queue" or "board" || (anchor.StartsWith("table:", StringComparison.Ordinal) && Guid.TryParse(anchor[6..], out _))))
            throw new HubException("INVALID_DRAG_POINTER");
        if (tableId == Guid.Empty || seatNumber is < 1 or > 20 || (seatNumber != null && tableId == null))
            throw new HubException("INVALID_DRAG_TARGET");
        // The live lease already establishes permission and tab ownership. No DB lookup on pointer movement.
        Lease(() => { leases.QueuePointer(weddingId, token, UserId, Context.ConnectionId, anchor, x, y, sequence, tableId, seatNumber, hasTarget); return true; });
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        leases.Disconnect(Context.ConnectionId);
        if (builderPresence.LeaveConnection(Context.ConnectionId) is { } presence)
            await Clients.Group(GroupName(presence.WeddingId)).SendAsync("builderPresenceLeft", presence.SessionId);
        await base.OnDisconnectedAsync(exception);
    }

    private Guid UserId => Guid.Parse(Context.User!.FindFirst("sub")!.Value);
    private async Task RequireSeating(Guid weddingId, bool edit)
    {
        try
        {
            await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, Context.ConnectionAborted);
            await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_VIEW, Context.ConnectionAborted);
            if (edit) await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_EDIT, Context.ConnectionAborted);
        }
        catch (ApplicationProblemException) { throw new HubException("SEATING_ACCESS_DENIED"); }
    }

    private async Task RequireBuilderOwner(Guid weddingId)
    {
        try
        {
            var userId = UserId;
            if (await access.FindAsync(weddingId, userId, Context.ConnectionAborted) is not { } grant || !grant.IsOwner(userId))
                throw new HubException("WEBSITE_OWNER_REQUIRED");
        }
        catch (ApplicationProblemException) { throw new HubException("WEDDING_ACCESS_DENIED"); }
        catch (FormatException) { throw new HubException("AUTHENTICATION_REQUIRED"); }
    }

    private static string CleanDisplayName(string? value)
    {
        var clean = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).Take(40).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Người chỉnh sửa" : clean;
    }

    private static string? CleanToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(maxLength).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static string? CleanFieldPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '[' or ']' or ':')
            .Take(160).ToArray());
        return clean.Length == 0 ? null : clean;
    }

    private static string CleanStatus(string? value) => value is "editing" or "idle" ? value : "viewing";
    private static T Lease<T>(Func<T> action)
    {
        try { return action(); }
        catch (ApplicationProblemException error) { throw new HubException(error.Code); }
    }

    internal static string GroupName(Guid weddingId) => $"wedding:{weddingId:D}";
    internal static string SeatingGroupName(Guid weddingId) => $"seating:{weddingId:D}";
}
