using System.Text;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Ports.Invitations;

namespace InviteMe.Application.Features.Invitations.PersonalQr;

public sealed record PersonalQrQuery(string Format = "svg", bool Download = false);
public sealed record PersonalQrImage(byte[] Content, string ContentType, string? DownloadName);
// Frontend origin that serves the public invitation page (Frontend:Url).
public sealed record PersonalQrOptions(string FrontendUrl);

public sealed class PersonalQrQueryValidator : AbstractValidator<PersonalQrQuery>
{
    public PersonalQrQueryValidator() => RuleFor(x => x.Format).Must(x => x is "svg" or "png").WithMessage("Use svg or png.");
}

// Guest06: the QR holds the personal invitation link, so a phone camera opens the invitation page
// and the entrance scanner (QrPayload) reads the token from the last path segment.
public sealed class PersonalQrHandler(IInvitationStore invitations, IQrCodeRenderer renderer, PersonalQrOptions options,
    RequestValidation<PersonalQrQuery> validation)
{
    public async Task<PersonalQrImage> RenderAsync(string token, PersonalQrQuery query, CancellationToken ct)
    {
        await validation.ValidateAsync(query, ct);
        // Same rules as opening the invitation: unknown, revoked or expired tokens and closed weddings return 404.
        await invitations.PublicAsync(token, false, ct);
        var link = Link(token);
        var name = query.Download ? $"inviteme-qr.{query.Format}" : null;
        return query.Format == "png"
            ? new(renderer.RenderPng(link), "image/png", name)
            : new(Encoding.UTF8.GetBytes(renderer.RenderSvg(link)), "image/svg+xml", name);
    }

    public string Link(string token) => options.FrontendUrl.TrimEnd('/') + InvitationLinks.PublicPath(token);
}
