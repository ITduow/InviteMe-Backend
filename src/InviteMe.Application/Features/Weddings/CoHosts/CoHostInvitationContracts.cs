using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Weddings;
using System.Security.Cryptography;
using System.Text;

namespace InviteMe.Application.Features.Weddings.CoHosts;

public sealed record CoHostPermissionScopeInput(string Permission, IReadOnlyList<Guid> GroupIds, bool WeddingWide = false);
public sealed record CreateCoHostInvitationInput(string Email, IReadOnlyList<CoHostPermissionScopeInput> Grants);
public sealed record CoHostInvitationGrantDto(string Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds);
public sealed record CoHostInvitationDto(Guid Id, Guid WeddingId, string Email, string Status, DateTimeOffset ExpiresAt,
    string WeddingTitle, IReadOnlyList<CoHostInvitationGrantDto> Grants);
public sealed record CoHostInvitationCreated(Guid Id, string Email, string Status, DateTimeOffset ExpiresAt);

public interface ICoHostInvitationStore
{
    Task<Guid?> GetWeddingOwnerIdAsync(Guid weddingId, CancellationToken ct);
    Task<string?> GetWeddingTitleAsync(Guid weddingId, CancellationToken ct);
    Task<Guid> CreateAsync(Guid weddingId, string email, string tokenHash, Guid actorId, DateTimeOffset expiresAt,
        IReadOnlyList<(WeddingPermission Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds)> grants, CancellationToken ct);
    Task MarkSentAsync(Guid invitationId, CancellationToken ct);
    Task<CoHostInvitationDto?> GetForAcceptAsync(string tokenHash, Guid userId, CancellationToken ct);
    Task AcceptAsync(string tokenHash, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<CoHostInvitationDto>> ListAsync(Guid weddingId, CancellationToken ct);
    Task RevokeAsync(Guid weddingId, Guid invitationId, Guid actorId, CancellationToken ct);
}

public interface ICoHostInvitationEmailSender
{
    Task SendAsync(string email, string weddingTitle, string token, DateTimeOffset expiresAt, CancellationToken ct);
}

public sealed class CreateCoHostInvitationValidator : AbstractValidator<CreateCoHostInvitationInput>
{
    private static readonly HashSet<WeddingPermission> Assignable = [WeddingPermission.GUEST_VIEW, WeddingPermission.GUEST_EDIT,
        WeddingPermission.RSVP_VIEW, WeddingPermission.SEATING_VIEW, WeddingPermission.SEATING_EDIT, WeddingPermission.CHECKIN_MANAGE];
    public CreateCoHostInvitationValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Grants).NotNull().NotEmpty();
        RuleForEach(x => x.Grants).ChildRules(g =>
        {
            g.RuleFor(x => x.Permission).Must(CoHostPermissionCodes.IsAssignable);
            g.RuleFor(x => x.GroupIds).NotNull();
            g.RuleFor(x => x.GroupIds).Must(x => x is not null && x.Distinct().Count() == x.Count && x.All(id => id != Guid.Empty));
            g.RuleFor(x => x).Must(x => x.GroupIds is not null && (x.WeddingWide ? x.GroupIds.Count == 0 : x.GroupIds.Count > 0))
                .WithMessage("Choose either wedding-wide scope or one or more guest groups.");
            g.RuleFor(x => x).Must(x => x.Permission != nameof(WeddingPermission.CHECKIN_MANAGE) ||
                                        (x.WeddingWide && x.GroupIds is { Count: 0 }))
                .WithMessage("CHECKIN_MANAGE must be granted for the whole wedding, without guest-group scope.");
        });
        RuleFor(x => x.Grants).Must(x => x is null || x.Select(g => g.Permission).Distinct(StringComparer.OrdinalIgnoreCase).Count() == x.Count)
            .WithMessage("Permission grants must not be duplicated.");
    }
}

public sealed class CoHostInvitationHandler(ICoHostInvitationStore store, ICoHostInvitationEmailSender email,
    ICurrentUser current, RequestValidation<CreateCoHostInvitationInput> validation, InviteMe.Application.Features.Billing.IWeddingEntitlements plans)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    public async Task<CoHostInvitationCreated> CreateAsync(Guid weddingId, CreateCoHostInvitationInput input, CancellationToken ct)
    {
        var actor = RequireUser();
        await EnsureOwnerAsync(weddingId, actor, ct);
        await validation.ValidateAsync(input, ct);
        await plans.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.CoHosts, 1, ct);
        var grants = input.Grants.Select(x => (Enum.Parse<WeddingPermission>(x.Permission), x.WeddingWide, x.GroupIds)).ToArray();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTimeOffset.UtcNow.Add(Lifetime);
        var id = await store.CreateAsync(weddingId, input.Email.Trim(), Hash(token), actor, expires, grants, ct);
        var title = await store.GetWeddingTitleAsync(weddingId, ct) ?? "InviteMe wedding";
        try
        {
            await email.SendAsync(input.Email.Trim(), title, token, expires, ct);
            await store.MarkSentAsync(id, ct);
            return new(id, input.Email.Trim(), "SENT", expires);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Keep the pending invitation so the owner can resend; never log or return the bearer token.
            return new(id, input.Email.Trim(), "PENDING", expires);
        }
    }

    public async Task<IReadOnlyList<CoHostInvitationDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        await EnsureOwnerAsync(weddingId, RequireUser(), ct);
        return await store.ListAsync(weddingId, ct);
    }

    public async Task<CoHostInvitationDto> GetAsync(string token, CancellationToken ct)
    {
        ValidateToken(token);
        var result = await store.GetForAcceptAsync(Hash(token), RequireUser(), ct);
        return result ?? throw InvalidToken();
    }

    public async Task AcceptAsync(string token, CancellationToken ct)
    {
        ValidateToken(token);
        await store.AcceptAsync(Hash(token), RequireUser(), ct);
    }

    public async Task RevokeAsync(Guid weddingId, Guid invitationId, CancellationToken ct) =>
        await store.RevokeAsync(weddingId, invitationId, RequireUser(), ct);

    private async Task EnsureOwnerAsync(Guid weddingId, Guid userId, CancellationToken ct)
    {
        if (await store.GetWeddingOwnerIdAsync(weddingId, ct) != userId)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage co-host invitations.");
    }
    private Guid RequireUser() => current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static void ValidateToken(string? token)
    {
        if (token is null || token.Length is < 40 or > 100 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw InvalidToken();
    }
    private static ApplicationProblemException InvalidToken() => new(ProblemKind.NotFound, "COHOST_INVITATION_NOT_FOUND", "Invitation is invalid, expired, or not available to this account.");
}
