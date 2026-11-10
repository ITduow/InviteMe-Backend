using InviteMe.Api.Hubs;

namespace InviteMe.IntegrationTests;

public sealed class BuilderPresenceTests
{
    [Fact]
    public void Sessions_are_ephemeral_scoped_to_wedding_and_only_current_connection_can_leave()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        var registry = new BuilderPresenceRegistry(clock);
        var weddingId = Guid.NewGuid();
        var otherWeddingId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var location = new BuilderPresenceLocation("home", "hero", "hero.title", "editing");

        var snapshot = registry.Join(weddingId, sessionId, ownerId, "connection-1", "Cô dâu", location);
        Assert.Single(snapshot);
        Assert.Equal("Cô dâu", snapshot[0].DisplayName);
        Assert.DoesNotContain("connection-1", System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.Empty(registry.Snapshot(otherWeddingId));

        registry.Join(weddingId, sessionId, ownerId, "connection-2", "Chú rể", location);
        Assert.Null(registry.LeaveConnection("connection-1"));
        Assert.False(registry.Leave(weddingId, sessionId, ownerId, "connection-1"));
        Assert.Single(registry.Snapshot(weddingId));
        Assert.True(registry.Leave(weddingId, sessionId, ownerId, "connection-2"));
        Assert.Empty(registry.Snapshot(weddingId));
    }

    [Fact]
    public void Heartbeat_keeps_a_session_alive_and_expired_sessions_are_removed_from_snapshots()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        var registry = new BuilderPresenceRegistry(clock);
        var weddingId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var location = new BuilderPresenceLocation("home", null, null, "viewing");
        registry.Join(weddingId, firstSession, userId, "one", "Cô dâu", location);
        registry.Join(weddingId, secondSession, userId, "two", "Chú rể", location);

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.True(registry.Heartbeat(weddingId, firstSession, userId, "one"));
        clock.Advance(TimeSpan.FromSeconds(30));
        var snapshot = registry.Snapshot(weddingId);
        Assert.Single(snapshot);
        Assert.Equal(firstSession, snapshot[0].SessionId);
        Assert.False(registry.Heartbeat(weddingId, secondSession, userId, "two"));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
