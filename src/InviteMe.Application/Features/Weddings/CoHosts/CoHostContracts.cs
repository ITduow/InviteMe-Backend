using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Weddings.CoHosts;

public sealed record CoHostDto(
    Guid Id,
    Guid UserId,
    string Email,
    string FullName,
    string Status,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? JoinedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CoHostPermissionScopeDto>? Scopes = null);

public sealed record CoHostPermissionScopeDto(string Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds);

public sealed record AddCoHostInput(string Email, IReadOnlyList<string> Permissions);

public sealed record UpdateCoHostPermissionsInput(IReadOnlyList<string> Permissions);
public sealed record UpdateCoHostScopesInput(IReadOnlyList<CoHostPermissionScopeInput> Grants);

internal static class CoHostPermissionCodes
{
    private static readonly HashSet<WeddingPermission> Assignable = [WeddingPermission.GUEST_VIEW, WeddingPermission.GUEST_EDIT,
        WeddingPermission.RSVP_VIEW, WeddingPermission.SEATING_VIEW, WeddingPermission.SEATING_EDIT, WeddingPermission.CHECKIN_MANAGE];
    private static readonly IReadOnlyDictionary<string, WeddingPermission> Known =
        Enum.GetValues<WeddingPermission>().ToDictionary(x => x.ToString(), StringComparer.Ordinal);

    public static bool TryParse(string? code, out WeddingPermission permission)
    {
        permission = default;
        return code is not null && Known.TryGetValue(code, out permission);
    }

    public static bool IsKnown(string? code) => TryParse(code, out _);
    public static bool IsAssignable(string? code) => TryParse(code, out var permission) && Assignable.Contains(permission);

    public static bool HasNoDuplicates(IReadOnlyList<string>? codes) =>
        codes is null || codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Count;
}

public sealed class AddCoHostInputValidator : AbstractValidator<AddCoHostInput>
{
    public AddCoHostInputValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).Must(CoHostPermissionCodes.IsAssignable).WithMessage("Invalid or non-delegable permission code.");
        RuleFor(x => x.Permissions).Must(CoHostPermissionCodes.HasNoDuplicates).WithMessage("Permissions list cannot have duplicates.");
    }
}

public sealed class UpdateCoHostPermissionsInputValidator : AbstractValidator<UpdateCoHostPermissionsInput>
{
    public UpdateCoHostPermissionsInputValidator()
    {
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).Must(CoHostPermissionCodes.IsAssignable).WithMessage("Invalid or non-delegable permission code.");
        RuleFor(x => x.Permissions).Must(CoHostPermissionCodes.HasNoDuplicates).WithMessage("Permissions list cannot have duplicates.");
    }
}

public sealed class UpdateCoHostScopesInputValidator : AbstractValidator<UpdateCoHostScopesInput>
{
    public UpdateCoHostScopesInputValidator()
    {
        RuleFor(x => x.Grants).NotNull();
        RuleForEach(x => x.Grants).ChildRules(g =>
        {
            g.RuleFor(x => x.Permission).Must(CoHostPermissionCodes.IsAssignable);
            g.RuleFor(x => x.GroupIds).NotNull().Must(x => x is not null && x.Distinct().Count() == x.Count && x.All(id => id != Guid.Empty));
            g.RuleFor(x => x).Must(x => x.WeddingWide ? x.GroupIds.Count == 0 : x.GroupIds.Count > 0);
            g.RuleFor(x => x).Must(x => x.Permission != nameof(WeddingPermission.CHECKIN_MANAGE) ||
                                        (x.WeddingWide && x.GroupIds.Count == 0))
                .WithMessage("CHECKIN_MANAGE must be granted for the whole wedding, without guest-group scope.");
        });
        RuleFor(x => x.Grants).Must(x => x.Select(g => g.Permission).Distinct(StringComparer.Ordinal).Count() == x.Count);
    }
}

public sealed class CoHostHandler(
    ICoHostStore store,
    ICurrentUser current,
    RequestValidation<AddCoHostInput> addValidation,
    RequestValidation<UpdateCoHostPermissionsInput> updateValidation,
    RequestValidation<UpdateCoHostScopesInput> scopesValidation)
{
    public async Task<IReadOnlyList<CoHostDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        return await store.ListAsync(weddingId, ct);
    }

    public async Task<CoHostDto> GetByIdAsync(Guid weddingId, Guid coHostUserId, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        return await store.GetByIdAsync(weddingId, coHostUserId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");
    }

    public async Task<CoHostDto> AddAsync(Guid weddingId, AddCoHostInput input, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        var ownerId = await EnsureOwnerAsync(weddingId, callerId, ct);
        await addValidation.ValidateAsync(input, ct);

        var targetUserId = await store.FindUserIdByEmailAsync(input.Email.Trim(), ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "USER_NOT_FOUND", "No user found with the provided email.");

        if (targetUserId == ownerId)
            throw new ApplicationProblemException(ProblemKind.Conflict, "OWNER_CANNOT_BE_COHOST", "The wedding owner cannot be added as a co-host.");

        var permissions = ParsePermissions(input.Permissions);
        return await store.AddOrReactivateAsync(weddingId, targetUserId, permissions, callerId, ct);
    }

    public async Task<CoHostDto> UpdatePermissionsAsync(Guid weddingId, Guid coHostUserId, UpdateCoHostPermissionsInput input, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        await updateValidation.ValidateAsync(input, ct);

        var permissions = ParsePermissions(input.Permissions);
        return await store.UpdatePermissionsAsync(weddingId, coHostUserId, permissions, callerId, ct);
    }

    public async Task<CoHostDto> UpdateScopesAsync(Guid weddingId, Guid coHostUserId, UpdateCoHostScopesInput input, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        await scopesValidation.ValidateAsync(input, ct);
        return await store.UpdateScopesAsync(weddingId, coHostUserId, input.Grants.Select(x =>
            (Enum.Parse<WeddingPermission>(x.Permission), x.WeddingWide, x.GroupIds)).ToArray(), callerId, ct);
    }

    public async Task RevokeAsync(Guid weddingId, Guid coHostUserId, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        await store.RevokeAsync(weddingId, coHostUserId, callerId, ct);
    }

    private Guid RequireCallerId() =>
        current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");

    private async Task<Guid> EnsureOwnerAsync(Guid weddingId, Guid callerId, CancellationToken ct)
    {
        var ownerId = await store.GetWeddingOwnerIdAsync(weddingId, ct);
        if (ownerId != callerId)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage co-hosts.");

        return callerId;
    }

    private static IReadOnlyList<WeddingPermission> ParsePermissions(IReadOnlyList<string> permissionCodes)
    {
        var list = new List<WeddingPermission>(permissionCodes.Count);
        foreach (var code in permissionCodes)
        {
            if (CoHostPermissionCodes.TryParse(code, out var permission))
                list.Add(permission);
        }
        return list;
    }
}
