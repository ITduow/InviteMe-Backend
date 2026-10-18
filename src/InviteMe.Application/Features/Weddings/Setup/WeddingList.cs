using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Weddings.Setup;

public sealed record WeddingListQuery(int Page = 1, int PageSize = 20, string? Search = null, string Sort = "weddingDate");
public sealed record WeddingSummaryDto(Guid Id, string Title, DateOnly? WeddingDate, long ParticipantCount, int MaxCapacity, int Version);

public interface IWeddingListReader
{
    Task<PagedResult<WeddingSummaryDto>> ListAsync(Guid userId, WeddingListQuery query, CancellationToken ct);
}

public sealed class WeddingListQueryValidator : AbstractValidator<WeddingListQuery>
{
    public WeddingListQueryValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Sort).Must(x => x is "weddingDate" or "title" or "createdAt")
            .WithMessage("Sort must be weddingDate, title, or createdAt.");
    }
}

public sealed class WeddingListHandler(IWeddingListReader reader, ICurrentUser currentUser,
    RequestValidation<WeddingListQuery> validation)
{
    public async Task<PagedResult<WeddingSummaryDto>> ListAsync(WeddingListQuery query, CancellationToken ct)
    {
        await validation.ValidateAsync(query, ct);
        var userId = currentUser.UserId is { } id && id != Guid.Empty ? id :
            throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
        return await reader.ListAsync(userId, query, ct);
    }
}
