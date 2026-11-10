using System.Collections.Concurrent;

namespace InviteMe.Api.Hubs;

/// <summary>
/// Short lived presence for Wedding Builder tabs. This is intentionally ephemeral and is not persisted.
/// </summary>
public sealed class BuilderPresenceRegistry(TimeProvider clock)
{
    public static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<(Guid WeddingId, Guid SessionId), Entry> _entries = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLocationUpdate = new(StringComparer.Ordinal);

    public bool AllowLocationUpdate(string connectionId)
    {
        var now = clock.GetUtcNow();
        while (true)
        {
            if (!_lastLocationUpdate.TryGetValue(connectionId, out var previous))
            {
                if (_lastLocationUpdate.TryAdd(connectionId, now)) return true;
                continue;
            }
            if (now - previous < TimeSpan.FromMilliseconds(100)) return false;
            if (_lastLocationUpdate.TryUpdate(connectionId, now, previous)) return true;
        }
    }

    public IReadOnlyList<BuilderPresenceDto> Join(Guid weddingId, Guid sessionId, Guid userId, string connectionId,
        string displayName, BuilderPresenceLocation location)
    {
        var now = clock.GetUtcNow();
        _entries[(weddingId, sessionId)] = new Entry(weddingId, sessionId, userId, connectionId, displayName,
            location.PageId, location.SectionId, location.FieldPath, location.Status, now, now);
        return Snapshot(weddingId);
    }

    public (BuilderPresenceDto? Presence, bool Changed) Update(Guid weddingId, Guid sessionId, Guid userId,
        string connectionId, string displayName, BuilderPresenceLocation location)
    {
        var key = (weddingId, sessionId);
        var now = clock.GetUtcNow();
        if (!_entries.TryGetValue(key, out var old) || old.UserId != userId || old.ConnectionId != connectionId || IsExpired(old, now))
            return (null, false);

        var changed = old.DisplayName != displayName || old.PageId != location.PageId || old.SectionId != location.SectionId ||
            old.FieldPath != location.FieldPath || old.Status != location.Status;
        var next = old with
        {
            DisplayName = displayName,
            PageId = location.PageId,
            SectionId = location.SectionId,
            FieldPath = location.FieldPath,
            Status = location.Status,
            LastSeenAt = now,
            UpdatedAt = changed ? now : old.UpdatedAt
        };
        _entries[key] = next;
        return (ToDto(next), changed);
    }

    public bool Heartbeat(Guid weddingId, Guid sessionId, Guid userId, string connectionId)
    {
        var key = (weddingId, sessionId);
        var now = clock.GetUtcNow();
        if (!_entries.TryGetValue(key, out var old) || old.UserId != userId || old.ConnectionId != connectionId || IsExpired(old, now))
            return false;
        _entries[key] = old with { LastSeenAt = now };
        return true;
    }

    public IReadOnlyList<BuilderPresenceDto> Snapshot(Guid weddingId)
    {
        Prune();
        return _entries.Values.Where(entry => entry.WeddingId == weddingId)
            .OrderBy(entry => entry.UpdatedAt)
            .Select(ToDto)
            .ToArray();
    }

    public (Guid WeddingId, Guid SessionId)? LeaveConnection(string connectionId)
    {
        _lastLocationUpdate.TryRemove(connectionId, out _);
        foreach (var pair in _entries)
        {
            if (pair.Value.ConnectionId == connectionId && _entries.TryRemove(pair.Key, out _))
                return (pair.Key.WeddingId, pair.Key.SessionId);
        }
        return null;
    }

    public bool Leave(Guid weddingId, Guid sessionId, Guid userId, string connectionId) =>
        _entries.TryGetValue((weddingId, sessionId), out var entry) && entry.UserId == userId &&
        entry.ConnectionId == connectionId && _entries.TryRemove((weddingId, sessionId), out _);

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var pair in _entries)
            if (IsExpired(pair.Value, now)) _entries.TryRemove(pair.Key, out _);
    }

    private static bool IsExpired(Entry entry, DateTimeOffset now) => now - entry.LastSeenAt > SessionTtl;

    private static BuilderPresenceDto ToDto(Entry entry) => new(entry.SessionId, entry.DisplayName, entry.PageId,
        entry.SectionId, entry.FieldPath, entry.Status, entry.UpdatedAt);

    private sealed record Entry(Guid WeddingId, Guid SessionId, Guid UserId, string ConnectionId, string DisplayName,
        string? PageId, string? SectionId, string? FieldPath, string Status, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt);
}

public sealed record BuilderPresenceDto(Guid SessionId, string DisplayName, string? PageId, string? SectionId,
    string? FieldPath, string Status, DateTimeOffset UpdatedAt);

public sealed record BuilderPresenceLocation(string? PageId, string? SectionId, string? FieldPath, string Status);
