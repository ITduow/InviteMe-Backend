using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Audit;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;

namespace InviteMe.Application.Features.Audit;

public sealed record AuditLogDto(
    Guid Id,
    string ActorType,
    Guid? ActorUserId,
    string? ActorEmail,
    string? ActorName,
    Guid? ActorGuestId,
    string Action,
    string EntityType,
    Guid? EntityId,
    JsonElement? Metadata,
    DateTimeOffset CreatedAt);

public sealed record AuditLogQuery(
    string? Action = null,
    string? EntityType = null,
    Guid? EntityId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed class AuditLogQueryValidator : AbstractValidator<AuditLogQuery>
{
    public AuditLogQueryValidator()
    {
        RuleFor(x => x.Action).MaximumLength(100);
        RuleFor(x => x.EntityType).MaximumLength(100);
        RuleFor(x => x.EntityId).NotEqual(Guid.Empty).When(x => x.EntityId.HasValue);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From!.Value)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("'To' must not be earlier than 'From'.");
    }
}

public sealed class AuditHandler(
    IAuditReader reader,
    IWeddingAccessReader access,
    ICurrentUser current,
    RequestValidation<AuditLogQuery> queryValidation,
    RequestValidation<PageRequest> pageValidation)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(Guid weddingId, AuditLogQuery filter, PageRequest page, CancellationToken ct)
    {
        var callerId = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var found = await access.FindAsync(weddingId, callerId, ct);
        if (found is null || found.OwnerId != callerId)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can view the audit history.");

        await queryValidation.ValidateAsync(filter, ct);
        await pageValidation.ValidateAsync(page, ct);
        return await reader.ListAsync(weddingId, filter, page, ct);
    }
}
