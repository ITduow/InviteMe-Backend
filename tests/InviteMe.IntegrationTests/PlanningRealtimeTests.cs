using System.Text.Json;
using InviteMe.Api.Hubs;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using InviteMe.Infrastructure.Persistence.Adapters;

namespace InviteMe.IntegrationTests;

public sealed class PlanningRealtimeTests
{
    [Fact]
    public async Task DragBroadcastRetriesWithTheLatestSnapshotAndNeverExposesTheLeaseToken()
    {
        var leases = new SeatingDragLeaseService(TimeProvider.System);
        var clients = new RecordingClients(); clients.Proxy.Fail = true;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clients.Proxy.Sending = () =>
        {
            if (clients.Proxy.Fail) failed.TrySetResult();
            else if (clients.Proxy.Arguments?[0] is SeatingDragSnapshot { Leases.Count: 0 }) delivered.TrySetResult();
        };
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid();
        var lease = leases.Acquire(wedding, user, "a", "An", [Guid.NewGuid()]);
        Assert.DoesNotContain(lease.Token.ToString(), JsonSerializer.Serialize(leases.Snapshot(wedding)));
        using var worker = new SeatingDragBroadcaster(leases, new TestHubContext(clients), NullLogger<SeatingDragBroadcaster>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            leases.Release(wedding, lease.Token, user, "a");
            clients.Proxy.Fail = false;
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal($"seating:{wedding:D}", clients.GroupName);
            Assert.Equal("SeatingDragSnapshot", clients.Proxy.Method);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public void ApiResolvesPlanningHandlersAndTheirOwnPublisher()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        Assert.IsType<SignalRPlanningChangePublisher>(scope.ServiceProvider.GetRequiredService<IPlanningChangePublisher>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GuestPlanningHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PlanningSeatingHandler>());
    }

    [Theory]
    [InlineData("PlanningGuestsImported", null)]
    [InlineData("PlanningSeatingChanged", 3)]
    public async Task NotificationKeepsFrontendContractAndTargetsOnlyItsWeddingEvenAfterRequestCancellation(string type, int? version)
    {
        var weddingId = Guid.NewGuid();
        var change = new PlanningChange(type, weddingId, version);
        var clients = new RecordingClients();
        var publisher = new SignalRPlanningChangePublisher(new TestHubContext(clients), NullLogger<SignalRPlanningChangePublisher>.Instance);
        using var request = new CancellationTokenSource();
        request.Cancel();

        await publisher.PublishAsync(weddingId, change, request.Token);

        Assert.Equal($"wedding:{weddingId:D}", clients.GroupName);
        Assert.Equal("weddingChanged", clients.Proxy.Method);
        Assert.Same(change, Assert.Single(clients.Proxy.Arguments!));
        Assert.False(clients.Proxy.Token.CanBeCanceled);
        var json = JsonSerializer.SerializeToElement(change, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.Equal(type, json.GetProperty("type").GetString());
        Assert.Equal(weddingId, json.GetProperty("entityId").GetGuid());
        Assert.Equal(version, json.GetProperty("version").Deserialize<int?>());
    }

    [Fact]
    public async Task DeliveryFailureDoesNotFailAnAlreadyCommittedMutation()
    {
        var clients = new RecordingClients();
        clients.Proxy.Fail = true;
        var publisher = new SignalRPlanningChangePublisher(new TestHubContext(clients), NullLogger<SignalRPlanningChangePublisher>.Instance);
        var weddingId = Guid.NewGuid();

        var error = await Record.ExceptionAsync(() => publisher.PublishAsync(weddingId, new("PlanningSeatingChanged", weddingId, 2), CancellationToken.None));

        Assert.Null(error);
        Assert.Equal("weddingChanged", clients.Proxy.Method);
    }

    private sealed class TestHubContext(IHubClients clients) : IHubContext<WeddingHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    internal sealed class RecordingProxy : IClientProxy
    {
        public string? Method { get; private set; }
        public object?[]? Arguments { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Fail { get; set; }
        public Action? Sending { get; set; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Method = method; Arguments = args; Token = cancellationToken;
            var fail = Fail;
            Sending?.Invoke();
            return fail ? Task.FromException(new InvalidOperationException("Transport unavailable")) : Task.CompletedTask;
        }
    }

    internal sealed class RecordingClients : IHubCallerClients, IHubClients
    {
        public string? GroupName { get; private set; }
        public RecordingProxy Proxy { get; } = new();
        public IClientProxy Caller => Proxy;
        public IClientProxy Others => Proxy;
        public IClientProxy OthersInGroup(string groupName) { GroupName = groupName; return Proxy; }
        public IClientProxy Group(string groupName) { GroupName = groupName; return Proxy; }
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
}
