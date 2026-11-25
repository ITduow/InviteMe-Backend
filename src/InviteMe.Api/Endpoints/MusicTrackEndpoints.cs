using InviteMe.Api.Authorization;
using InviteMe.Application.Features.Music;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace InviteMe.Api.Endpoints;

internal sealed class MusicUploadForm
{
    public string? Name { get; set; }
    public IFormFile? File { get; set; }
    public int ExpectedVersion { get; set; }
}

internal static class MusicTrackEndpoints
{
    public static IEndpointRouteBuilder MapMusicTracks(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin/music-tracks").RequireAuthorization("PlatformAdmin").WithTags("Music Administration");
        admin.MapGet("", (IMusicTrackStore store, CancellationToken ct, int page = 1, int pageSize = 25, string? search = null, bool? isActive = null) =>
            store.ListAsync(new(page, pageSize, search, isActive), ct)).RequireAuthorization(AdminPolicyNames.MusicView);
        admin.MapGet("/{id:guid}", (Guid id, IMusicTrackStore store, CancellationToken ct) => store.GetAsync(id, ct))
            .RequireAuthorization(AdminPolicyNames.MusicView);
        admin.MapPost("", ([FromForm] MusicUploadForm form, IMusicTrackStore store, IMusicFileStorage files,
            ICurrentUser user, ILoggerFactory logs, CancellationToken ct) => Upload(null, form, store, files, user, logs, ct))
            .RequireAuthorization(AdminPolicyNames.MusicManage).DisableAntiforgery().RequireRateLimiting("wedding-cover-upload")
            .WithMetadata(new RequestSizeLimitAttribute(Mp3File.MaxRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = Mp3File.MaxRequestBytes });
        admin.MapPut("/{id:guid}", (Guid id, [FromForm] MusicUploadForm form, IMusicTrackStore store, IMusicFileStorage files,
            ICurrentUser user, ILoggerFactory logs, CancellationToken ct) => Upload(id, form, store, files, user, logs, ct))
            .RequireAuthorization(AdminPolicyNames.MusicManage).DisableAntiforgery().RequireRateLimiting("wedding-cover-upload")
            .WithMetadata(new RequestSizeLimitAttribute(Mp3File.MaxRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = Mp3File.MaxRequestBytes });
        admin.MapPut("/{id:guid}/status", (Guid id, SetMusicTrackStatus input, IMusicTrackStore store, ICurrentUser user, CancellationToken ct) =>
            store.SetStatusAsync(user.UserId!.Value, id, input, ct)).RequireAuthorization(AdminPolicyNames.MusicManage);
        endpoints.MapGet("/api/music-tracks", (IMusicTrackStore store, CancellationToken ct, int page = 1, int pageSize = 25, string? search = null) =>
            store.ListAsync(new(page, pageSize, search, true), ct)).RequireAuthorization().WithTags("Music Library");
        endpoints.MapGet("/api/music-tracks/{id:guid}", async (Guid id, IMusicTrackStore store, CancellationToken ct) =>
        {
            var track = await store.GetAsync(id, ct);
            return track.IsActive ? Results.Ok(track) : Results.NotFound();
        }).RequireAuthorization().WithTags("Music Library");
        endpoints.MapGet("/media/music/{key}", async (string key, IMusicFileStorage files, HttpContext context, CancellationToken ct) =>
        {
            var stream = await files.OpenAsync(key, ct);
            if (stream is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(stream, "audio/mpeg", enableRangeProcessing: true);
        }).AllowAnonymous().WithTags("Music Library");
        return endpoints;
    }

    private static async Task<IResult> Upload(Guid? id, MusicUploadForm form, IMusicTrackStore store,
        IMusicFileStorage files, ICurrentUser user, ILoggerFactory logs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(form.Name) || form.Name.Trim().Length > 150)
            return Results.BadRequest(new { code = "MUSIC_NAME_INVALID" });
        MusicTrackDto? existing = id.HasValue ? await store.GetAsync(id.Value, ct) : null;
        if (existing is not null && existing.Version != form.ExpectedVersion)
            return Results.Conflict(new { code = "MUSIC_VERSION_CONFLICT" });
        if (form.File is null && existing is null) return Results.BadRequest(new { code = "MUSIC_FILE_REQUIRED" });
        StoredMusicFile? uploaded = null;
        if (form.File is { } file)
        {
            if (file.Length is <= 0 or > Mp3File.MaxBytes) return Results.BadRequest(new { code = "MUSIC_FILE_SIZE" });
            if (!string.Equals(Path.GetExtension(file.FileName), ".mp3", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { code = "MUSIC_FILE_INVALID" });
            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream();
            var chunk = new byte[65536];
            int read;
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > Mp3File.MaxBytes) return Results.BadRequest(new { code = "MUSIC_FILE_SIZE" });
                buffer.Write(chunk, 0, read);
            }
            var payload = buffer.ToArray();
            if (!Mp3File.IsValid(payload)) return Results.BadRequest(new { code = "MUSIC_FILE_INVALID" });
            uploaded = await files.SaveAsync(payload, ct);
        }
        try
        {
            var url = uploaded?.AudioUrl ?? existing!.AudioUrl;
            var result = existing is null
                ? await store.CreateAsync(user.UserId!.Value, new(form.Name, url), ct)
                : await store.UpdateAsync(user.UserId!.Value, existing.Id, new(form.Name, url, form.ExpectedVersion), ct);
            // Retain replaced files: published invitation snapshots can still reference them.
            return Results.Json(result, statusCode: existing is null ? 201 : 200);
        }
        catch
        {
            if (uploaded is not null)
            {
                try { await files.DeleteAsync(uploaded.Key, CancellationToken.None); }
                catch (Exception error) { logs.CreateLogger("MusicUpload").LogError(error, "Failed to clean up music object {Key}", uploaded.Key); }
            }
            throw;
        }
    }
}
