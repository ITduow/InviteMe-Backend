using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Guests;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.ListWeddingGuests;

public sealed record ListWeddingGuestsQuery(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string RecordStatus = "ACTIVE",
    string Sort = "fullName:asc",
    bool WithoutInvitation = false)
{
    public WeddingGuestFilter ToFilter() =>
        new(Page, PageSize, Search?.Trim(), RecordStatus, Sort, WithoutInvitation);
}

public sealed class ListWeddingGuestsQueryValidator : AbstractValidator<ListWeddingGuestsQuery>
{
    private static readonly string[] AllowedRecordStatuses = ["ACTIVE", "ARCHIVED", "ALL"];
    private static readonly string[] AllowedSorts = ["fullName:asc", "guestCode:asc", "createdAt:desc"];

    public ListWeddingGuestsQueryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .Must((x, page) => (long)(page - 1) * x.PageSize <= int.MaxValue)
            .WithMessage("Offset must fit within a 32-bit integer.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100);

        RuleFor(x => x.Search)
            .MaximumLength(200)
            .When(x => !string.IsNullOrEmpty(x.Search));

        RuleFor(x => x.RecordStatus)
            .Must(x => AllowedRecordStatuses.Contains(x))
            .WithMessage("RecordStatus must be ACTIVE, ARCHIVED, or ALL.");

        RuleFor(x => x.Sort)
            .Must(x => AllowedSorts.Contains(x))
            .WithMessage("Sort must be fullName:asc, guestCode:asc, or createdAt:desc.");
    }
}

public sealed class ListWeddingGuestsHandler(
    IWeddingGuestReader reader,
    IWeddingPermissionService permissions,
    RequestValidation<ListWeddingGuestsQuery> validation)
{
    public async Task<WeddingGuestPage> HandleAsync(Guid weddingId, ListWeddingGuestsQuery query, CancellationToken cancellationToken)
    {
        await validation.ValidateAsync(query, cancellationToken);
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, cancellationToken);
        return await reader.ListAsync(weddingId, query.ToFilter(), cancellationToken);
    }
}
