using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Invitations;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.Access;

// Frontend origin that serves the guest invitation page (Frontend:Url).
public sealed record GuestLinkOptions(string FrontendUrl);
public sealed record PersonalQrQuery(string Format = "svg", bool Download = false);
public sealed record PersonalQrImage(byte[] Content, string ContentType, string? DownloadName);
public sealed record GuestAccessTarget(Guid GuestId, Guid WeddingId, string RecipientName, string? Honorific);
// One reserved place of the invitation; Table is null until the couple seats it.
// Status is the RSVP place status (PENDING, USED, UNUSED), the same value the RSVP API shows for the place.
public sealed record GuestSeatDto(int Position, string? PersonName, string Status, string? Table, int? SeatNumber);
public sealed record GuestSeatsDto(string RecipientName, string? Honorific, bool Published, IReadOnlyList<GuestSeatDto> Seats);
// Reception view of a place: the place id to confirm check-in (Cohost04) and when it was checked in.
public sealed record ReceptionSeatDto(Guid PlaceId, int Position, Guid? ParticipantId, string? PersonName, string Status, string? Table,
    int? SeatNumber, DateTimeOffset? CheckedInAt);
public sealed record SeatLookupDto(Guid GuestId, string RecipientName, string? Honorific, bool Published, IReadOnlyList<ReceptionSeatDto> Seats);
public sealed record SeatingPublicationDto(bool Published, DateTimeOffset? PublishedAt, Guid? PublishedBy);
public sealed record SeatLookupQuery(string Code);

public interface IGuestAccessStore
{
    // Same rules as the public invitation page: active owner, DRAFT or PUBLISHED wedding. Without weddingId (guest
    // routes) the invitation must also be published; with it (reception lookup) the token must belong to that wedding.
    Task<GuestAccessTarget?> FindAsync(string token, Guid? weddingId, CancellationToken ct);
    Task<IReadOnlyList<ReceptionSeatDto>> SeatsAsync(Guid guestId, CancellationToken ct);
    Task<SeatingPublicationDto> PublicationAsync(Guid weddingId, CancellationToken ct);
    Task<SeatingPublicationDto> SetPublishedAsync(Guid weddingId, Guid? actor, bool published, CancellationToken ct);
}

public sealed class PersonalQrQueryValidator : AbstractValidator<PersonalQrQuery>
{
    public PersonalQrQueryValidator() => RuleFor(x => x.Format).Must(x => x is "svg" or "png").WithMessage("Use svg or png.");
}
public sealed class SeatLookupQueryValidator : AbstractValidator<SeatLookupQuery>
{
    public SeatLookupQueryValidator() => RuleFor(x => x.Code).Must(x => GuestAccessHandler.TryReadToken(x, out _))
        .WithMessage("Scan the guest QR or paste the invitation link / token.");
}

public sealed partial class GuestAccessHandler(IGuestAccessStore store, IQrCodeRenderer renderer, GuestLinkOptions options,
    IWeddingPermissionService permissions, ICurrentUser user, IPlanningChangePublisher changes,
    RequestValidation<PersonalQrQuery> qrValidation, RequestValidation<SeatLookupQuery> lookupValidation)
{
    public const string InvitationPath = "/invite/";

    // Guest06: the QR holds the full invitation link, so a phone camera opens the invitation page
    // and the entrance scanner reads the token from the last path segment.
    public async Task<PersonalQrImage> QrAsync(string token, PersonalQrQuery query, CancellationToken ct)
    {
        await qrValidation.ValidateAsync(query, ct);
        _ = await Find(token, null, ct);
        var link = Link(token);
        var name = query.Download ? $"inviteme-qr.{query.Format}" : null;
        return query.Format == "png"
            ? new(renderer.RenderPng(link), "image/png", name)
            : new(Encoding.UTF8.GetBytes(renderer.RenderSvg(link)), "image/svg+xml", name);
    }

    // Guest07: the guest sees table numbers only after the couple publishes the seating plan.
    // RSVP keeps the seat of a place that is no longer used (the hosts clear it), so the guest page hides its table.
    public async Task<GuestSeatsDto> PublicSeatsAsync(string token, CancellationToken ct)
    {
        var guest = await Find(token, null, ct);
        var published = (await store.PublicationAsync(guest.WeddingId, ct)).Published;
        if (!published) return new(guest.RecipientName, guest.Honorific, false, []);
        var seats = await store.SeatsAsync(guest.GuestId, ct);
        return new(guest.RecipientName, guest.Honorific, true, seats.Select(x => x.Status == RsvpPlaceStatus.Unused
            ? new GuestSeatDto(x.Position, x.PersonName, x.Status, null, null)
            : new GuestSeatDto(x.Position, x.PersonName, x.Status, x.Table, x.SeatNumber)).ToList());
    }

    // Reception counter (Cohost05): staff see the table right after scanning, published or not, also for an
    // UNUSED place, so a guest who declined but still comes can take the seat that is kept for them.
    public async Task<SeatLookupDto> LookupAsync(Guid weddingId, SeatLookupQuery query, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct);
        await lookupValidation.ValidateAsync(query, ct);
        TryReadToken(query.Code, out var token);
        var guest = await Find(token, weddingId, ct);
        var published = (await store.PublicationAsync(weddingId, ct)).Published;
        return new(guest.GuestId, guest.RecipientName, guest.Honorific, published, await store.SeatsAsync(guest.GuestId, ct));
    }

    public async Task<SeatingPublicationDto> PublicationAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.PublicationAsync(weddingId, ct);
    }

    public async Task<SeatingPublicationDto> SetPublishedAsync(Guid weddingId, bool published, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_EDIT, ct);
        var result = await store.SetPublishedAsync(weddingId, user.UserId, published, ct);
        await changes.PublishAsync(weddingId, new(published ? "PlanningSeatingPublished" : "PlanningSeatingUnpublished", weddingId), ct);
        return result;
    }

    public string Link(string token) => options.FrontendUrl.TrimEnd('/') + InvitationPath + token;

    // Accepts the bare token or any http(s) link whose last path segment is the token.
    public static bool TryReadToken(string? code, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(code) || code.Length > 2048) return false;
        var candidate = code.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            candidate = uri.AbsolutePath.TrimEnd('/').Split('/')[^1];
        if (!TokenPattern().IsMatch(candidate)) return false;
        token = candidate.ToLowerInvariant();
        return true;
    }

    private async Task<GuestAccessTarget> Find(string token, Guid? weddingId, CancellationToken ct) =>
        token.Length == 64 && token.All(char.IsAsciiHexDigitLower) ? await store.FindAsync(token, weddingId, ct) ?? throw Missing() : throw Missing();

    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound,
        "INVITATION_UNAVAILABLE", "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex TokenPattern();
}
