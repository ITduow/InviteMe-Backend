using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

public sealed class PlanningInvitationLinkTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinksRequireInvitationSendInBothListAndDetail(bool canSend)
    {
        var store = new Store();
        var handler = new GuestPlanningHandler(store, new Permissions(canSend),
            new RequestValidation<PlanningImport>([]), new RequestValidation<PlanningGuestQuery>([]),
            new RequestValidation<UpdatePlanningGuest>([]), new RequestValidation<CreatePlanningGuest>([]),
            new CurrentUser(Guid.NewGuid()), new Publisher());
        var weddingId = Guid.NewGuid();
        var page = await handler.ListAsync(weddingId, new(), default);
        var detail = await handler.DetailAsync(weddingId, store.Guest.Id, default);
        Assert.Equal(canSend ? store.Guest.InvitationPath : null, page.Items[0].InvitationPath);
        Assert.Equal(canSend ? store.Guest.InvitationPath : null, detail.Guest.InvitationPath);
        Assert.Equal("An", page.Items[0].RecipientName);
    }

    private sealed class Permissions(bool canSend) : IWeddingPermissionService
    {
        public Task RequireAsync(Guid id, WeddingPermission permission, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> HasPermissionAsync(Guid id, WeddingPermission permission, CancellationToken ct) =>
            Task.FromResult(permission == WeddingPermission.INVITATION_SEND && canSend);
    }

    private sealed record CurrentUser(Guid? UserId) : ICurrentUser;

    private sealed class Publisher : IPlanningChangePublisher
    {
        public Task PublishAsync(Guid id, PlanningChange change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Store : IGuestPlanningStore
    {
        public PlanningGuestDto Guest { get; } = new(Guid.NewGuid(), 1, "An", null, "INDIVIDUAL", 1,
            "BRIDE", null, null, null, InvitationPath: "/invite/" + new string('a', 64));
        public Task<PlanningGuestPage> ListAsync(Guid id, int page, int size, string? search, string? side, Guid? groupId, CancellationToken ct) =>
            Task.FromResult(new PlanningGuestPage([Guest], page, size, 1));
        public Task<IReadOnlyList<PlanningGroupDto>> ListGroupsAsync(Guid weddingId, CancellationToken ct,
            IReadOnlySet<Guid>? groupScope = null) => Task.FromResult<IReadOnlyList<PlanningGroupDto>>([]);
        public Task<PlanningGuestDetail> DetailAsync(Guid id, Guid guestId, CancellationToken ct) =>
            Task.FromResult(new PlanningGuestDetail(Guest, []));
        public Task<PlanningImportOutcome> ImportAsync(Guid id, Guid key, PlanningImport input, Guid actorId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, Guid key, CreatePlanningGuest input,
            Guid actorId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null) => throw new NotSupportedException();
        public Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion,
            Guid actorId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null) => throw new NotSupportedException();
    }
}
