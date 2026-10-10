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
    DateTimeOffset CreatedAt);

public sealed record AddCoHostInput(string Email, IReadOnlyList<string> Permissions);

public sealed record UpdateCoHostPermissionsInput(IReadOnlyList<string> Permissions);

public sealed class AddCoHostInputValidator : AbstractValidator<AddCoHostInput>
{
    public AddCoHostInputValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).NotEmpty().Must(BeValidPermissionCode).WithMessage("Invalid permission code.");
        RuleFor(x => x.Permissions).Must(HaveNoDuplicates).WithMessage("Permissions list cannot have duplicates.");
    }

    private static bool BeValidPermissionCode(string code) =>
        Enum.TryParse<WeddingPermission>(code, false, out _);

    private static bool HaveNoDuplicates(IReadOnlyList<string>? permissions) =>
        permissions is null || permissions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == permissions.Count;
}

public sealed class UpdateCoHostPermissionsInputValidator : AbstractValidator<UpdateCoHostPermissionsInput>
{
    public UpdateCoHostPermissionsInputValidator()
    {
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).NotEmpty().Must(BeValidPermissionCode).WithMessage("Invalid permission code.");
        RuleFor(x => x.Permissions).Must(HaveNoDuplicates).WithMessage("Permissions list cannot have duplicates.");
    }

    private static bool BeValidPermissionCode(string code) =>
        Enum.TryParse<WeddingPermission>(code, false, out _);

    private static bool HaveNoDuplicates(IReadOnlyList<string>? permissions) =>
        permissions is null || permissions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == permissions.Count;
}

public sealed class CoHostHandler(
    ICoHostStore store,
    ICurrentUser current,
    RequestValidation<AddCoHostInput> addValidation,
    RequestValidation<UpdateCoHostPermissionsInput> updateValidation)
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
        await addValidation.ValidateAsync(input, ct);
        var ownerId = await EnsureOwnerAsync(weddingId, callerId, ct);

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
        await updateValidation.ValidateAsync(input, ct);
        await EnsureOwnerAsync(weddingId, callerId, ct);

        var permissions = ParsePermissions(input.Permissions);
        return await store.UpdatePermissionsAsync(weddingId, coHostUserId, permissions, callerId, ct);
    }

    public async Task RevokeAsync(Guid weddingId, Guid coHostUserId, CancellationToken ct)
    {
        var callerId = RequireCallerId();
        await EnsureOwnerAsync(weddingId, callerId, ct);
        await store.RevokeAsync(weddingId, coHostUserId, callerId, ct);
    }

    private Guid RequireCallerId() =>
        current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");

    private async Task<Guid> EnsureOwnerAsync(Guid weddingId, Guid callerId, CancellationToken ct)
    {
        var ownerId = await store.GetWeddingOwnerIdAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding not found.");

        if (ownerId != callerId)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage co-hosts.");

        return ownerId;
    }

    private static IReadOnlyList<WeddingPermission> ParsePermissions(IReadOnlyList<string> permissionCodes)
    {
        var list = new List<WeddingPermission>(permissionCodes.Count);
        foreach (var code in permissionCodes)
        {
            if (Enum.TryParse<WeddingPermission>(code, false, out var perm))
                list.Add(perm);
        }
        return list;
    }
}
