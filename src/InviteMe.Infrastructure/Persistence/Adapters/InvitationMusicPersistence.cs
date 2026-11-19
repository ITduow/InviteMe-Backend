using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Music;
using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

internal static class InvitationMusicPersistence
{
    public static async Task<InvitationMusic?> Resolve(InviteMeDbContext db, JsonElement document, CancellationToken ct,
        bool lockTrack = false, Guid? retainedTrack = null)
    {
        if (!InvitationDesignHandler.ValidDocument(document) || MusicTrackValidation.ReadSettings(document) is not { } settings) return null;
        var tracks = db.Set<MusicTrack>().AsNoTracking();
        if (lockTrack) tracks = db.Set<MusicTrack>().FromSqlInterpolated($"SELECT * FROM inviteme.music_tracks WHERE id={settings.TrackId} FOR SHARE").AsNoTracking();
        var track = await tracks.SingleOrDefaultAsync(x => x.Id == settings.TrackId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "MUSIC_NOT_FOUND", "The selected music track is unavailable.");
        if (lockTrack && !track.IsActive && retainedTrack != track.Id)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "MUSIC_TRACK_INACTIVE", "This track is hidden. Choose another track.");
        return new(track.Id, track.Name, track.AudioUrl, track.IsActive, settings.Enabled, settings.Volume, settings.Loop);
    }

    public static Guid? TrackId(string? document)
    {
        if (document is null) return null;
        var json = JsonSerializer.Deserialize<JsonElement>(document);
        return InvitationDesignHandler.ValidDocument(json) ? MusicTrackValidation.ReadSettings(json)?.TrackId : null;
    }

    public static InvitationMusic? Published(JsonElement snapshot) =>
        snapshot.TryGetProperty("music", out var music) && music.ValueKind != JsonValueKind.Null
            ? music.Deserialize<InvitationMusic>(PublicationJson.Web) : null;
}
