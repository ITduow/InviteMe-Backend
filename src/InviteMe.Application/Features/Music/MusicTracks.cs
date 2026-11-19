using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Models;

namespace InviteMe.Application.Features.Music;

public sealed record MusicTrackDto(Guid Id, string Name, string AudioUrl, bool IsActive, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateMusicTrack(string? Name, string? AudioUrl);
public sealed record UpdateMusicTrack(string? Name, string? AudioUrl, int ExpectedVersion);
public sealed record SetMusicTrackStatus(bool? IsActive, int ExpectedVersion);
public sealed record MusicTrackQuery(int Page = 1, int PageSize = 25, string? Search = null, bool? IsActive = null);
public sealed record InvitationMusicSettings(Guid TrackId, bool Enabled, double Volume, bool Loop);
public sealed record InvitationMusic(Guid TrackId, string Name, string AudioUrl, bool IsActive,
    bool Enabled, double Volume, bool Loop);

public interface IMusicTrackStore
{
    Task<PagedResult<MusicTrackDto>> ListAsync(MusicTrackQuery query, CancellationToken ct);
    Task<MusicTrackDto> GetAsync(Guid id, CancellationToken ct);
    Task<MusicTrackDto> CreateAsync(Guid actorId, CreateMusicTrack input, CancellationToken ct);
    Task<MusicTrackDto> UpdateAsync(Guid actorId, Guid id, UpdateMusicTrack input, CancellationToken ct);
    Task<MusicTrackDto> SetStatusAsync(Guid actorId, Guid id, SetMusicTrackStatus input, CancellationToken ct);
}

public static class MusicTrackValidation
{
    public static bool AudioUrl(string? value) => value is not null && value.Trim().Length is > 0 and <= 2048 &&
        !value.Any(char.IsControl) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public static InvitationMusicSettings? ReadSettings(JsonElement document)
    {
        if (!document.GetProperty("nodes").GetProperty("ROOT").GetProperty("props").TryGetProperty("music", out var music)) return null;
        return new(music.GetProperty("trackId").GetGuid(), music.GetProperty("enabled").GetBoolean(),
            music.GetProperty("volume").GetDouble(), music.GetProperty("loop").GetBoolean());
    }
}

public sealed class CreateMusicTrackValidator : AbstractValidator<CreateMusicTrack>
{
    public CreateMusicTrackValidator()
    {
        RuleFor(x => x.Name).Must(x => x is not null && x.Trim().Length is > 0 and <= 150)
            .WithMessage("Enter a name between 1 and 150 characters.");
        RuleFor(x => x.AudioUrl).Must(MusicTrackValidation.AudioUrl)
            .WithMessage("Enter a public HTTPS audio URL, at most 2048 characters, without credentials or a fragment.");
    }
}
public sealed class UpdateMusicTrackValidator : AbstractValidator<UpdateMusicTrack>
{
    public UpdateMusicTrackValidator()
    {
        RuleFor(x => new CreateMusicTrack(x.Name, x.AudioUrl)).SetValidator(new CreateMusicTrackValidator()).OverridePropertyName("");
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
    }
}
public sealed class SetMusicTrackStatusValidator : AbstractValidator<SetMusicTrackStatus>
{
    public SetMusicTrackStatusValidator()
    { RuleFor(x => x.IsActive).NotNull(); RuleFor(x => x.ExpectedVersion).GreaterThan(0); }
}
public sealed class MusicTrackQueryValidator : AbstractValidator<MusicTrackQuery>
{
    public MusicTrackQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(150);
    }
}
