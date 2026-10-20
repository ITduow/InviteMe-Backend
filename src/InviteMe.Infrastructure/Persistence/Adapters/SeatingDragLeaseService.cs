using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.Infrastructure.Persistence.Adapters;

// One process only. All group members are checked and acquired under the same short lock.
public sealed class SeatingDragLeaseService(TimeProvider clock) : ISeatingDragLeaseService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, SeatingDragLease> leases = [];
    private readonly Dictionary<Guid, long> revisions = [];
    private readonly HashSet<Guid> dirty = [];
    private long revision;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    public SeatingDragLease Acquire(Guid weddingId, Guid userId, string connectionId, string displayName, IReadOnlyList<Guid> placeIds)
    {
        lock (gate)
        {
            Expire();
            if (placeIds.Count is < 1 or > 500 || placeIds.Any(x => x == Guid.Empty) || placeIds.Distinct().Count() != placeIds.Count)
                throw new ApplicationProblemException(ProblemKind.BadRequest, "INVALID_DRAG_PLACES", "Chọn các chỗ hợp lệ.");
            if (leases.Values.Any(x => x.WeddingId == weddingId && x.PlaceIds.Intersect(placeIds).Any()))
                throw Conflict("SEATING_PLACE_LOCKED");
            if (leases.Values.Any(x => x.ConnectionId == connectionId)) throw Conflict("SEATING_DRAG_ALREADY_ACTIVE");
            var lease = new SeatingDragLease(Guid.NewGuid(), weddingId, userId, connectionId,
                displayName, placeIds.ToArray(), clock.GetUtcNow() + Lifetime);
            leases.Add(lease.Token, lease);
            Changed(weddingId);
            return lease;
        }
    }

    public SeatingDragLease Renew(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        lock (gate)
        {
            var lease = Owned(weddingId, token, userId, connectionId) with { ExpiresAt = clock.GetUtcNow() + Lifetime };
            leases[token] = lease;
            Changed(weddingId);
            return lease;
        }
    }

    public void Target(Guid weddingId, Guid token, Guid userId, string connectionId, Guid? tableId, int? seatNumber)
    {
        lock (gate)
        {
            var lease = Owned(weddingId, token, userId, connectionId);
            if (lease.TableId == tableId && lease.SeatNumber == seatNumber) return;
            leases[token] = lease with { TableId = tableId, SeatNumber = seatNumber };
            Changed(weddingId);
        }
    }

    public void Release(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        lock (gate)
        {
            Expire();
            if (!leases.ContainsKey(token)) return;
            Owned(weddingId, token, userId, connectionId);
            leases.Remove(token);
            Changed(weddingId);
        }
    }

    public void Disconnect(string connectionId, Guid? weddingId = null)
    {
        lock (gate)
            foreach (var lease in leases.Values.Where(x => x.ConnectionId == connectionId &&
                         (weddingId == null || x.WeddingId == weddingId)).ToArray())
            {
                leases.Remove(lease.Token);
                Changed(lease.WeddingId);
            }
    }

    public SeatingDragSnapshot Snapshot(Guid weddingId)
    {
        lock (gate) { Expire(); return Read(weddingId); }
    }

    public IReadOnlyList<SeatingDragSnapshot> DrainChanges()
    {
        lock (gate)
        {
            Expire();
            var result = dirty.Select(Read).ToArray();
            dirty.Clear();
            return result;
        }
    }

    private SeatingDragLease Owned(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        Expire();
        if (!leases.TryGetValue(token, out var lease) || lease.WeddingId != weddingId ||
            lease.UserId != userId || lease.ConnectionId != connectionId) throw Conflict("SEATING_DRAG_EXPIRED");
        return lease;
    }
    private void Expire()
    {
        foreach (var lease in leases.Values.Where(x => x.ExpiresAt <= clock.GetUtcNow()).ToArray())
        {
            leases.Remove(lease.Token);
            Changed(lease.WeddingId);
        }
    }
    private void Changed(Guid weddingId) { revisions[weddingId] = ++revision; dirty.Add(weddingId); }
    private SeatingDragSnapshot Read(Guid weddingId) =>
        new(weddingId, revisions.GetValueOrDefault(weddingId), leases.Values.Where(x => x.WeddingId == weddingId)
            .Select(x => new SeatingDragPresence(x.UserId, x.ConnectionId, x.DisplayName, x.PlaceIds, x.ExpiresAt, x.TableId, x.SeatNumber)).ToArray());
    private static ApplicationProblemException Conflict(string code) =>
        new(ProblemKind.Conflict, code, "Chỗ đang được thao tác hoặc quyền kéo đã hết hạn.");
}
