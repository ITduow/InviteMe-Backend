using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Music;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class MusicTrackStore(InviteMeDbContext db, TimeProvider clock) : IMusicTrackStore
{
    public async Task<PagedResult<MusicTrackDto>> ListAsync(MusicTrackQuery input, CancellationToken ct)
    {
        new MusicTrackQueryValidator().ValidateAndThrow(input);
        var query = db.Set<MusicTrack>().AsNoTracking();
        if (input.IsActive is { } active) query = query.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var search = input.Search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(search));
        }
        var count = await query.CountAsync(ct);
        var skip = (long)(input.Page - 1) * input.PageSize;
        if (skip >= count) return new([], input.Page, input.PageSize, count);
        var rows = await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((int)skip).Take(input.PageSize).ToListAsync(ct);
        return new(rows.Select(Dto).ToArray(), input.Page, input.PageSize, count);
    }

    public async Task<MusicTrackDto> GetAsync(Guid id, CancellationToken ct) =>
        Dto(await db.Set<MusicTrack>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing());

    public async Task<MusicTrackDto> CreateAsync(Guid actorId, CreateMusicTrack input, CancellationToken ct)
    {
        new CreateMusicTrackValidator().ValidateAndThrow(input);
        var track = MusicTrack.Create(input.Name!, input.AudioUrl!, clock.GetUtcNow());
        db.Add(track);
        await Save(track, actorId, "MUSIC_TRACK_CREATED", ct);
        return Dto(track);
    }

    public async Task<MusicTrackDto> UpdateAsync(Guid actorId, Guid id, UpdateMusicTrack input, CancellationToken ct)
    {
        new UpdateMusicTrackValidator().ValidateAndThrow(input);
        var track = await Find(id, input.ExpectedVersion, ct);
        track.Update(input.Name!, input.AudioUrl!, clock.GetUtcNow());
        await Save(track, actorId, "MUSIC_TRACK_UPDATED", ct);
        return Dto(track);
    }

    public async Task<MusicTrackDto> SetStatusAsync(Guid actorId, Guid id, SetMusicTrackStatus input, CancellationToken ct)
    {
        new SetMusicTrackStatusValidator().ValidateAndThrow(input);
        var track = await Find(id, input.ExpectedVersion, ct);
        if (track.IsActive == input.IsActive!.Value) return Dto(track);
        track.SetActive(input.IsActive.Value, clock.GetUtcNow());
        await Save(track, actorId, "MUSIC_TRACK_STATUS_CHANGED", ct);
        return Dto(track);
    }

    private async Task<MusicTrack> Find(Guid id, int version, CancellationToken ct)
    {
        var track = await db.Set<MusicTrack>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        if (track.Version != version) throw Conflict();
        return track;
    }

    private async Task Save(MusicTrack track, Guid actorId, string action, CancellationToken ct)
    {
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "music_tracks", track.Id, action));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_music_tracks_audio_url" })
        { throw new ApplicationProblemException(ProblemKind.Conflict, "MUSIC_URL_EXISTS", "This audio URL is already in the library."); }
    }

    public static MusicTrackDto Dto(MusicTrack x) => new(x.Id, x.Name, x.AudioUrl, x.IsActive, x.Version, x.CreatedAt, x.UpdatedAt);
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "MUSIC_NOT_FOUND", "Music track is unavailable.");
    private static ApplicationProblemException Conflict() => new(ProblemKind.Conflict, "MUSIC_VERSION_CONFLICT", "This track changed. Refresh before editing again.");
}
