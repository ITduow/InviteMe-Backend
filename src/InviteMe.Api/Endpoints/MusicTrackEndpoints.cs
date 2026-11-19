using InviteMe.Api.Authorization;
using InviteMe.Application.Features.Music;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Api.Endpoints;

internal static class MusicTrackEndpoints
{
    public static IEndpointRouteBuilder MapMusicTracks(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin/music-tracks").RequireAuthorization("PlatformAdmin").WithTags("Music Administration");
        admin.MapGet("", (IMusicTrackStore store, CancellationToken ct, int page = 1, int pageSize = 25, string? search = null, bool? isActive = null) =>
            store.ListAsync(new(page, pageSize, search, isActive), ct)).RequireAuthorization(AdminPolicyNames.MusicView);
        admin.MapGet("/{id:guid}", (Guid id, IMusicTrackStore store, CancellationToken ct) => store.GetAsync(id, ct))
            .RequireAuthorization(AdminPolicyNames.MusicView);
        admin.MapPost("", async (CreateMusicTrack input, IMusicTrackStore store, ICurrentUser user, CancellationToken ct) =>
            Results.Json(await store.CreateAsync(user.UserId!.Value, input, ct), statusCode: 201)).RequireAuthorization(AdminPolicyNames.MusicManage);
        admin.MapPut("/{id:guid}", (Guid id, UpdateMusicTrack input, IMusicTrackStore store, ICurrentUser user, CancellationToken ct) =>
            store.UpdateAsync(user.UserId!.Value, id, input, ct)).RequireAuthorization(AdminPolicyNames.MusicManage);
        admin.MapPut("/{id:guid}/status", (Guid id, SetMusicTrackStatus input, IMusicTrackStore store, ICurrentUser user, CancellationToken ct) =>
            store.SetStatusAsync(user.UserId!.Value, id, input, ct)).RequireAuthorization(AdminPolicyNames.MusicManage);
        endpoints.MapGet("/api/music-tracks", (IMusicTrackStore store, CancellationToken ct, int page = 1, int pageSize = 25, string? search = null) =>
            store.ListAsync(new(page, pageSize, search, true), ct)).RequireAuthorization().WithTags("Music Library");
        endpoints.MapGet("/api/music-tracks/{id:guid}", async (Guid id, IMusicTrackStore store, CancellationToken ct) =>
        {
            var track = await store.GetAsync(id, ct);
            return track.IsActive ? Results.Ok(track) : Results.NotFound();
        }).RequireAuthorization().WithTags("Music Library");
        return endpoints;
    }
}
