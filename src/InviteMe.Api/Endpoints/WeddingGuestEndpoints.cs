using InviteMe.Application.Features.Guests.ListWeddingGuests;

namespace InviteMe.Api.Endpoints;

internal static class WeddingGuestEndpoints
{
    public static IEndpointRouteBuilder MapWeddingGuests(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guests")
            .RequireAuthorization()
            .WithTags("Wedding Guests");

        group.MapGet("", (
            Guid weddingId,
            ListWeddingGuestsHandler handler,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20,
            string? search = null,
            string recordStatus = "ACTIVE",
            string sort = "fullName:asc",
            bool withoutInvitation = false) =>
        {
            var query = new ListWeddingGuestsQuery(page, pageSize, search, recordStatus, sort, withoutInvitation);
            return handler.HandleAsync(weddingId, query, ct);
        });

        return endpoints;
    }
}
