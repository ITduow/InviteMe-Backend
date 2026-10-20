using System.Security.Claims;
using InviteMe.Api.Hubs;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class SeatingDragHubTests
{
    [Fact]
    public async Task PointerPacketsRequireTheOwningLeaseAndAreRateLimitedWithoutReadingTheDatabase()
    {
        using var factory = new ApiFactory(); using var scope = factory.Services.CreateScope();
        var account = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var leases = new SeatingDragLeaseService(TimeProvider.System); var plan = new Plan();
        var owner = Hub("a", Guid.NewGuid(), new Permissions(), leases, plan, account);
        var other = Hub("b", Guid.NewGuid(), new Permissions(), leases, plan, account);
        var lease = await owner.StartSeatingDrag(plan.Wedding, [plan.Place]);
        var reads = plan.Reads;
        await owner.MoveSeatingPointer(plan.Wedding, lease.Token, "board", .2, .3, 1);
        for (var i = 0; i < 10; i++) await owner.MoveSeatingPointer(plan.Wedding, lease.Token, "queue", .5, .6, i + 2);
        Assert.Equal(reads, plan.Reads);
        var pointer = Assert.Single(leases.DrainPointers());
        Assert.Equal(11, pointer.Sequence);
        Assert.Equal(lease.DragId, pointer.DragId);
        Assert.Equal("a", pointer.ConnectionId);
        await Assert.ThrowsAsync<HubException>(() => other.MoveSeatingPointer(plan.Wedding, lease.Token, "board", .2, .3, 20));
        await Assert.ThrowsAsync<HubException>(() => owner.MoveSeatingPointer(plan.Wedding, lease.Token, "board", double.NaN, .3, 20));
        await Assert.ThrowsAsync<HubException>(() => owner.MoveSeatingPointer(plan.Wedding, lease.Token, "unknown", .2, .3, 20));
        await owner.EndSeatingDrag(plan.Wedding, lease.Token);
        Assert.Empty(leases.DrainPointers());
        await Assert.ThrowsAsync<HubException>(() => owner.MoveSeatingPointer(plan.Wedding, lease.Token, "board", .2, .3, 20));
    }
    [Fact]
    public async Task TwoConnectionsContendForTheSamePlaceAndDisconnectReleasesIt()
    {
        using var factory = new ApiFactory(); using var scope = factory.Services.CreateScope();
        var account = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var leases = new SeatingDragLeaseService(TimeProvider.System);
        var plan = new Plan(); var user = Guid.NewGuid();
        var a = Hub("a", user, new Permissions(), leases, plan, account);
        var b = Hub("b", user, new Permissions(), leases, plan, account);
        var lease = await a.StartSeatingDrag(plan.Wedding, [plan.Place]);
        Assert.Equal("SEATING_PLACE_LOCKED", (await Assert.ThrowsAsync<HubException>(() => b.StartSeatingDrag(plan.Wedding, [plan.Place]))).Message);
        Assert.Equal("SEATING_DRAG_EXPIRED", (await Assert.ThrowsAsync<HubException>(() => b.RenewSeatingDrag(plan.Wedding, lease.Token))).Message);
        await a.OnDisconnectedAsync(null);
        Assert.Empty((await b.GetSeatingDrags(plan.Wedding)).Leases);
        Assert.NotEqual(lease.Token, (await b.StartSeatingDrag(plan.Wedding, [plan.Place])).Token);
    }

    [Fact]
    public async Task ViewersCanSubscribeButCannotAcquireAndOutsidersCannotReadSnapshots()
    {
        using var factory = new ApiFactory(); using var scope = factory.Services.CreateScope();
        var account = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        var leases = new SeatingDragLeaseService(TimeProvider.System); var plan = new Plan();
        var viewer = Hub("viewer", Guid.NewGuid(), new Permissions { Edit = false }, leases, plan, account);
        Assert.Empty((await viewer.GetSeatingDrags(plan.Wedding)).Leases);
        Assert.Equal("SEATING_ACCESS_DENIED", (await Assert.ThrowsAsync<HubException>(() => viewer.StartSeatingDrag(plan.Wedding, [plan.Place]))).Message);
        var outsider = Hub("outsider", Guid.NewGuid(), new Permissions { View = false }, leases, plan, account);
        await Assert.ThrowsAsync<HubException>(() => outsider.GetSeatingDrags(plan.Wedding));
        var owner = Hub("owner", Guid.NewGuid(), new Permissions(), leases, plan, account);
        await Assert.ThrowsAsync<HubException>(() => owner.StartSeatingDrag(plan.Wedding, [Guid.NewGuid()]));
        Assert.Empty(leases.Snapshot(plan.Wedding).Leases);
    }

    private static WeddingHub Hub(string connection, Guid user, Permissions permissions, ISeatingDragLeaseService leases,
        IPlanningSeatingStore plan, IAccountStore account) => new(permissions, leases, plan, account)
        { Context = new Caller(connection, user), Groups = new Groups() };
    private sealed class Caller(string connection, Guid user) : HubCallerContext
    {
        public override string ConnectionId => connection;
        public override string? UserIdentifier => user.ToString();
        public override ClaimsPrincipal User => new(new ClaimsIdentity([new Claim("sub", user.ToString())], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
    private sealed class Groups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Permissions : IWeddingPermissionService
    {
        public bool View = true; public bool Edit = true;
        public Task RequireAsync(Guid wedding, WeddingPermission permission, CancellationToken ct) =>
            View && (permission != WeddingPermission.SEATING_EDIT || Edit) ? Task.CompletedTask :
                Task.FromException(new ApplicationProblemException(ProblemKind.Forbidden, "DENIED", "Denied"));
        public Task<bool> HasPermissionAsync(Guid wedding, WeddingPermission permission, CancellationToken ct) => Task.FromResult(View);
    }
    private sealed class Plan : IPlanningSeatingStore
    {
        public int Reads;
        public Guid Wedding = Guid.NewGuid(); public Guid Place = Guid.NewGuid();
        public Task<PlanningSeatingDto> GetAsync(Guid wedding, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new PlanningSeatingDto(Wedding, 1, [], [], [new(Place, Guid.NewGuid(), 1, null)], []));
        }
        public Task<PlanningSeatingDto> ChangeAsync(Guid wedding, ChangePlanningSeating input, CancellationToken ct) => throw new NotSupportedException();
    }
}
